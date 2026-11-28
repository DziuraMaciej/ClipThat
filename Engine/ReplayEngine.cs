using System.Diagnostics;
using System.IO;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Capture;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Encoding;
using ClipThat.Engine.Interop;
using ClipThat.Engine.Muxing;
using ClipThat.Engine.Video;
using ClipThat.Models;
using ClipThat.Services;
using Vortice.Direct3D11;

namespace ClipThat.Engine;

/// <summary>
/// Universal user-mode GPU screen capture, dual-track WASAPI AAC audio capture, 3-tier Media Foundation
/// H.264 encoding (NVIDIA NVENC / AMD AMF / Intel QuickSync / Software MFT) with 1.5s keyframe watchdog
/// and automatic tier downgrade, RAM circular buffer, and native Media Foundation + portable FFmpeg MP4 muxing.
/// </summary>
public sealed class ReplayEngine : IReplayEngine
{
    private const int KeyframeWatchdogTimeoutMs = 1500;
    private const int MaxIdleWatchdogDeferrals = 6;
    private const int StartupPrimingIntervalMs = 60;
    private const int MaxStartupPrimingFrames = 6;
    private const int StaticDesktopKeepAliveIntervalMs = 1500;
    private static readonly int[] RecoveryRetryDelaysMs = { 0, 400, 900, 1600 };

    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly Mp4ReplayMuxer _mp4Muxer = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaRingBuffer _ringBuffer = new();

    private AppSettings? _currentSettings;
    private CapturePipelineSession? _activeSession;
    private string? _currentStatus;
    private string? _lastError;
    private int _statusLevel = (int)EngineStatusLevel.Paused;
    private int _consecutiveDxgiFaults;
    private long _pipelineGeneration;
    private volatile bool _isRecording;
    private int _disposed;

    public ReplayEngine(ResourceEstimatorService resourceEstimatorService)
    {
        ArgumentNullException.ThrowIfNull(resourceEstimatorService);
        _resourceEstimatorService = resourceEstimatorService;
    }

    public bool IsRecording => _isRecording;

    public EngineStatusLevel StatusLevel => (EngineStatusLevel)Volatile.Read(ref _statusLevel);

    public string? CurrentStatus => Volatile.Read(ref _currentStatus);

    public string? LastError => Volatile.Read(ref _lastError);

    public event Action<string>? StatusChanged;

    public event Action<string>? ErrorOccurred;

    public event Action<string>? ReplaySaved;

    public async Task StartAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string? statusMessage = null;
        string? startupError = null;
        CapturePipelineSession? startedSession = null;
        IVideoEncoder? startedEncoder = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            AppSettings normalized = settings.Clone();
            normalized.Normalize();

            if (_activeSession is not null)
            {
                TeardownActiveSessionLocked();
            }

            Interlocked.Increment(ref _pipelineGeneration);
            _ringBuffer.Clear();
            _ringBuffer.SetBufferDuration(normalized.BufferDurationMinutes);
            _currentSettings = normalized;
            Interlocked.Exchange(ref _consecutiveDxgiFaults, 0);

            _activeSession = StartCapturePipelineLocked(normalized, EncoderNegotiationTier.HardwareGpuZeroCopy, preferDxgiDuplication: true);
            _isRecording = true;

            IVideoEncoder activeEncoder = _activeSession.Encoder
                ?? throw new InvalidOperationException("Koder wideo nie został zainicjalizowany.");

            statusMessage = BuildActiveStatusMessage(
                normalized,
                _activeSession.Capture,
                activeEncoder,
                _activeSession.AudioCapture);

