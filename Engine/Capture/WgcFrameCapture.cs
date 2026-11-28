using System.Diagnostics;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Interop;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClipThat.Engine.Capture;

/// <summary>
/// Universal Windows.Graphics.Capture (WGC) session with Monitor-to-DXGI Adapter matching.
/// Enumerates <see cref="IDXGIFactory1.EnumAdapters1"/> and each adapter's <see cref="IDXGIAdapter.EnumOutputs"/>
/// to locate the exact <see cref="IDXGIAdapter1"/> driving the target <c>HMONITOR</c>, creates the
/// <see cref="ID3D11Device"/> on that adapter with <see cref="DeviceCreationFlags.BgraSupport"/> |
/// <see cref="DeviceCreationFlags.VideoSupport"/>, and delivers phase-paced GPU BGRA textures across
/// NVIDIA, AMD, Intel, single-GPU, and hybrid iGPU+dGPU configurations.
/// </summary>
public sealed class WgcFrameCapture : IFrameCaptureSource
{
    private const int FramePoolBufferCount = 3;
    private const long ClockReanchorThresholdTicks = 250 * TimeSpan.TicksPerMillisecond;

    private readonly long _frameIntervalTicks;
    private readonly long _pacingToleranceTicks;
    private readonly Stopwatch _sessionClock;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediateContext;
    private readonly IDirect3DDevice _direct3DDevice;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _captureSession;
    private readonly Action<ID3D11Texture2D, long, long> _onFrameCaptured;
    private readonly Action _onSessionClosed;
    private readonly Action<Exception> _onFrameError;
    private readonly object _callbackGate = new();
    private readonly ManualResetEventSlim _frameIdleEvent = new(initialState: true);

    private SizeInt32 _lastContentSize;
    private bool _hasFirstFrame;
    private long _firstFrameClockTicks;
    private long _firstFrameRelativeTicks;
    private long _nextTargetRelativeTicks;
    private long _lastSubmittedTimestamp100Ns = -1;
    private int _consecutiveErrors;
    private int _capturedFrameCount;
    private bool _inFlightFrame;
    private int _started;
    private int _stopped;
    private int _disposed;

    public WgcFrameCapture(
        string? monitorDeviceName,
        int targetFps,
        Stopwatch sessionClock,
        Action<ID3D11Texture2D, long, long> onFrameCaptured,
        Action onSessionClosed,
        Action<Exception> onFrameError)
    {
        ArgumentNullException.ThrowIfNull(sessionClock);
        ArgumentNullException.ThrowIfNull(onFrameCaptured);
        ArgumentNullException.ThrowIfNull(onSessionClosed);
        ArgumentNullException.ThrowIfNull(onFrameError);

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException(
                "Windows.Graphics.Capture nie jest obsługiwane w tej wersji systemu Windows.");
        }

        int normalizedFps = targetFps == 30 ? 30 : 60;
        _frameIntervalTicks = TimeSpan.TicksPerSecond / normalizedFps;
        _pacingToleranceTicks = _frameIntervalTicks / 4;
        _sessionClock = sessionClock;
        _onFrameCaptured = onFrameCaptured;
        _onSessionClosed = onSessionClosed;
        _onFrameError = onFrameError;

        ID3D11Device? device = null;
        ID3D11DeviceContext? immediateContext = null;
        IDirect3DDevice? direct3DDevice = null;
        GraphicsCaptureItem? captureItem = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? captureSession = null;

