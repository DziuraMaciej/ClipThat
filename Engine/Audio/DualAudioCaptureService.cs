using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Diagnostics;
using ClipThat.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using AudioDataFlow = NAudio.CoreAudioApi.DataFlow;
using IMMNotificationClient = NAudio.CoreAudioApi.Interfaces.IMMNotificationClient;
using ResultCode = Vortice.MediaFoundation.ResultCode;
using Role = NAudio.CoreAudioApi.Role;

namespace ClipThat.Engine.Audio;

/// <summary>
/// Captures two independent WASAPI audio streams (System Loopback + Microphone) synchronized to the
/// shared <see cref="Stopwatch"/> replay timeline, converts/resamples incoming audio through a universal NAudio
/// sample conversion chain to 48000 Hz 16-bit Stereo PCM, encodes both tracks to 128 kbps AAC in real time via
/// Media Foundation <see cref="IMFTransform"/>, pushes compressed AAC packets into <see cref="MediaRingBuffer"/>,
/// and automatically hot-swaps default playback/microphone endpoints via <see cref="IMMNotificationClient"/>.
/// </summary>
public sealed class DualAudioCaptureService : IDisposable, IMMNotificationClient
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
    private const int LoopbackCaptureBufferMs = 20;
    private const int MicrophoneCaptureBufferMs = 20;
    private const int HotSwapRetryDelayMs = 150;

    private delegate void PcmFramesCallback(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns);

    private readonly Stopwatch _sessionClock;
    private readonly MediaRingBuffer _ringBuffer;
    private readonly string? _systemAudioDeviceId;
    private readonly string? _microphoneDeviceId;
    private readonly Action<string>? _onStatusChanged;
    private readonly Action? _onDevicesChanged;

    private readonly object _systemSwapLock = new();
    private readonly object _microphoneSwapLock = new();
    private readonly object _combinedMixLock = new();
    private readonly object _enumeratorLock = new();

    private MMDeviceEnumerator? _notificationEnumerator;
    private bool _notificationRegistered;

    private AudioTrackPipeline? _systemAudioTrack;
    private AudioTrackPipeline? _microphoneTrack;
    private CombinedAudioMixPipeline? _combinedMixPipeline;
    private long _lastSystemEncodedTimestamp100Ns = -1;
    private long _lastMicrophoneEncodedTimestamp100Ns = -1;
    private long _lastCombinedMixEncodedTimestamp100Ns = -1;
    private string? _systemAudioError;
    private string? _microphoneError;
    private int _systemSwapVersion;
    private int _microphoneSwapVersion;
    private int _started;
    private int _disposed;

    public DualAudioCaptureService(
        Stopwatch sessionClock,
        MediaRingBuffer ringBuffer,
        string? systemAudioDeviceId,
        string? microphoneDeviceId,
        Action<string>? onStatusChanged = null,
        Action? onDevicesChanged = null)
    {
        ArgumentNullException.ThrowIfNull(sessionClock);
        ArgumentNullException.ThrowIfNull(ringBuffer);

        _sessionClock = sessionClock;
        _ringBuffer = ringBuffer;
        _systemAudioDeviceId = string.IsNullOrWhiteSpace(systemAudioDeviceId) ? null : systemAudioDeviceId.Trim();
        _microphoneDeviceId = string.IsNullOrWhiteSpace(microphoneDeviceId) ? null : microphoneDeviceId.Trim();
        _onStatusChanged = onStatusChanged;
        _onDevicesChanged = onDevicesChanged;
    }

    public bool IsSystemAudioActive => Volatile.Read(ref _systemAudioTrack) is not null;

    public bool IsMicrophoneActive => Volatile.Read(ref _microphoneTrack) is not null;

    public bool IsCombinedMixActive => Volatile.Read(ref _combinedMixPipeline) is not null;

    public string? SystemAudioError => Volatile.Read(ref _systemAudioError);

    public string? MicrophoneError => Volatile.Read(ref _microphoneError);

    /// <summary>
    /// Returns a combined Polish summary of any audio capture initialization errors,
    /// or <see langword="null"/> when all configured audio pipelines started cleanly.
    /// </summary>
    public string? GetInitializationErrorSummary()
    {
        string? sysError = SystemAudioError;
        string? micError = MicrophoneError;

        if (!string.IsNullOrWhiteSpace(sysError) && !string.IsNullOrWhiteSpace(micError))
        {
            return $"{sysError}; {micError}";
        }

        return !string.IsNullOrWhiteSpace(sysError) ? sysError : micError;
    }

    /// <summary>
    /// Starts both WASAPI capture pipelines (system loopback and optional microphone) inside independent
    /// <c>try/catch</c> blocks so missing microphones, virtual audio routers, or spatial audio formats
    /// never prevent video recording from starting, and registers <see cref="IMMNotificationClient"/>
    /// for seamless default device hot-swapping.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        RegisterDeviceNotifications();

        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            string audioSvcError = $"Ostrzeżenie audio: nie udało się zainicjować podsystemu WASAPI ({ex.Message}).";
            EngineLogger.Warn("DualAudioCaptureService", audioSvcError, ex);
            Volatile.Write(ref _systemAudioError, audioSvcError);
            return;
        }

        using (enumerator)
        {
            // Independent try/catch for System Audio (WASAPI Loopback)
            try
            {
                lock (_systemSwapLock)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _systemAudioTrack = TryStartSystemAudioTrack(
                            enumerator,
                            minimumTimestamp100Ns: -1L,
                            out string? systemError);
                        Volatile.Write(ref _systemAudioError, systemError);
                    }
                }
            }
            catch (Exception ex)
            {
                string error = $"Błąd dźwięku systemu (WASAPI Loopback): {ex.Message}";
                EngineLogger.Warn("DualAudioCaptureService", error, ex);
                Volatile.Write(ref _systemAudioError, error);
            }

            // Independent try/catch for Microphone (WASAPI Capture)
            try
            {
                lock (_microphoneSwapLock)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _microphoneTrack = TryStartMicrophoneTrack(
                            enumerator,
                            minimumTimestamp100Ns: -1L,
                            out string? micError);
                        Volatile.Write(ref _microphoneError, micError);
                    }
                }
            }
            catch (Exception ex)
            {
                string error = $"Błąd mikrofonu (WASAPI): {ex.Message}";
                EngineLogger.Warn("DualAudioCaptureService", error, ex);
                Volatile.Write(ref _microphoneError, error);
            }
        }

        try
        {
            SyncCombinedMixPipelineState();
        }
        catch (Exception ex)
        {
            EngineLogger.Warn("DualAudioCaptureService", $"Combined audio mix pipeline failed to start: {ex.Message}", ex);
        }
    }

    private void RegisterDeviceNotifications()
    {
        lock (_enumeratorLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _notificationRegistered)
            {
                return;
            }

            try
            {
                _notificationEnumerator = new MMDeviceEnumerator();
                _notificationEnumerator.RegisterEndpointNotificationCallback(this);
                _notificationRegistered = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Nie udało się zarejestrować IMMNotificationClient: {ex.Message}");
                _onStatusChanged?.Invoke($"Ostrzeżenie audio: nie udało się monitorować zmian urządzeń ({ex.Message})");
                CleanupNotificationEnumeratorLocked();
            }
        }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(AudioDataFlow flow, Role role, string defaultDeviceId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        NotifyDevicesChangedAsync();

        if (role != Role.Multimedia)
        {
            return;
        }

        if (flow == AudioDataFlow.Render && string.IsNullOrWhiteSpace(_systemAudioDeviceId))
        {
            int targetVersion = Interlocked.Increment(ref _systemSwapVersion);
            _ = Task.Run(() => HotSwapSystemAudioTrack(targetVersion, defaultDeviceId));
        }
        else if (flow == AudioDataFlow.Capture && string.IsNullOrWhiteSpace(_microphoneDeviceId))
        {
            int targetVersion = Interlocked.Increment(ref _microphoneSwapVersion);
            _ = Task.Run(() => HotSwapMicrophoneTrack(targetVersion, defaultDeviceId));
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            NotifyDevicesChangedAsync();
        }
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            NotifyDevicesChangedAsync();
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            NotifyDevicesChangedAsync();
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Frequent volume/mixer property changes do not alter endpoint topology or default routing.
    }

    private void NotifyDevicesChangedAsync()
    {
        Action? callback = _onDevicesChanged;
        if (callback is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((Action)state!).Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd powiadomienia UI o zmianie urządzeń audio: {ex.Message}");
            }
        }, callback);
    }

    private void HotSwapSystemAudioTrack(int targetVersion, string? notifiedDefaultDeviceId)
    {
        string? statusNotice = null;

        lock (_systemSwapLock)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _systemSwapVersion) != targetVersion)
            {
                return;
            }

            AudioTrackPipeline? current = _systemAudioTrack;
            if (current is not null &&
                !string.IsNullOrWhiteSpace(notifiedDefaultDeviceId) &&
                string.Equals(current.DeviceId, notifiedDefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (current is not null)
            {
                long lastTs = current.LastEncodedTimestamp100Ns;
                if (lastTs > _lastSystemEncodedTimestamp100Ns)
                {
                    _lastSystemEncodedTimestamp100Ns = lastTs;
                }

                _systemAudioTrack = null;
                SafeDispose(current);
            }

            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _systemSwapVersion) != targetVersion)
            {
                return;
            }

            long minNextTimestamp100Ns = _lastSystemEncodedTimestamp100Ns >= 0
                ? _lastSystemEncodedTimestamp100Ns + 1
                : -1L;

            AudioTrackPipeline? replacement = StartSystemAudioWithRetry(minNextTimestamp100Ns, out string? error);
            if (Volatile.Read(ref _disposed) != 0)
            {
                SafeDispose(replacement);
                return;
            }

            _systemAudioTrack = replacement;
            Volatile.Write(ref _systemAudioError, error);

            if (replacement is not null)
            {
                statusNotice = $"Przełączono dźwięk systemu: {replacement.DeviceFriendlyName}";
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                statusNotice = error;
            }
        }

        if (statusNotice is not null && Volatile.Read(ref _disposed) == 0)
        {
            _onStatusChanged?.Invoke(statusNotice);
        }
    }

    private void HotSwapMicrophoneTrack(int targetVersion, string? notifiedDefaultDeviceId)
    {
        string? statusNotice = null;

        lock (_microphoneSwapLock)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _microphoneSwapVersion) != targetVersion)
            {
                return;
            }

            AudioTrackPipeline? current = _microphoneTrack;
            if (current is not null &&
                !string.IsNullOrWhiteSpace(notifiedDefaultDeviceId) &&
                string.Equals(current.DeviceId, notifiedDefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (current is not null)
            {
                long lastTs = current.LastEncodedTimestamp100Ns;
                if (lastTs > _lastMicrophoneEncodedTimestamp100Ns)
                {
                    _lastMicrophoneEncodedTimestamp100Ns = lastTs;
                }

                _microphoneTrack = null;
                SafeDispose(current);
            }

            if (Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _microphoneSwapVersion) != targetVersion)
            {
                return;
            }

            long minNextTimestamp100Ns = _lastMicrophoneEncodedTimestamp100Ns >= 0
                ? _lastMicrophoneEncodedTimestamp100Ns + 1
                : -1L;

            AudioTrackPipeline? replacement = StartMicrophoneWithRetry(minNextTimestamp100Ns, out string? error);
            if (Volatile.Read(ref _disposed) != 0)
            {
                SafeDispose(replacement);
                return;
            }

            _microphoneTrack = replacement;
            Volatile.Write(ref _microphoneError, error);

            if (replacement is not null)
            {
                statusNotice = $"Przełączono mikrofon: {replacement.DeviceFriendlyName}";
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                statusNotice = error;
            }
        }

        SyncCombinedMixPipelineState();

        if (statusNotice is not null && Volatile.Read(ref _disposed) == 0)
        {
            _onStatusChanged?.Invoke(statusNotice);
        }
    }

    private void SyncCombinedMixPipelineState()
    {
        lock (_combinedMixLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            bool shouldRun = Volatile.Read(ref _microphoneTrack) is not null;
            if (shouldRun && _combinedMixPipeline is null)
            {
                long minTs = _lastCombinedMixEncodedTimestamp100Ns >= 0
                    ? _lastCombinedMixEncodedTimestamp100Ns + 1
                    : -1L;

                try
                {
                    _combinedMixPipeline = new CombinedAudioMixPipeline(_ringBuffer, minTs);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Nie udało się uruchomić miksera audio (CombinedMixAudio): {ex.Message}");
                }
            }
            else if (!shouldRun && _combinedMixPipeline is not null)
            {
                CombinedAudioMixPipeline oldMixer = _combinedMixPipeline;
                _combinedMixPipeline = null;

                long lastTs = oldMixer.LastEncodedTimestamp100Ns;
                if (lastTs > _lastCombinedMixEncodedTimestamp100Ns)
                {
                    _lastCombinedMixEncodedTimestamp100Ns = lastTs;
                }

                SafeDispose(oldMixer);
            }
        }
    }

    private void OnSystemPcmFrames(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns)
    {
        Volatile.Read(ref _combinedMixPipeline)?.SubmitSystemPcm(pcmBytes, startTimestamp100Ns);
    }

    private void OnMicrophonePcmFrames(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns)
    {
        Volatile.Read(ref _combinedMixPipeline)?.SubmitMicrophonePcm(pcmBytes, startTimestamp100Ns);
    }

    private AudioTrackPipeline? StartSystemAudioWithRetry(long minimumTimestamp100Ns, out string? error)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                error = null;
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                AudioTrackPipeline? pipeline = TryStartSystemAudioTrack(enumerator, minimumTimestamp100Ns, out error);
                if (pipeline is not null || attempt == 1)
                {
                    return pipeline;
                }
            }
            catch (Exception ex)
            {
                error = $"Błąd dźwięku systemu (WASAPI Loopback): {ex.Message}";
                EngineLogger.Warn("DualAudioCaptureService", error, ex);
                if (attempt == 1)
                {
                    return null;
                }
            }

            Thread.Sleep(HotSwapRetryDelayMs);
        }

        error = null;
        return null;
    }

    private AudioTrackPipeline? StartMicrophoneWithRetry(long minimumTimestamp100Ns, out string? error)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                error = null;
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                AudioTrackPipeline? pipeline = TryStartMicrophoneTrack(enumerator, minimumTimestamp100Ns, out error);
                if (pipeline is not null || string.IsNullOrWhiteSpace(error) || attempt == 1)
                {
                    return pipeline;
                }
            }
            catch (Exception ex)
            {
                error = $"Błąd mikrofonu (WASAPI): {ex.Message}";
                EngineLogger.Warn("DualAudioCaptureService", error, ex);
                if (attempt == 1)
                {
                    return null;
                }
            }

            Thread.Sleep(HotSwapRetryDelayMs);
        }

        error = null;
        return null;
    }

    private AudioTrackPipeline? TryStartSystemAudioTrack(
        MMDeviceEnumerator enumerator,
        long minimumTimestamp100Ns,
        out string? error)
    {
        error = null;
        MMDevice? renderDevice = null;

        try
        {
            renderDevice = ResolveRenderEndpoint(enumerator, _systemAudioDeviceId, out string? resolveWarning);
            if (renderDevice is null)
            {
                error = resolveWarning ?? "Błąd dźwięku systemu: brak aktywnego urządzenia wyjściowego audio.";
                EngineLogger.Warn("DualAudioCaptureService", error);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(resolveWarning))
            {
                EngineLogger.Warn("DualAudioCaptureService", resolveWarning);
                error = resolveWarning;
            }

            try
            {
                MMDevice ownedDevice = renderDevice;
                renderDevice = null;
                AudioTrackPipeline pipeline = CreateAndStartLoopbackPipeline(
                    ownedDevice,
                    useLowLatencyBuffer: true,
                    minimumTimestamp100Ns);
                EngineLogger.Info(
                    "DualAudioCaptureService",
                    $"System audio loopback active (low-latency): \"{pipeline.DeviceFriendlyName}\" ({pipeline.SourceFormatDescription})");
                return pipeline;
            }
            catch (Exception ex)
            {
                EngineLogger.Warn(
                    "DualAudioCaptureService",
                    $"Low-latency WASAPI loopback failed ({ex.Message}); retrying with standard WasapiLoopbackCapture.");

                // Re-resolve a fresh MMDevice handle because the failed pipeline disposed the previous handle.
                MMDevice? fallbackDevice = ResolveRenderEndpoint(enumerator, _systemAudioDeviceId, out _);
                if (fallbackDevice is null)
                {
                    error = $"Błąd dźwięku systemu (WASAPI Loopback): {ex.Message}";
                    return null;
                }

                try
                {
                    AudioTrackPipeline pipeline = CreateAndStartLoopbackPipeline(
                        fallbackDevice,
                        useLowLatencyBuffer: false,
                        minimumTimestamp100Ns);
                    EngineLogger.Info(
                        "DualAudioCaptureService",
                        $"System audio loopback active (standard): \"{pipeline.DeviceFriendlyName}\" ({pipeline.SourceFormatDescription})");
                    return pipeline;
                }
                catch (Exception standardEx)
                {
                    // If a specific virtual/spatial output failed, attempt one fallback to the default render endpoint.
                    if (!string.IsNullOrWhiteSpace(_systemAudioDeviceId))
                    {
                        EngineLogger.Warn(
                            "DualAudioCaptureService",
                            $"Configured render endpoint failed ({standardEx.Message}); retrying system default render endpoint.");

                        MMDevice? defaultRender = ResolveRenderEndpoint(enumerator, configuredDeviceId: null, out _);
                        if (defaultRender is not null)
                        {
                            AudioTrackPipeline defaultPipeline = CreateAndStartLoopbackPipeline(
                                defaultRender,
                                useLowLatencyBuffer: false,
                                minimumTimestamp100Ns);
                            error = $"Wybrane urządzenie wyjściowe nieobsługiwane — użyto domyślnego ({defaultPipeline.DeviceFriendlyName}).";
                            EngineLogger.Info(
                                "DualAudioCaptureService",
                                $"System audio loopback active (default fallback): \"{defaultPipeline.DeviceFriendlyName}\" ({defaultPipeline.SourceFormatDescription})");
                            return defaultPipeline;
                        }
                    }

                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Błąd dźwięku systemu (WASAPI Loopback): {ex.Message}";
            EngineLogger.Warn("DualAudioCaptureService", error, ex);
            SafeDispose(renderDevice);
            return null;
        }
    }

    private AudioTrackPipeline CreateAndStartLoopbackPipeline(
        MMDevice renderDevice,
        bool useLowLatencyBuffer,
        long minimumTimestamp100Ns)
    {
        WasapiCapture? loopbackCapture = null;
        AudioTrackPipeline? pipeline = null;

        try
        {
            loopbackCapture = useLowLatencyBuffer
                ? new LowLatencyWasapiLoopbackCapture(renderDevice, LoopbackCaptureBufferMs)
                : new WasapiLoopbackCapture(renderDevice);

            pipeline = new AudioTrackPipeline(
                MediaTrackType.SystemAudio,
                renderDevice,
                loopbackCapture,
                _sessionClock,
                _ringBuffer,
                enableSilenceWatchdog: true,
                minimumTimestamp100Ns: minimumTimestamp100Ns,
                onRuntimeError: HandleTrackRuntimeError,
                onPcmFrames: OnSystemPcmFrames);

            pipeline.Start();
            return pipeline;
        }
        catch
        {
            if (pipeline is not null)
            {
                pipeline.Dispose();
            }
            else
            {
                loopbackCapture?.Dispose();
                renderDevice.Dispose();
            }

            throw;
        }
    }

    private AudioTrackPipeline? TryStartMicrophoneTrack(
        MMDeviceEnumerator enumerator,
        long minimumTimestamp100Ns,
        out string? error)
    {
        error = null;

        if (AudioDeviceService.IsDisabledDeviceId(_microphoneDeviceId))
        {
            EngineLogger.Info("DualAudioCaptureService", "Microphone capture explicitly disabled by user setting.");
            return null;
        }

        MMDevice? captureDevice = null;

        try
        {
            captureDevice = ResolveCaptureEndpoint(enumerator, _microphoneDeviceId, out string? resolveError);
            if (captureDevice is null)
            {
                error = resolveError;
                if (!string.IsNullOrWhiteSpace(resolveError))
                {
                    EngineLogger.Warn("DualAudioCaptureService", resolveError);
                }
                else
                {
                    EngineLogger.Info("DualAudioCaptureService", "No active microphone detected; continuing without microphone track.");
                }

                return null;
            }

            try
            {
                MMDevice ownedDevice = captureDevice;
                captureDevice = null;
                AudioTrackPipeline pipeline = CreateAndStartMicrophonePipeline(
                    ownedDevice,
                    useEventSync: true,
                    bufferMilliseconds: MicrophoneCaptureBufferMs,
                    minimumTimestamp100Ns);
                EngineLogger.Info(
                    "DualAudioCaptureService",
                    $"Microphone capture active (event-sync): \"{pipeline.DeviceFriendlyName}\" ({pipeline.SourceFormatDescription})");
                return pipeline;
            }
            catch (Exception ex)
            {
                EngineLogger.Warn(
                    "DualAudioCaptureService",
                    $"WASAPI microphone event-sync failed ({ex.Message}); retrying in standard polling mode.");

                MMDevice? fallbackDevice = ResolveCaptureEndpoint(enumerator, _microphoneDeviceId, out _);
                if (fallbackDevice is null)
                {
                    error = $"Błąd mikrofonu (WASAPI): {ex.Message}";
                    return null;
                }

                AudioTrackPipeline pipeline = CreateAndStartMicrophonePipeline(
                    fallbackDevice,
                    useEventSync: false,
                    bufferMilliseconds: null,
                    minimumTimestamp100Ns);
                EngineLogger.Info(
                    "DualAudioCaptureService",
                    $"Microphone capture active (polling fallback): \"{pipeline.DeviceFriendlyName}\" ({pipeline.SourceFormatDescription})");
                return pipeline;
            }
        }
        catch (Exception ex)
        {
            error = $"Błąd mikrofonu (WASAPI): {ex.Message}";
            EngineLogger.Warn("DualAudioCaptureService", error, ex);
            SafeDispose(captureDevice);
            return null;
        }
    }

    private AudioTrackPipeline CreateAndStartMicrophonePipeline(
        MMDevice captureDevice,
        bool useEventSync,
        int? bufferMilliseconds,
        long minimumTimestamp100Ns)
    {
        WasapiCapture? micCapture = null;
        AudioTrackPipeline? pipeline = null;

        try
        {
            micCapture = bufferMilliseconds.HasValue
                ? new WasapiCapture(
                    captureDevice,
                    useEventSync: useEventSync,
                    audioBufferMillisecondsLength: bufferMilliseconds.Value)
                : new WasapiCapture(
                    captureDevice,
                    useEventSync: useEventSync);

            pipeline = new AudioTrackPipeline(
                MediaTrackType.MicrophoneAudio,
                captureDevice,
                micCapture,
                _sessionClock,
                _ringBuffer,
                enableSilenceWatchdog: false,
                minimumTimestamp100Ns: minimumTimestamp100Ns,
                onRuntimeError: HandleTrackRuntimeError,
                onPcmFrames: OnMicrophonePcmFrames);

            pipeline.Start();
            return pipeline;
        }
        catch
        {
            if (pipeline is not null)
            {
                pipeline.Dispose();
            }
            else
            {
                micCapture?.Dispose();
                captureDevice.Dispose();
            }

            throw;
        }
    }

    private static MMDevice? ResolveRenderEndpoint(
        MMDeviceEnumerator enumerator,
        string? configuredDeviceId,
        out string? warningOrError)
    {
        warningOrError = null;

        if (!string.IsNullOrWhiteSpace(configuredDeviceId))
        {
            try
            {
                MMDevice specific = enumerator.GetDevice(configuredDeviceId);
                if (specific.State == DeviceState.Active)
                {
                    return specific;
                }

                specific.Dispose();
                warningOrError = "Wybrane urządzenie wyjściowe audio jest nieaktywne — próba użycia domyślnego.";
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                warningOrError = $"Wybrane urządzenie wyjściowe audio jest niedostępne ({ex.Message}) — próba użycia domyślnego.";
            }
        }

        try
        {
            MMDevice defaultRender = enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, Role.Multimedia);
            if (defaultRender.State == DeviceState.Active)
            {
                return defaultRender;
            }

            defaultRender.Dispose();
        }
        catch (COMException ex)
        {
            warningOrError = $"Brak domyślnego urządzenia wyjściowego audio (Multimedia): {ex.Message}";
        }

        MMDevice? fallback = AudioDeviceService.ResolveDevice(enumerator, AudioDataFlow.Render, deviceId: null);
        if (fallback is not null)
        {
            return fallback;
        }

        warningOrError ??= "Błąd dźwięku systemu: nie znaleziono aktywnego urządzenia wyjściowego audio.";
        return null;
    }

    private static MMDevice? ResolveCaptureEndpoint(
        MMDeviceEnumerator enumerator,
        string? configuredDeviceId,
        out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(configuredDeviceId))
        {
            try
            {
                MMDevice specific = enumerator.GetDevice(configuredDeviceId);
                if (specific.State == DeviceState.Active)
                {
                    return specific;
                }

                specific.Dispose();
                error = "Błąd mikrofonu: wybrane urządzenie mikrofonu jest nieaktywne.";
                return null;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                error = $"Błąd mikrofonu: wybrane urządzenie jest niedostępne ({ex.Message}).";
                return null;
            }
        }

        try
        {
            MMDevice defaultMic = enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Capture, Role.Multimedia);
            if (defaultMic.State == DeviceState.Active)
            {
                return defaultMic;
            }

            defaultMic.Dispose();
        }
        catch (COMException)
        {
            // No default multimedia microphone endpoint; fall through to check other roles/active endpoints.
        }

        return AudioDeviceService.ResolveDevice(enumerator, AudioDataFlow.Capture, deviceId: null);
    }

    private void HandleTrackRuntimeError(MediaTrackType trackType, Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string label = trackType == MediaTrackType.SystemAudio ? "dźwięku systemu" : "mikrofonu";
        string message = $"Błąd przechwytywania {label}: {exception.Message}";
        Debug.WriteLine(message);
        _onStatusChanged?.Invoke(message);
    }

    private void CleanupNotificationEnumeratorLocked()
    {
        if (_notificationRegistered && _notificationEnumerator is not null)
        {
            _notificationRegistered = false;
            try
            {
                _notificationEnumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
                // Ignore COM unregister failures during teardown.
            }
        }

        if (_notificationEnumerator is not null)
        {
            try
            {
                _notificationEnumerator.Dispose();
            }
            catch
            {
                // Ignore COM disposal failures during teardown.
            }

            _notificationEnumerator = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _systemSwapVersion);
        Interlocked.Increment(ref _microphoneSwapVersion);

        lock (_enumeratorLock)
        {
            CleanupNotificationEnumeratorLocked();
        }

        AudioTrackPipeline? sys;
        lock (_systemSwapLock)
        {
            sys = _systemAudioTrack;
            _systemAudioTrack = null;
        }

        AudioTrackPipeline? mic;
        lock (_microphoneSwapLock)
        {
            mic = _microphoneTrack;
            _microphoneTrack = null;
        }

        SafeDispose(sys);
        SafeDispose(mic);

        CombinedAudioMixPipeline? mix;
        lock (_combinedMixLock)
        {
            mix = _combinedMixPipeline;
            _combinedMixPipeline = null;
        }

        SafeDispose(mix);
    }

    private static long FramesTo100Ns(long frames)
        => (frames / TargetSampleRate) * TimeSpan.TicksPerSecond
           + ((frames % TargetSampleRate) * TimeSpan.TicksPerSecond) / TargetSampleRate;

    private static long Ticks100NsToFrames(long ticks100Ns)
        => (ticks100Ns / TimeSpan.TicksPerSecond) * TargetSampleRate
           + ((ticks100Ns % TimeSpan.TicksPerSecond) * TargetSampleRate) / TimeSpan.TicksPerSecond;

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
    /// Shared-mode WASAPI loopback capture configured with a 20 ms buffer (10 ms polling cadence)
    /// instead of NAudio's default 100 ms buffer (50 ms polling sleep), ensuring real loopback packets
    /// arrive promptly and never collide with the 25 ms silence-injection threshold.
    /// </summary>
    private sealed class LowLatencyWasapiLoopbackCapture : WasapiCapture
    {
        public LowLatencyWasapiLoopbackCapture(MMDevice captureDevice, int audioBufferMillisecondsLength)
            : base(captureDevice, useEventSync: false, audioBufferMillisecondsLength: audioBufferMillisecondsLength)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            => AudioClientStreamFlags.Loopback;
    }

    /// <summary>
    /// Encapsulates one independent WASAPI capture -> universal NAudio PCM converter/resampler ->
    /// real-time AAC MFT encoder track synchronized to the shared session <see cref="Stopwatch"/>.
    /// </summary>
    private sealed class AudioTrackPipeline : IDisposable
    {
        // Only inject zero-filled 20 ms PCM frames when > 25 ms has elapsed without real loopback data.
        private const long SilenceTriggerDelay100Ns = 25 * TimeSpan.TicksPerMillisecond;
        private const long SilenceLeadMargin100Ns = 20 * TimeSpan.TicksPerMillisecond;
        private const long SilenceChunkDuration100Ns = 20 * TimeSpan.TicksPerMillisecond;
        private const long GapBridgeThreshold100Ns = 45 * TimeSpan.TicksPerMillisecond;
        private const long MaxSyncGapToFill100Ns = 2 * TimeSpan.TicksPerSecond;
        private const long OverrunDriftLimit100Ns = 250 * TimeSpan.TicksPerMillisecond;
        private const int MaxSilenceFramesPerTick = 4800; // 100 ms max per watchdog tick
        private const int SilenceChunkFrames = 960;       // 20 ms at 48 kHz

        private static readonly byte[] ZeroSilenceChunk = new byte[SilenceChunkFrames * TargetBlockAlignment];

        private readonly MediaTrackType _trackType;
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly Stopwatch _sessionClock;
        private readonly MediaRingBuffer _ringBuffer;
        private readonly bool _enableSilenceWatchdog;
        private readonly long _minimumTimestamp100Ns;
        private readonly Action<MediaTrackType, Exception>? _onRuntimeError;
        private readonly PcmFramesCallback? _onPcmFrames;
        private readonly object _syncLock = new();

        private readonly AudioPcmConverter _pcmConverter;
        private readonly AacMftEncoder _aacEncoder;
        private byte[] _pcmScratchBuffer = new byte[16 * 1024];

        private Timer? _silenceWatchdogTimer;
        private long _trackOrigin100Ns;
        private long _totalPcmFramesFed;
        private long _lastRealAudioClock100Ns;
        private long _lastEncodedTimestamp100Ns = -1;
        private int _stopped;
        private int _disposed;

        public AudioTrackPipeline(
            MediaTrackType trackType,
            MMDevice device,
            WasapiCapture capture,
            Stopwatch sessionClock,
            MediaRingBuffer ringBuffer,
            bool enableSilenceWatchdog,
            long minimumTimestamp100Ns,
            Action<MediaTrackType, Exception>? onRuntimeError,
            PcmFramesCallback? onPcmFrames = null)
        {
            _trackType = trackType;
            _device = device;
            _capture = capture;
            _sessionClock = sessionClock;
            _ringBuffer = ringBuffer;
            _enableSilenceWatchdog = enableSilenceWatchdog;
            _minimumTimestamp100Ns = minimumTimestamp100Ns;
            _onRuntimeError = onRuntimeError;
            _onPcmFrames = onPcmFrames;

            DeviceId = TryReadDeviceId(device);
            DeviceFriendlyName = TryReadFriendlyName(device, DeviceId);
            WaveFormat wf = capture.WaveFormat;
            SourceFormatDescription = $"{wf.SampleRate} Hz, {wf.BitsPerSample}-bit, {wf.Channels} ch, {wf.Encoding}";

            _pcmConverter = new AudioPcmConverter(wf);
            _aacEncoder = new AacMftEncoder(
                trackType,
                initialLastTimestamp100Ns: minimumTimestamp100Ns > 0 ? minimumTimestamp100Ns - 1 : -1L,
                onEncodedPacket: PushEncodedPacket,
                onUserData: userData => _ringBuffer.SetAudioUserData(_trackType, userData.Span));
        }

        public string DeviceId { get; }

        public string DeviceFriendlyName { get; }

        public string SourceFormatDescription { get; }

        public long LastEncodedTimestamp100Ns => Volatile.Read(ref _lastEncodedTimestamp100Ns);

        public void Start()
        {
            lock (_syncLock)
            {
                long now100Ns = _sessionClock.Elapsed.Ticks;
                _trackOrigin100Ns = _minimumTimestamp100Ns > 0
                    ? Math.Max(now100Ns, _minimumTimestamp100Ns)
                    : now100Ns;
                _lastRealAudioClock100Ns = now100Ns;
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

                    // Record arrival of real loopback/mic audio immediately so the silence timer never competes.
                    _lastRealAudioClock100Ns = now100Ns;

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

                    // Guard against severe audio hardware clock drift pushing audio far ahead of video.
                    if (streamCursor100Ns - now100Ns > OverrunDriftLimit100Ns)
                    {
                        return;
                    }

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

                // Strictly require > 25 ms without real loopback data before injecting silence.
                if (now100Ns - _lastRealAudioClock100Ns <= SilenceTriggerDelay100Ns)
                {
                    return;
                }

                // Keep the silence cursor behind real-time by one 20 ms margin so incoming real audio
                // never collides with or gets overwritten by silence timestamps.
                long targetCursor100Ns = now100Ns - SilenceLeadMargin100Ns;
                long currentCursor100Ns = GetCurrentPcmCursor100NsLocked();
                long deficit100Ns = targetCursor100Ns - currentCursor100Ns;

                if (deficit100Ns < SilenceChunkDuration100Ns)
                {
                    return;
                }

                if (deficit100Ns > MaxSyncGapToFill100Ns)
                {
                    ReanchorTimelineLocked(targetCursor100Ns);
                    return;
                }

                long framesNeeded = Math.Min(Ticks100NsToFrames(deficit100Ns), MaxSilenceFramesPerTick);
                long fullChunks = framesNeeded / SilenceChunkFrames;
                for (long i = 0; i < fullChunks; i++)
                {
                    FeedPcmFramesLocked(ZeroSilenceChunk, SilenceChunkFrames);
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
                _onRuntimeError?.Invoke(_trackType, e.Exception);
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

            long chunkStart100Ns = _trackOrigin100Ns + FramesTo100Ns(_totalPcmFramesFed);
            _aacEncoder.FeedPcm(pcmBytes, _trackOrigin100Ns, _totalPcmFramesFed);
            _totalPcmFramesFed += frameCount;
            _onPcmFrames?.Invoke(pcmBytes, chunkStart100Ns);
        }

        private long GetCurrentPcmCursor100NsLocked()
            => _trackOrigin100Ns + FramesTo100Ns(_totalPcmFramesFed);

        private void ReanchorTimelineLocked(long newCursor100Ns)
        {
            _aacEncoder.ResetPendingPcm();
            long minAllowed = Math.Max(0L, _lastEncodedTimestamp100Ns + 1);
            _trackOrigin100Ns = Math.Max(minAllowed, newCursor100Ns);
            _totalPcmFramesFed = 0;
        }

        private void PushEncodedPacket(byte[] rentedBuffer, int length, long timestamp100Ns, long duration100Ns)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return;
            }

            Volatile.Write(ref _lastEncodedTimestamp100Ns, timestamp100Ns);
            _ringBuffer.AddAudioPacket(_trackType, rentedBuffer, length, timestamp100Ns, duration100Ns);
        }

        private static string TryReadDeviceId(MMDevice device)
        {
            try
            {
                return device.ID ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryReadFriendlyName(MMDevice device, string fallback)
        {
            try
            {
                return string.IsNullOrWhiteSpace(device.FriendlyName)
                    ? fallback
                    : device.FriendlyName.Trim();
            }
            catch
            {
                return fallback;
            }
        }

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
    /// Universal NAudio sample conversion chain converting any WASAPI capture format
    /// (IEEE Float / 8-16-24-32-bit PCM / <see cref="WaveFormatExtensible"/>, mono / stereo / multichannel,
    /// any sample rate such as 44.1 / 48 / 96 kHz) cleanly into 48000 Hz, 16-bit, 2-channel Stereo PCM:
    /// <list type="number">
    /// <item>Bytes -&gt; <see cref="ISampleProvider"/> (<see cref="WaveToSampleProvider"/> / <see cref="Pcm16BitToSampleProvider"/> / PCM bit-depth providers)</item>
    /// <item>Channel normalization to Stereo (<see cref="MonoToStereoSampleProvider"/> or <see cref="MultiplexingSampleProvider"/> mapping FL/FR 0 &amp; 1)</item>
    /// <item>Resampling to 48000 Hz via <see cref="WdlResamplingSampleProvider"/> when sample rate != 48000</item>
    /// <item>16-bit PCM output via <see cref="SampleToWaveProvider16"/></item>
    /// </list>
    /// </summary>
    private sealed class AudioPcmConverter
    {
        private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

        private const int MaxDrainIterations = 64;

        private readonly int _sourceBlockAlign;
        private readonly BufferedWaveProvider _inputBuffer;
        private readonly SampleToWaveProvider16 _pcm16Output;
        private readonly byte[] _drainChunkBuffer = new byte[16 * 1024]; // Multiple of 4 (TargetBlockAlignment)

        public AudioPcmConverter(WaveFormat sourceFormat)
        {
            ArgumentNullException.ThrowIfNull(sourceFormat);

            int channels = Math.Max(1, sourceFormat.Channels);
            int sampleRate = Math.Max(8000, sourceFormat.SampleRate);
            bool isFloat = IsIeeeFloatFormat(sourceFormat);

            // Normalize WaveFormatExtensible into a standard WaveFormat with identical interleaved byte layout
            // so NAudio sample providers never reject Extensible descriptors (e.g., 5.1/7.1 spatial headsets).
            WaveFormat normalizedInputFormat = isFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
                : new WaveFormat(sampleRate, sourceFormat.BitsPerSample, channels);

            _sourceBlockAlign = Math.Max(1, normalizedInputFormat.BlockAlign);

            _inputBuffer = new BufferedWaveProvider(normalizedInputFormat, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            // 1. Convert incoming bytes to ISampleProvider.
            ISampleProvider sampleProvider = CreateSampleProvider(_inputBuffer, normalizedInputFormat);

            // 2. Normalize channel count to 2 (Stereo).
            if (sampleProvider.WaveFormat.Channels == 1)
            {
                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
            }
            else if (sampleProvider.WaveFormat.Channels > TargetChannels)
            {
                var multiplexer = new MultiplexingSampleProvider(new[] { sampleProvider }, TargetChannels);
                multiplexer.ConnectInputToOutput(0, 0); // Front-Left -> Left
                multiplexer.ConnectInputToOutput(1, 1); // Front-Right -> Right
                sampleProvider = multiplexer;
            }

            // 3. Resample to 48000 Hz whenever the endpoint's native mix rate differs.
            if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
            {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
            }

            // 4. Convert back to 16-bit Stereo PCM for Media Foundation AAC encoding.
            _pcm16Output = new SampleToWaveProvider16(sampleProvider);
        }

        public int ConvertToStereoPcm16At48k(byte[] inputBuffer, int bytesRecorded, ref byte[] outputPcm16Buffer)
        {
            int alignedInputBytes = (bytesRecorded / _sourceBlockAlign) * _sourceBlockAlign;
            if (alignedInputBytes <= 0)
            {
                return 0;
            }

            _inputBuffer.AddSamples(inputBuffer, 0, alignedInputBytes);

            int totalWrittenBytes = 0;
            for (int iter = 0; iter < MaxDrainIterations; iter++)
            {
                int bytesRead = _pcm16Output.Read(_drainChunkBuffer, 0, _drainChunkBuffer.Length);
                if (bytesRead <= 0)
                {
                    break;
                }

                // Guarantee stereo 16-bit frame alignment (multiple of 4 bytes).
                int alignedBytes = bytesRead & ~3;
                if (alignedBytes <= 0)
                {
                    break;
                }

                EnsureCapacity(ref outputPcm16Buffer, totalWrittenBytes + alignedBytes);
                System.Buffer.BlockCopy(_drainChunkBuffer, 0, outputPcm16Buffer, totalWrittenBytes, alignedBytes);
                totalWrittenBytes += alignedBytes;
            }

            return totalWrittenBytes;
        }

        private static ISampleProvider CreateSampleProvider(IWaveProvider source, WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return new WaveToSampleProvider(source);
            }

            return format.BitsPerSample switch
            {
                16 => new Pcm16BitToSampleProvider(source),
                24 => new Pcm24BitToSampleProvider(source),
                32 => new Pcm32BitToSampleProvider(source),
                8 => new Pcm8BitToSampleProvider(source),
                _ => throw new NotSupportedException(
                    $"Nieobsługiwany format audio WASAPI: {format.Encoding}, {format.BitsPerSample}-bit, {format.SampleRate} Hz, {format.Channels} ch.")
            };
        }

        private static bool IsIeeeFloatFormat(WaveFormat format)
        {
            if (format.BitsPerSample != 32)
            {
                return false;
            }

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

            return format.Encoding != WaveFormatEncoding.Pcm;
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
        private long _lastOutputTimestamp100Ns;
        private int _disposed;

        public AacMftEncoder(
            MediaTrackType trackType,
            long initialLastTimestamp100Ns,
            Action<byte[], int, long, long> onEncodedPacket,
            Action<ReadOnlyMemory<byte>>? onUserData)
        {
            _trackType = trackType;
            _lastOutputTimestamp100Ns = initialLastTimestamp100Ns;
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
                    long sampleTime100Ns = _accumulatorOrigin100Ns + FramesTo100Ns(_accumulatorStartFrameIndex);
                    long nextTime100Ns = _accumulatorOrigin100Ns + FramesTo100Ns(_accumulatorStartFrameIndex + AacFrameSampleCount);
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
            var candidates = new List<IMFActivate>();
            nint activatesPtr = nint.Zero;
            uint count = 0;

            try
            {
                MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.AudioEncoder,
                    flags,
                    inputTypeInfo,
                    outputTypeInfo,
                    out activatesPtr,
                    out count);

                if (activatesPtr != nint.Zero && count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        nint itemPtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                        if (itemPtr != nint.Zero)
                        {
                            candidates.Add(new IMFActivate(itemPtr));
                        }
                    }
                }
            }
            finally
            {
                if (activatesPtr != nint.Zero)
                {
                    Marshal.FreeCoTaskMem(activatesPtr);
                }
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

    /// <summary>
    /// Time-aligns normalized 48 kHz 16-bit Stereo PCM chunks from System Audio and Microphone streams,
    /// mixes aligned samples with saturating 16-bit clamping, and encodes the 3rd default
    /// <see cref="MediaTrackType.CombinedMixAudio"/> AAC stream into <see cref="MediaRingBuffer"/>.
    /// </summary>
    private sealed class CombinedAudioMixPipeline : IDisposable
    {
        private const int MixChunkFrames = AacFrameSampleCount;
        private const int MaxLeadFrames = 3840;      // 80 ms at 48 kHz
        private const int LeadCushionFrames = 1920;  // 40 ms at 48 kHz
        private const int LargeJumpFrames = TargetSampleRate * 2;
        private const int MaxChunksPerPass = 16;

        private readonly MediaRingBuffer _ringBuffer;
        private readonly object _mixLock = new();
        private readonly TrackPcmTimelineBuffer _systemBuffer = new();
        private readonly TrackPcmTimelineBuffer _micBuffer = new();
        private readonly AacMftEncoder _aacEncoder;

        private readonly short[] _sysScratch = new short[MixChunkFrames * TargetChannels];
        private readonly short[] _micScratch = new short[MixChunkFrames * TargetChannels];
        private readonly byte[] _mixedPcmBytes = new byte[AacFrameByteCount];

        private bool _mixCursorInitialized;
        private long _mixCursorFrame;
        private long _lastEncodedTimestamp100Ns = -1;
        private int _disposed;

        public CombinedAudioMixPipeline(MediaRingBuffer ringBuffer, long minimumTimestamp100Ns)
        {
            _ringBuffer = ringBuffer;
            _aacEncoder = new AacMftEncoder(
                MediaTrackType.CombinedMixAudio,
                initialLastTimestamp100Ns: minimumTimestamp100Ns > 0 ? minimumTimestamp100Ns - 1 : -1L,
                onEncodedPacket: PushEncodedPacket,
                onUserData: userData => _ringBuffer.SetAudioUserData(MediaTrackType.CombinedMixAudio, userData.Span));
        }

        public long LastEncodedTimestamp100Ns => Volatile.Read(ref _lastEncodedTimestamp100Ns);

        public void SubmitSystemPcm(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns)
        {
            if (Volatile.Read(ref _disposed) != 0 || pcmBytes.IsEmpty)
            {
                return;
            }

            lock (_mixLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _systemBuffer.Write(pcmBytes, startTimestamp100Ns);
                MixAndEncodeAvailableLocked();
            }
        }

        public void SubmitMicrophonePcm(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns)
        {
            if (Volatile.Read(ref _disposed) != 0 || pcmBytes.IsEmpty)
            {
                return;
            }

            lock (_mixLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _micBuffer.Write(pcmBytes, startTimestamp100Ns);
                MixAndEncodeAvailableLocked();
            }
        }

        private void MixAndEncodeAvailableLocked()
        {
            bool sysHasData = _systemBuffer.HasData;
            bool micHasData = _micBuffer.HasData;
            if (!sysHasData && !micHasData)
            {
                return;
            }

            long maxEndFrame = Math.Max(
                sysHasData ? _systemBuffer.EndFrame : 0L,
                micHasData ? _micBuffer.EndFrame : 0L);

            if (!_mixCursorInitialized)
            {
                if (sysHasData && micHasData)
                {
                    long minStart = Math.Min(_systemBuffer.StartFrame, _micBuffer.StartFrame);
                    _mixCursorFrame = Math.Max(minStart, maxEndFrame - MaxLeadFrames);
                    _mixCursorInitialized = true;
                }
                else
                {
                    TrackPcmTimelineBuffer activeBuffer = sysHasData ? _systemBuffer : _micBuffer;
                    if (activeBuffer.EndFrame - activeBuffer.StartFrame < MaxLeadFrames)
                    {
                        return;
                    }

                    _mixCursorFrame = activeBuffer.StartFrame;
                    _mixCursorInitialized = true;
                }
            }

            long minActiveStart = (sysHasData, micHasData) switch
            {
                (true, true) => Math.Min(_systemBuffer.StartFrame, _micBuffer.StartFrame),
                (true, false) => _systemBuffer.StartFrame,
                (false, true) => _micBuffer.StartFrame,
                _ => _mixCursorFrame
            };

            if (minActiveStart - _mixCursorFrame > LargeJumpFrames)
            {
                _aacEncoder.ResetPendingPcm();
                _mixCursorFrame = minActiveStart;
            }

            long timeoutReadyFrame = (maxEndFrame - _mixCursorFrame >= MaxLeadFrames)
                ? maxEndFrame - LeadCushionFrames
                : _mixCursorFrame;

            long mixUpToFrame = (sysHasData && micHasData)
                ? Math.Max(Math.Min(_systemBuffer.EndFrame, _micBuffer.EndFrame), timeoutReadyFrame)
                : timeoutReadyFrame;

            Span<short> outSamples = MemoryMarshal.Cast<byte, short>(_mixedPcmBytes);

            for (int chunk = 0; chunk < MaxChunksPerPass && (mixUpToFrame - _mixCursorFrame) >= MixChunkFrames; chunk++)
            {
                _systemBuffer.ReadOrZero(_mixCursorFrame, MixChunkFrames, _sysScratch);
                _micBuffer.ReadOrZero(_mixCursorFrame, MixChunkFrames, _micScratch);

                for (int i = 0; i < outSamples.Length; i++)
                {
                    outSamples[i] = (short)Math.Clamp(_sysScratch[i] + _micScratch[i], short.MinValue, short.MaxValue);
                }

                _aacEncoder.FeedPcm(_mixedPcmBytes, trackOrigin100Ns: 0L, firstSampleFrameIndex: _mixCursorFrame);
                _mixCursorFrame += MixChunkFrames;
            }
        }

        private void PushEncodedPacket(byte[] rentedBuffer, int length, long timestamp100Ns, long duration100Ns)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                return;
            }

            Volatile.Write(ref _lastEncodedTimestamp100Ns, timestamp100Ns);
            _ringBuffer.AddAudioPacket(MediaTrackType.CombinedMixAudio, rentedBuffer, length, timestamp100Ns, duration100Ns);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_mixLock)
            {
                _aacEncoder.Dispose();
            }
        }

        private sealed class TrackPcmTimelineBuffer
        {
            private const int CapacityFrames = TargetSampleRate * 2; // 2 seconds at 48 kHz
            private readonly short[] _ring = new short[CapacityFrames * TargetChannels];

            public bool HasData { get; private set; }

            public long StartFrame { get; private set; }

            public long EndFrame { get; private set; }

            public void Write(ReadOnlySpan<byte> pcmBytes, long startTimestamp100Ns)
            {
                int frameCount = pcmBytes.Length / TargetBlockAlignment;
                if (frameCount <= 0)
                {
                    return;
                }

                long incomingFrame = Math.Max(0L, Ticks100NsToFrames(startTimestamp100Ns));
                ReadOnlySpan<short> srcSamples = MemoryMarshal.Cast<byte, short>(
                    pcmBytes.Slice(0, frameCount * TargetBlockAlignment));

                if (frameCount > CapacityFrames)
                {
                    int trimFrames = frameCount - CapacityFrames;
                    srcSamples = srcSamples.Slice(trimFrames * TargetChannels);
                    incomingFrame += trimFrames;
                    frameCount = CapacityFrames;
                }

                if (!HasData)
                {
                    HasData = true;
                    StartFrame = incomingFrame;
                    EndFrame = incomingFrame;
                }
                else
                {
                    long diff = incomingFrame - EndFrame;
                    if (Math.Abs(diff) <= 4)
                    {
                        incomingFrame = EndFrame;
                    }
                    else if (diff > 4)
                    {
                        if (diff <= CapacityFrames / 2)
                        {
                            ZeroFillRing(EndFrame, (int)diff);
                            EndFrame = incomingFrame;
                            StartFrame = Math.Max(StartFrame, EndFrame - CapacityFrames);
                        }
                        else
                        {
                            StartFrame = incomingFrame;
                            EndFrame = incomingFrame;
                        }
                    }
                    else
                    {
                        long overlap = -diff;
                        if (overlap >= frameCount)
                        {
                            return;
                        }

                        int skipFrames = (int)overlap;
                        srcSamples = srcSamples.Slice(skipFrames * TargetChannels);
                        frameCount -= skipFrames;
                        incomingFrame = EndFrame;
                    }
                }

                int startSlot = (int)(incomingFrame % CapacityFrames);
                int firstFrames = Math.Min(frameCount, CapacityFrames - startSlot);
                srcSamples.Slice(0, firstFrames * TargetChannels)
                    .CopyTo(_ring.AsSpan(startSlot * TargetChannels));

                int remainingFrames = frameCount - firstFrames;
                if (remainingFrames > 0)
                {
                    srcSamples.Slice(firstFrames * TargetChannels, remainingFrames * TargetChannels)
                        .CopyTo(_ring.AsSpan(0));
                }

                EndFrame = incomingFrame + frameCount;
                StartFrame = Math.Max(StartFrame, EndFrame - CapacityFrames);
            }

            public void ReadOrZero(long frameIndex, int frameCount, Span<short> dest)
            {
                dest.Slice(0, frameCount * TargetChannels).Clear();

                if (HasData && frameIndex < EndFrame && frameIndex + frameCount > StartFrame)
                {
                    long copyStart = Math.Max(frameIndex, StartFrame);
                    long copyEnd = Math.Min(frameIndex + frameCount, EndFrame);
                    int framesToCopy = (int)(copyEnd - copyStart);

                    if (framesToCopy > 0)
                    {
                        int destOffsetFrames = (int)(copyStart - frameIndex);
                        int ringSlot = (int)(copyStart % CapacityFrames);
                        int firstFrames = Math.Min(framesToCopy, CapacityFrames - ringSlot);

                        _ring.AsSpan(ringSlot * TargetChannels, firstFrames * TargetChannels)
                            .CopyTo(dest.Slice(destOffsetFrames * TargetChannels));

                        int remainingFrames = framesToCopy - firstFrames;
                        if (remainingFrames > 0)
                        {
                            _ring.AsSpan(0, remainingFrames * TargetChannels)
                                .CopyTo(dest.Slice((destOffsetFrames + firstFrames) * TargetChannels));
                        }
                    }
                }

                if (HasData)
                {
                    StartFrame = Math.Max(StartFrame, Math.Min(EndFrame, frameIndex + frameCount));
                }
            }

            private void ZeroFillRing(long startFrame, int frameCount)
            {
                int slot = (int)(startFrame % CapacityFrames);
                int firstFrames = Math.Min(frameCount, CapacityFrames - slot);
                _ring.AsSpan(slot * TargetChannels, firstFrames * TargetChannels).Clear();

                int remainingFrames = frameCount - firstFrames;
                if (remainingFrames > 0)
                {
                    _ring.AsSpan(0, remainingFrames * TargetChannels).Clear();
                }
            }
        }
    }
}