            Volatile.Write(ref _lastError, null);
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Recording);
            Volatile.Write(ref _currentStatus, statusMessage);

            startedSession = _activeSession;
            startedEncoder = activeEncoder;
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            startupError = $"Błąd uruchamiania przechwytywania: {ex.Message}";
            EngineLogger.Error("ReplayEngine", startupError, ex);
            Volatile.Write(ref _lastError, startupError);
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Error);
            Volatile.Write(ref _currentStatus, startupError);
            throw;
        }
        finally
        {
            _gate.Release();

            if (startupError is not null)
            {
                StatusChanged?.Invoke(startupError);
                ErrorOccurred?.Invoke(startupError);
            }
        }

        if (startedSession is not null && startedEncoder is not null)
        {
            ArmKeyframeWatchdog(startedSession, startedEncoder);
        }

        if (statusMessage is not null)
        {
            StatusChanged?.Invoke(statusMessage);
        }
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool wasRecording;

        if (!await TryEnterGateAsync().ConfigureAwait(false))
        {
            return;
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            wasRecording = _isRecording || _activeSession is not null;
            Interlocked.Increment(ref _pipelineGeneration);
            TeardownActiveSessionLocked();
            _ringBuffer.Clear();
            _isRecording = false;
        }
        finally
        {
            ReleaseGateSafe();
        }

        if (wasRecording)
        {
            const string pausedMessage = "Nagrywanie wstrzymane";
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Paused);
            Volatile.Write(ref _currentStatus, pausedMessage);
            StatusChanged?.Invoke(pausedMessage);
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string? statusMessage = null;
        string? updateError = null;
        CapturePipelineSession? restartedSession = null;
        IVideoEncoder? restartedEncoder = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            AppSettings updated = settings.Clone();
            updated.Normalize();

            AppSettings? previous = _currentSettings;
            _currentSettings = updated;
            _ringBuffer.SetBufferDuration(updated.BufferDurationMinutes);

            if (!_isRecording || _activeSession is null)
            {
                return;
            }

            bool requiresPipelineRestart =
                previous is null ||
                previous.Resolution != updated.Resolution ||
                previous.Fps != updated.Fps ||
                !string.Equals(
                    previous.SelectedMonitorDeviceName,
                    updated.SelectedMonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    previous.SystemAudioDeviceId,
                    updated.SystemAudioDeviceId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    previous.MicrophoneDeviceId,
                    updated.MicrophoneDeviceId,
                    StringComparison.OrdinalIgnoreCase);

            if (requiresPipelineRestart)
            {
                Interlocked.Increment(ref _pipelineGeneration);
                TeardownActiveSessionLocked();
                _ringBuffer.Clear();
                Interlocked.Exchange(ref _consecutiveDxgiFaults, 0);

                _activeSession = StartCapturePipelineLocked(updated, EncoderNegotiationTier.HardwareGpuZeroCopy, preferDxgiDuplication: true);
                _isRecording = true;

                restartedSession = _activeSession;
                restartedEncoder = _activeSession.Encoder;
            }

            IVideoEncoder? currentEncoder = _activeSession.Encoder;
            if (currentEncoder is not null)
            {
                statusMessage = BuildActiveStatusMessage(
                    updated,
                    _activeSession.Capture,
                    currentEncoder,
                    _activeSession.AudioCapture);

                Volatile.Write(ref _lastError, null);
                Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Recording);
                Volatile.Write(ref _currentStatus, statusMessage);
            }
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            updateError = $"Błąd aktualizacji potoku wideo: {ex.Message}";
            EngineLogger.Error("ReplayEngine", updateError, ex);
            Volatile.Write(ref _lastError, updateError);
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Error);
            Volatile.Write(ref _currentStatus, updateError);
            throw;
        }
        finally
        {
            _gate.Release();

            if (updateError is not null)
            {
                StatusChanged?.Invoke(updateError);
                ErrorOccurred?.Invoke(updateError);
            }
        }

        if (restartedSession is not null && restartedEncoder is not null)
        {
            ArmKeyframeWatchdog(restartedSession, restartedEncoder);
        }

        if (statusMessage is not null)
        {
            StatusChanged?.Invoke(statusMessage);
        }
    }

    /// <summary>
    /// Captures a non-blocking snapshot of the ring buffer from the oldest valid IDR keyframe
    /// and remuxes compressed H.264 video + dual-track AAC audio into an <c>.mp4</c> file on a background thread
    /// without interrupting ongoing capture.
    /// </summary>
    public async Task<string?> SaveReplayAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        string? outputDirectory;
        bool isRecordingSnapshot;
        int targetWidth = 0;
        int targetHeight = 0;
        int targetFps = 60;
        uint targetBitrateBps = 0;
        EncodedPacketSnapshot? snapshot = null;

        if (!await TryEnterGateAsync().ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            isRecordingSnapshot = _isRecording;
            outputDirectory = _currentSettings?.OutputDirectory;

            if (isRecordingSnapshot && !string.IsNullOrWhiteSpace(outputDirectory))
            {
                IVideoEncoder? activeEncoder = _activeSession?.Encoder;
                if (activeEncoder is not null)
                {
                    targetWidth = activeEncoder.OutputWidth;
                    targetHeight = activeEncoder.OutputHeight;
                    targetFps = activeEncoder.TargetFps;
                    targetBitrateBps = activeEncoder.TargetBitrateBps;
                }
                else if (_currentSettings is not null)
                {
                    (targetWidth, targetHeight) = GpuVideoProcessor.GetTargetDimensions(_currentSettings.Resolution);
                    targetFps = _currentSettings.Fps;
                    double bitrateMbps = _resourceEstimatorService.GetVideoBitrateMbps(_currentSettings.Resolution, targetFps);
                    targetBitrateBps = (uint)Math.Clamp((long)Math.Round(bitrateMbps * 1_000_000.0), 250_000L, 100_000_000L);
                }

                // Retain packet handles atomically inside _gate so resolution/FPS metadata always matches
                // the buffered packets and Dispose()/UpdateSettingsAsync cannot race with SnapshotPackets().
                snapshot = _ringBuffer.SnapshotPackets();
            }
        }
        finally
        {
            ReleaseGateSafe();
        }

        if (!isRecordingSnapshot || string.IsNullOrWhiteSpace(outputDirectory))
        {
            snapshot?.Dispose();
            const string pausedNotice = "Nagrywanie jest wstrzymane — brak bufora do zapisania";
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Info);
            Volatile.Write(ref _currentStatus, pausedNotice);
            StatusChanged?.Invoke(pausedNotice);
            return null;
        }

        if (snapshot is null || snapshot.Count == 0)
        {
            snapshot?.Dispose();
            const string noKeyframeNotice = "Bufor powtórki nie zawiera jeszcze gotowej klatki kluczowej";
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Info);
            Volatile.Write(ref _currentStatus, noKeyframeNotice);
            StatusChanged?.Invoke(noKeyframeNotice);
            return null;
        }

        try
        {
            string outputFilePath = await Task.Run(() =>
            {
                using (snapshot)
                {
                    return _mp4Muxer.MuxToMp4(
                        snapshot,
                        outputDirectory,
                        targetWidth,
                        targetHeight,
                        targetFps,
                        targetBitrateBps);
                }
            }).ConfigureAwait(false);

            string fileName = Path.GetFileName(outputFilePath);
            string savedMessage = $"Zapisano powtórkę: {fileName}";
            Volatile.Write(ref _lastError, null);
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Info);
            Volatile.Write(ref _currentStatus, savedMessage);
            StatusChanged?.Invoke(savedMessage);
            ReplaySaved?.Invoke(outputFilePath);
            return outputFilePath;
        }
        catch (Exception ex)
        {
            string errorMessage = $"Błąd zapisu powtórki: {ex.Message}";
            EngineLogger.Error("ReplayEngine", errorMessage, ex);
            Volatile.Write(ref _lastError, errorMessage);
            Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Error);
            Volatile.Write(ref _currentStatus, errorMessage);
            StatusChanged?.Invoke(errorMessage);
            ErrorOccurred?.Invoke(errorMessage);
            throw;
        }
    }

    private CapturePipelineSession StartCapturePipelineLocked(
        AppSettings settings,
        EncoderNegotiationTier minimumTier,
        bool preferDxgiDuplication = true)
    {
        IFrameCaptureSource? captureSource = null;
        GpuVideoProcessor? gpuProcessor = null;
        IVideoEncoder? encoder = null;
        DualAudioCaptureService? audioCapture = null;

        try
        {
            var sharedSessionClock = new Stopwatch();
            CapturePipelineSession? createdSession = null;

            Action<ID3D11Texture2D, long, long> onFrameCaptured = (bgraTexture, timestamp100Ns, frameDuration100Ns) =>
            {
                createdSession?.HandleCapturedFrame(bgraTexture, timestamp100Ns, frameDuration100Ns);
            };
            Action onSessionClosed = () =>
            {
                if (createdSession is not null)
                {
                    OnSessionClosed(createdSession);
                }
            };
            Action<Exception> onCaptureError = ex =>
            {
                if (createdSession is not null)
                {
                    OnActivePipelineFault(createdSession, ex, isEncoderFault: false);
                }
            };
            Action<Exception> onEncoderError = ex =>
            {
                if (createdSession is not null)
                {
                    OnActivePipelineFault(createdSession, ex, isEncoderFault: true);
                }
            };

            if (preferDxgiDuplication)
            {
                try
                {
                    captureSource = new DxgiFrameCapture(
                        settings.SelectedMonitorDeviceName,
                        settings.Fps,
                        sharedSessionClock,
                        onFrameCaptured,
                        onSessionClosed,
                        onCaptureError);
                }
                catch (Exception ex)
                {
                    EngineLogger.Warn(
                        "ReplayEngine",
                        $"DXGI Desktop Duplication initialization failed ({ex.Message}); falling back to Windows.Graphics.Capture (WGC).");
                    captureSource = null;
                }
            }

            captureSource ??= new WgcFrameCapture(
                settings.SelectedMonitorDeviceName,
                settings.Fps,
                sharedSessionClock,
                onFrameCaptured,
                onSessionClosed,
                onCaptureError);

            gpuProcessor = new GpuVideoProcessor(
                captureSource.Device,
                captureSource.ImmediateContext,
                settings.Resolution,
                settings.Fps,
                captureSource.AdapterName,
                captureSource.AdapterLuid,
                ownsDevice: false,
                sourceWidth: captureSource.CaptureWidth,
                sourceHeight: captureSource.CaptureHeight);

            encoder = CreateEncoder(gpuProcessor, settings, minimumTier, onEncoderError);

            try
            {
                audioCapture = new DualAudioCaptureService(
                    sharedSessionClock,
                    _ringBuffer,
                    settings.SystemAudioDeviceId,
                    settings.MicrophoneDeviceId,
                    onStatusChanged: OnAudioStatusChanged);
            }
            catch (Exception ex)
            {
                EngineLogger.Warn(
                    "ReplayEngine",
                    $"DualAudioCaptureService construction failed ({ex.Message}); continuing video-only recording.",
                    ex);
                audioCapture = null;
            }

            var pipeline = new CapturePipelineSession(
                settings.Fps,
                sharedSessionClock,
                captureSource,
                gpuProcessor,
                encoder,
                audioCapture,
                onEncodeError: onEncoderError);

            createdSession = pipeline;
            pipeline.Start();

            EngineLogger.Info(
                "ReplayEngine",
                $"Capture pipeline started: Backend=\"{captureSource.CaptureBackendName}\" | " +
                $"Monitor={settings.SelectedMonitorDeviceName ?? "(Primary)"} | " +
                $"GPU=\"{captureSource.AdapterName}\" | Encoder=\"{encoder.EncoderName}\" ({encoder.ActiveTier}) | " +
                $"Output={encoder.OutputWidth}x{encoder.OutputHeight}@{encoder.TargetFps}fps");

            return pipeline;
        }
        catch
        {
            audioCapture?.Dispose();
            encoder?.Dispose();
            gpuProcessor?.Dispose();
            captureSource?.Dispose();
            throw;
        }
    }

    private IVideoEncoder CreateEncoder(
        GpuVideoProcessor gpuProcessor,
        AppSettings settings,
        EncoderNegotiationTier minimumTier,
        Action<Exception> onEncoderError)
    {
        // On AMD GPUs, prefer direct AMD AMF runtime (amfrt64.dll) zero-copy D3D11 encoding over
        // Media Foundation's AMDh264Encoder MFT, which stalls on newer Adrenalin/RDNA4 drivers.
        if (minimumTier == EncoderNegotiationTier.HardwareGpuZeroCopy &&
            gpuProcessor.SupportsHardwareVideoProcessor &&
            AmfH264Encoder.ShouldTryForAdapter(gpuProcessor.AdapterName))
        {
            try
            {
                return new AmfH264Encoder(
                    gpuProcessor,
                    settings.Resolution,
                    settings.Fps,
                    _resourceEstimatorService,
                    onPacketEncoded: _ringBuffer.AddPacket,
                    onSequenceHeader: header => _ringBuffer.SetVideoSequenceHeader(header.Span),
                    onEncoderError: onEncoderError);
            }
            catch (Exception ex)
            {
                EngineLogger.Warn(
                    "ReplayEngine",
                    $"Native AMD AMF encoder failed to start ({ex.Message}); falling back to Media Foundation MFT.");
            }
        }

        return new MfH264Encoder(
            gpuProcessor,
            settings.Resolution,
            settings.Fps,
            _resourceEstimatorService,
            onPacketEncoded: _ringBuffer.AddPacket,
            onSequenceHeader: header => _ringBuffer.SetVideoSequenceHeader(header.Span),
            onEncoderError: onEncoderError,
            minimumTier: minimumTier);
    }

    /// <summary>
    /// Arms a 1.5-second watchdog verifying that <paramref name="monitoredEncoder"/> emits at least
    /// one H.264 keyframe (<c>IsKeyFrame == true</c>) once frames have been submitted. If zero keyframes
    /// arrive in time despite submitted frames, automatically steps down <c>Tier 1 -&gt; Tier 2 -&gt; Tier 3</c>.
    /// </summary>
    private void ArmKeyframeWatchdog(
        CapturePipelineSession session,
        IVideoEncoder monitoredEncoder,
        int idleDeferralsRemaining = MaxIdleWatchdogDeferrals)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(KeyframeWatchdogTimeoutMs).ConfigureAwait(false);
                await CheckKeyframeWatchdogAsync(session, monitoredEncoder, idleDeferralsRemaining).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLogger.Warn("ReplayEngine", $"Keyframe watchdog task error: {ex.Message}", ex);
            }
        });
    }

    private async Task CheckKeyframeWatchdogAsync(
        CapturePipelineSession session,
        IVideoEncoder monitoredEncoder,
        int idleDeferralsRemaining)
    {
        if (Volatile.Read(ref _disposed) != 0 || monitoredEncoder.HasEmittedKeyframe)
        {
            return;
        }

        string? updatedStatus = null;
        string? fatalError = null;
        IVideoEncoder? nextEncoderToMonitor = null;
        bool deferWatchdog = false;

        if (!await TryEnterGateAsync().ConfigureAwait(false))
        {
            return;
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_isRecording ||
                !ReferenceEquals(_activeSession, session) ||
                !ReferenceEquals(session.Encoder, monitoredEncoder) ||
                _currentSettings is null)
            {
                return;
            }

            if (monitoredEncoder.HasEmittedKeyframe)
            {
                return;
            }

            EncoderNegotiationTier currentTier = monitoredEncoder.ActiveTier;
            string currentEncoderName = monitoredEncoder.EncoderName;
            int wgcFrames = session.Capture.CapturedFrameCount;
            int submittedFrames = monitoredEncoder.SubmittedFrameCount;
            int encodedPackets = monitoredEncoder.EncodedPacketCount;
            int needInputEvents = monitoredEncoder.NeedInputEventCount;
            int haveOutputEvents = monitoredEncoder.HaveOutputEventCount;

            // Do not falsely downgrade a healthy hardware GPU encoder when 0 frames were captured/submitted
            // (for example on a locked desktop, UAC screen, or sleeping monitor during Windows autostart).
            if (submittedFrames == 0)
            {
                if (idleDeferralsRemaining > 0)
                {
                    EngineLogger.Info(
                        "ReplayEngine",
                        $"Keyframe watchdog deferred ({idleDeferralsRemaining} left): 0 frames submitted yet " +
                        $"(captureFrames={wgcFrames}, tier={currentTier}, encoder=\"{currentEncoderName}\").");
                    deferWatchdog = true;
                }
                else
                {
                    EngineLogger.Warn(
                        "ReplayEngine",
                        $"Keyframe watchdog: still 0 frames submitted after deferrals (captureFrames={wgcFrames}, tier={currentTier}); keeping current encoder.");
                }

                return;
            }

            EncoderNegotiationTier? nextTier = GetNextDowngradeTier(currentTier);
            if (!nextTier.HasValue)
            {
                EngineLogger.Warn(
                    "ReplayEngine",
                    $"Keyframe watchdog timeout ({KeyframeWatchdogTimeoutMs}ms) on final tier {currentTier} " +
                    $"(\"{currentEncoderName}\"): 0 keyframes emitted (WGC frames={wgcFrames}, submitted={submittedFrames}, " +
                    $"packets={encodedPackets}, needInput={needInputEvents}, haveOutput={haveOutputEvents}).");
                return;
            }

            EngineLogger.Warn(
                "ReplayEngine",
                $"Keyframe watchdog timeout ({KeyframeWatchdogTimeoutMs}ms): Tier {currentTier} (\"{currentEncoderName}\") " +
                $"produced 0 keyframes (WGC frames={wgcFrames}, submitted={submittedFrames}, packets={encodedPackets}, " +
                $"needInput={needInputEvents}, haveOutput={haveOutputEvents}). " +
                $"Disposing {currentTier} encoder and stepping down to {nextTier.Value}.");

            try
            {
                _ringBuffer.Clear();

                Action<Exception> sessionEncoderError = ex => OnActivePipelineFault(session, ex, isEncoderFault: true);
                IVideoEncoder replacement;
                try
                {
                    replacement = session.ReplaceEncoder(
                        () => CreateEncoder(session.GpuProcessor, _currentSettings, nextTier.Value, sessionEncoderError));
                }
                catch (Exception tierEx) when (nextTier.Value != EncoderNegotiationTier.SoftwareFallback)
                {
                    EngineLogger.Warn(
                        "ReplayEngine",
                        $"Step-down to {nextTier.Value} failed ({tierEx.Message}); retrying directly with SoftwareFallback.",
                        tierEx);
                    replacement = session.ReplaceEncoder(
                        () => CreateEncoder(session.GpuProcessor, _currentSettings, EncoderNegotiationTier.SoftwareFallback, sessionEncoderError));
                }

                EngineLogger.Info(
                    "ReplayEngine",
                    $"Automatic encoder tier downgrade complete: {currentTier} (\"{currentEncoderName}\") -> " +
                    $"{replacement.ActiveTier} (\"{replacement.EncoderName}\") on GPU \"{session.Capture.AdapterName}\".");

                updatedStatus = BuildActiveStatusMessage(
                    _currentSettings,
                    session.Capture,
                    replacement,
                    session.AudioCapture);

                Volatile.Write(ref _lastError, null);
                Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Recording);
                Volatile.Write(ref _currentStatus, updatedStatus);
                nextEncoderToMonitor = replacement;
            }
            catch (Exception ex)
            {
                TeardownActiveSessionLocked();
                _isRecording = false;
                fatalError = $"Błąd automatycznego przełączania kodera ({nextTier.Value}): {ex.Message}";
                updatedStatus = fatalError;
                EngineLogger.Error("ReplayEngine", fatalError, ex);
                Volatile.Write(ref _lastError, fatalError);
                Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Error);
                Volatile.Write(ref _currentStatus, fatalError);
            }
        }
        finally
        {
            ReleaseGateSafe();
        }

        if (deferWatchdog)
        {
            ArmKeyframeWatchdog(session, monitoredEncoder, idleDeferralsRemaining - 1);
            return;
        }

        if (nextEncoderToMonitor is not null)
        {
            ArmKeyframeWatchdog(session, nextEncoderToMonitor);
        }

        if (updatedStatus is not null)
        {
            StatusChanged?.Invoke(updatedStatus);
        }

        if (fatalError is not null)
        {
            ErrorOccurred?.Invoke(fatalError);
        }
    }

    private static EncoderNegotiationTier? GetNextDowngradeTier(EncoderNegotiationTier currentTier) => currentTier switch
    {
        EncoderNegotiationTier.HardwareGpuZeroCopy => EncoderNegotiationTier.HardwareCpuBuffer,
        EncoderNegotiationTier.HardwareCpuBuffer => EncoderNegotiationTier.SoftwareFallback,
        _ => null
    };

    private void OnSessionClosed(CapturePipelineSession closedSession)
    {
        bool preferDxgi = ShouldRetryDxgiAfterCaptureInterruption(closedSession);
        LaunchRecoveryAsync(
            closedSession,
            "Monitor zmienił stan — ponowne uruchamianie przechwytywania...",
            minimumTier: EncoderNegotiationTier.HardwareGpuZeroCopy,
            preferDxgiDuplication: preferDxgi);
    }

    private void OnActivePipelineFault(CapturePipelineSession faultedSession, Exception exception, bool isEncoderFault)
    {
        EngineLogger.Error("ReplayEngine", $"Active pipeline fault (encoderFault={isEncoderFault}): {exception.Message}", exception);

        EncoderNegotiationTier recoveryTier = EncoderNegotiationTier.HardwareGpuZeroCopy;
        bool preferDxgi = faultedSession.Capture is DxgiFrameCapture;
        if (isEncoderFault)
        {
            IVideoEncoder? currentEncoder = faultedSession.Encoder;
            if (currentEncoder is not null)
            {
                recoveryTier = GetNextDowngradeTier(currentEncoder.ActiveTier) ?? EncoderNegotiationTier.SoftwareFallback;
            }
        }
        else
        {
            preferDxgi = ShouldRetryDxgiAfterCaptureInterruption(faultedSession);
        }

        LaunchRecoveryAsync(
            faultedSession,
            $"Błąd potoku wideo ({exception.Message}) — próba wznowienia ({recoveryTier})...",
            minimumTier: recoveryTier,
            preferDxgiDuplication: preferDxgi);
    }

    private void LaunchRecoveryAsync(
        CapturePipelineSession session,
        string notice,
        EncoderNegotiationTier minimumTier,
        bool preferDxgiDuplication)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverFromSessionInterruptionAsync(session, notice, minimumTier, preferDxgiDuplication)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLogger.Error("ReplayEngine", $"Unhandled recovery exception: {ex.Message}", ex);
            }
        });
    }

    private bool ShouldRetryDxgiAfterCaptureInterruption(CapturePipelineSession session)
    {
        if (session.Capture is not DxgiFrameCapture)
        {
            return true;
        }

        if (session.Capture.CapturedFrameCount == 0)
        {
            Interlocked.Exchange(ref _consecutiveDxgiFaults, 2);
            return false;
        }

        return Interlocked.Increment(ref _consecutiveDxgiFaults) < 2;
    }

    private async Task RecoverFromSessionInterruptionAsync(
        CapturePipelineSession interruptedSession,
        string notice,
        EncoderNegotiationTier minimumTier,
        bool preferDxgiDuplication = true)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string? finalStatus = null;
        string? fatalError = null;
        CapturePipelineSession? recoveredSession = null;
        IVideoEncoder? recoveredEncoder = null;
        long expectedGeneration = 0;

        if (!await TryEnterGateAsync().ConfigureAwait(false))
        {
            return;
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(_activeSession, interruptedSession) ||
                !_isRecording ||
                _currentSettings is null)
            {
                return;
            }

            EngineLogger.Warn("ReplayEngine", $"{notice} (minimumTier={minimumTier}, preferDxgi={preferDxgiDuplication})");
            expectedGeneration = Interlocked.Increment(ref _pipelineGeneration);
            TeardownActiveSessionLocked();
            _ringBuffer.Clear();
        }
        finally
        {
            ReleaseGateSafe();
        }

        Exception? lastException = null;
        for (int attempt = 0; attempt < RecoveryRetryDelaysMs.Length; attempt++)
        {
            int delayMs = RecoveryRetryDelaysMs[attempt];
            if (delayMs > 0)
            {
                // Release _gate during retry backoff so StopAsync(), UpdateSettingsAsync(), and Dispose() never stall.
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _disposed) != 0 ||
                !_isRecording ||
                Volatile.Read(ref _pipelineGeneration) != expectedGeneration)
            {
                return;
            }

            if (!await TryEnterGateAsync().ConfigureAwait(false))
            {
                return;
            }
            try
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    !_isRecording ||
                    _currentSettings is null ||
                    Volatile.Read(ref _pipelineGeneration) != expectedGeneration)
                {
                    return;
                }

                bool useDxgiThisAttempt = preferDxgiDuplication && attempt == 0;
                try
                {
                    _activeSession = StartCapturePipelineLocked(_currentSettings, minimumTier, useDxgiThisAttempt);
                    _isRecording = true;
                    recoveredSession = _activeSession;
                    recoveredEncoder = _activeSession.Encoder;
                    if (recoveredEncoder is not null)
                    {
                        finalStatus = BuildActiveStatusMessage(
                            _currentSettings,
                            _activeSession.Capture,
                            recoveredEncoder,
                            _activeSession.AudioCapture);
                        Volatile.Write(ref _lastError, null);
                        Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Recording);
                        Volatile.Write(ref _currentStatus, finalStatus);
                    }

                    lastException = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    TeardownActiveSessionLocked();
                    EngineLogger.Warn(
                        "ReplayEngine",
                        $"Pipeline recovery attempt {attempt + 1}/{RecoveryRetryDelaysMs.Length} failed: {ex.Message}");
                }
            }
            finally
            {
                ReleaseGateSafe();
            }
        }

        if (lastException is not null)
        {
            if (!await TryEnterGateAsync().ConfigureAwait(false))
            {
                return;
            }
            try
            {
                if (Volatile.Read(ref _disposed) == 0 &&
                    Volatile.Read(ref _pipelineGeneration) == expectedGeneration)
                {
                    TeardownActiveSessionLocked();
                    _isRecording = false;
                    fatalError = $"Przechwytywanie wstrzymane po błędzie potoku: {lastException.Message}";
                    finalStatus = fatalError;
                    EngineLogger.Error("ReplayEngine", fatalError, lastException);
                    Volatile.Write(ref _lastError, fatalError);
                    Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Error);
                    Volatile.Write(ref _currentStatus, fatalError);
                }
            }
            finally
            {
                ReleaseGateSafe();
            }
        }

        if (recoveredSession is not null && recoveredEncoder is not null)
        {
            ArmKeyframeWatchdog(recoveredSession, recoveredEncoder);
        }

        if (finalStatus is not null)
        {
            StatusChanged?.Invoke(finalStatus);
        }

        if (fatalError is not null)
        {
            ErrorOccurred?.Invoke(fatalError);
        }
    }

    private void TeardownActiveSessionLocked()
    {
        CapturePipelineSession? session = _activeSession;
        _activeSession = null;
        session?.Dispose();
    }

    private void OnAudioStatusChanged(string message)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_isRecording || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Volatile.Write(ref _statusLevel, (int)EngineStatusLevel.Warning);
        Volatile.Write(ref _currentStatus, message);
        StatusChanged?.Invoke(message);
    }

    private static string BuildActiveStatusMessage(
        AppSettings settings,
        IFrameCaptureSource capture,
        IVideoEncoder encoder,
        DualAudioCaptureService? audioCapture)
    {
        string mode = encoder.IsHardwareAccelerated ? "GPU" : "CPU";
        string backendShort = capture is DxgiFrameCapture ? "DXGI" : "WGC";
        string resolutionLabel = $"{(int)settings.Resolution}p";
        string baseStatus = $"Nagrywanie aktywne ({backendShort} · {mode} · {resolutionLabel} {settings.Fps} FPS · {capture.AdapterName} / {encoder.EncoderName})";

        string? audioError = audioCapture?.GetInitializationErrorSummary();
        return string.IsNullOrWhiteSpace(audioError)
            ? baseStatus
            : $"{baseStatus} · {audioError}";
    }

    private async Task<bool> TryEnterGateAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ReleaseGateSafe()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Engine was disposed concurrently while exiting _gate.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _pipelineGeneration);
        _gate.Wait();
        try
        {
            _isRecording = false;
            TeardownActiveSessionLocked();
            _currentSettings = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        _ringBuffer.Dispose();
    }

    /// <summary>
    /// Encapsulates one live monitor-matched DXGI/WGC + D3D11 + 3-tier H.264 MFT + dual-track WASAPI AAC
    /// session on a single shared <see cref="Stopwatch"/> epoch with decoupled background video encoding,
    /// live encoder tier replacement, and deterministic reverse-order COM teardown.
    /// </summary>
    private sealed class CapturePipelineSession : IDisposable
    {
        private readonly long _frameIntervalTicks;
        private readonly Stopwatch _sessionClock;
        private readonly IFrameCaptureSource _capture;
        private readonly bool _isDxgiCapture;
        private readonly GpuVideoProcessor _gpuProcessor;
        private readonly DualAudioCaptureService? _audioCapture;
        private readonly Action<Exception> _onEncodeError;
        private readonly object _frameSubmitLock = new();
        private readonly object _encodeQueueLock = new();
        private readonly AutoResetEvent _encodeWorkSignal = new(initialState: false);

        private IVideoEncoder? _encoder;
        private Func<ID3D11Texture2D, bool>? _encoderTextureBusyCheck;
        private Thread? _encodeWorkerThread;
        private PendingVideoFrame? _pendingEncodeFrame;
        private ID3D11Texture2D? _activeEncodeTexture;
        private ID3D11Texture2D? _lastProcessedFrame;
        private long _lastSubmittedTimestamp100Ns = -1;
        private Timer? _startupPrimingTimer;
        private Timer? _staticKeepAliveTimer;
        private int _primingFramesRemaining = MaxStartupPrimingFrames;
        private int _encodeWorkerStopped;
        private int _disposed;

        public CapturePipelineSession(
            int targetFps,
            Stopwatch sessionClock,
            IFrameCaptureSource capture,
            GpuVideoProcessor gpuProcessor,
            IVideoEncoder encoder,
            DualAudioCaptureService? audioCapture,
            Action<Exception> onEncodeError)
        {
            int normalizedFps = targetFps == 30 ? 30 : 60;
            _frameIntervalTicks = TimeSpan.TicksPerSecond / normalizedFps;
            _sessionClock = sessionClock;
            _capture = capture;
            _isDxgiCapture = capture is DxgiFrameCapture;
            _gpuProcessor = gpuProcessor;
            _encoder = encoder;
            _encoderTextureBusyCheck = encoder.IsTextureInUse;
            _audioCapture = audioCapture;
            _onEncodeError = onEncodeError;
        }

        public IFrameCaptureSource Capture => _capture;

        public GpuVideoProcessor GpuProcessor => _gpuProcessor;

        public IVideoEncoder? Encoder => Volatile.Read(ref _encoder);

        public DualAudioCaptureService? AudioCapture => _audioCapture;

        public void Start()
        {
            _sessionClock.Start();

            if (_gpuProcessor.SupportsHardwareVideoProcessor)
            {
                _encodeWorkerThread = new Thread(VideoEncodeWorkerLoop)
                {
                    Name = "ClipThat.VideoEncodeWorker",
                    IsBackground = true
                };
                _encodeWorkerThread.SetApartmentState(ApartmentState.MTA);
                _encodeWorkerThread.Start();
            }

            if (_audioCapture is not null)
            {
                try
                {
                    _audioCapture.Start();
                }
                catch (Exception ex)
                {
                    // Audio failures must never prevent video recording from starting.
                    EngineLogger.Warn("ReplayEngine", $"Audio capture start failed ({ex.Message}); continuing video capture.", ex);
                }
            }

            _capture.Start();
            ArmStartupPrimingTimer();
            ArmStaticKeepAliveTimer();
        }

        public void HandleCapturedFrame(ID3D11Texture2D bgraTexture, long timestamp100Ns, long frameDuration100Ns)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_gpuProcessor.SupportsHardwareVideoProcessor)
            {
                ID3D11Texture2D? busyActive;
                ID3D11Texture2D? busyPending;
                ID3D11Texture2D? busyCached;
                lock (_encodeQueueLock)
                {
                    if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _encodeWorkerStopped) != 0)
                    {
                        return;
                    }

                    busyActive = _activeEncodeTexture;
                    busyPending = _pendingEncodeFrame?.Texture;
                    busyCached = _lastProcessedFrame;
                }

                Func<ID3D11Texture2D, bool>? encoderBusyCheck = Volatile.Read(ref _encoderTextureBusyCheck);

                // Perform only the fast GPU BGRA->NV12 VideoProcessorBlt on the capture thread (<0.2ms)
                // and offload staging/encoding to ClipThat.VideoEncodeWorker so capture never stalls DWM.
                ID3D11Texture2D processed = _gpuProcessor.ProcessFrame(
                    bgraTexture,
                    busyActive,
                    busyPending,
                    encoderBusyCheck,
                    busyCached,
                    cacheInputView: _isDxgiCapture);

                // If VideoProcessor fell back to CPU at runtime and returned the transient input texture,
                // encode synchronously right now so WGC's frame surface is never accessed after disposal.
                if (_gpuProcessor.SupportsHardwareVideoProcessor && !ReferenceEquals(processed, bgraTexture))
                {
                    lock (_encodeQueueLock)
                    {
                        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _encodeWorkerStopped) != 0)
                        {
                            return;
                        }

                        long monotonicTs = NextMonotonicTimestampLocked(timestamp100Ns);
                        _lastProcessedFrame = processed;
                        _pendingEncodeFrame = new PendingVideoFrame(processed, monotonicTs, frameDuration100Ns);
                    }

                    SignalEncodeWorkerSafe();
                    return;
                }

                // Clear cached GPU surface pointer after runtime fallback so keep-alive timers never touch stale surfaces.
                lock (_encodeQueueLock)
                {
                    _lastProcessedFrame = null;
                    _pendingEncodeFrame = null;
                }
            }

            // Fallback when GPU VideoProcessor is unavailable: bgraTexture is transient so encode synchronously.
            lock (_frameSubmitLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                IVideoEncoder? encoder = _encoder;
                if (encoder is null)
                {
                    return;
                }

                long monotonicTs;
                lock (_encodeQueueLock)
                {
                    monotonicTs = NextMonotonicTimestampLocked(timestamp100Ns);
                }

                try
                {
                    encoder.EncodeFrame(bgraTexture, monotonicTs, frameDuration100Ns);
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _encodeWorkerStopped) == 0)
                    {
                        EngineLogger.Error("ReplayEngine", $"Synchronous fallback video encode failed: {ex.Message}", ex);
                        _onEncodeError(ex);
                    }
                }
            }
        }

        private void VideoEncodeWorkerLoop()
        {
            while (Volatile.Read(ref _encodeWorkerStopped) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                PendingVideoFrame? nextFrame;
                lock (_encodeQueueLock)
                {
                    nextFrame = _pendingEncodeFrame;
                    _pendingEncodeFrame = null;
                }

                if (!nextFrame.HasValue)
                {
                    try
                    {
                        _encodeWorkSignal.WaitOne(1000);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    continue;
                }

                PendingVideoFrame frame = nextFrame.Value;
                try
                {
                    lock (_frameSubmitLock)
                    {
                        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _encodeWorkerStopped) != 0)
                        {
                            break;
                        }

                        lock (_encodeQueueLock)
                        {
                            _activeEncodeTexture = frame.Texture;
                        }

                        try
                        {
                            IVideoEncoder? encoder = _encoder;
                            encoder?.EncodeFrame(frame.Texture, frame.Timestamp100Ns, frame.Duration100Ns);
                        }
                        finally
                        {
                            lock (_encodeQueueLock)
                            {
                                if (ReferenceEquals(_activeEncodeTexture, frame.Texture))
                                {
                                    _activeEncodeTexture = null;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _encodeWorkerStopped) == 0)
                    {
                        EngineLogger.Error("ReplayEngine", $"Background video encode worker failed: {ex.Message}", ex);
                        _onEncodeError(ex);
                    }
                }
            }
        }

        private long NextMonotonicTimestampLocked(long candidateTicks)
        {
            long monotonicTs = candidateTicks;
            if (monotonicTs <= _lastSubmittedTimestamp100Ns)
            {
                monotonicTs = _lastSubmittedTimestamp100Ns + 1;
            }

            _lastSubmittedTimestamp100Ns = monotonicTs;
            return monotonicTs;
        }

        private void SignalEncodeWorkerSafe()
        {
            try
            {
                _encodeWorkSignal.Set();
            }
            catch (ObjectDisposedException)
            {
                // Pipeline is shutting down.
            }
        }

        /// <summary>
        /// Disposes the currently active encoder and swaps in a lower-tier encoder without interrupting
        /// capture or WASAPI audio streams.
        /// </summary>
        public IVideoEncoder ReplaceEncoder(Func<IVideoEncoder> encoderFactory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            StopStartupPrimingTimer();

            lock (_encodeQueueLock)
            {
                _pendingEncodeFrame = null;
            }

            IVideoEncoder? oldEncoder;
            lock (_frameSubmitLock)
            {
                oldEncoder = _encoder;
                _encoder = null;
                Volatile.Write(ref _encoderTextureBusyCheck, null);
            }

            SafeDispose(oldEncoder);

            IVideoEncoder replacement = encoderFactory();
            lock (_frameSubmitLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    SafeDispose(replacement);
                    throw new ObjectDisposedException(nameof(CapturePipelineSession));
                }

                _encoder = replacement;
                Volatile.Write(ref _encoderTextureBusyCheck, replacement.IsTextureInUse);
                Volatile.Write(ref _primingFramesRemaining, MaxStartupPrimingFrames);

                // Immediately feed the most recent NV12 surface if available so the replacement encoder
                // can produce its initial keyframe even on a completely static desktop.
                ID3D11Texture2D? cachedFrame;
                long ts = 0;
                lock (_encodeQueueLock)
                {
                    cachedFrame = _lastProcessedFrame;
                    if (cachedFrame is not null)
                    {
                        _activeEncodeTexture = cachedFrame;
                        ts = NextMonotonicTimestampLocked(_sessionClock.Elapsed.Ticks);
                    }
                }

                if (cachedFrame is not null)
                {
                    try
                    {
                        replacement.EncodeFrame(cachedFrame, ts, _frameIntervalTicks);
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.Warn("ReplayEngine", $"Initial priming submit on replacement encoder failed: {ex.Message}");
                    }
                    finally
                    {
                        lock (_encodeQueueLock)
                        {
                            if (ReferenceEquals(_activeEncodeTexture, cachedFrame))
                            {
                                _activeEncodeTexture = null;
                            }
                        }
                    }
                }
            }

            ArmStartupPrimingTimer();
            return replacement;
        }

        private void ArmStartupPrimingTimer()
        {
            StopStartupPrimingTimer();
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _startupPrimingTimer = new Timer(
                OnStartupPrimingTick,
                state: null,
                dueTime: StartupPrimingIntervalMs * 2,
                period: StartupPrimingIntervalMs);
        }

        private void OnStartupPrimingTick(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!Monitor.TryEnter(_frameSubmitLock))
            {
                return;
            }

            bool shouldStopTimer = false;
            try
            {
                IVideoEncoder? encoder = _encoder;
                if (Volatile.Read(ref _disposed) != 0 || encoder is null || encoder.HasEmittedKeyframe)
                {
                    shouldStopTimer = true;
                    return;
                }

                // If capture delivered at least one initial frame on a static desktop and the encoder has
                // not yet emitted a keyframe (e.g. multi-frame internal priming depth), re-submit the
                // cached NV12 frame a few times until the first keyframe emerges.
                ID3D11Texture2D? cachedFrame;
                long nowTicks = _sessionClock.Elapsed.Ticks;
                long ts = 0;
                bool shouldSubmit = false;

                lock (_encodeQueueLock)
                {
                    cachedFrame = _lastProcessedFrame;
                }

                if (cachedFrame is not null)
                {
                    if (Interlocked.Decrement(ref _primingFramesRemaining) < 0)
                    {
                        shouldStopTimer = true;
                        return;
                    }

                    lock (_encodeQueueLock)
                    {
                        cachedFrame = _lastProcessedFrame;
                        if (cachedFrame is not null && nowTicks - _lastSubmittedTimestamp100Ns >= _frameIntervalTicks)
                        {
                            _activeEncodeTexture = cachedFrame;
                            ts = NextMonotonicTimestampLocked(nowTicks);
                            shouldSubmit = true;
                        }
                    }

                    try
                    {
                        if (shouldSubmit && cachedFrame is not null)
                        {
                            try
                            {
                                encoder.EncodeFrame(cachedFrame, ts, _frameIntervalTicks);
                            }
                            catch (Exception ex)
                            {
                                EngineLogger.Warn("ReplayEngine", $"Startup priming frame submit failed: {ex.Message}");
                                shouldStopTimer = true;
                            }
                        }
                    }
                    finally
                    {
                        if (shouldSubmit)
                        {
                            lock (_encodeQueueLock)
                            {
                                if (ReferenceEquals(_activeEncodeTexture, cachedFrame))
                                {
                                    _activeEncodeTexture = null;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(_frameSubmitLock);
            }

            if (shouldStopTimer)
            {
                // Avoid self-deadlocking Timer.Dispose(WaitHandle) from inside the timer's own callback.
                StopStartupPrimingTimer(waitForCallbackExit: false);
            }
        }

        private void ArmStaticKeepAliveTimer()
        {
            StopStaticKeepAliveTimer();
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _staticKeepAliveTimer = new Timer(
                OnStaticKeepAliveTick,
                state: null,
                dueTime: StaticDesktopKeepAliveIntervalMs,
                period: StaticDesktopKeepAliveIntervalMs);
        }

        private void OnStaticKeepAliveTick(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!Monitor.TryEnter(_frameSubmitLock))
            {
                return;
            }

            try
            {
                IVideoEncoder? encoder = _encoder;
                if (Volatile.Read(ref _disposed) != 0 || encoder is null || !encoder.HasEmittedKeyframe)
                {
                    return;
                }

                ID3D11Texture2D? cachedFrame;
                long ts = 0;
                bool shouldSubmit = false;
                long nowTicks = _sessionClock.Elapsed.Ticks;
                long idleThresholdTicks = StaticDesktopKeepAliveIntervalMs * TimeSpan.TicksPerMillisecond;

                lock (_encodeQueueLock)
                {
                    cachedFrame = _lastProcessedFrame;
                    if (cachedFrame is not null &&
                        _pendingEncodeFrame is null &&
                        _lastSubmittedTimestamp100Ns >= 0 &&
                        nowTicks - _lastSubmittedTimestamp100Ns >= idleThresholdTicks)
                    {
                        _activeEncodeTexture = cachedFrame;
                        ts = NextMonotonicTimestampLocked(nowTicks);
                        shouldSubmit = true;
                    }
                }

                if (shouldSubmit && cachedFrame is not null)
                {
                    // Emit a periodic static keep-alive IDR frame so the ring buffer always maintains
                    // fresh GOP boundaries even if the user leaves a completely static desktop for hours.
                    try
                    {
                        encoder.EncodeFrame(cachedFrame, ts, _frameIntervalTicks, forceKeyFrame: true);
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.Warn("ReplayEngine", $"Static keep-alive frame submit failed: {ex.Message}");
                    }
                    finally
                    {
                        lock (_encodeQueueLock)
                        {
                            if (ReferenceEquals(_activeEncodeTexture, cachedFrame))
                            {
                                _activeEncodeTexture = null;
                            }
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(_frameSubmitLock);
            }
        }

        private void StopStaticKeepAliveTimer()
        {
            Timer? timer = Interlocked.Exchange(ref _staticKeepAliveTimer, null);
            if (timer is not null)
            {
                try
                {
                    using var stopped = new ManualResetEvent(false);
                    if (timer.Dispose(stopped))
                    {
                        stopped.WaitOne(250);
                    }
                }
                catch
                {
                    // Ignore timer disposal errors.
                }
            }
        }

        private void StopStartupPrimingTimer(bool waitForCallbackExit = true)
        {
            Timer? timer = Interlocked.Exchange(ref _startupPrimingTimer, null);
            if (timer is not null)
            {
                try
                {
                    if (!waitForCallbackExit)
                    {
                        timer.Dispose();
                        return;
                    }

                    using var stopped = new ManualResetEvent(false);
                    if (timer.Dispose(stopped))
                    {
                        stopped.WaitOne(250);
                    }
                }
                catch
                {
                    // Ignore timer disposal errors.
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _sessionClock.Stop();
            StopStartupPrimingTimer();
            StopStaticKeepAliveTimer();

            // 1. Stop capture callbacks/loop first so no further frames enter HandleCapturedFrame.
            try
            {
                _capture.StopCapture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd zatrzymywania przechwytywania: {ex.Message}");
            }

            // 2. Stop and join the background encode worker before disposing encoder or GPU surfaces.
            Interlocked.Exchange(ref _encodeWorkerStopped, 1);
            lock (_encodeQueueLock)
            {
                _pendingEncodeFrame = null;
                _lastProcessedFrame = null;
            }

            SignalEncodeWorkerSafe();

            Thread? encodeWorker = _encodeWorkerThread;
            bool encodeWorkerExited = true;
            if (encodeWorker is not null && encodeWorker != Thread.CurrentThread)
            {
                encodeWorkerExited = encodeWorker.Join(2000);
            }

            if (encodeWorkerExited)
            {
                _encodeWorkerThread = null;
            }

            // 3. Stop audio capture so no further audio packets enter the ring buffer.
            SafeDispose(_audioCapture);

            if (!encodeWorkerExited)
            {
                EngineLogger.Warn(
                    "ReplayEngine",
                    "VideoEncodeWorker did not exit within 2000 ms during Dispose; skipping unsafe GPU/encoder COM teardown.");
                return;
            }

            IVideoEncoder? activeEncoder;
            lock (_frameSubmitLock)
            {
                activeEncoder = _encoder;
                _encoder = null;
                Volatile.Write(ref _encoderTextureBusyCheck, null);
            }

            // 4. Dispose downstream GPU consumers BEFORE the capture source releases the shared ID3D11Device.
            SafeDispose(activeEncoder);
            SafeDispose(_gpuProcessor);
            SafeDispose(_capture);
            SafeDispose(_encodeWorkSignal);
        }

        private readonly record struct PendingVideoFrame(
            ID3D11Texture2D Texture,
            long Timestamp100Ns,
            long Duration100Ns);

        private static void SafeDispose(IDisposable? disposable)
            => EngineDisposeHelper.SafeDispose(disposable, "Błąd zwalniania zasobu potoku");
    }
}
