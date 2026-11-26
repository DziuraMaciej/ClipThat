using System.Diagnostics;
using System.IO;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Interop;
using ClipThat.Engine.Muxing;
using ClipThat.Engine.Video;
using ClipThat.Models;
using ClipThat.Services;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClipThat.Engine;

/// <summary>
/// Real user-mode GPU screen capture, dual-track WASAPI AAC audio capture, hardware H.264 encoding,
/// RAM circular buffer, and native Media Foundation MP4 replay muxing engine.
/// </summary>
public sealed class ReplayEngine : IReplayEngine
{
    private const int FramePoolBufferCount = 2;

    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly Mp4ReplayMuxer _mp4Muxer = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MediaRingBuffer _ringBuffer = new();

    private AppSettings? _currentSettings;
    private CapturePipelineSession? _activeSession;
    private volatile bool _isRecording;
    private int _disposed;

    public ReplayEngine(ResourceEstimatorService resourceEstimatorService)
    {
        ArgumentNullException.ThrowIfNull(resourceEstimatorService);
        _resourceEstimatorService = resourceEstimatorService;
    }

    public bool IsRecording => _isRecording;

    /// <summary>
    /// Exposes the active thread-safe circular buffer for diagnostics and upcoming Step 2B/2C audio &amp; muxer integration.
    /// </summary>
    public MediaRingBuffer RingBuffer => _ringBuffer;

    public event Action<string>? StatusChanged;

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

            _activeSession = StartCapturePipelineLocked(normalized);
            _isRecording = true;

            statusMessage = BuildActiveStatusMessage(normalized, _activeSession.Encoder, _activeSession.AudioCapture);
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            StatusChanged?.Invoke($"Błąd uruchamiania przechwytywania: {ex.Message}");
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
            StatusChanged?.Invoke("Nagrywanie wstrzymane");
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

