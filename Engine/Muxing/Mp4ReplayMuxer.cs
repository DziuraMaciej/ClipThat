using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Buffer;
using Vortice.MediaFoundation;

namespace ClipThat.Engine.Muxing;

/// <summary>
/// Remuxes compressed H.264 video and dual-track AAC audio packets from an <see cref="EncodedPacketSnapshot"/>
/// directly into a native Windows Media Foundation <c>.mp4</c> file without re-encoding video or audio.
/// </summary>
public sealed class Mp4ReplayMuxer
{
    private const uint AacRawPayloadType = 0u;
    private const uint AacProfileLevelIndicationLcL2 = 0x29u;
    private const long DefaultAacDuration100Ns =
        (DualAudioCaptureService.AacFrameSampleCount * TimeSpan.TicksPerSecond) / DualAudioCaptureService.TargetSampleRate;

    /// <summary>
    /// Deterministic MP4 track registration order:
    /// Stream 0: H.264 Video,
    /// Stream 1: System Audio (first audio track so standard players and Discord play system sound by default),
    /// Stream 2: Microphone Audio (second audio track).
    /// </summary>
    private static readonly MediaTrackType[] DeterministicTrackOrder =
    {
        MediaTrackType.Video,
        MediaTrackType.SystemAudio,
        MediaTrackType.MicrophoneAudio
    };

    /// <summary>
    /// Standard Media Foundation <c>MF_MT_USER_DATA</c> payload for raw AAC-LC 48000 Hz Stereo:
    /// 12-byte <c>HEAACWAVEINFO</c> header fields + 2-byte ISO 14496-3 <c>AudioSpecificConfig</c> (<c>0x11, 0x90</c>).
    /// Used as fallback when the AAC encoder MFT did not expose a custom blob.
    /// </summary>
    private static readonly byte[] DefaultAac48kStereoUserData =
    {
        0x00, 0x00,             // wPayloadType = 0 (raw AAC)
        0x29, 0x00,             // wAudioProfileLevelIndication = 0x29 (AAC-LC L2)
        0x00, 0x00,             // wStructType = 0
        0x00, 0x00,             // wReserved1 = 0
        0x00, 0x00, 0x00, 0x00, // dwReserved2 = 0
        0x11, 0x90              // AudioSpecificConfig: AAC-LC (2), 48000 Hz (3), Stereo (2)
    };

