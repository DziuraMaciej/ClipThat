using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using ClipThat.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using AudioDataFlow = NAudio.CoreAudioApi.DataFlow;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace ClipThat.Engine.Audio;

/// <summary>
/// Captures two independent WASAPI audio streams (System Loopback + Microphone) synchronized to the
/// shared <see cref="Stopwatch"/> replay timeline, converts/resamples incoming audio to 48000 Hz 16-bit Stereo PCM,
/// encodes both tracks to 128 kbps AAC in real time via Media Foundation <see cref="IMFTransform"/>,
/// and pushes compressed AAC packets into <see cref="MediaRingBuffer"/>.
/// </summary>
public sealed class DualAudioCaptureService : IDisposable
{
    public const int TargetSampleRate = 48000;
    public const int TargetBitsPerSample = 16;
    public const int TargetChannels = 2;
    public const int TargetBlockAlignment = TargetChannels * (TargetBitsPerSample / 8); // 4 bytes
    public const int TargetPcmAvgBytesPerSecond = TargetSampleRate * TargetBlockAlignment; // 192000 B/s
    public const int TargetAacBitrateBps = 128_000;
    public const int TargetAacAvgBytesPerSecond = TargetAacBitrateBps / 8; // 16000 B/s
    public const int AacFrameSampleCount = 1024;
    public const int AacFrameByteCount = AacFrameSampleCount * TargetBlockAlignment; // 4096 bytes

    private const int SilenceWatchdogPeriodMs = 20;
    private const int MicrophoneCaptureBufferMs = 20;

    private readonly Stopwatch _sessionClock;
    private readonly MediaRingBuffer _ringBuffer;
    private readonly string? _systemAudioDeviceId;
    private readonly string? _microphoneDeviceId;

    private AudioTrackPipeline? _systemAudioTrack;
    private AudioTrackPipeline? _microphoneTrack;
    private int _started;
    private int _disposed;

    public DualAudioCaptureService(
        Stopwatch sessionClock,
        MediaRingBuffer ringBuffer,
        string? systemAudioDeviceId,
        string? microphoneDeviceId)
    {
        ArgumentNullException.ThrowIfNull(sessionClock);
        ArgumentNullException.ThrowIfNull(ringBuffer);

        _sessionClock = sessionClock;
        _ringBuffer = ringBuffer;
        _systemAudioDeviceId = systemAudioDeviceId;
        _microphoneDeviceId = microphoneDeviceId;
    }

    public bool IsSystemAudioActive => _systemAudioTrack is not null;

    public bool IsMicrophoneActive => _microphoneTrack is not null;

    /// <summary>
    /// Starts both WASAPI capture pipelines (system loopback and optional microphone).
    /// Missing or unavailable microphones (or playback devices on headless machines) are skipped gracefully
    /// without interrupting video recording.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();