                _activeSession = StartCapturePipelineLocked(updated);
                _isRecording = true;
            }

            statusMessage = BuildActiveStatusMessage(updated, _activeSession.Encoder, _activeSession.AudioCapture);
        }
        catch (Exception ex)
        {
            TeardownActiveSessionLocked();
            _isRecording = false;
            StatusChanged?.Invoke($"Błąd aktualizacji potoku wideo: {ex.Message}");
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
    /// and remuxes compressed H.264 video + dual-track AAC audio into a native <c>.mp4</c> file on a background thread
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

            if (_activeSession is not null)
            {
                targetWidth = _activeSession.Encoder.OutputWidth;
                targetHeight = _activeSession.Encoder.OutputHeight;
                targetFps = _activeSession.Encoder.TargetFps;
                targetBitrateBps = _activeSession.Encoder.TargetBitrateBps;
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
            StatusChanged?.Invoke("Nagrywanie jest wstrzymane — brak bufora do zapisania");
            return null;
        }

        EncodedPacketSnapshot snapshot = _ringBuffer.SnapshotPackets();
        if (snapshot.Count == 0)
        {
            snapshot.Dispose();
            StatusChanged?.Invoke("Bufor powtórki nie zawiera jeszcze gotowej klatki kluczowej");
            return null;
        }

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
        StatusChanged?.Invoke($"Zapisano powtórkę: {fileName}");
        ReplaySaved?.Invoke(outputFilePath);
        return outputFilePath;
    }

    /// <summary>
    /// Extension point for Step 2B audio capture modules to enqueue encoded audio packets into the shared ring buffer.
    /// </summary>
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

    /// <summary>
    /// Extension point for Step 2B audio capture modules to enqueue raw pooled buffers with timestamps.
    /// </summary>
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

    /// <summary>
    /// Returns the current monotonic session timestamp (in 100ns units) aligned with captured video frames,
    /// allowing Step 2B WASAPI audio pipelines to share the same presentation timeline.
    /// </summary>
    public long GetCurrentSessionTimestamp100Ns()
        => _activeSession?.GetCurrentSessionTimestamp100Ns() ?? 0L;

    private CapturePipelineSession StartCapturePipelineLocked(AppSettings settings)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException(
                "Windows.Graphics.Capture nie jest obsługiwane w tej wersji systemu Windows.");
        }

        GpuVideoProcessor? gpuProcessor = null;
        IDirect3DDevice? direct3DDevice = null;
        HardwareH264Encoder? encoder = null;
        GraphicsCaptureItem? captureItem = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? captureSession = null;
        DualAudioCaptureService? audioCapture = null;

        try
        {
            var sharedSessionClock = new Stopwatch();

            gpuProcessor = new GpuVideoProcessor(settings.Resolution, settings.Fps);
            direct3DDevice = CaptureInterop.CreateDirect3DDevice(gpuProcessor.Device);

            encoder = new HardwareH264Encoder(
                gpuProcessor,
                settings.Resolution,
                settings.Fps,
                _resourceEstimatorService,
                onPacketEncoded: _ringBuffer.AddPacket,
                onSequenceHeader: header => _ringBuffer.SetVideoSequenceHeader(header.Span),
                onEncoderError: OnActivePipelineFault);

            captureItem = CaptureInterop.CreateCaptureItemForDisplay(settings.SelectedMonitorDeviceName);
            SizeInt32 initialSize = captureItem.Size;
            if (initialSize.Width <= 0 || initialSize.Height <= 0)
            {
                throw new InvalidOperationException("Wybrany monitor zgłosił nieprawidłowe wymiary przechwytywania.");
            }

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                initialSize);

            captureSession = framePool.CreateCaptureSession(captureItem);
            ConfigureCaptureSessionOptions(captureSession);

            audioCapture = new DualAudioCaptureService(
                sharedSessionClock,
                _ringBuffer,
                settings.SystemAudioDeviceId,
                settings.MicrophoneDeviceId,
                onStatusChanged: OnAudioStatusChanged);

            var pipeline = new CapturePipelineSession(
                settings.Fps,
                sharedSessionClock,
                gpuProcessor,
                direct3DDevice,
                encoder,
                captureItem,
                framePool,
                captureSession,
                audioCapture,
                initialSize,
                OnSessionClosed,
                OnActivePipelineFault);

            pipeline.Start();
            return pipeline;
        }
        catch
        {
            audioCapture?.Dispose();
            captureSession?.Dispose();
            framePool?.Dispose();
            encoder?.Dispose();
            direct3DDevice?.Dispose();
            gpuProcessor?.Dispose();
            throw;
        }
    }

    private static void ConfigureCaptureSessionOptions(GraphicsCaptureSession captureSession)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            {
                captureSession.IsCursorCaptureEnabled = true;
            }
        }
        catch
        {
            // Optional WGC property.
        }

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) &&
                ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                captureSession.IsBorderRequired = false;
            }
        }
        catch
        {
            // Disabling the yellow capture border requires OS permission/Windows 11; ignore if unavailable.
        }
    }

    private void OnSessionClosed(CapturePipelineSession closedSession)
    {
        _ = Task.Run(() => RecoverFromSessionInterruptionAsync(
            closedSession,
            "Monitor zmienił stan — ponowne uruchamianie przechwytywania..."));
    }

    private void OnActivePipelineFault(Exception exception)
    {
        Debug.WriteLine($"Błąd potoku przechwytywania/kodowania: {exception}");
        CapturePipelineSession? session = _activeSession;
        if (session is not null)
        {
            _ = Task.Run(() => RecoverFromSessionInterruptionAsync(
                session,
                $"Błąd kodera wideo ({exception.Message}) — próba wznowienia..."));
        }
    }

    private async Task RecoverFromSessionInterruptionAsync(CapturePipelineSession interruptedSession, string notice)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        string? finalStatus = null;

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

            Debug.WriteLine(notice);
            TeardownActiveSessionLocked();
            _ringBuffer.Clear();

            // Attempt one clean recovery (automatically falling back to the primary monitor if disconnected).
            try
            {
                _activeSession = StartCapturePipelineLocked(_currentSettings);
                _isRecording = true;
                finalStatus = BuildActiveStatusMessage(_currentSettings, _activeSession.Encoder, _activeSession.AudioCapture);
            }
            catch (Exception ex)
            {
                TeardownActiveSessionLocked();
                _isRecording = false;
                finalStatus = $"Przechwytywanie wstrzymane po zmianie monitora: {ex.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }

        if (finalStatus is not null)
        {
            StatusChanged?.Invoke(finalStatus);
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

        StatusChanged?.Invoke(message);
    }

    private static string BuildActiveStatusMessage(
        AppSettings settings,
        HardwareH264Encoder encoder,
        DualAudioCaptureService audioCapture)
    {
        string mode = encoder.IsHardwareAccelerated ? "GPU" : "CPU";
        string resolutionLabel = $"{settings.Resolution.ToString()[1..]}p";
        string baseStatus = $"Nagrywanie aktywne ({mode} · {resolutionLabel} {settings.Fps} FPS)";

        string? audioError = audioCapture.GetInitializationErrorSummary();
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
    /// Encapsulates one live WGC + D3D11 + H.264 encoder + dual-track WASAPI AAC session on a single shared
    /// <see cref="Stopwatch"/> epoch with deterministic teardown synchronization so no callback can ever
    /// access disposed GPU or COM resources.
    /// </summary>
    private sealed class CapturePipelineSession : IDisposable
    {
        private const long ClockReanchorThresholdTicks = 250 * TimeSpan.TicksPerMillisecond;

        private readonly long _frameIntervalTicks;
        private readonly long _pacingToleranceTicks;
        private readonly Stopwatch _sessionClock;
        private readonly GpuVideoProcessor _gpuProcessor;
        private readonly IDirect3DDevice _direct3DDevice;
        private readonly HardwareH264Encoder _encoder;
        private readonly GraphicsCaptureItem _captureItem;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _captureSession;
        private readonly DualAudioCaptureService _audioCapture;
        private readonly Action<CapturePipelineSession> _onClosed;
        private readonly Action<Exception> _onFault;
        private readonly ManualResetEventSlim _frameIdleEvent = new(initialState: true);

        private SizeInt32 _lastContentSize;
        private bool _hasFirstFrame;
        private long _firstFrameClockTicks;
        private long _firstFrameRelativeTicks;
        private long _nextTargetRelativeTicks;
        private long _lastSubmittedTimestamp100Ns = -1;
        private int _consecutiveErrors;
        private int _inFlightFrame;
        private int _stopped;
        private int _disposed;

        public CapturePipelineSession(
            int targetFps,
            Stopwatch sessionClock,
            GpuVideoProcessor gpuProcessor,
            IDirect3DDevice direct3DDevice,
            HardwareH264Encoder encoder,
            GraphicsCaptureItem captureItem,
            Direct3D11CaptureFramePool framePool,
            GraphicsCaptureSession captureSession,
            DualAudioCaptureService audioCapture,
            SizeInt32 initialSize,
            Action<CapturePipelineSession> onClosed,
            Action<Exception> onFault)
        {
            _frameIntervalTicks = TimeSpan.TicksPerSecond / targetFps;
            _pacingToleranceTicks = _frameIntervalTicks / 4;
            _sessionClock = sessionClock;
            _gpuProcessor = gpuProcessor;
            _direct3DDevice = direct3DDevice;
            _encoder = encoder;
            _captureItem = captureItem;
            _framePool = framePool;
            _captureSession = captureSession;
            _audioCapture = audioCapture;
            _lastContentSize = initialSize;
            _onClosed = onClosed;
            _onFault = onFault;
        }

        public HardwareH264Encoder Encoder => _encoder;

        public DualAudioCaptureService AudioCapture => _audioCapture;

        public void Start()
        {
            _captureItem.Closed += OnCaptureItemClosed;
            _framePool.FrameArrived += OnFrameArrived;

            // Start the shared timeline epoch once, then launch dual-track audio and video capture simultaneously.
            _sessionClock.Start();
            _audioCapture.Start();
            _captureSession.StartCapture();
        }

        public long GetCurrentSessionTimestamp100Ns()
            => _sessionClock.IsRunning ? _sessionClock.Elapsed.Ticks : 0L;

        private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            _onClosed(this);
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                DrainAndDiscardFrameSafe(sender);
                return;
            }

            if (Interlocked.CompareExchange(ref _inFlightFrame, 1, 0) != 0)
            {
                // Avoid frame pool starvation if a previous GPU frame is still finishing.
                DrainAndDiscardFrameSafe(sender);
                return;
            }

            _frameIdleEvent.Reset();
            try
            {
                if (Volatile.Read(ref _stopped) != 0)
                {
                    DrainAndDiscardFrameSafe(sender);
                    return;
                }

                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                SizeInt32 contentSize = frame.ContentSize;
                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    return;
                }

                bool sizeChanged =
                    contentSize.Width != _lastContentSize.Width ||
                    contentSize.Height != _lastContentSize.Height;

                if (sizeChanged)
                {
                    _lastContentSize = contentSize;
                    _framePool.Recreate(
                        _direct3DDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        FramePoolBufferCount,
                        contentSize);
                }

                long frameRelativeTicks = frame.SystemRelativeTime.Ticks;
                if (!ShouldAcceptFrame(frameRelativeTicks, out long normalizedTimestamp100Ns))
                {
                    return;
                }

                using ID3D11Texture2D capturedBgraTexture = CaptureInterop.GetTextureFromSurface(frame.Surface);
                ID3D11Texture2D nv12Texture = _gpuProcessor.ProcessFrame(capturedBgraTexture);
                _encoder.EncodeFrame(nv12Texture, normalizedTimestamp100Ns, _frameIntervalTicks);

                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _stopped) == 0)
                {
                    Debug.WriteLine($"Błąd przetwarzania klatki WGC: {ex.Message}");
                    if (Interlocked.Increment(ref _consecutiveErrors) >= 5)
                    {
                        Interlocked.Exchange(ref _stopped, 1);
                        _onFault(ex);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _inFlightFrame, 0);
                _frameIdleEvent.Set();
            }
        }

        /// <summary>
        /// Phase-accurate frame-rate pacer that cleanly drops excess frames when the monitor refresh rate
        /// exceeds target 30/60 FPS (e.g. 144Hz/240Hz -> exact 60 FPS without beat-frequency drop),
        /// anchored to the shared <see cref="Stopwatch"/> session timeline so video and audio never drift.
        /// </summary>
        private bool ShouldAcceptFrame(long frameRelativeTicks, out long normalizedTimestamp100Ns)
        {
            long sessionClockTicks = _sessionClock.Elapsed.Ticks;

            if (!_hasFirstFrame)
            {
                _hasFirstFrame = true;
                _firstFrameClockTicks = sessionClockTicks;
                _firstFrameRelativeTicks = frameRelativeTicks;
                _nextTargetRelativeTicks = frameRelativeTicks + _frameIntervalTicks;
                _lastSubmittedTimestamp100Ns = sessionClockTicks;
                normalizedTimestamp100Ns = sessionClockTicks;
                return true;
            }

            if (frameRelativeTicks + _pacingToleranceTicks < _nextTargetRelativeTicks)
            {
                normalizedTimestamp100Ns = 0;
                return false;
            }

            long nextTarget = _nextTargetRelativeTicks + _frameIntervalTicks;
            if (frameRelativeTicks - nextTarget > _frameIntervalTicks)
            {
                // Desktop was idle (no new DWM frames); re-anchor pacing smoothly without bursting.
                nextTarget = frameRelativeTicks + _frameIntervalTicks;
            }

            _nextTargetRelativeTicks = nextTarget;

            long relativeTimestamp = _firstFrameClockTicks + (frameRelativeTicks - _firstFrameRelativeTicks);
            if (Math.Abs(relativeTimestamp - sessionClockTicks) > ClockReanchorThresholdTicks)
            {
                // System sleep or WGC timestamp discontinuity; smoothly re-anchor to the shared Stopwatch.
                _firstFrameClockTicks = sessionClockTicks;
                _firstFrameRelativeTicks = frameRelativeTicks;
                relativeTimestamp = sessionClockTicks;
            }

            if (relativeTimestamp <= _lastSubmittedTimestamp100Ns)
            {
                relativeTimestamp = _lastSubmittedTimestamp100Ns + 1;
            }

            _lastSubmittedTimestamp100Ns = relativeTimestamp;
            normalizedTimestamp100Ns = relativeTimestamp;
            return true;
        }

        private static void DrainAndDiscardFrameSafe(Direct3D11CaptureFramePool pool)
        {
            try
            {
                using Direct3D11CaptureFrame? discarded = pool.TryGetNextFrame();
            }
            catch
            {
                // Frame pool may already be closing.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _stopped, 1);
            _sessionClock.Stop();

            try
            {
                _captureItem.Closed -= OnCaptureItemClosed;
            }
            catch
            {
                // Ignore event detachment failures if WinRT item is already closed.
            }

            try
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
            catch
            {
                // Ignore event detachment failures if pool is already closing.
            }

            try
            {
                _captureSession.Dispose();
            }
            catch
            {
                // Ignore capture session teardown errors.
            }

            // Stop dual audio capture immediately so no further audio packets are enqueued during teardown.
            SafeDispose(_audioCapture);

            // Wait deterministically for any in-flight GPU frame callback before releasing D3D11/MFT resources.
            _frameIdleEvent.Wait(2000);

            // Dispose in strict reverse dependency order: FramePool -> Encoder -> WinRT IDirect3DDevice -> D3D11 GPU Processor.
            SafeDispose(_framePool);
            SafeDispose(_encoder);
            SafeDispose(_direct3DDevice);
            SafeDispose(_gpuProcessor);

            _frameIdleEvent.Dispose();
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