    /// <summary>
    /// Muxes the supplied ring-buffer snapshot into a new <c>ssmmHHddMMyyyy.mp4</c> file inside
    /// <paramref name="outputDirectory"/>, cleanly rebasing all stream timestamps to <c>00:00:00.000</c>
    /// starting at the first video IDR keyframe.
    /// </summary>
    public string MuxToMp4(
        EncodedPacketSnapshot snapshot,
        string outputDirectory,
        int width,
        int height,
        int fps,
        uint videoBitrateBps)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Ścieżka katalogu wyjściowego nie może być pusta.", nameof(outputDirectory));
        }

        if (width <= 0 || height <= 0 || fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Nieprawidłowe parametry wideo dla muksera MP4.");
        }

        int firstKeyframeIndex = FindFirstVideoKeyframeIndex(snapshot);
        if (firstKeyframeIndex < 0)
        {
            throw new InvalidOperationException("Bufor powtórki nie zawiera gotowej klatki kluczowej wideo.");
        }

        long baseTimestamp100Ns = snapshot[firstKeyframeIndex].Timestamp100Ns;
        var orderedPackets = new List<IndexedPacket>(snapshot.Count - firstKeyframeIndex);
        bool hasSystemAudio = false;
        bool hasMicrophoneAudio = false;

        for (int i = 0; i < snapshot.Count; i++)
        {
            EncodedPacket packet = snapshot[i];
            if (packet.Length <= 0 || packet.Timestamp100Ns < baseTimestamp100Ns)
            {
                continue;
            }

            if (packet.TrackType == MediaTrackType.Video)
            {
                if (i < firstKeyframeIndex)
                {
                    continue;
                }
            }
            else if (packet.TrackType == MediaTrackType.SystemAudio)
            {
                hasSystemAudio = true;
            }
            else if (packet.TrackType == MediaTrackType.MicrophoneAudio)
            {
                hasMicrophoneAudio = true;
            }

            orderedPackets.Add(new IndexedPacket(packet, i));
        }

        if (orderedPackets.Count == 0)
        {
            throw new InvalidOperationException("Brak pakietów wideo do zapisania w pliku MP4.");
        }

        // Stable timestamp ordering ensures smooth MP4 interleaving across video and audio streams
        // while preserving deterministic track priority (Video -> SystemAudio -> MicrophoneAudio)
        // and exact encoder packet order within each track.
        orderedPackets.Sort(static (a, b) =>
        {
            int cmp = a.Packet.Timestamp100Ns.CompareTo(b.Packet.Timestamp100Ns);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = GetTrackOrderPriority(a.Packet.TrackType).CompareTo(GetTrackOrderPriority(b.Packet.TrackType));
            return cmp != 0 ? cmp : a.OriginalIndex.CompareTo(b.OriginalIndex);
        });

        Directory.CreateDirectory(outputDirectory);
        string fileName = DateTime.Now.ToString("ssmmHHddMMyyyy") + ".mp4";
        string outputFilePath = Path.Combine(outputDirectory, fileName);

        byte[]? videoSequenceHeader = snapshot.VideoSequenceHeader is { Length: > 0 }
            ? snapshot.VideoSequenceHeader
            : TryExtractAnnexBSpsPps(snapshot[firstKeyframeIndex].Data);

        long defaultVideoDuration100Ns = TimeSpan.TicksPerSecond / fps;

        bool mfStarted = false;
        bool finalized = false;
        IMFAttributes? sinkAttributes = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? videoMediaType = null;
        IMFMediaType? systemAudioMediaType = null;
        IMFMediaType? microphoneAudioMediaType = null;

        try
        {
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            mfStarted = true;

            sinkAttributes = MediaFactory.MFCreateAttributes(3);
            sinkAttributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4).CheckError();
            sinkAttributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
            sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteDisableConverters, 1u);

            sinkWriter = MediaFactory.MFCreateSinkWriterFromURL(outputFilePath, null, sinkAttributes);

            // Register streams in strict deterministic order:
            // Stream 0: H.264 Video
            // Stream 1: System Audio (MediaTrackType.SystemAudio — first audio track for players & Discord)
            // Stream 2: Microphone Audio (MediaTrackType.MicrophoneAudio — secondary audio track)
            int videoStreamIndex = -1;
            int systemAudioStreamIndex = -1;
            int microphoneAudioStreamIndex = -1;

            foreach (MediaTrackType trackType in DeterministicTrackOrder)
            {
                switch (trackType)
                {
                    case MediaTrackType.Video:
                        videoMediaType = CreateH264MediaType(width, height, fps, videoBitrateBps, videoSequenceHeader);
                        videoStreamIndex = sinkWriter.AddStream(videoMediaType);
                        sinkWriter.SetInputMediaType(videoStreamIndex, videoMediaType, null);
                        break;

                    case MediaTrackType.SystemAudio when hasSystemAudio:
                    {
                        byte[] sysUserData = snapshot.SystemAudioUserData is { Length: > 0 }
                            ? snapshot.SystemAudioUserData
                            : DefaultAac48kStereoUserData;

                        systemAudioMediaType = CreateAacMediaType(sysUserData);
                        systemAudioStreamIndex = sinkWriter.AddStream(systemAudioMediaType);
                        sinkWriter.SetInputMediaType(systemAudioStreamIndex, systemAudioMediaType, null);
                        break;
                    }

                    case MediaTrackType.MicrophoneAudio when hasMicrophoneAudio:
                    {
                        byte[] micUserData = snapshot.MicrophoneAudioUserData is { Length: > 0 }
                            ? snapshot.MicrophoneAudioUserData
                            : DefaultAac48kStereoUserData;

                        microphoneAudioMediaType = CreateAacMediaType(micUserData);
                        microphoneAudioStreamIndex = sinkWriter.AddStream(microphoneAudioMediaType);
                        sinkWriter.SetInputMediaType(microphoneAudioStreamIndex, microphoneAudioMediaType, null);
                        break;
                    }
                }
            }

            if (videoStreamIndex != 0 ||
                (hasSystemAudio && systemAudioStreamIndex != 1) ||
                (hasSystemAudio && hasMicrophoneAudio && microphoneAudioStreamIndex != 2))
            {
                throw new InvalidOperationException(
                    $"Nieoczekiwana kolejność strumieni MP4 (Video={videoStreamIndex}, SystemAudio={systemAudioStreamIndex}, MicrophoneAudio={microphoneAudioStreamIndex}).");
            }

            sinkWriter.BeginWriting();

            long lastVideoTimestamp = -1;
            long lastSystemAudioTimestamp = -1;
            long lastMicAudioTimestamp = -1;

            for (int i = 0; i < orderedPackets.Count; i++)
            {
                EncodedPacket packet = orderedPackets[i].Packet;
                int targetStreamIndex;
                long sampleTime100Ns = packet.Timestamp100Ns - baseTimestamp100Ns;
                long sampleDuration100Ns;

                switch (packet.TrackType)
                {
                    case MediaTrackType.Video:
                        targetStreamIndex = videoStreamIndex;
                        sampleTime100Ns = EnsureStrictlyMonotonic(sampleTime100Ns, ref lastVideoTimestamp);
                        sampleDuration100Ns = packet.Duration100Ns > 0 ? packet.Duration100Ns : defaultVideoDuration100Ns;
                        break;

                    case MediaTrackType.SystemAudio when systemAudioStreamIndex >= 0:
                        targetStreamIndex = systemAudioStreamIndex;
                        sampleTime100Ns = EnsureStrictlyMonotonic(sampleTime100Ns, ref lastSystemAudioTimestamp);
                        sampleDuration100Ns = packet.Duration100Ns > 0 ? packet.Duration100Ns : DefaultAacDuration100Ns;
                        break;

                    case MediaTrackType.MicrophoneAudio when microphoneAudioStreamIndex >= 0:
                        targetStreamIndex = microphoneAudioStreamIndex;
                        sampleTime100Ns = EnsureStrictlyMonotonic(sampleTime100Ns, ref lastMicAudioTimestamp);
                        sampleDuration100Ns = packet.Duration100Ns > 0 ? packet.Duration100Ns : DefaultAacDuration100Ns;
                        break;

                    default:
                        continue;
                }

                WritePacketSample(sinkWriter, targetStreamIndex, packet, sampleTime100Ns, sampleDuration100Ns);
            }

            sinkWriter.Finalize();
            finalized = true;
            return outputFilePath;
        }
        finally
        {
            SafeDispose(sinkWriter);
            SafeDispose(microphoneAudioMediaType);
            SafeDispose(systemAudioMediaType);
            SafeDispose(videoMediaType);
            SafeDispose(sinkAttributes);

            if (!finalized)
            {
                TryDeletePartialFile(outputFilePath);
            }

            if (mfStarted)
            {
                try
                {
                    MediaFactory.MFShutdown();
                }
                catch
                {
                    // Ignore MFShutdown errors on teardown.
                }
            }
        }
    }

    private static void WritePacketSample(
        IMFSinkWriter sinkWriter,
        int streamIndex,
        EncodedPacket packet,
        long sampleTime100Ns,
        long sampleDuration100Ns)
    {
        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.SampleTime = sampleTime100Ns;
        sample.SampleDuration = sampleDuration100Ns;

        if (packet.TrackType == MediaTrackType.Video && packet.IsKeyframe)
        {
            sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        }

        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(packet.Length);
        buffer.Lock(out nint dstPtr, out int maxLength, out _);
        try
        {
            if (maxLength < packet.Length)
            {
                throw new InvalidOperationException("Za mały bufor próbki Media Foundation podczas zapisu MP4.");
            }

            Marshal.Copy(packet.Buffer, 0, dstPtr, packet.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = packet.Length;
        sample.AddBuffer(buffer);
        sinkWriter.WriteSample(streamIndex, sample);
    }

    private static IMFMediaType CreateH264MediaType(
        int width,
        int height,
        int fps,
        uint videoBitrateBps,
        byte[]? sequenceHeader)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        try
        {
            mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AvgBitrate, videoBitrateBps).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)width, (uint)height)).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)fps, 1u)).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u)).CheckError();

            if (sequenceHeader is { Length: > 0 })
            {
                mediaType.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, sequenceHeader).CheckError();
            }

            return mediaType;
        }
        catch
        {
            mediaType.Dispose();
            throw;
        }
    }

    private static IMFMediaType CreateAacMediaType(byte[] userData)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        try
        {
            mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)DualAudioCaptureService.TargetBitsPerSample).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)DualAudioCaptureService.TargetSampleRate).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)DualAudioCaptureService.TargetChannels).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)DualAudioCaptureService.TargetAacAvgBytesPerSecond).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AacPayloadType, AacRawPayloadType).CheckError();
            mediaType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, AacProfileLevelIndicationLcL2);
            mediaType.SetBlob(MediaTypeAttributeKeys.UserData, userData).CheckError();
            return mediaType;
        }
        catch
        {
            mediaType.Dispose();
            throw;
        }
    }

    private static int FindFirstVideoKeyframeIndex(EncodedPacketSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.Count; i++)
        {
            EncodedPacket packet = snapshot[i];
            if (packet.TrackType == MediaTrackType.Video && packet.IsKeyframe && packet.Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static long EnsureStrictlyMonotonic(long candidateTimestamp, ref long lastTimestamp)
    {
        if (candidateTimestamp < 0)
        {
            candidateTimestamp = 0;
        }

        if (lastTimestamp >= 0 && candidateTimestamp <= lastTimestamp)
        {
            candidateTimestamp = lastTimestamp + 1;
        }

        lastTimestamp = candidateTimestamp;
        return candidateTimestamp;
    }

    private static byte[]? TryExtractAnnexBSpsPps(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream();
        int pos = FindNextStartCode(data, 0, out int startCodeLen);

        while (pos >= 0 && pos + startCodeLen < data.Length)
        {
            int nalStart = pos + startCodeLen;
            int nextPos = FindNextStartCode(data, nalStart, out int nextStartCodeLen);
            int nalEnd = nextPos >= 0 ? nextPos : data.Length;

            int nalType = data[nalStart] & 0x1F;
            if (nalType is 7 or 8)
            {
                stream.Write(data.Slice(pos, nalEnd - pos));
            }

            pos = nextPos;
            startCodeLen = nextStartCodeLen;
        }

        return stream.Length > 0 ? stream.ToArray() : null;
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int startIndex, out int startCodeLength)
    {
        for (int i = startIndex; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLength = 3;
                    return i;
                }

                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                    return i;
                }
            }
        }

        startCodeLength = 0;
        return -1;
    }

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się usunąć niekompletnego pliku MP4 '{path}': {ex.Message}");
        }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd zwalniania obiektu COM muksera MP4: {ex.Message}");
        }
    }

    private static int GetTrackOrderPriority(MediaTrackType trackType) => trackType switch
    {
        MediaTrackType.Video => 0,
        MediaTrackType.SystemAudio => 1,
        MediaTrackType.MicrophoneAudio => 2,
        _ => 3
    };

    private readonly record struct IndexedPacket(EncodedPacket Packet, int OriginalIndex);
}
