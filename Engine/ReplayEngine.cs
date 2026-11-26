using System.Diagnostics;
using System.IO;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Capture;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Encoding;
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
    private const int StartupPrimingIntervalMs = 60;
    private const int MaxStartupPrimingFrames = 6;

    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly Mp4ReplayMuxer _mp4Muxer = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaRingBuffer _ringBuffer = new();

    private AppSettings? _currentSettings;
    private CapturePipelineSession? _activeSession;
    private string? _currentStatus;
    private string? _lastError;
    private volatile bool _isRecording;
    private int _disposed;

    public ReplayEngine(ResourceEstimatorService resourceEstimatorService)
    {
        ArgumentNullException.ThrowIfNull(resourceEstimatorService);
        _resourceEstimatorService = resourceEstimatorService;
    }

    public bool IsRecording => _isRecording;

    public string? CurrentStatus => Volatile.Read(ref _currentStatus);

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// Exposes the active thread-safe circular buffer for diagnostics and replay inspection.
    /// </summary>
    public MediaRingBuffer RingBuffer => _ringBuffer;

    public event Action<string>? StatusChanged;

    public event Action<string>? ErrorOccurred;

    public event Action<string>? ReplaySaved;

    public async Task StartAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string statusMessage;

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

            _ringBuffer.Clear();
            _ringBuffer.SetBufferDuration(normalized.BufferDurationMinutes);
            _currentSettings = normalized;

            _activeSession = StartCapturePipelineLocked(normalized, EncoderNegotiationTier.HardwareGpuZeroCopy);
            _isRecording = true;

            MfH264Encoder activeEncoder = _activeSession.Encoder
                ?? throw new InvalidOperationException("Koder wideo nie został zainicjalizowany.");

            statusMessage = BuildActiveStatusMessage(
                normalized,
                _activeSession.Capture,
                activeEncoder,
                _activeSession.AudioCapture);

            Volatile.Write(ref _lastError, null);
            Volatile.Write(ref _currentStatus, statusMessage);

            ArmKeyframeWatchdog(_activeSession, activeEncoder);
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            string errorMessage = $"Błąd uruchamiania przechwytywania: {ex.Message}";
            EngineLogger.Error("ReplayEngine", errorMessage, ex);
            Volatile.Write(ref _lastError, errorMessage);
            Volatile.Write(ref _currentStatus, errorMessage);
            StatusChanged?.Invoke(errorMessage);
            ErrorOccurred?.Invoke(errorMessage);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(statusMessage);
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool wasRecording;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            wasRecording = _isRecording || _activeSession is not null;
            TeardownActiveSessionLocked();
            _ringBuffer.Clear();
            _isRecording = false;
        }
        finally
        {
            _gate.Release();
        }

        if (wasRecording)
        {
            const string pausedMessage = "Nagrywanie wstrzymane";
            Volatile.Write(ref _currentStatus, pausedMessage);
            StatusChanged?.Invoke(pausedMessage);
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string? statusMessage = null;

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
                TeardownActiveSessionLocked();
                _ringBuffer.Clear();

                _activeSession = StartCapturePipelineLocked(updated, EncoderNegotiationTier.HardwareGpuZeroCopy);
                _isRecording = true;

                if (_activeSession.Encoder is not null)
                {
                    ArmKeyframeWatchdog(_activeSession, _activeSession.Encoder);
                }
            }

            MfH264Encoder? currentEncoder = _activeSession.Encoder;
            if (currentEncoder is not null)
            {
                statusMessage = BuildActiveStatusMessage(
                    updated,
                    _activeSession.Capture,
                    currentEncoder,
                    _activeSession.AudioCapture);

                Volatile.Write(ref _lastError, null);
                Volatile.Write(ref _currentStatus, statusMessage);
            }
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            string errorMessage = $"Błąd aktualizacji potoku wideo: {ex.Message}";
            EngineLogger.Error("ReplayEngine", errorMessage, ex);
            Volatile.Write(ref _lastError, errorMessage);
            Volatile.Write(ref _currentStatus, errorMessage);
            StatusChanged?.Invoke(errorMessage);
            ErrorOccurred?.Invoke(errorMessage);
            throw;
        }
        finally
        {
            _gate.Release();
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

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            isRecordingSnapshot = _isRecording;
            outputDirectory = _currentSettings?.OutputDirectory;

            MfH264Encoder? activeEncoder = _activeSession?.Encoder;
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
        }
        finally
        {
            _gate.Release();
        }

        if (!isRecordingSnapshot || string.IsNullOrWhiteSpace(outputDirectory))
        {
            const string pausedNotice = "Nagrywanie jest wstrzymane — brak bufora do zapisania";
            Volatile.Write(ref _currentStatus, pausedNotice);
            StatusChanged?.Invoke(pausedNotice);
            return null;
        }

        EncodedPacketSnapshot snapshot = _ringBuffer.SnapshotPackets();
        if (snapshot.Count == 0)
        {
            snapshot.Dispose();
            const string noKeyframeNotice = "Bufor powtórki nie zawiera jeszcze gotowej klatki kluczowej";
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
            Volatile.Write(ref _currentStatus, errorMessage);
            StatusChanged?.Invoke(errorMessage);
            ErrorOccurred?.Invoke(errorMessage);
            throw;
        }
    }

    public void PushAudioPacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_isRecording || Volatile.Read(ref _disposed) != 0)
        {
            packet.Dispose();
            return;
        }

        _ringBuffer.AddPacket(packet);
    }

    public void PushAudioPacket(
        MediaTrackType trackType,
        byte[] rentedBuffer,
        int length,
        long timestamp100Ns,
        long duration100Ns)
    {
        if (!_isRecording || Volatile.Read(ref _disposed) != 0)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            return;
        }

        _ringBuffer.AddAudioPacket(trackType, rentedBuffer, length, timestamp100Ns, duration100Ns);
    }

    public long GetCurrentSessionTimestamp100Ns()
        => _activeSession?.GetCurrentSessionTimestamp100Ns() ?? 0L;

    private CapturePipelineSession StartCapturePipelineLocked(
        AppSettings settings,
        EncoderNegotiationTier minimumTier)
    {
        WgcFrameCapture? wgcCapture = null;
        GpuVideoProcessor? gpuProcessor = null;
        MfH264Encoder? encoder = null;
        DualAudioCaptureService? audioCapture = null;

        try
        {
            var sharedSessionClock = new Stopwatch();
            CapturePipelineSession? createdSession = null;

            wgcCapture = new WgcFrameCapture(
                settings.SelectedMonitorDeviceName,
                settings.Fps,
                sharedSessionClock,
                onFrameCaptured: (bgraTexture, timestamp100Ns, frameDuration100Ns) =>
                {
                    createdSession?.HandleCapturedFrame(bgraTexture, timestamp100Ns, frameDuration100Ns);
                },
                onSessionClosed: () =>
                {
                    if (createdSession is not null)
                    {
                        OnSessionClosed(createdSession);
                    }
                },
                onFrameError: ex => OnActivePipelineFault(ex, isEncoderFault: false));

            gpuProcessor = new GpuVideoProcessor(
                wgcCapture.Device,
                wgcCapture.ImmediateContext,
                settings.Resolution,
                settings.Fps,
                wgcCapture.AdapterName,
                wgcCapture.AdapterLuid,
                ownsDevice: false);

            encoder = CreateEncoder(gpuProcessor, settings, minimumTier);

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
                wgcCapture,
                gpuProcessor,
                encoder,
                audioCapture);

            createdSession = pipeline;
            pipeline.Start();

            EngineLogger.Info(
                "ReplayEngine",
                $"Capture pipeline started: Monitor={settings.SelectedMonitorDeviceName ?? "(Primary)"} | " +
                $"GPU=\"{wgcCapture.AdapterName}\" | Encoder=\"{encoder.EncoderName}\" ({encoder.ActiveTier}) | " +
                $"Output={encoder.OutputWidth}x{encoder.OutputHeight}@{encoder.TargetFps}fps");

            return pipeline;
        }
        catch
        {
            audioCapture?.Dispose();
            encoder?.Dispose();
            gpuProcessor?.Dispose();
            wgcCapture?.Dispose();
            throw;
        }
    }

    private MfH264Encoder CreateEncoder(
        GpuVideoProcessor gpuProcessor,
        AppSettings settings,
        EncoderNegotiationTier minimumTier)
    {
        return new MfH264Encoder(
            gpuProcessor,
            settings.Resolution,
            settings.Fps,
            _resourceEstimatorService,
            onPacketEncoded: _ringBuffer.AddPacket,
            onSequenceHeader: header => _ringBuffer.SetVideoSequenceHeader(header.Span),
            onEncoderError: ex => OnActivePipelineFault(ex, isEncoderFault: true),
            minimumTier: minimumTier);
    }

    /// <summary>
    /// Arms a 1.5-second watchdog verifying that <paramref name="monitoredEncoder"/> emits at least
    /// one H.264 keyframe (<c>IsKeyFrame == true</c>). If zero keyframes arrive in time, automatically
    /// disposes the stalled encoder and steps down <c>Tier 1 -&gt; Tier 2 -&gt; Tier 3</c>.
    /// </summary>
    private void ArmKeyframeWatchdog(CapturePipelineSession session, MfH264Encoder monitoredEncoder)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(KeyframeWatchdogTimeoutMs).ConfigureAwait(false);
                await CheckKeyframeWatchdogAsync(session, monitoredEncoder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLogger.Warn("ReplayEngine", $"Keyframe watchdog task error: {ex.Message}", ex);
            }
        });
    }

    private async Task CheckKeyframeWatchdogAsync(CapturePipelineSession session, MfH264Encoder monitoredEncoder)
    {
        if (Volatile.Read(ref _disposed) != 0 || monitoredEncoder.HasEmittedKeyframe)
        {
            return;
        }

        string? updatedStatus = null;
        string? fatalError = null;
        MfH264Encoder? nextEncoderToMonitor = null;

        await _gate.WaitAsync().ConfigureAwait(false);
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

                MfH264Encoder replacement;
                try
                {
                    replacement = session.ReplaceEncoder(
                        () => CreateEncoder(session.GpuProcessor, _currentSettings, nextTier.Value));
                }
                catch (Exception tierEx) when (nextTier.Value != EncoderNegotiationTier.SoftwareFallback)
                {
                    EngineLogger.Warn(
                        "ReplayEngine",
                        $"Step-down to {nextTier.Value} failed ({tierEx.Message}); retrying directly with SoftwareFallback.",
                        tierEx);
                    replacement = session.ReplaceEncoder(
                        () => CreateEncoder(session.GpuProcessor, _currentSettings, EncoderNegotiationTier.SoftwareFallback));
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
                Volatile.Write(ref _currentStatus, fatalError);
            }
        }
        finally
        {
            _gate.Release();
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
        _ = Task.Run(() => RecoverFromSessionInterruptionAsync(
            closedSession,
            "Monitor zmienił stan — ponowne uruchamianie przechwytywania...",
            minimumTier: EncoderNegotiationTier.HardwareGpuZeroCopy));
    }

    private void OnActivePipelineFault(Exception exception, bool isEncoderFault)
    {
        EngineLogger.Error("ReplayEngine", $"Active pipeline fault (encoderFault={isEncoderFault}): {exception.Message}", exception);
        CapturePipelineSession? session = _activeSession;
        if (session is not null)
        {
            EncoderNegotiationTier recoveryTier = EncoderNegotiationTier.HardwareGpuZeroCopy;
            if (isEncoderFault)
            {
                MfH264Encoder? currentEncoder = session.Encoder;
                if (currentEncoder is not null)
                {
                    recoveryTier = GetNextDowngradeTier(currentEncoder.ActiveTier) ?? EncoderNegotiationTier.SoftwareFallback;
                }
            }

            _ = Task.Run(() => RecoverFromSessionInterruptionAsync(
                session,
                $"Błąd kodera wideo ({exception.Message}) — próba wznowienia ({recoveryTier})...",
                minimumTier: recoveryTier));
        }
    }

    private async Task RecoverFromSessionInterruptionAsync(
        CapturePipelineSession interruptedSession,
        string notice,
        EncoderNegotiationTier minimumTier)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string? finalStatus = null;
        string? fatalError = null;
        CapturePipelineSession? recoveredSession = null;
        MfH264Encoder? recoveredEncoder = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(_activeSession, interruptedSession) ||
                !_isRecording ||
                _currentSettings is null)
            {
                return;
            }

            EngineLogger.Warn("ReplayEngine", $"{notice} (minimumTier={minimumTier})");
            TeardownActiveSessionLocked();
            _ringBuffer.Clear();

            // Attempt clean recovery (falling back to primary monitor if disconnected or lower encoder tier on fault).
            try
            {
                _activeSession = StartCapturePipelineLocked(_currentSettings, minimumTier);
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
                    Volatile.Write(ref _currentStatus, finalStatus);
                }
            }
            catch (Exception ex)
            {
                TeardownActiveSessionLocked();
                _isRecording = false;
                fatalError = $"Przechwytywanie wstrzymane po błędzie potoku: {ex.Message}";
                finalStatus = fatalError;
                EngineLogger.Error("ReplayEngine", fatalError, ex);
                Volatile.Write(ref _lastError, fatalError);
                Volatile.Write(ref _currentStatus, fatalError);
            }
        }
        finally
        {
            _gate.Release();
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

        Volatile.Write(ref _currentStatus, message);
        StatusChanged?.Invoke(message);
    }

    private static string BuildActiveStatusMessage(
        AppSettings settings,
        WgcFrameCapture capture,
        MfH264Encoder encoder,
        DualAudioCaptureService? audioCapture)
    {
        string mode = encoder.IsHardwareAccelerated ? "GPU" : "CPU";
        string resolutionLabel = $"{settings.Resolution.ToString()[1..]}p";
        string baseStatus = $"Nagrywanie aktywne ({mode} · {resolutionLabel} {settings.Fps} FPS · {capture.AdapterName} / {encoder.EncoderName})";

        string? audioError = audioCapture?.GetInitializationErrorSummary();
        return string.IsNullOrWhiteSpace(audioError)
            ? baseStatus
            : $"{baseStatus} · {audioError}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
    /// Encapsulates one live monitor-matched WGC + D3D11 + 3-tier H.264 MFT + dual-track WASAPI AAC session
    /// on a single shared <see cref="Stopwatch"/> epoch with live encoder tier replacement and deterministic
    /// reverse-order COM teardown.
    /// </summary>
    private sealed class CapturePipelineSession : IDisposable
    {
        private readonly long _frameIntervalTicks;
        private readonly Stopwatch _sessionClock;
        private readonly WgcFrameCapture _wgcCapture;
        private readonly GpuVideoProcessor _gpuProcessor;
        private readonly DualAudioCaptureService? _audioCapture;
        private readonly object _frameSubmitLock = new();

        private MfH264Encoder? _encoder;
        private ID3D11Texture2D? _lastProcessedFrame;
        private long _lastSubmittedTimestamp100Ns = -1;
        private Timer? _startupPrimingTimer;
        private int _primingFramesRemaining = MaxStartupPrimingFrames;
        private int _disposed;

        public CapturePipelineSession(
            int targetFps,
            Stopwatch sessionClock,
            WgcFrameCapture wgcCapture,
            GpuVideoProcessor gpuProcessor,
            MfH264Encoder encoder,
            DualAudioCaptureService? audioCapture)
        {
            int normalizedFps = targetFps == 30 ? 30 : 60;
            _frameIntervalTicks = TimeSpan.TicksPerSecond / normalizedFps;
            _sessionClock = sessionClock;
            _wgcCapture = wgcCapture;
            _gpuProcessor = gpuProcessor;
            _encoder = encoder;
            _audioCapture = audioCapture;
        }

        public WgcFrameCapture Capture => _wgcCapture;

        public GpuVideoProcessor GpuProcessor => _gpuProcessor;

        public MfH264Encoder? Encoder => Volatile.Read(ref _encoder);

        public DualAudioCaptureService? AudioCapture => _audioCapture;

        public void Start()
        {
            _sessionClock.Start();

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

            _wgcCapture.Start();
            ArmStartupPrimingTimer();
        }

        public void HandleCapturedFrame(ID3D11Texture2D bgraTexture, long timestamp100Ns, long frameDuration100Ns)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_frameSubmitLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                MfH264Encoder? encoder = _encoder;
                if (encoder is null)
                {
                    return;
                }

                ID3D11Texture2D processed = _gpuProcessor.ProcessFrame(bgraTexture);
                // Only cache pooled GPU NV12 textures for startup priming (never cache transient WGC BGRA surfaces).
                if (_gpuProcessor.SupportsHardwareVideoProcessor)
                {
                    _lastProcessedFrame = processed;
                }

                long monotonicTs = timestamp100Ns;
                if (monotonicTs <= _lastSubmittedTimestamp100Ns)
                {
                    monotonicTs = _lastSubmittedTimestamp100Ns + 1;
                }

                _lastSubmittedTimestamp100Ns = monotonicTs;
                encoder.EncodeFrame(processed, monotonicTs, frameDuration100Ns);
            }
        }

        /// <summary>
        /// Disposes the currently active encoder and swaps in a lower-tier encoder without interrupting
        /// WGC capture or WASAPI audio streams.
        /// </summary>
        public MfH264Encoder ReplaceEncoder(Func<MfH264Encoder> encoderFactory)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            StopStartupPrimingTimer();

            MfH264Encoder? oldEncoder;
            lock (_frameSubmitLock)
            {
                oldEncoder = _encoder;
                _encoder = null;
            }

            SafeDispose(oldEncoder);

            MfH264Encoder replacement = encoderFactory();
            lock (_frameSubmitLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    SafeDispose(replacement);
                    throw new ObjectDisposedException(nameof(CapturePipelineSession));
                }

                _encoder = replacement;
                Volatile.Write(ref _primingFramesRemaining, MaxStartupPrimingFrames);

                // Immediately feed the most recent NV12 surface if available so the replacement encoder
                // can produce its initial keyframe even on a completely static desktop.
                if (_lastProcessedFrame is not null)
                {
                    long ts = Math.Max(_sessionClock.Elapsed.Ticks, _lastSubmittedTimestamp100Ns + 1);
                    _lastSubmittedTimestamp100Ns = ts;
                    try
                    {
                        replacement.EncodeFrame(_lastProcessedFrame, ts, _frameIntervalTicks);
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.Warn("ReplayEngine", $"Initial priming submit on replacement encoder failed: {ex.Message}");
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
                MfH264Encoder? encoder = _encoder;
                if (Volatile.Read(ref _disposed) != 0 || encoder is null || encoder.HasEmittedKeyframe)
                {
                    shouldStopTimer = true;
                    return;
                }

                // If WGC captured at least one initial frame on a static desktop and the encoder has
                // not yet emitted a keyframe (e.g. multi-frame internal priming depth), re-submit the
                // cached NV12 frame a few times until the first keyframe emerges.
                if (_lastProcessedFrame is not null)
                {
                    if (Interlocked.Decrement(ref _primingFramesRemaining) < 0)
                    {
                        shouldStopTimer = true;
                        return;
                    }

                    long nowTicks = _sessionClock.Elapsed.Ticks;
                    if (nowTicks - _lastSubmittedTimestamp100Ns >= _frameIntervalTicks)
                    {
                        long ts = Math.Max(nowTicks, _lastSubmittedTimestamp100Ns + 1);
                        _lastSubmittedTimestamp100Ns = ts;
                        try
                        {
                            encoder.EncodeFrame(_lastProcessedFrame, ts, _frameIntervalTicks);
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.Warn("ReplayEngine", $"Startup priming frame submit failed: {ex.Message}");
                            shouldStopTimer = true;
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
                StopStartupPrimingTimer();
            }
        }

        private void StopStartupPrimingTimer()
        {
            Timer? timer = Interlocked.Exchange(ref _startupPrimingTimer, null);
            if (timer is not null)
            {
                try
                {
                    timer.Dispose();
                }
                catch
                {
                    // Ignore timer disposal errors.
                }
            }
        }

        public long GetCurrentSessionTimestamp100Ns()
            => _sessionClock.IsRunning ? _sessionClock.Elapsed.Ticks : 0L;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _sessionClock.Stop();
            StopStartupPrimingTimer();

            // 1. Stop WGC callbacks and wait deterministically for any in-flight GPU frame to finish.
            try
            {
                _wgcCapture.StopCapture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd zatrzymywania przechwytywania WGC: {ex.Message}");
            }

            // 2. Stop audio capture so no further audio packets enter the ring buffer.
            SafeDispose(_audioCapture);

            MfH264Encoder? activeEncoder;
            lock (_frameSubmitLock)
            {
                activeEncoder = _encoder;
                _encoder = null;
                _lastProcessedFrame = null;
            }

            // 3. Dispose downstream GPU consumers BEFORE WgcFrameCapture releases the shared ID3D11Device.
            SafeDispose(activeEncoder);
            SafeDispose(_gpuProcessor);
            SafeDispose(_wgcCapture);
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
                Debug.WriteLine($"Błąd zwalniania zasobu potoku: {ex.Message}");
            }
        }
    }
}