        _systemAudioTrack = TryStartSystemAudioTrack(enumerator);
        _microphoneTrack = TryStartMicrophoneTrack(enumerator);
    }

    private AudioTrackPipeline? TryStartSystemAudioTrack(MMDeviceEnumerator enumerator)
    {
        MMDevice? renderDevice = null;
        WasapiLoopbackCapture? loopbackCapture = null;
        AudioTrackPipeline? pipeline = null;

        try
        {
            renderDevice = AudioDeviceService.ResolveDevice(enumerator, AudioDataFlow.Render, _systemAudioDeviceId);
            if (renderDevice is null)
            {
                Debug.WriteLine("Brak aktywnego urządzenia wyjściowego audio — przechwytywanie dźwięku systemu pominięte.");
                return null;
            }

            loopbackCapture = new WasapiLoopbackCapture(renderDevice);
            pipeline = new AudioTrackPipeline(
                MediaTrackType.SystemAudio,
                renderDevice,
                loopbackCapture,
                _sessionClock,
                _ringBuffer,
                enableSilenceWatchdog: true);

            pipeline.Start();
            return pipeline;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się uruchomić przechwytywania dźwięku systemu (WASAPI Loopback): {ex.Message}");
            if (pipeline is not null)
            {
                pipeline.Dispose();
            }
            else
            {
                loopbackCapture?.Dispose();
                renderDevice?.Dispose();
            }

            return null;
        }
    }

    private AudioTrackPipeline? TryStartMicrophoneTrack(MMDeviceEnumerator enumerator)
    {
        MMDevice? captureDevice = null;
        WasapiCapture? micCapture = null;
        AudioTrackPipeline? pipeline = null;

        try
        {
            captureDevice = AudioDeviceService.ResolveDevice(enumerator, AudioDataFlow.Capture, _microphoneDeviceId);
            if (captureDevice is null)
            {
                Debug.WriteLine("Brak aktywnego mikrofonu — przechwytywanie ścieżki mikrofonu pominięte.");
                return null;
            }

            micCapture = new WasapiCapture(captureDevice, useEventSync: true, audioBufferMillisecondsLength: MicrophoneCaptureBufferMs);
            pipeline = new AudioTrackPipeline(
                MediaTrackType.MicrophoneAudio,
                captureDevice,
                micCapture,
                _sessionClock,
                _ringBuffer,
                enableSilenceWatchdog: false);

            pipeline.Start();
            return pipeline;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się uruchomić przechwytywania mikrofonu (pominięto bezpiecznie): {ex.Message}");
            if (pipeline is not null)
            {
                pipeline.Dispose();
            }
            else
            {
                micCapture?.Dispose();
                captureDevice?.Dispose();
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AudioTrackPipeline? sys = Interlocked.Exchange(ref _systemAudioTrack, null);
        AudioTrackPipeline? mic = Interlocked.Exchange(ref _microphoneTrack, null);

        SafeDispose(sys);
        SafeDispose(mic);
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
            Debug.WriteLine($"Błąd zwalniania ścieżki audio: {ex.Message}");
        }
    }

    /// <summary>
    /// Encapsulates one independent WASAPI capture -> PCM resampler/converter -> real-time AAC MFT encoder track
    /// synchronized to the shared session <see cref="Stopwatch"/>.
    /// </summary>
    private sealed class AudioTrackPipeline : IDisposable
    {
        private const long SilenceTriggerDelay100Ns = 35 * TimeSpan.TicksPerMillisecond;
        private const long SilenceLeadMargin100Ns = 15 * TimeSpan.TicksPerMillisecond;
        private const long MinSilenceInjection100Ns = 10 * TimeSpan.TicksPerMillisecond;
        private const long GapBridgeThreshold100Ns = 45 * TimeSpan.TicksPerMillisecond;
        private const long MaxSyncGapToFill100Ns = 2 * TimeSpan.TicksPerSecond;
        private const long OverrunDriftLimit100Ns = 90 * TimeSpan.TicksPerMillisecond;
        private const int MaxSilenceFramesPerTick = 4800; // 100 ms max per watchdog tick
        private const int SilenceChunkFrames = 960;       // 20 ms at 48 kHz

        private static readonly byte[] ZeroSilenceChunk = new byte[SilenceChunkFrames * TargetBlockAlignment];

        private readonly MediaTrackType _trackType;
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly Stopwatch _sessionClock;
        private readonly MediaRingBuffer _ringBuffer;
        private readonly bool _enableSilenceWatchdog;
        private readonly object _syncLock = new();

        private readonly AudioPcmConverter _pcmConverter;
        private readonly AacMftEncoder _aacEncoder;
        private byte[] _pcmScratchBuffer = new byte[16 * 1024];

        private Timer? _silenceWatchdogTimer;
        private long _trackOrigin100Ns;
        private long _totalPcmFramesFed;
        private long _lastRealAudioClock100Ns;
        private int _stopped;
        private int _disposed;

        public AudioTrackPipeline(
            MediaTrackType trackType,
            MMDevice device,
            WasapiCapture capture,
            Stopwatch sessionClock,
            MediaRingBuffer ringBuffer,
            bool enableSilenceWatchdog)
        {
            _trackType = trackType;
            _device = device;
            _capture = capture;
            _sessionClock = sessionClock;
            _ringBuffer = ringBuffer;
            _enableSilenceWatchdog = enableSilenceWatchdog;

            _pcmConverter = new AudioPcmConverter(capture.WaveFormat);
            _aacEncoder = new AacMftEncoder(
                trackType,
                onEncodedPacket: PushEncodedPacket,
                onUserData: userData => _ringBuffer.SetAudioUserData(_trackType, userData.Span));
        }

        public void Start()
        {
            lock (_syncLock)
            {
                _trackOrigin100Ns = _sessionClock.Elapsed.Ticks;
                _lastRealAudioClock100Ns = _trackOrigin100Ns;
                _totalPcmFramesFed = 0;
            }

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            if (_enableSilenceWatchdog)
            {
                _silenceWatchdogTimer = new Timer(
                    OnSilenceWatchdogTick,
                    state: null,
                    dueTime: SilenceWatchdogPeriodMs,
                    period: SilenceWatchdogPeriodMs);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (Volatile.Read(ref _stopped) != 0 || e.BytesRecorded <= 0)
            {
                return;
            }

            lock (_syncLock)
            {
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                try
                {
                    long now100Ns = _sessionClock.Elapsed.Ticks;
                    int pcmByteCount = _pcmConverter.ConvertToStereoPcm16At48k(
                        e.Buffer,
                        e.BytesRecorded,
                        ref _pcmScratchBuffer);

                    if (pcmByteCount <= 0)
                    {
                        return;
                    }

                    int incomingFrames = pcmByteCount / TargetBlockAlignment;
                    if (incomingFrames <= 0)
                    {
                        return;
                    }

                    long chunkDuration100Ns = FramesTo100Ns(incomingFrames);
                    long expectedChunkStart100Ns = Math.Max(0L, now100Ns - chunkDuration100Ns);
                    long streamCursor100Ns = GetCurrentPcmCursor100NsLocked();

                    // Bridge any silence gap before this incoming audio chunk so timestamps stay aligned
                    // with the shared Stopwatch timeline.
                    long gap100Ns = expectedChunkStart100Ns - streamCursor100Ns;
                    if (gap100Ns > GapBridgeThreshold100Ns)
                    {
                        if (gap100Ns > MaxSyncGapToFill100Ns)
                        {
                            // System resumed from sleep/stall; re-anchor origin rather than encoding huge silence.
                            ReanchorTimelineLocked(expectedChunkStart100Ns);
                        }
                        else
                        {
                            long gapFrames = Ticks100NsToFrames(gap100Ns);
                            FeedSilentFramesLocked(gapFrames);
                        }

                        streamCursor100Ns = GetCurrentPcmCursor100NsLocked();
                    }

                    // Guard against fast audio hardware crystal drift pushing audio ahead of video.
                    if (streamCursor100Ns - now100Ns > OverrunDriftLimit100Ns)
                    {
                        _lastRealAudioClock100Ns = now100Ns;
                        return;
                    }

                    _lastRealAudioClock100Ns = now100Ns;
                    FeedPcmFramesLocked(_pcmScratchBuffer.AsSpan(0, incomingFrames * TargetBlockAlignment), incomingFrames);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Błąd kodowania audio ({_trackType}): {ex.Message}");
                }
            }
        }

        private void OnSilenceWatchdogTick(object? state)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            if (!Monitor.TryEnter(_syncLock))
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                long now100Ns = _sessionClock.Elapsed.Ticks;
                if (now100Ns - _lastRealAudioClock100Ns < SilenceTriggerDelay100Ns)
                {
                    // Real loopback data is actively arriving on schedule.
                    return;
                }

                long targetCursor100Ns = now100Ns - SilenceLeadMargin100Ns;
                long currentCursor100Ns = GetCurrentPcmCursor100NsLocked();
                long deficit100Ns = targetCursor100Ns - currentCursor100Ns;

                if (deficit100Ns < MinSilenceInjection100Ns)
                {
                    return;
                }

                if (deficit100Ns > MaxSyncGapToFill100Ns)
                {
                    ReanchorTimelineLocked(targetCursor100Ns);
                    return;
                }

                long framesToInject = Math.Min(Ticks100NsToFrames(deficit100Ns), MaxSilenceFramesPerTick);
                if (framesToInject > 0)
                {
                    FeedSilentFramesLocked(framesToInject);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd wstrzykiwania ciszy WASAPI ({_trackType}): {ex.Message}");
            }
            finally
            {
                Monitor.Exit(_syncLock);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null && Volatile.Read(ref _stopped) == 0)
            {
                Debug.WriteLine($"Przechwytywanie WASAPI ({_trackType}) zostało zatrzymane z błędem: {e.Exception.Message}");
            }
        }

        private void FeedSilentFramesLocked(long frameCount)
        {
            long remaining = frameCount;
            while (remaining > 0)
            {
                int batchFrames = (int)Math.Min(remaining, SilenceChunkFrames);
                FeedPcmFramesLocked(ZeroSilenceChunk.AsSpan(0, batchFrames * TargetBlockAlignment), batchFrames);
                remaining -= batchFrames;
            }
        }

        private void FeedPcmFramesLocked(ReadOnlySpan<byte> pcmBytes, int frameCount)
        {
            if (frameCount <= 0)
            {
                return;
            }

            _aacEncoder.FeedPcm(pcmBytes, _trackOrigin100Ns, _totalPcmFramesFed);
            _totalPcmFramesFed += frameCount;
        }

        private long GetCurrentPcmCursor100NsLocked()
            => _trackOrigin100Ns + FramesTo100Ns(_totalPcmFramesFed);

        private void ReanchorTimelineLocked(long newCursor100Ns)
        {
            _aacEncoder.ResetPendingPcm();
            _trackOrigin100Ns = Math.Max(0L, newCursor100Ns);
            _totalPcmFramesFed = 0;
        }

        private void PushEncodedPacket(byte[] rentedBuffer, int length, long timestamp100Ns, long duration100Ns)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return;
            }

            _ringBuffer.AddAudioPacket(_trackType, rentedBuffer, length, timestamp100Ns, duration100Ns);
        }

        private static long FramesTo100Ns(long frames)
            => (frames * TimeSpan.TicksPerSecond) / TargetSampleRate;

        private static long Ticks100NsToFrames(long ticks100Ns)
            => (ticks100Ns * TargetSampleRate) / TimeSpan.TicksPerSecond;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _stopped, 1);

            Timer? timer = Interlocked.Exchange(ref _silenceWatchdogTimer, null);
            if (timer is not null)
            {
                using var timerStopped = new ManualResetEvent(false);
                if (timer.Dispose(timerStopped))
                {
                    timerStopped.WaitOne(500);
                }
            }

            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                if (_capture.CaptureState != CaptureState.Stopped)
                {
                    _capture.StopRecording();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd zatrzymywania WASAPI ({_trackType}): {ex.Message}");
            }

            lock (_syncLock)
            {
                try
                {
                    _capture.Dispose();
                }
                catch
                {
                    // Ignore WASAPI teardown errors.
                }

                try
                {
                    _aacEncoder.Dispose();
                }
                catch
                {
                    // Ignore AAC encoder teardown errors.
                }

                try
                {
                    _device.Dispose();
                }
                catch
                {
                    // Ignore MMDevice teardown errors.
                }
            }
        }
    }

    /// <summary>
    /// Converts arbitrary WASAPI capture formats (IEEE Float / 16-24-32-bit PCM / Extensible,
    /// mono / stereo / 5.1 / 7.1, any sample rate) into 48000 Hz, 16-bit, 2-channel Stereo PCM.
    /// </summary>
    private sealed class AudioPcmConverter
    {
        private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
        private const float CenterDownmixGain = 0.70710678f;

        private readonly bool _isFloat;
        private readonly int _sourceChannels;
        private readonly int _sourceBitsPerSample;
        private readonly int _sourceBytesPerSample;
        private readonly int _sourceBlockAlign;
        private readonly bool _needsResampling;

        private readonly BufferedWaveProvider? _resampleInputBuffer;
        private readonly WdlResamplingSampleProvider? _resampler;

        private float[] _stereoFloatScratch = new float[8192];
        private byte[] _resampleInputBytes = new byte[32768];
        private float[] _resampledFloatScratch = new float[8192];

        public AudioPcmConverter(WaveFormat sourceFormat)
        {
            _sourceChannels = Math.Max(1, sourceFormat.Channels);
            _sourceBitsPerSample = sourceFormat.BitsPerSample;
            _sourceBytesPerSample = Math.Max(1, _sourceBitsPerSample / 8);
            _sourceBlockAlign = Math.Max(1, sourceFormat.BlockAlign);
            _isFloat = IsIeeeFloatFormat(sourceFormat);
            _needsResampling = sourceFormat.SampleRate != TargetSampleRate;

            if (_needsResampling)
            {
                WaveFormat stereoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, TargetChannels);
                _resampleInputBuffer = new BufferedWaveProvider(stereoFloatFormat, TimeSpan.FromSeconds(2))
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = false
                };

                ISampleProvider sampleProvider = new WaveToSampleProvider(_resampleInputBuffer);
                _resampler = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
            }
        }

        public int ConvertToStereoPcm16At48k(byte[] inputBuffer, int bytesRecorded, ref byte[] outputPcm16Buffer)
        {
            int inputFrames = bytesRecorded / _sourceBlockAlign;
            if (inputFrames <= 0)
            {
                return 0;
            }

            int stereoFloatCount = inputFrames * TargetChannels;
            if (_stereoFloatScratch.Length < stereoFloatCount)
            {
                _stereoFloatScratch = new float[stereoFloatCount];
            }

            DecodeAndMixToStereoFloat(inputBuffer.AsSpan(0, inputFrames * _sourceBlockAlign), inputFrames, _stereoFloatScratch);

            if (!_needsResampling)
            {
                int outputByteCount = inputFrames * TargetBlockAlignment;
                EnsureCapacity(ref outputPcm16Buffer, outputByteCount);
                ConvertStereoFloatToPcm16(_stereoFloatScratch.AsSpan(0, stereoFloatCount), outputPcm16Buffer.AsSpan(0, outputByteCount));
                return outputByteCount;
            }

            int stereoFloatBytes = stereoFloatCount * sizeof(float);
            EnsureCapacity(ref _resampleInputBytes, stereoFloatBytes);
            MemoryMarshal.AsBytes(_stereoFloatScratch.AsSpan(0, stereoFloatCount)).CopyTo(_resampleInputBytes);
            _resampleInputBuffer!.AddSamples(_resampleInputBytes, 0, stereoFloatBytes);

            int totalWrittenBytes = 0;
            const int maxDrainIterations = 32;
            for (int iter = 0; iter < maxDrainIterations; iter++)
            {
                int samplesRead = _resampler!.Read(_resampledFloatScratch, 0, _resampledFloatScratch.Length);
                if (samplesRead <= 0)
                {
                    break;
                }

                // Guarantee stereo frame alignment (multiple of 2 samples).
                int alignedSamples = samplesRead & ~1;
                if (alignedSamples <= 0)
                {
                    break;
                }

                int chunkBytes = alignedSamples * sizeof(short);
                EnsureCapacity(ref outputPcm16Buffer, totalWrittenBytes + chunkBytes);
                ConvertStereoFloatToPcm16(
                    _resampledFloatScratch.AsSpan(0, alignedSamples),
                    outputPcm16Buffer.AsSpan(totalWrittenBytes, chunkBytes));
                totalWrittenBytes += chunkBytes;
            }

            return totalWrittenBytes;
        }

        private void DecodeAndMixToStereoFloat(ReadOnlySpan<byte> src, int frameCount, Span<float> dstStereo)
        {
            int channels = _sourceChannels;
            int blockAlign = _sourceBlockAlign;
            int bytesPerSample = _sourceBytesPerSample;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * blockAlign;
                float left;
                float right;

                if (channels == 1)
                {
                    float mono = ReadSampleAsFloat(src, frameOffset);
                    left = mono;
                    right = mono;
                }
                else if (channels == 2)
                {
                    left = ReadSampleAsFloat(src, frameOffset);
                    right = ReadSampleAsFloat(src, frameOffset + bytesPerSample);
                }
                else if (channels == 6)
                {
                    // 5.1 layout: FL, FR, FC, LFE, BL, BR
                    float fl = ReadSampleAsFloat(src, frameOffset);
                    float fr = ReadSampleAsFloat(src, frameOffset + bytesPerSample);
                    float fc = ReadSampleAsFloat(src, frameOffset + (2 * bytesPerSample));
                    float bl = ReadSampleAsFloat(src, frameOffset + (4 * bytesPerSample));
                    float br = ReadSampleAsFloat(src, frameOffset + (5 * bytesPerSample));

                    left = fl + (CenterDownmixGain * (fc + bl));
                    right = fr + (CenterDownmixGain * (fc + br));
                }
                else if (channels == 8)
                {
                    // 7.1 layout: FL, FR, FC, LFE, BL, BR, SL, SR
                    float fl = ReadSampleAsFloat(src, frameOffset);
                    float fr = ReadSampleAsFloat(src, frameOffset + bytesPerSample);
                    float fc = ReadSampleAsFloat(src, frameOffset + (2 * bytesPerSample));
                    float bl = ReadSampleAsFloat(src, frameOffset + (4 * bytesPerSample));
                    float br = ReadSampleAsFloat(src, frameOffset + (5 * bytesPerSample));
                    float sl = ReadSampleAsFloat(src, frameOffset + (6 * bytesPerSample));
                    float sr = ReadSampleAsFloat(src, frameOffset + (7 * bytesPerSample));

                    left = fl + (CenterDownmixGain * fc) + (0.5f * (bl + sl));
                    right = fr + (CenterDownmixGain * fc) + (0.5f * (br + sr));
                }
                else
                {
                    left = ReadSampleAsFloat(src, frameOffset);
                    right = ReadSampleAsFloat(src, frameOffset + bytesPerSample);
                    for (int ch = 2; ch < channels; ch++)
                    {
                        float extra = ReadSampleAsFloat(src, frameOffset + (ch * bytesPerSample)) * 0.5f;
                        if ((ch & 1) == 0)
                        {
                            left += extra;
                        }
                        else
                        {
                            right += extra;
                        }
                    }
                }

                dstStereo[frame * 2] = Math.Clamp(left, -1.0f, 1.0f);
                dstStereo[(frame * 2) + 1] = Math.Clamp(right, -1.0f, 1.0f);
            }
        }

        private float ReadSampleAsFloat(ReadOnlySpan<byte> buffer, int offset)
        {
            if (_isFloat && _sourceBitsPerSample == 32)
            {
                return MemoryMarshal.Read<float>(buffer.Slice(offset, 4));
            }

            if (_sourceBitsPerSample == 16)
            {
                short s = MemoryMarshal.Read<short>(buffer.Slice(offset, 2));
                return s / 32768.0f;
            }

            if (_sourceBitsPerSample == 24)
            {
                int s24 = (buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16));
                if ((s24 & 0x800000) != 0)
                {
                    s24 |= unchecked((int)0xFF000000);
                }

                return s24 / 8388608.0f;
            }

            if (_sourceBitsPerSample == 32)
            {
                int s32 = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
                return s32 / 2147483648.0f;
            }

            if (_sourceBitsPerSample == 8)
            {
                return (buffer[offset] - 128) / 128.0f;
            }

            return 0f;
        }

        private static void ConvertStereoFloatToPcm16(ReadOnlySpan<float> srcFloats, Span<byte> dstBytes)
        {
            Span<short> dstShorts = MemoryMarshal.Cast<byte, short>(dstBytes);
            int count = Math.Min(srcFloats.Length, dstShorts.Length);

            for (int i = 0; i < count; i++)
            {
                float clamped = Math.Clamp(srcFloats[i], -1.0f, 1.0f);
                dstShorts[i] = (short)MathF.Round(clamped * 32767.0f);
            }
        }

        private static bool IsIeeeFloatFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return true;
            }

            if (format is WaveFormatExtensible extensible)
            {
                if (extensible.SubFormat == KsDataFormatSubtypeIeeeFloat)
                {
                    return true;
                }

                if (extensible.SubFormat == KsDataFormatSubtypePcm)
                {
                    return false;
                }
            }

            return format.BitsPerSample == 32 && format.Encoding != WaveFormatEncoding.Pcm;
        }

        private static void EnsureCapacity(ref byte[] buffer, int requiredBytes)
        {
            if (buffer.Length < requiredBytes)
            {
                int nextSize = Math.Max(requiredBytes, buffer.Length * 2);
                Array.Resize(ref buffer, nextSize);
            }
        }
    }

    /// <summary>
    /// Real-time Media Foundation <see cref="IMFTransform"/> AAC-LC encoder (48000 Hz, 16-bit Stereo PCM ->
    /// 128 kbps raw AAC packets suitable for direct remuxing into MP4).
    /// </summary>
    private sealed class AacMftEncoder : IDisposable
    {
        private const uint AacRawPayloadType = 0u;
        private const uint AacProfileLevelIndicationLcL2 = 0x29u;
        private const long DefaultAacPacketDuration100Ns = (AacFrameSampleCount * TimeSpan.TicksPerSecond) / TargetSampleRate; // 213333
        private const int MinimumOutputBufferBytes = 4096;

        private readonly MediaTrackType _trackType;
        private readonly Action<byte[], int, long, long> _onEncodedPacket;
        private readonly Action<ReadOnlyMemory<byte>>? _onUserData;

        private readonly byte[] _pcmAccumulator = new byte[AacFrameByteCount];
        private int _pcmAccumulatedBytes;
        private long _accumulatorOrigin100Ns;
        private long _accumulatorStartFrameIndex;

        private bool _mfStarted;
        private IMFActivate? _activeActivate;
        private IMFTransform? _transform;
        private int _inputStreamId;
        private int _outputStreamId;
        private OutputStreamInfo _outputStreamInfo;
        private bool _mftProvidesOutputSamples;
        private long _lastOutputTimestamp100Ns = -1;
        private int _disposed;

        public AacMftEncoder(
            MediaTrackType trackType,
            Action<byte[], int, long, long> onEncodedPacket,
            Action<ReadOnlyMemory<byte>>? onUserData)
        {
            _trackType = trackType;
            _onEncodedPacket = onEncodedPacket;
            _onUserData = onUserData;

            try
            {
                MediaFactory.MFStartup(useLightVersion: false).CheckError();
                _mfStarted = true;

                InitializeAacMft();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void ResetPendingPcm()
        {
            _pcmAccumulatedBytes = 0;
        }

        public void FeedPcm(ReadOnlySpan<byte> pcmBytes, long trackOrigin100Ns, long firstSampleFrameIndex)
        {
            if (Volatile.Read(ref _disposed) != 0 || _transform is null || pcmBytes.IsEmpty)
            {
                return;
            }

            int offset = 0;
            long currentFrameIndex = firstSampleFrameIndex;

            while (offset < pcmBytes.Length)
            {
                if (_pcmAccumulatedBytes == 0)
                {
                    _accumulatorOrigin100Ns = trackOrigin100Ns;
                    _accumulatorStartFrameIndex = currentFrameIndex;
                }

                int spaceLeft = AacFrameByteCount - _pcmAccumulatedBytes;
                int toCopy = Math.Min(spaceLeft, pcmBytes.Length - offset);
                pcmBytes.Slice(offset, toCopy).CopyTo(_pcmAccumulator.AsSpan(_pcmAccumulatedBytes, toCopy));

                _pcmAccumulatedBytes += toCopy;
                offset += toCopy;
                currentFrameIndex += toCopy / TargetBlockAlignment;

                if (_pcmAccumulatedBytes == AacFrameByteCount)
                {
                    long sampleTime100Ns = _accumulatorOrigin100Ns +
                        ((_accumulatorStartFrameIndex * TimeSpan.TicksPerSecond) / TargetSampleRate);
                    long nextTime100Ns = _accumulatorOrigin100Ns +
                        (((_accumulatorStartFrameIndex + AacFrameSampleCount) * TimeSpan.TicksPerSecond) / TargetSampleRate);
                    long duration100Ns = Math.Max(1L, nextTime100Ns - sampleTime100Ns);

                    EncodePcmFrame(_pcmAccumulator, sampleTime100Ns, duration100Ns);
                    _pcmAccumulatedBytes = 0;
                }
            }
        }

        private void EncodePcmFrame(byte[] pcmFrameBytes, long sampleTime100Ns, long sampleDuration100Ns)
        {
            if (_transform is null)
            {
                return;
            }

            using IMFSample inputSample = MediaFactory.MFCreateSample();
            inputSample.SampleTime = sampleTime100Ns;
            inputSample.SampleDuration = sampleDuration100Ns;

            using IMFMediaBuffer inputBuffer = MediaFactory.MFCreateMemoryBuffer(pcmFrameBytes.Length);
            inputBuffer.Lock(out nint dstPtr, out int maxLength, out _);
            try
            {
                if (maxLength < pcmFrameBytes.Length)
                {
                    throw new InvalidOperationException("Za mały bufor wejściowy kodera AAC.");
                }

                Marshal.Copy(pcmFrameBytes, 0, dstPtr, pcmFrameBytes.Length);
            }
            finally
            {
                inputBuffer.Unlock();
            }

            inputBuffer.CurrentLength = pcmFrameBytes.Length;
            inputSample.AddBuffer(inputBuffer);

            try
            {
                _transform.ProcessInput(_inputStreamId, inputSample, 0);
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
            {
                DrainOutputs();
                _transform.ProcessInput(_inputStreamId, inputSample, 0);
            }

            DrainOutputs();
        }

        private void DrainOutputs()
        {
            if (_transform is null)
            {
                return;
            }

            while (Volatile.Read(ref _disposed) == 0)
            {
                OutputDataBuffer outputBuffer = new()
                {
                    StreamID = _outputStreamId
                };

                IMFSample? callerAllocatedSample = null;
                if (!_mftProvidesOutputSamples)
                {
                    callerAllocatedSample = MediaFactory.MFCreateSample();
                    int bufferCapacity = Math.Max(_outputStreamInfo.Size, MinimumOutputBufferBytes);
                    using IMFMediaBuffer memoryBuffer = MediaFactory.MFCreateMemoryBuffer(bufferCapacity);
                    callerAllocatedSample.AddBuffer(memoryBuffer);
                    outputBuffer.Sample = callerAllocatedSample;
                }

                try
                {
                    Result hr = _transform.ProcessOutput(
                        ProcessOutputFlags.None,
                        cOutputBufferCount: 1,
                        ref outputBuffer,
                        out ProcessOutputStatus _);

                    if (hr == ResultCode.TransformNeedMoreInput)
                    {
                        return;
                    }

                    if (hr == ResultCode.TransformStreamChange)
                    {
                        HandleOutputStreamChange();
                        continue;
                    }

                    if (hr.Failure)
                    {
                        hr.CheckError();
                        return;
                    }

                    OutputDataBufferFlags flags = (OutputDataBufferFlags)outputBuffer.Status;
                    if ((flags & OutputDataBufferFlags.OutputDataBufferFormatChange) != 0)
                    {
                        HandleOutputStreamChange();
                    }

                    IMFSample? sample = outputBuffer.Sample;
                    if (sample is not null && (flags & OutputDataBufferFlags.OutputDataBufferNoSample) == 0)
                    {
                        EmitEncodedSample(sample);
                    }
                }
                finally
                {
                    outputBuffer.Events?.Dispose();
                    if (outputBuffer.Sample is not null)
                    {
                        outputBuffer.Sample.Dispose();
                    }
                    else
                    {
                        callerAllocatedSample?.Dispose();
                    }
                }
            }
        }

        private void EmitEncodedSample(IMFSample sample)
        {
            using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();

            byte[]? rented = null;
            int length;
            contiguous.Lock(out nint dataPtr, out _, out length);
            try
            {
                if (length <= 0 || dataPtr == nint.Zero)
                {
                    return;
                }

                rented = ArrayPool<byte>.Shared.Rent(length);
                Marshal.Copy(dataPtr, rented, 0, length);
            }
            catch
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                throw;
            }
            finally
            {
                contiguous.Unlock();
            }

            long duration100Ns = TryGetSampleDuration(sample);
            long timestamp100Ns = TryGetSampleTime(sample, duration100Ns);

            try
            {
                _onEncodedPacket(rented, length, timestamp100Ns, duration100Ns);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw;
            }
        }

        private long TryGetSampleTime(IMFSample sample, long fallbackDuration100Ns)
        {
            long candidate;
            try
            {
                candidate = sample.SampleTime;
            }
            catch (SharpGenException)
            {
                candidate = _lastOutputTimestamp100Ns >= 0
                    ? _lastOutputTimestamp100Ns + fallbackDuration100Ns
                    : 0L;
            }

            if (candidate < 0)
            {
                candidate = 0L;
            }

            if (_lastOutputTimestamp100Ns >= 0 && candidate <= _lastOutputTimestamp100Ns)
            {
                candidate = _lastOutputTimestamp100Ns + 1;
            }

            _lastOutputTimestamp100Ns = candidate;
            return candidate;
        }

        private static long TryGetSampleDuration(IMFSample sample)
        {
            try
            {
                long duration = sample.SampleDuration;
                return duration > 0 ? duration : DefaultAacPacketDuration100Ns;
            }
            catch (SharpGenException)
            {
                return DefaultAacPacketDuration100Ns;
            }
        }

        private void InitializeAacMft()
        {
            var inputTypeInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Audio,
                GuidSubtype = AudioFormatGuids.Pcm
            };

            var outputTypeInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Audio,
                GuidSubtype = AudioFormatGuids.Aac
            };

            uint flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter);
            using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.AudioEncoder,
                flags,
                inputTypeInfo,
                outputTypeInfo);

            var candidates = new List<IMFActivate>();
            foreach (IMFActivate activate in activates)
            {
                candidates.Add(activate);
            }

            int winningIndex = -1;
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (TryConfigureAacCandidate(candidates[i]))
                    {
                        winningIndex = i;
                        return;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (i != winningIndex)
                    {
                        candidates[i].Dispose();
                    }
                }
            }

            throw new InvalidOperationException(
                $"Nie znaleziono działającego kodera audio AAC Media Foundation dla ścieżki {_trackType}.");
        }

        private bool TryConfigureAacCandidate(IMFActivate activate)
        {
            IMFTransform? transform = null;
            try
            {
                transform = activate.ActivateObject<IMFTransform>();
                (int inputStreamId, int outputStreamId) = ResolveStreamIds(transform);

                using IMFMediaType inputType = CreatePcmInputMediaType();
                using IMFMediaType outputType = CreateAacOutputMediaType();

                // Microsoft AAC Encoder MFT requires SetInputType before SetOutputType.
                try
                {
                    transform.SetInputType(inputStreamId, inputType, 0);
                    SetOutputTypeWithNegotiation(transform, outputStreamId, outputType);
                }
                catch
                {
                    SetOutputTypeWithNegotiation(transform, outputStreamId, outputType);
                    transform.SetInputType(inputStreamId, inputType, 0);
                }

                OutputStreamInfo outputStreamInfo = transform.GetOutputStreamInfo(outputStreamId);
                OutputStreamInfoFlags streamFlags = (OutputStreamInfoFlags)outputStreamInfo.Flags;
                bool mftProvidesSamples =
                    (streamFlags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0 ||
                    (streamFlags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, nuint.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, nuint.Zero);

                _activeActivate = activate;
                _transform = transform;
                _inputStreamId = inputStreamId;
                _outputStreamId = outputStreamId;
                _outputStreamInfo = outputStreamInfo;
                _mftProvidesOutputSamples = mftProvidesSamples;

                CaptureAudioUserData();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pominięto koder AAC MFT ({_trackType}): {ex.Message}");
                if (transform is not null)
                {
                    try
                    {
                        MediaFactory.MFShutdownObject(transform);
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }

                    transform.Dispose();
                }

                try
                {
                    activate.ShutdownObject();
                }
                catch
                {
                    // Ignore activate shutdown failure.
                }

                return false;
            }
        }

        private static void SetOutputTypeWithNegotiation(IMFTransform transform, int outputStreamId, IMFMediaType preferredType)
        {
            try
            {
                transform.SetOutputType(outputStreamId, preferredType, 0);
                return;
            }
            catch (SharpGenException)
            {
                // Fall back to negotiating from the MFT's advertised AAC output templates.
            }

            for (int index = 0; index < 64; index++)
            {
                IMFMediaType? available;
                try
                {
                    available = transform.GetOutputAvailableType(outputStreamId, index);
                }
                catch (SharpGenException ex) when (ex.ResultCode == ResultCode.NoMoreTypes)
                {
                    break;
                }

                using (available)
                {
                    if (available is null)
                    {
                        break;
                    }

                    if (available.GetGUID(MediaTypeAttributeKeys.Subtype, out Guid subtype).Success &&
                        subtype == AudioFormatGuids.Aac &&
                        available.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond, out uint sampleRate).Success &&
                        sampleRate == TargetSampleRate &&
                        available.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels, out uint channels).Success &&
                        channels == TargetChannels &&
                        available.GetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, out uint avgBytes).Success &&
                        avgBytes == TargetAacAvgBytesPerSecond)
                    {
                        available.Set(MediaTypeAttributeKeys.AacPayloadType, AacRawPayloadType);
                        transform.SetOutputType(outputStreamId, available, 0);
                        return;
                    }
                }
            }

            throw new InvalidOperationException("Koder AAC nie zaakceptował konfiguracji 48 kHz / stereo / 128 kbps.");
        }

        private void HandleOutputStreamChange()
        {
            if (_transform is null)
            {
                return;
            }

            using IMFMediaType updatedType = _transform.GetOutputAvailableType(_outputStreamId, 0);
            _transform.SetOutputType(_outputStreamId, updatedType, 0);

            _outputStreamInfo = _transform.GetOutputStreamInfo(_outputStreamId);
            OutputStreamInfoFlags streamFlags = (OutputStreamInfoFlags)_outputStreamInfo.Flags;
            _mftProvidesOutputSamples =
                (streamFlags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0 ||
                (streamFlags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

            CaptureAudioUserData();
        }

        private void CaptureAudioUserData()
        {
            if (_transform is null)
            {
                return;
            }

            try
            {
                using IMFMediaType currentOutputType = _transform.GetOutputCurrentType(_outputStreamId);
                if (currentOutputType.GetBlobSize(MediaTypeAttributeKeys.UserData, out uint blobSize).Success &&
                    blobSize > 0)
                {
                    byte[] userData = new byte[blobSize];
                    if (currentOutputType.GetBlob(MediaTypeAttributeKeys.UserData, userData).Success)
                    {
                        _onUserData?.Invoke(userData);
                    }
                }
            }
            catch
            {
                // Muxer provides a verified standard 48 kHz stereo AAC-LC fallback AudioSpecificConfig blob.
            }
        }

        private static IMFMediaType CreatePcmInputMediaType()
        {
            IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
            try
            {
                mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)TargetBitsPerSample).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)TargetSampleRate).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)TargetChannels).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)TargetBlockAlignment).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)TargetPcmAvgBytesPerSecond).CheckError();
                return mediaType;
            }
            catch
            {
                mediaType.Dispose();
                throw;
            }
        }

        private static IMFMediaType CreateAacOutputMediaType()
        {
            IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
            try
            {
                mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)TargetBitsPerSample).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)TargetSampleRate).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)TargetChannels).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)TargetAacAvgBytesPerSecond).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AacPayloadType, AacRawPayloadType).CheckError();
                mediaType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, AacProfileLevelIndicationLcL2);
                return mediaType;
            }
            catch
            {
                mediaType.Dispose();
                throw;
            }
        }

        private static (int InputStreamId, int OutputStreamId) ResolveStreamIds(IMFTransform transform)
        {
            int[] inputIds = new int[1];
            int[] outputIds = new int[1];
            try
            {
                transform.GetStreamIDs(1, inputIds, 1, outputIds);
                return (inputIds[0], outputIds[0]);
            }
            catch (SharpGenException)
            {
                return (0, 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_transform is not null)
            {
                try
                {
                    _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, nuint.Zero);
                    _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, nuint.Zero);
                }
                catch
                {
                    // Ignore MFT stop errors during disposal.
                }

                try
                {
                    MediaFactory.MFShutdownObject(_transform);
                }
                catch
                {
                    // Ignore IMFShutdown errors.
                }

                _transform.Dispose();
                _transform = null;
            }

            if (_activeActivate is not null)
            {
                try
                {
                    _activeActivate.ShutdownObject();
                }
                catch
                {
                    // Ignore activate shutdown errors.
                }

                _activeActivate.Dispose();
                _activeActivate = null;
            }

            if (_mfStarted)
            {
                _mfStarted = false;
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
}
