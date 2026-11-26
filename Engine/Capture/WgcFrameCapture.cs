using System.Diagnostics;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Interop;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
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
public sealed class WgcFrameCapture : IDisposable
{
    private const int FramePoolBufferCount = 2;
    private const long ClockReanchorThresholdTicks = 250 * TimeSpan.TicksPerMillisecond;

    private static readonly FeatureLevel[] SupportedFeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

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
    private readonly ManualResetEventSlim _frameIdleEvent = new(initialState: true);

    private SizeInt32 _lastContentSize;
    private bool _hasFirstFrame;
    private long _firstFrameClockTicks;
    private long _firstFrameRelativeTicks;
    private long _nextTargetRelativeTicks;
    private long _lastSubmittedTimestamp100Ns = -1;
    private int _consecutiveErrors;
    private int _capturedFrameCount;
    private int _inFlightFrame;
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
            MonitorHandle = monitorHandle;

            MatchedDeviceInfo deviceInfo = CreateD3D11DeviceForMonitor(monitorHandle, monitorDeviceName);
            device = deviceInfo.Device;
            immediateContext = deviceInfo.ImmediateContext;
            AdapterName = deviceInfo.AdapterName;
            AdapterVendorId = deviceInfo.VendorId;
            AdapterDeviceId = deviceInfo.DeviceId;
            AdapterLuid = deviceInfo.Luid;
            MatchedMonitorAdapter = deviceInfo.MatchedMonitorOutput;

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
                $"(VendorId=0x{AdapterVendorId:X4}, DeviceId=0x{AdapterDeviceId:X4}, LUID={AdapterLuid.HighPart:X8}:{AdapterLuid.LowPart:X8}, " +
                $"ExactMonitorMatch={MatchedMonitorAdapter})");
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

    public nint MonitorHandle { get; }

    public string AdapterName { get; }

    public uint AdapterVendorId { get; }

    public uint AdapterDeviceId { get; }

    public Luid AdapterLuid { get; }

    public bool MatchedMonitorAdapter { get; }

    public SizeInt32 ContentSize => _lastContentSize;

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
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

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

        // Wait deterministically for any in-flight GPU frame callback to finish.
        _frameIdleEvent.Wait(2000);
    }

    /// <summary>
    /// Enumerates DXGI adapters via <see cref="IDXGIFactory1.EnumAdapters1"/> and each adapter's
    /// <see cref="IDXGIAdapter.EnumOutputs"/> to locate the exact <see cref="IDXGIAdapter1"/> whose
    /// <see cref="OutputDescription1.Monitor"/> handle matches <paramref name="targetMonitorHandle"/>.
    /// Returns <see langword="null"/> only if no DXGI output matches.
    /// </summary>
    public static IDXGIAdapter1? FindAdapterForMonitor(nint targetMonitorHandle)
    {
        if (targetMonitorHandle == nint.Zero)
        {
            return null;
        }

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                Result enumAdapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
                if (enumAdapterResult.Failure || adapter is null)
                {
                    break;
                }

                bool matched = false;
                try
                {
                    AdapterDescription1 adapterDesc = adapter.Description1;
                    string adapterName = SanitizeNativeString(adapterDesc.Description, "Unknown GPU");

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        Result enumOutputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                        if (enumOutputResult.Failure || output is null)
                        {
                            break;
                        }

                        using (output)
                        {
                            nint outputMonitor = GetOutputMonitorHandle(output, out string outputDeviceName);
                            if (outputMonitor == targetMonitorHandle)
                            {
                                EngineLogger.Info(
                                    "WgcFrameCapture",
                                    $"Matched HMONITOR=0x{targetMonitorHandle:X} ({outputDeviceName}) -> " +
                                    $"DXGI Adapter[{adapterIndex}] \"{adapterName}\" " +
                                    $"(VendorId=0x{adapterDesc.VendorId:X4}, LUID={adapterDesc.Luid.HighPart:X8}:{adapterDesc.Luid.LowPart:X8})");
                                matched = true;
                                return adapter;
                            }
                        }
                    }
                }
                finally
                {
                    if (!matched)
                    {
                        adapter.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "WgcFrameCapture",
                $"DXGI adapter/output enumeration failed for HMONITOR=0x{targetMonitorHandle:X}; falling back to default hardware adapter.",
                ex);
        }

        EngineLogger.Warn(
            "WgcFrameCapture",
            $"No DXGI output matched HMONITOR=0x{targetMonitorHandle:X}; falling back to default hardware adapter.");
        return null;
    }

    private static nint GetOutputMonitorHandle(IDXGIOutput output, out string deviceName)
    {
        try
        {
            using IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
            if (output6 is not null)
            {
                OutputDescription1 desc1 = output6.Description1;
                deviceName = SanitizeNativeString(desc1.DeviceName, string.Empty);
                return desc1.Monitor;
            }
        }
        catch
        {
            // Fall back to base IDXGIOutput.Description if IDXGIOutput6 is unavailable on an older driver.
        }

        OutputDescription desc = output.Description;
        deviceName = SanitizeNativeString(desc.DeviceName, string.Empty);
        return desc.Monitor;
    }

    private static MatchedDeviceInfo CreateD3D11DeviceForMonitor(nint targetMonitorHandle, string? monitorDeviceName)
    {
        using IDXGIAdapter1? matchedAdapter = FindAdapterForMonitor(targetMonitorHandle);

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        bool usedMatchedAdapter = false;

        if (matchedAdapter is not null)
        {
            // Direct3D11 contract: when passing an explicit IDXGIAdapter, DriverType MUST be DriverType.Unknown.
            if (TryCreateDeviceOnAdapter(
                    matchedAdapter,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    out device,
                    out context))
            {
                usedMatchedAdapter = true;
            }
            else if (TryCreateDeviceOnAdapter(
                         matchedAdapter,
                         DeviceCreationFlags.BgraSupport,
                         out device,
                         out context))
            {
                usedMatchedAdapter = true;
                EngineLogger.Warn(
                    "WgcFrameCapture",
                    $"Matched GPU adapter \"{SanitizeNativeString(matchedAdapter.Description1.Description, "Unknown GPU")}\" rejected VideoSupport flag; initialized with BgraSupport only.");
            }
            else
            {
                EngineLogger.Warn(
                    "WgcFrameCapture",
                    $"D3D11CreateDevice failed on monitor-matched adapter \"{SanitizeNativeString(matchedAdapter.Description1.Description, "Unknown GPU")}\"; falling back to default hardware adapter.");
            }
        }

        if (device is null || context is null)
        {
            Result hr = D3D11.D3D11CreateDevice(
                nint.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                SupportedFeatureLevels,
                out device,
                out FeatureLevel _,
                out context);

            if (hr.Failure || device is null || context is null)
            {
                SafeDispose(context);
                SafeDispose(device);

                EngineLogger.Warn(
                    "WgcFrameCapture",
                    $"Default hardware D3D11CreateDevice with VideoSupport failed (HR=0x{hr.Code:X8}); retrying with BgraSupport.");

                hr = D3D11.D3D11CreateDevice(
                    nint.Zero,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    SupportedFeatureLevels,
                    out device,
                    out FeatureLevel _,
                    out context);

                hr.CheckError();
            }
        }

        if (device is null || context is null)
        {
            SafeDispose(context);
            SafeDispose(device);
            throw new InvalidOperationException(
                $"Nie udało się utworzyć sprzętowego urządzenia Direct3D11 dla monitora '{monitorDeviceName ?? "domyślnego"}'.");
        }

        try
        {
            using ID3D11Multithread? multithread = device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);

            (string adapterName, uint vendorId, uint deviceId, Luid luid) = QueryActiveAdapterInfo(device);

            return new MatchedDeviceInfo(
                device,
                context,
                adapterName,
                vendorId,
                deviceId,
                luid,
                usedMatchedAdapter);
        }
        catch
        {
            SafeDispose(context);
            SafeDispose(device);
            throw;
        }
    }

    private static bool TryCreateDeviceOnAdapter(
        IDXGIAdapter1 adapter,
        DeviceCreationFlags flags,
        out ID3D11Device? device,
        out ID3D11DeviceContext? context)
    {
        Result hr = D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            flags,
            SupportedFeatureLevels,
            out device,
            out FeatureLevel _,
            out context);

        if (hr.Success && device is not null && context is not null)
        {
            return true;
        }

        SafeDispose(context);
        SafeDispose(device);
        device = null;
        context = null;
        return false;
    }

    private static (string AdapterName, uint VendorId, uint DeviceId, Luid Luid) QueryActiveAdapterInfo(ID3D11Device device)
    {
        try
        {
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIAdapter1? adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            if (adapter1 is not null)
            {
                AdapterDescription1 desc1 = adapter1.Description1;
                string name = SanitizeNativeString(desc1.Description, "Unknown GPU");
                return (name, desc1.VendorId, desc1.DeviceId, desc1.Luid);
            }

            AdapterDescription desc = adapter.Description;
            string fallbackName = SanitizeNativeString(desc.Description, "Unknown GPU");
            return (fallbackName, desc.VendorId, desc.DeviceId, desc.Luid);
        }
        catch
        {
            return ("Unknown GPU", 0u, 0u, default);
        }
    }

    private static string SanitizeNativeString(string? raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        int nullIdx = raw.IndexOf('\0');
        string cleaned = (nullIdx >= 0 ? raw[..nullIdx] : raw).Trim();
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
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
                    Interlocked.Exchange(ref _stopped, 1);
                    _onFrameError(ex);
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

        StopCapture();

        // Dispose in strict reverse dependency order.
        SafeDispose(_framePool);
        SafeDispose(_direct3DDevice);
        SafeDispose(_immediateContext);
        SafeDispose(_device);
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
            Debug.WriteLine($"Błąd zwalniania zasobu WGC/D3D11: {ex.Message}");
        }
    }

    private readonly record struct MatchedDeviceInfo(
        ID3D11Device Device,
        ID3D11DeviceContext ImmediateContext,
        string AdapterName,
        uint VendorId,
        uint DeviceId,
        Luid Luid,
        bool MatchedMonitorOutput);
}