        try
        {
            nint monitorHandle = CaptureInterop.ResolveMonitorHandle(monitorDeviceName);

            MonitorDeviceFactory.MatchedDeviceInfo deviceInfo = MonitorDeviceFactory.CreateD3D11DeviceForMonitor(
                monitorHandle,
                monitorDeviceName,
                logTag: "WgcFrameCapture",
                requireExactMonitorMatch: false,
                out IDXGIOutput? matchedOutput);
            matchedOutput?.Dispose();
            device = deviceInfo.Device;
            immediateContext = deviceInfo.ImmediateContext;
            AdapterName = deviceInfo.AdapterName;
            AdapterLuid = deviceInfo.Luid;

            direct3DDevice = CaptureInterop.CreateDirect3DDevice(device);
            captureItem = CaptureInterop.CreateCaptureItemForMonitor(monitorHandle);

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

            _device = device;
            _immediateContext = immediateContext;
            _direct3DDevice = direct3DDevice;
            _captureItem = captureItem;
            _framePool = framePool;
            _captureSession = captureSession;
            _lastContentSize = initialSize;

            EngineLogger.Info(
                "WgcFrameCapture",
                $"Initialized WGC capture: Monitor={monitorDeviceName ?? "(Primary)"} (HMONITOR=0x{monitorHandle:X}), " +
                $"Size={initialSize.Width}x{initialSize.Height}, ActiveGPU=\"{AdapterName}\" " +
                $"(VendorId=0x{deviceInfo.VendorId:X4}, DeviceId=0x{deviceInfo.DeviceId:X4}, LUID={AdapterLuid.HighPart:X8}:{AdapterLuid.LowPart:X8}, " +
                $"ExactMonitorMatch={deviceInfo.MatchedMonitorOutput})");
        }
        catch (Exception ex)
        {
            EngineLogger.Error(
                "WgcFrameCapture",
                $"Failed to initialize WGC capture for monitor '{monitorDeviceName ?? "(Primary)"}'",
                ex);

            SafeDispose(captureSession);
            SafeDispose(framePool);
            SafeDispose(direct3DDevice);
            SafeDispose(immediateContext);
            SafeDispose(device);
            _frameIdleEvent.Dispose();
            throw;
        }
    }

    public ID3D11Device Device => _device;

    public ID3D11DeviceContext ImmediateContext => _immediateContext;

    public string AdapterName { get; }

    public Luid AdapterLuid { get; }

    public string CaptureBackendName => "WGC";

    public int CaptureWidth => _lastContentSize.Width;

    public int CaptureHeight => _lastContentSize.Height;

    public int CapturedFrameCount => Volatile.Read(ref _capturedFrameCount);

    /// <summary>
    /// Attaches WGC event handlers and begins asynchronous frame delivery.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _captureItem.Closed += OnCaptureItemClosed;
        _framePool.FrameArrived += OnFrameArrived;
        _captureSession.StartCapture();
    }

    /// <summary>
    /// Stops frame delivery and waits deterministically for any in-flight <c>FrameArrived</c> callback
    /// to complete before downstream GPU resources (<see cref="Video.GpuVideoProcessor"/> and
    /// <see cref="Encoding.MfH264Encoder"/>) are disposed.
    /// </summary>
    public void StopCapture()
    {
        _ = StopCaptureAndWaitForIdle(timeoutMs: 3000);
    }

    private bool StopCaptureAndWaitForIdle(int timeoutMs)
    {
        bool alreadyStopped;
        lock (_callbackGate)
        {
            alreadyStopped = Volatile.Read(ref _stopped) != 0;
            Volatile.Write(ref _stopped, 1);
        }

        if (!alreadyStopped)
        {
            try
            {
                _captureItem.Closed -= OnCaptureItemClosed;
            }
            catch
            {
                // Ignore event detachment errors if WinRT item is already closed.
            }

            try
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
            catch
            {
                // Ignore event detachment errors if frame pool is already closing.
            }

            SafeDispose(_captureSession);
        }

        // Wait deterministically for any in-flight GPU frame callback to finish.
        try
        {
            return _frameIdleEvent.Wait(timeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return true;
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

        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
            {
                if (CaptureInterop.TrySetMinUpdateInterval(captureSession, TimeSpan.FromMilliseconds(1)))
                {
                    EngineLogger.Info("WgcFrameCapture", "Configured WGC MinUpdateInterval=1ms (unthrottled high-refresh capture).");
                }
            }
        }
        catch
        {
            // MinUpdateInterval requires Windows 11 24H2+; ignore if unavailable.
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        _onSessionClosed();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Atomically check stop state, claim the single in-flight slot, and reset _frameIdleEvent
        // under _callbackGate so StopCapture() can never observe _frameIdleEvent signaled mid-entry.
        lock (_callbackGate)
        {
            if (Volatile.Read(ref _stopped) != 0 || _inFlightFrame)
            {
                DrainAndDiscardFrameSafe(sender);
                return;
            }

            _inFlightFrame = true;
            _frameIdleEvent.Reset();
        }

        try
        {
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

            using IDirect3DSurface frameSurface = frame.Surface;
            using ID3D11Texture2D capturedBgraTexture = CaptureInterop.GetTextureFromSurface(frameSurface);
            if (Interlocked.Increment(ref _capturedFrameCount) == 1)
            {
                EngineLogger.Info(
                    "WgcFrameCapture",
                    $"First WGC frame captured: Size={contentSize.Width}x{contentSize.Height}, " +
                    $"Timestamp100Ns={normalizedTimestamp100Ns}, ActiveGPU=\"{AdapterName}\"");
            }

            _onFrameCaptured(capturedBgraTexture, normalizedTimestamp100Ns, _frameIntervalTicks);

            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _stopped) == 0)
            {
                EngineLogger.Warn("WgcFrameCapture", $"Frame processing error: {ex.Message}", ex);
                if (Interlocked.Increment(ref _consecutiveErrors) >= 5)
                {
                    Volatile.Write(ref _stopped, 1);
                    _onFrameError(ex);
                }
            }
        }
        finally
        {
            lock (_callbackGate)
            {
                _inFlightFrame = false;
                try
                {
                    _frameIdleEvent.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if already torn down.
                }
            }
        }
    }

    /// <summary>
    /// Phase-accurate frame-rate pacer that cleanly drops excess frames when the monitor refresh rate
    /// exceeds target 30/60 FPS (e.g. 144Hz/240Hz -&gt; exact 60 FPS without beat-frequency stutter),
    /// anchored to the shared <see cref="Stopwatch"/> session timeline so video and audio stay synchronized.
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

        bool drained = StopCaptureAndWaitForIdle(timeoutMs: 3500);
        if (!drained)
        {
            EngineLogger.Warn(
                "WgcFrameCapture",
                "In-flight WGC frame callback did not finish within 3500 ms during Dispose; skipping unsafe COM teardown.");
            return;
        }

        // Dispose in strict reverse dependency order.
        SafeDispose(_framePool);
        SafeDispose(_direct3DDevice);
        SafeDispose(_immediateContext);
        SafeDispose(_device);
        _frameIdleEvent.Dispose();
    }

    private static void SafeDispose(IDisposable? disposable)
        => EngineDisposeHelper.SafeDispose(disposable, "Błąd zwalniania zasobu WGC/D3D11");
}
