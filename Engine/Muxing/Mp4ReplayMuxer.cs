using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Diagnostics;
using ClipThat.Services;
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

    // ISO/IEC 14496-12 (MP4) FourCC box and handler identifiers
    private const uint BoxTypeMoov = 0x6D6F6F76u;      // "moov"
    private const uint BoxTypeMvhd = 0x6D766864u;      // "mvhd"
    private const uint BoxTypeTrak = 0x7472616Bu;      // "trak"
    private const uint BoxTypeTkhd = 0x746B6864u;      // "tkhd"
    private const uint BoxTypeMdia = 0x6D646961u;      // "mdia"
    private const uint BoxTypeHdlr = 0x68646C72u;      // "hdlr"
    private const uint BoxTypeUuid = 0x75756964u;      // "uuid"
    private const uint HandlerTypeSound = 0x736F756Eu; // "soun"

    /// <summary>
    /// Deterministic MP4 track registration order for Windows Media Foundation compatibility:
    /// Windows Media Foundation's MPEG-4 demuxer selects the LAST audio stream by default.
    /// When microphone packets exist (3 audio tracks):
    /// Stream 0: H.264 Video,
    /// Stream 1 (0:a:0): Isolated System Audio,
    /// Stream 2 (0:a:1): Isolated Microphone Audio,
    /// Stream 3 (0:a:2, LAST): Combined Mix Audio (System + Mic mix — default track for WMP, VLC, browsers, Discord).
    /// When microphone capture is disabled or absent (1 audio track):
    /// Stream 0: H.264 Video,
    /// Stream 1 (0:a:0): System Audio.
    /// </summary>
    private static readonly MediaTrackType[] DeterministicTrackOrder =
    {
        MediaTrackType.Video,
        MediaTrackType.SystemAudio,
        MediaTrackType.MicrophoneAudio,
        MediaTrackType.CombinedMixAudio
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

        int evenWidth = Math.Max(2, width & ~1);
        int evenHeight = Math.Max(2, height & ~1);

        int firstKeyframeIndex = FindFirstVideoKeyframeIndex(snapshot);
        if (firstKeyframeIndex < 0)
        {
            throw new InvalidOperationException("Bufor powtórki nie zawiera gotowej klatki kluczowej wideo.");
        }

        long baseTimestamp100Ns = snapshot[firstKeyframeIndex].Timestamp100Ns;
        var orderedPackets = new List<IndexedPacket>(snapshot.Count - firstKeyframeIndex);
        bool hasCombinedMixAudio = false;
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
            else if (packet.TrackType == MediaTrackType.CombinedMixAudio)
            {
                hasCombinedMixAudio = true;
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

        // Only include the 3-track layout (System + Mic + CombinedMix last) when microphone packets are present;
        // otherwise fall back to SystemAudio as Stream 1.
        bool includeCombinedMix = hasMicrophoneAudio && hasCombinedMixAudio;

        // Stable timestamp ordering ensures smooth MP4 interleaving across video and audio streams
        // while preserving deterministic track priority (Video -> SystemAudio -> MicrophoneAudio -> CombinedMixAudio)
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

        try
        {
            MuxWithMediaFoundation(
                snapshot,
                orderedPackets,
                baseTimestamp100Ns,
                outputFilePath,
                evenWidth,
                evenHeight,
                fps,
                videoBitrateBps,
                videoSequenceHeader,
                hasSystemAudio,
                hasMicrophoneAudio,
                includeCombinedMix);

            EngineLogger.Info("Mp4ReplayMuxer", $"Saved replay via Media Foundation SinkWriter: \"{outputFilePath}\"");
            return outputFilePath;
        }
        catch (Exception mfEx)
        {
            EngineLogger.Warn(
                "Mp4ReplayMuxer",
                $"Media Foundation SinkWriter mux failed ({mfEx.Message}); attempting portable embedded FFmpeg fallback.",
                mfEx);

            TryDeletePartialFile(outputFilePath);

            try
            {
                MuxWithPortableFfmpeg(
                    orderedPackets,
                    outputFilePath,
                    fps,
                    videoSequenceHeader,
                    hasSystemAudio,
                    hasMicrophoneAudio,
                    includeCombinedMix);

                EngineLogger.Info("Mp4ReplayMuxer", $"Saved replay via portable FFmpeg fallback: \"{outputFilePath}\"");
                return outputFilePath;
            }
            catch (Exception ffmpegEx)
            {
                EngineLogger.Error(
                    "Mp4ReplayMuxer",
                    $"Portable FFmpeg fallback mux also failed for \"{outputFilePath}\".",
                    ffmpegEx);
                TryDeletePartialFile(outputFilePath);
                throw new InvalidOperationException(
                    $"Nie udało się zapisać pliku MP4 (Media Foundation: {mfEx.Message}; FFmpeg: {ffmpegEx.Message})",
                    ffmpegEx);
            }
        }
    }

    private static void MuxWithMediaFoundation(
        EncodedPacketSnapshot snapshot,
        List<IndexedPacket> orderedPackets,
        long baseTimestamp100Ns,
        string outputFilePath,
        int width,
        int height,
        int fps,
        uint videoBitrateBps,
        byte[]? videoSequenceHeader,
        bool hasSystemAudio,
        bool hasMicrophoneAudio,
        bool includeCombinedMix)
    {
        long defaultVideoDuration100Ns = TimeSpan.TicksPerSecond / fps;

        bool mfStarted = false;
        bool finalized = false;
        IMFAttributes? sinkAttributes = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? videoMediaType = null;
        IMFMediaType? combinedMixAudioMediaType = null;
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

            // Register streams in strict deterministic order (CombinedMixAudio LAST for Windows Media Foundation):
            // Stream 0: H.264 Video
            // When microphone packets are present:
            //   Stream 1 (0:a:0): System Audio
            //   Stream 2 (0:a:1): Microphone Audio
            //   Stream 3 (0:a:2): Combined Mix Audio (default track for WMP, VLC, browsers, Discord)
            // Otherwise:
            //   Stream 1 (0:a:0): System Audio
            int videoStreamIndex = -1;
            int systemAudioStreamIndex = -1;
            int microphoneAudioStreamIndex = -1;
            int combinedMixAudioStreamIndex = -1;

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

                    case MediaTrackType.CombinedMixAudio when includeCombinedMix:
                    {
                        byte[] mixUserData = snapshot.CombinedMixAudioUserData is { Length: > 0 }
                            ? snapshot.CombinedMixAudioUserData
                            : DefaultAac48kStereoUserData;

                        combinedMixAudioMediaType = CreateAacMediaType(mixUserData);
                        combinedMixAudioStreamIndex = sinkWriter.AddStream(combinedMixAudioMediaType);
                        sinkWriter.SetInputMediaType(combinedMixAudioStreamIndex, combinedMixAudioMediaType, null);
                        break;
                    }
                }
            }

            int expectedNextIndex = 1;
            bool invalidStreamOrder = videoStreamIndex != 0;
            if (hasSystemAudio)
            {
                invalidStreamOrder |= systemAudioStreamIndex != expectedNextIndex++;
            }

            if (hasMicrophoneAudio)
            {
                invalidStreamOrder |= microphoneAudioStreamIndex != expectedNextIndex++;
            }

            if (includeCombinedMix)
            {
                invalidStreamOrder |= combinedMixAudioStreamIndex != expectedNextIndex++;
            }

            if (invalidStreamOrder)
            {
                throw new InvalidOperationException(
                    $"Nieoczekiwana kolejność strumieni MP4 (Video={videoStreamIndex}, SystemAudio={systemAudioStreamIndex}, MicrophoneAudio={microphoneAudioStreamIndex}, CombinedMix={combinedMixAudioStreamIndex}).");
            }

            sinkWriter.BeginWriting();

            long lastVideoTimestamp = -1;
            long lastCombinedMixAudioTimestamp = -1;
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

                    case MediaTrackType.CombinedMixAudio when combinedMixAudioStreamIndex >= 0:
                        targetStreamIndex = combinedMixAudioStreamIndex;
                        sampleTime100Ns = EnsureStrictlyMonotonic(sampleTime100Ns, ref lastCombinedMixAudioTimestamp);
                        sampleDuration100Ns = packet.Duration100Ns > 0 ? packet.Duration100Ns : DefaultAacDuration100Ns;
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

            // Release the sink writer before opening the finalized MP4 in-place for moov/tkhd patching.
            SafeDispose(sinkWriter);
            sinkWriter = null;

            TryPatchAudioTrackSelectionFlags(outputFilePath);
        }
        finally
        {
            SafeDispose(sinkWriter);
            SafeDispose(microphoneAudioMediaType);
            SafeDispose(systemAudioMediaType);
            SafeDispose(combinedMixAudioMediaType);
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

    private static void MuxWithPortableFfmpeg(
        List<IndexedPacket> orderedPackets,
        string outputFilePath,
        int fps,
        byte[]? videoSequenceHeader,
        bool hasSystemAudio,
        bool hasMicrophoneAudio,
        bool includeCombinedMix)
    {
        string? ffmpegPath = PortableFfmpegProvider.TryEnsureExtracted();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException("Osadzony plik ffmpeg.exe nie jest dostępny do zapasowego zapisu MP4.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "ClipThat", "Mux", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "video.h264");
            string? sysAudioPath = hasSystemAudio ? Path.Combine(tempDir, "sys.aac") : null;
            string? micAudioPath = hasMicrophoneAudio ? Path.Combine(tempDir, "mic.aac") : null;
            string? mixAudioPath = includeCombinedMix ? Path.Combine(tempDir, "mix.aac") : null;

            bool wroteVideoHeader = false;
            Span<byte> adtsHeader = stackalloc byte[7];

            using (var videoStream = new FileStream(videoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (FileStream? sysStream = sysAudioPath is not null ? new FileStream(sysAudioPath, FileMode.Create, FileAccess.Write, FileShare.None, 32768) : null)
            using (FileStream? micStream = micAudioPath is not null ? new FileStream(micAudioPath, FileMode.Create, FileAccess.Write, FileShare.None, 32768) : null)
            using (FileStream? mixStream = mixAudioPath is not null ? new FileStream(mixAudioPath, FileMode.Create, FileAccess.Write, FileShare.None, 32768) : null)
            {
                for (int i = 0; i < orderedPackets.Count; i++)
                {
                    EncodedPacket packet = orderedPackets[i].Packet;
                    switch (packet.TrackType)
                    {
                        case MediaTrackType.Video:
                            if (!wroteVideoHeader)
                            {
                                wroteVideoHeader = true;
                                if (videoSequenceHeader is { Length: > 0 } &&
                                    TryExtractAnnexBSpsPps(packet.Data) is null)
                                {
                                    videoStream.Write(videoSequenceHeader, 0, videoSequenceHeader.Length);
                                }
                            }

                            videoStream.Write(packet.Data);
                            break;

                        case MediaTrackType.SystemAudio when sysStream is not null:
                            WriteAdtsAacFrame(sysStream, packet.Data, adtsHeader);
                            break;

                        case MediaTrackType.MicrophoneAudio when micStream is not null:
                            WriteAdtsAacFrame(micStream, packet.Data, adtsHeader);
                            break;

                        case MediaTrackType.CombinedMixAudio when mixStream is not null:
                            WriteAdtsAacFrame(mixStream, packet.Data, adtsHeader);
                            break;
                    }
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("h264");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);

            var audioInputIndices = new List<int>(3);
            int nextInputIndex = 1;

            void AddAudioInputIfValid(string? path)
            {
                if (path is not null && File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    startInfo.ArgumentList.Add("-f");
                    startInfo.ArgumentList.Add("aac");
                    startInfo.ArgumentList.Add("-i");
                    startInfo.ArgumentList.Add(path);
                    audioInputIndices.Add(nextInputIndex++);
                }
            }

            AddAudioInputIfValid(sysAudioPath);
            AddAudioInputIfValid(micAudioPath);
            AddAudioInputIfValid(mixAudioPath);

            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            for (int i = 0; i < audioInputIndices.Count; i++)
            {
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add($"{audioInputIndices[i]}:a:0");
            }

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add(outputFilePath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Nie udało się uruchomić procesu ffmpeg.exe.");
            }

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(outputFilePath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"Kod wyjścia: {process.ExitCode}"
                    : stderr.Trim();
                throw new InvalidOperationException($"FFmpeg zakończył się błędem: {detail}");
            }

            TryPatchAudioTrackSelectionFlags(outputFilePath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static void WriteAdtsAacFrame(FileStream stream, ReadOnlySpan<byte> rawAacPayload, Span<byte> headerBuffer)
    {
        if (rawAacPayload.IsEmpty)
        {
            return;
        }

        // Standard 7-byte ISO 14496-3 ADTS header for AAC-LC (profile=1), 48000 Hz (freqIdx=3), Stereo (chanCfg=2).
        const int profile = 1;
        const int freqIdx = 3;
        const int chanCfg = 2;
        int frameLength = rawAacPayload.Length + 7;

        headerBuffer[0] = 0xFF;
        headerBuffer[1] = 0xF1;
        headerBuffer[2] = (byte)((profile << 6) | (freqIdx << 2) | (chanCfg >> 2));
        headerBuffer[3] = (byte)(((chanCfg & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        headerBuffer[4] = (byte)((frameLength >> 3) & 0xFF);
        headerBuffer[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        headerBuffer[6] = 0xFC;

        stream.Write(headerBuffer);
        stream.Write(rawAacPayload);
    }

    private static unsafe void WritePacketSample(
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

        using (IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(packet.Length))
        {
            buffer.Lock(out nint dstPtr, out int maxLength, out _);
            try
            {
                if (maxLength < packet.Length)
                {
                    throw new InvalidOperationException("Za mały bufor próbki Media Foundation podczas zapisu MP4.");
                }

                packet.Data.CopyTo(new Span<byte>((void*)dstPtr, packet.Length));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = packet.Length;
            sample.AddBuffer(buffer);
        }

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

    /// <summary>
    /// Patches <c>moov -&gt; trak -&gt; tkhd</c> audio track headers in-place so Windows Media Player,
    /// VLC, browsers, and Discord select the LAST audio track (<c>CombinedMixAudio</c> when 3 tracks exist,
    /// or <c>SystemAudio</c> when 1 track exists) by default while keeping all isolated tracks selectable
    /// in VLC and video editors:
    /// <list type="bullet">
    /// <item>Sets <c>alternate_group = 1</c> (big-endian <c>0x0001</c>) on every audio track.</item>
    /// <item>Sets <c>tkhd</c> flags to <c>0x000003</c> (<c>track_enabled | track_in_movie</c>) on ONLY the last audio track.</item>
    /// <item>Sets <c>tkhd</c> flags to <c>0x000002</c> (<c>track_in_movie</c>, disabled by default) on all earlier audio tracks.</item>
    /// </list>
    /// </summary>
    public static void PatchAudioTrackSelectionFlags(string mp4FilePath)
    {
        if (string.IsNullOrWhiteSpace(mp4FilePath))
        {
            throw new ArgumentException("Ścieżka pliku MP4 nie może być pusta.", nameof(mp4FilePath));
        }

        using FileStream stream = OpenFileStreamWithRetry(
            mp4FilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        if (!TryFindChildBox(stream, 0, stream.Length, BoxTypeMoov, out Mp4BoxHeader moov))
        {
            return;
        }

        Span<byte> handlerBytes = stackalloc byte[4];
        Span<byte> enabledTrackFlags = stackalloc byte[3] { 0x00, 0x00, 0x03 };
        Span<byte> alternateTrackFlags = stackalloc byte[3] { 0x00, 0x00, 0x02 };
        Span<byte> alternateGroupOne = stackalloc byte[2] { 0x00, 0x01 };

        var audioTrackPatchTargets = new List<(long FlagsOffset, long AlternateGroupOffset)>(4);

        long offset = moov.PayloadStart;
        while (offset + 8 <= moov.BoxEnd)
        {
            if (!TryReadBoxHeader(stream, offset, moov.BoxEnd, out Mp4BoxHeader child))
            {
                break;
            }

            if (child.Type == BoxTypeTrak &&
                TryFindChildBox(stream, child.PayloadStart, child.BoxEnd, BoxTypeMdia, out Mp4BoxHeader mdia) &&
                TryFindChildBox(stream, mdia.PayloadStart, mdia.BoxEnd, BoxTypeHdlr, out Mp4BoxHeader hdlr) &&
                hdlr.PayloadLength >= 12)
            {
                stream.Position = hdlr.PayloadStart + 8;
                if (ReadExact(stream, handlerBytes) &&
                    BinaryPrimitives.ReadUInt32BigEndian(handlerBytes) == HandlerTypeSound &&
                    TryFindChildBox(stream, child.PayloadStart, child.BoxEnd, BoxTypeTkhd, out Mp4BoxHeader tkhd) &&
                    tkhd.PayloadLength >= 4)
                {
                    stream.Position = tkhd.PayloadStart;
                    int version = stream.ReadByte();
                    long alternateGroupRelativeOffset = version switch
                    {
                        0 when tkhd.PayloadLength >= 36 => 34,
                        1 when tkhd.PayloadLength >= 48 => 46,
                        _ => -1
                    };

                    if (alternateGroupRelativeOffset >= 0)
                    {
                        audioTrackPatchTargets.Add((
                            tkhd.PayloadStart + 1,
                            tkhd.PayloadStart + alternateGroupRelativeOffset));
                    }
                }
            }

            offset = child.BoxEnd;
        }

        if (audioTrackPatchTargets.Count == 0)
        {
            return;
        }

        int lastAudioIndex = audioTrackPatchTargets.Count - 1;
        for (int i = 0; i < audioTrackPatchTargets.Count; i++)
        {
            (long flagsOffset, long alternateGroupOffset) = audioTrackPatchTargets[i];
            bool isLastAudioTrack = i == lastAudioIndex;

            // Write 24-bit FullBox flags right after the 1-byte version field.
            stream.Position = flagsOffset;
            stream.Write(isLastAudioTrack ? enabledTrackFlags : alternateTrackFlags);

            // Write 16-bit big-endian alternate_group = 1 on every audio track.
            stream.Position = alternateGroupOffset;
            stream.Write(alternateGroupOne);
        }

        stream.Flush();
    }

    /// <summary>
    /// Reads duration (from <c>moov -&gt; mvhd</c>) and audio track count (from <c>moov -&gt; trak -&gt; mdia -&gt; hdlr</c>)
    /// directly from an MP4 file's ISO BMFF boxes without spawning external processes.
    /// </summary>
    public static bool TryReadMp4Metadata(string mp4FilePath, out TimeSpan duration, out int audioTrackCount)
    {
        duration = TimeSpan.Zero;
        audioTrackCount = 0;

        if (string.IsNullOrWhiteSpace(mp4FilePath) || !File.Exists(mp4FilePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                mp4FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (!TryFindChildBox(stream, 0, stream.Length, BoxTypeMoov, out Mp4BoxHeader moov))
            {
                return false;
            }

            Span<byte> buffer12 = stackalloc byte[12];
            Span<byte> handlerBytes = stackalloc byte[4];

            long offset = moov.PayloadStart;
            while (offset + 8 <= moov.BoxEnd)
            {
                if (!TryReadBoxHeader(stream, offset, moov.BoxEnd, out Mp4BoxHeader child))
                {
                    break;
                }

                if (child.Type == BoxTypeMvhd && child.PayloadLength >= 20)
                {
                    stream.Position = child.PayloadStart;
                    int version = stream.ReadByte();
                    if (version == 0)
                    {
                        stream.Position = child.PayloadStart + 12;
                        if (ReadExact(stream, buffer12[..8]))
                        {
                            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer12[..4]);
                            uint rawDuration = BinaryPrimitives.ReadUInt32BigEndian(buffer12[4..8]);
                            if (timescale > 0 && rawDuration > 0)
                            {
                                duration = TimeSpan.FromSeconds((double)rawDuration / timescale);
                            }
                        }
                    }
                    else if (version == 1 && child.PayloadLength >= 32)
                    {
                        stream.Position = child.PayloadStart + 20;
                        if (ReadExact(stream, buffer12))
                        {
                            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer12[..4]);
                            ulong rawDuration = BinaryPrimitives.ReadUInt64BigEndian(buffer12[4..12]);
                            if (timescale > 0 && rawDuration > 0)
                            {
                                duration = TimeSpan.FromSeconds((double)rawDuration / timescale);
                            }
                        }
                    }
                }
                else if (child.Type == BoxTypeTrak &&
                         TryFindChildBox(stream, child.PayloadStart, child.BoxEnd, BoxTypeMdia, out Mp4BoxHeader mdia) &&
                         TryFindChildBox(stream, mdia.PayloadStart, mdia.BoxEnd, BoxTypeHdlr, out Mp4BoxHeader hdlr) &&
                         hdlr.PayloadLength >= 12)
                {
                    stream.Position = hdlr.PayloadStart + 8;
                    if (ReadExact(stream, handlerBytes) &&
                        BinaryPrimitives.ReadUInt32BigEndian(handlerBytes) == HandlerTypeSound)
                    {
                        audioTrackCount++;
                    }
                }

                offset = child.BoxEnd;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryPatchAudioTrackSelectionFlags(string mp4FilePath)
    {
        try
        {
            PatchAudioTrackSelectionFlags(mp4FilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się zaktualizować flag ścieżek audio w pliku MP4 '{mp4FilePath}': {ex.Message}");
        }
    }

    private static FileStream OpenFileStreamWithRetry(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share)
    {
        const int maxAttempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, mode, access, share);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(30 * attempt);
            }
        }
    }

    private static bool TryFindChildBox(
        Stream stream,
        long startOffset,
        long containerEnd,
        uint targetType,
        out Mp4BoxHeader foundBox)
    {
        long offset = startOffset;
        while (offset + 8 <= containerEnd)
        {
            if (!TryReadBoxHeader(stream, offset, containerEnd, out Mp4BoxHeader box))
            {
                break;
            }

            if (box.Type == targetType)
            {
                foundBox = box;
                return true;
            }

            offset = box.BoxEnd;
        }

        foundBox = default;
        return false;
    }

    private static bool TryReadBoxHeader(
        Stream stream,
        long offset,
        long containerEnd,
        out Mp4BoxHeader box)
    {
        box = default;
        if (offset < 0 || containerEnd - offset < 8)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[8];
        stream.Position = offset;
        if (!ReadExact(stream, header))
        {
            return false;
        }

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        long headerSize = 8;
        long boxSize;

        if (size32 == 0)
        {
            boxSize = containerEnd - offset;
        }
        else if (size32 == 1)
        {
            if (containerEnd - offset < 16)
            {
                return false;
            }

            Span<byte> largeSizeBytes = stackalloc byte[8];
            if (!ReadExact(stream, largeSizeBytes))
            {
                return false;
            }

            ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(largeSizeBytes);
            if (size64 < 16 || size64 > (ulong)(containerEnd - offset))
            {
                return false;
            }

            headerSize = 16;
            boxSize = (long)size64;
        }
        else
        {
            if (size32 < 8)
            {
                return false;
            }

            boxSize = size32;
        }

        if (type == BoxTypeUuid)
        {
            headerSize += 16;
        }

        if (boxSize < headerSize || boxSize > containerEnd - offset)
        {
            return false;
        }

        box = new Mp4BoxHeader(type, offset, offset + headerSize, offset + boxSize);
        return true;
    }

    private static bool ReadExact(Stream stream, Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = stream.Read(destination[totalRead..]);
            if (read <= 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private static int GetTrackOrderPriority(MediaTrackType trackType) => trackType switch
    {
        MediaTrackType.Video => 0,
        MediaTrackType.SystemAudio => 1,
        MediaTrackType.MicrophoneAudio => 2,
        MediaTrackType.CombinedMixAudio => 3,
        _ => 4
    };

    private readonly record struct IndexedPacket(EncodedPacket Packet, int OriginalIndex);

    private readonly record struct Mp4BoxHeader(uint Type, long BoxStart, long PayloadStart, long BoxEnd)
    {
        public long PayloadLength => BoxEnd - PayloadStart;
    }
}
