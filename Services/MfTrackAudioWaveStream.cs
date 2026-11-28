using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ClipThat.Services;

/// <summary>
/// Streams a single audio track directly from an MP4 file as uncompressed PCM via Windows Media Foundation
/// (<see cref="IMFSourceReader"/>), implementing NAudio's <see cref="WaveStream"/> with zero temporary disk I/O
/// and frame-accurate seeking.
/// </summary>
public sealed class MfTrackAudioWaveStream : WaveStream
{
    private static readonly Guid MfPdDurationKey = new(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);
    private const int MaxStreamProbeCount = 32;
    private const int MaxSeekTrimFrames = 2048;

    private readonly object _syncLock = new();
    private readonly IMFSourceReader _reader;
    private readonly int _streamIndex;
    private readonly WaveFormat _waveFormat;
    private readonly int _blockAlign;
    private readonly int _avgBytesPerSecond;
    private readonly bool _mfStarted;

    private byte[] _pendingBuffer = Array.Empty<byte>();
    private int _pendingOffset;
    private int _pendingCount;
    private long _positionBytes;
    private long _lengthBytes;
    private long _seekTargetTime100Ns;
    private bool _needsSeekTrim;
    private bool _endOfStream;
    private int _disposed;

    public MfTrackAudioWaveStream(string filePath, int audioTrackOrdinal, TimeSpan knownDuration)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Plik multimedialny nie istnieje.", filePath);
        }

        if (audioTrackOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioTrackOrdinal));
        }

        IMFSourceReader? reader = null;
        bool mfStarted = false;

        try
        {
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            mfStarted = true;

            reader = MediaFactory.MFCreateSourceReaderFromURL(filePath, null);

            List<int> audioStreamIndices = EnumerateAudioStreamIndices(reader);
            FileAudioStreamCount = audioStreamIndices.Count;

            if (audioTrackOrdinal >= audioStreamIndices.Count)
            {
                throw new InvalidOperationException(
                    $"Plik '{Path.GetFileName(filePath)}' nie zawiera ścieżki audio o indeksie {audioTrackOrdinal} (znaleziono: {audioStreamIndices.Count}).");
            }

            int targetStreamIndex = audioStreamIndices[audioTrackOrdinal];

            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(targetStreamIndex, true);

            using (IMFMediaType requestedType = MediaFactory.MFCreateMediaType())
            {
                requestedType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                requestedType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                requestedType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                reader.SetCurrentMediaType(targetStreamIndex, requestedType);
            }

            using IMFMediaType currentType = reader.GetCurrentMediaType(targetStreamIndex);

            uint sampleRate = currentType.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond, out uint sr).Success && sr > 0
                ? sr
                : 48000u;
            uint channels = currentType.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels, out uint ch).Success && ch > 0
                ? ch
                : 2u;
            uint bitsPerSample = currentType.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample, out uint bps).Success && bps > 0
                ? bps
                : 16u;

            bool isFloat = currentType.GetGUID(MediaTypeAttributeKeys.Subtype, out Guid subtype).Success &&
                           subtype == AudioFormatGuids.Float;

            _waveFormat = isFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat((int)sampleRate, (int)channels)
                : new WaveFormat((int)sampleRate, (int)bitsPerSample, (int)channels);

            _blockAlign = Math.Max(1, _waveFormat.BlockAlign);
            _avgBytesPerSecond = Math.Max(_blockAlign, _waveFormat.AverageBytesPerSecond);

            TimeSpan effectiveDuration = knownDuration > TimeSpan.Zero
                ? knownDuration
                : TryReadPresentationDuration(reader);

            _lengthBytes = effectiveDuration > TimeSpan.Zero
                ? AlignToBlock((long)Math.Round(effectiveDuration.TotalSeconds * _avgBytesPerSecond), _blockAlign)
                : 0L;

            _reader = reader;
            _streamIndex = targetStreamIndex;
            _mfStarted = mfStarted;
        }
        catch
        {
            if (reader is not null)
            {
                try { reader.Dispose(); } catch { /* ignore */ }
            }

            if (mfStarted)
            {
                try { MediaFactory.MFShutdown(); } catch { /* ignore */ }
            }

            throw;
        }
    }

    /// <summary>
    /// Number of audio streams discovered in the container when this stream was opened.
    /// </summary>
    public int FileAudioStreamCount { get; }

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length
    {
        get
        {
            lock (_syncLock)
            {
                return _lengthBytes;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_syncLock)
            {
                return _positionBytes;
            }
        }
        set
        {
            lock (_syncLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                long maxValid = _lengthBytes > 0 ? _lengthBytes : long.MaxValue - _blockAlign;
                long clamped = AlignToBlock(Math.Clamp(value, 0L, maxValid), _blockAlign);
                long targetTime100Ns = (long)Math.Round((double)clamped / _avgBytesPerSecond * TimeSpan.TicksPerSecond);

                try
                {
                    _reader.Flush(_streamIndex);
                    _reader.SetCurrentPosition(targetTime100Ns);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Błąd przewijania IMFSourceReader (ścieżka {_streamIndex}): {ex.Message}");
                    _endOfStream = true;
                    return;
                }

                _pendingOffset = 0;
                _pendingCount = 0;
                _endOfStream = false;
                _positionBytes = clamped;
                _seekTargetTime100Ns = targetTime100Ns;
                _needsSeekTrim = targetTime100Ns > 0;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        count -= count % _blockAlign;
        if (count <= 0)
        {
            return 0;
        }

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _endOfStream)
            {
                return 0;
            }

            int totalCopied = 0;
            int seekTrimFrames = 0;

            while (totalCopied < count)
            {
                if (_pendingCount > 0)
                {
                    int toCopy = Math.Min(count - totalCopied, _pendingCount);
                    Buffer.BlockCopy(_pendingBuffer, _pendingOffset, buffer, offset + totalCopied, toCopy);
                    _pendingOffset += toCopy;
                    _pendingCount -= toCopy;
                    totalCopied += toCopy;

                    if (_pendingCount == 0)
                    {
                        _pendingOffset = 0;
                    }

                    continue;
                }

                if (_endOfStream)
                {
                    break;
                }

                IMFSample? sample;
                SourceReaderFlag flags;
                long sampleTime100Ns;

                try
                {
                    sample = _reader.ReadSample(
                        _streamIndex,
                        SourceReaderControlFlag.None,
                        out _,
                        out flags,
                        out sampleTime100Ns);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Błąd odczytu próbki IMFSourceReader (ścieżka {_streamIndex}): {ex.Message}");
                    _endOfStream = true;
                    break;
                }

                if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
                {
                    sample?.Dispose();
                    _endOfStream = true;
                    break;
                }

                if (sample is null)
                {
                    continue;
                }

                int decodedLength;
                try
                {
                    using (sample)
                    using (IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer())
                    {
                        contiguous.Lock(out nint dataPtr, out _, out decodedLength);
                        try
                        {
                            if (decodedLength <= 0 || dataPtr == nint.Zero)
                            {
                                continue;
                            }

                            if (_pendingBuffer.Length < decodedLength)
                            {
                                _pendingBuffer = new byte[Math.Max(decodedLength, 16384)];
                            }

                            Marshal.Copy(dataPtr, _pendingBuffer, 0, decodedLength);
                        }
                        finally
                        {
                            contiguous.Unlock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Błąd kopiowania bufora PCM z IMFSourceReader: {ex.Message}");
                    _endOfStream = true;
                    break;
                }

                int sampleOffset = 0;
                int sampleCount = decodedLength - (decodedLength % _blockAlign);
                if (sampleCount <= 0)
                {
                    continue;
                }

                if (_needsSeekTrim)
                {
                    long sampleDuration100Ns = (long)Math.Round(
                        (double)sampleCount / _avgBytesPerSecond * TimeSpan.TicksPerSecond);

                    if (sampleTime100Ns + sampleDuration100Ns <= _seekTargetTime100Ns &&
                        seekTrimFrames < MaxSeekTrimFrames)
                    {
                        seekTrimFrames++;
                        continue;
                    }

                    if (sampleTime100Ns < _seekTargetTime100Ns && seekTrimFrames < MaxSeekTrimFrames)
                    {
                        long skipTicks = _seekTargetTime100Ns - sampleTime100Ns;
                        int skipBytes = (int)AlignToBlock(
                            (long)Math.Round((double)skipTicks / TimeSpan.TicksPerSecond * _avgBytesPerSecond),
                            _blockAlign);

                        if (skipBytes >= sampleCount)
                        {
                            seekTrimFrames++;
                            continue;
                        }

                        sampleOffset = Math.Max(0, skipBytes);
                        sampleCount -= sampleOffset;
                    }

                    _needsSeekTrim = false;
                }

                _pendingOffset = sampleOffset;
                _pendingCount = sampleCount;
            }

            _positionBytes += totalCopied;
            if (_positionBytes > _lengthBytes)
            {
                _lengthBytes = _positionBytes;
            }

            return totalCopied;
        }
    }

    /// <summary>
    /// Attempts to open track 0 (System Audio) and optionally track 1 (Microphone Audio) directly from
    /// an MP4 file using Media Foundation without creating any temporary files on disk.
    /// </summary>
    public static bool TryOpenAudioTracks(
        string filePath,
        TimeSpan knownDuration,
        int expectedTrackCount,
        out MfTrackAudioWaveStream? systemStream,
        out MfTrackAudioWaveStream? micStream)
    {
        systemStream = null;
        micStream = null;

        try
        {
            systemStream = new MfTrackAudioWaveStream(filePath, audioTrackOrdinal: 0, knownDuration);

            bool shouldOpenMic = expectedTrackCount >= 2 || systemStream.FileAudioStreamCount >= 2;
            if (shouldOpenMic && systemStream.FileAudioStreamCount >= 2)
            {
                try
                {
                    micStream = new MfTrackAudioWaveStream(filePath, audioTrackOrdinal: 1, knownDuration);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Nie udało się otworzyć drugiej ścieżki audio przez MF ({ex.Message}); kontynuacja z 1 ścieżką.");
                    micStream = null;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Podgląd audio przez Media Foundation niedostępny dla '{Path.GetFileName(filePath)}': {ex.Message}");
            try { micStream?.Dispose(); } catch { /* ignore */ }
            try { systemStream?.Dispose(); } catch { /* ignore */ }
            systemStream = null;
            micStream = null;
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_syncLock)
        {
            _endOfStream = true;
            _pendingBuffer = Array.Empty<byte>();
            _pendingOffset = 0;
            _pendingCount = 0;

            try
            {
                _reader.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors.
            }

            if (_mfStarted)
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

        base.Dispose(disposing);
    }

    private static List<int> EnumerateAudioStreamIndices(IMFSourceReader reader)
    {
        var indices = new List<int>(4);

        for (int streamIndex = 0; streamIndex < MaxStreamProbeCount; streamIndex++)
        {
            IMFMediaType? nativeType;
            try
            {
                nativeType = reader.GetNativeMediaType(streamIndex, 0);
            }
            catch (SharpGenException)
            {
                break;
            }

            using (nativeType)
            {
                if (nativeType.GetGUID(MediaTypeAttributeKeys.MajorType, out Guid majorType).Success &&
                    majorType == MediaTypeGuids.Audio)
                {
                    indices.Add(streamIndex);
                }
            }
        }

        return indices;
    }

    private static TimeSpan TryReadPresentationDuration(IMFSourceReader reader)
    {
        try
        {
            SharpGen.Runtime.Win32.Variant variant = reader.GetPresentationAttribute(
                SourceReaderIndex.MediaSource,
                MfPdDurationKey);

            if (variant.Value is long ticks && ticks > 0)
            {
                return TimeSpan.FromTicks(ticks);
            }

            if (variant.Value is ulong uticks && uticks > 0 && uticks <= long.MaxValue)
            {
                return TimeSpan.FromTicks((long)uticks);
            }
        }
        catch
        {
            // Fallback to zero when the source reader does not expose duration.
        }

        return TimeSpan.Zero;
    }

    private static long AlignToBlock(long byteOffset, int blockAlign)
    {
        if (byteOffset <= 0)
        {
            return 0L;
        }

        return byteOffset - (byteOffset % blockAlign);
    }
}
