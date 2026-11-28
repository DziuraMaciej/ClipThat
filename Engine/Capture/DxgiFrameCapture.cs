using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Interop;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClipThat.Engine.Capture;

/// <summary>
/// High-refresh-rate-friendly DXGI Desktop Duplication (<see cref="IDXGIOutputDuplication"/>) capture
/// backend. Unlike Windows.Graphics.Capture with cursor capture enabled, DXGI Desktop Duplication
/// never forces Windows DWM to demote the live display's hardware cursor plane (keeping 144Hz/165Hz/240Hz
/// cursors natively smooth in games), while still compositing hardware pointer shapes cleanly onto
/// recorded frames via <see cref="DxgiCursorCompositor"/>.
/// </summary>
public sealed class DxgiFrameCapture : IFrameCaptureSource
{
    private const int MaxConsecutiveAccessLostRetries = 8;
    private const int MaxConsecutiveCaptureErrors = 6;
    private const int AccessLostRetryDelayMs = 150;
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    private static readonly Format[] PreferredDuplicationFormats =
    {
        Format.B8G8R8A8_UNorm,
        Format.R10G10B10A2_UNorm,
        Format.R16G16B16A16_Float
    };

    private readonly string? _monitorDeviceName;
    private readonly long _frameIntervalTicks;
    private readonly Stopwatch _sessionClock;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediateContext;
    private readonly DxgiCursorCompositor _cursorCompositor;
    private readonly Action<ID3D11Texture2D, long, long> _onFrameCaptured;
    private readonly Action _onSessionClosed;
    private readonly Action<Exception> _onFrameError;
    private readonly ManualResetEventSlim _stopEvent = new(initialState: false);

    private nint _monitorHandle;
    private IDXGIOutputDuplication? _duplication;
    private Thread? _captureThread;
    private byte[] _pointerShapeBuffer = Array.Empty<byte>();
    private bool _cursorVisible;
    private int _cursorX;
    private int _cursorY;
    private int _captureWidth;
    private int _captureHeight;
    private bool _isHdrPqOutput;
    private long _lastSubmittedTimestamp100Ns = -1;
    private int _capturedFrameCount;
    private int _started;
    private int _stopped;
    private int _disposed;

    public DxgiFrameCapture(
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

        _monitorDeviceName = monitorDeviceName;
        int normalizedFps = targetFps == 30 ? 30 : 60;
        _frameIntervalTicks = TimeSpan.TicksPerSecond / normalizedFps;
        _sessionClock = sessionClock;
        _onFrameCaptured = onFrameCaptured;
        _onSessionClosed = onSessionClosed;
        _onFrameError = onFrameError;

        ID3D11Device? device = null;
        ID3D11DeviceContext? immediateContext = null;
        IDXGIOutput? matchedOutput = null;
        IDXGIOutputDuplication? duplication = null;
        DxgiCursorCompositor? cursorCompositor = null;

        try
        {
            nint monitorHandle = CaptureInterop.ResolveMonitorHandle(monitorDeviceName);
            _monitorHandle = monitorHandle;

            MonitorDeviceFactory.MatchedDeviceInfo deviceInfo = MonitorDeviceFactory.CreateD3D11DeviceForMonitor(
                monitorHandle,
                monitorDeviceName,
                logTag: "DxgiFrameCapture",
                requireExactMonitorMatch: true,
                out matchedOutput);

            device = deviceInfo.Device;
            immediateContext = deviceInfo.ImmediateContext;
            AdapterName = deviceInfo.AdapterName;
            AdapterLuid = deviceInfo.Luid;

            if (matchedOutput is null)
            {
                throw new InvalidOperationException("Nie udało się zlokalizować wyjścia DXGI dla wybranego monitora.");
            }

            duplication = CreateDuplication(device, matchedOutput, out OutduplDescription duplDesc);
            if (duplDesc.Rotation != ModeRotation.Identity && duplDesc.Rotation != ModeRotation.Unspecified)
            {
                // Rotated portrait monitors are handled natively with upright orientation by WGC.
                throw new NotSupportedException(
                    $"Monitor ma obróconą orientację ({duplDesc.Rotation}); przełączanie na WGC.");
            }

            cursorCompositor = new DxgiCursorCompositor(device, immediateContext);

            _device = device;
            _immediateContext = immediateContext;
            _duplication = duplication;
            _cursorCompositor = cursorCompositor;
            _captureWidth = (int)duplDesc.ModeDescription.Width;
            _captureHeight = (int)duplDesc.ModeDescription.Height;
            _isHdrPqOutput = IsHdrPqOutput(matchedOutput);

            EngineLogger.Info(
                "DxgiFrameCapture",
                $"Initialized DXGI Desktop Duplication: Monitor={monitorDeviceName ?? "(Primary)"} (HMONITOR=0x{monitorHandle:X}), " +
                $"Mode={duplDesc.ModeDescription.Width}x{duplDesc.ModeDescription.Height} ({duplDesc.ModeDescription.Format}), " +
                $"ActiveGPU=\"{AdapterName}\" (VendorId=0x{deviceInfo.VendorId:X4}, DeviceId=0x{deviceInfo.DeviceId:X4}, " +
                $"LUID={AdapterLuid.HighPart:X8}:{AdapterLuid.LowPart:X8})");
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "DxgiFrameCapture",
                $"DXGI Desktop Duplication unavailable for monitor '{monitorDeviceName ?? "(Primary)"}': {ex.Message}");

            SafeDispose(cursorCompositor);
            SafeDispose(duplication);
            SafeDispose(immediateContext);
            SafeDispose(device);
            _stopEvent.Dispose();
            throw;
        }
        finally
        {
            SafeDispose(matchedOutput);
        }
    }

    public ID3D11Device Device => _device;

    public ID3D11DeviceContext ImmediateContext => _immediateContext;

    public string AdapterName { get; }

    public Luid AdapterLuid { get; }

    public string CaptureBackendName => "DXGI Desktop Duplication";

    public int CaptureWidth => Volatile.Read(ref _captureWidth);

    public int CaptureHeight => Volatile.Read(ref _captureHeight);

    public int CapturedFrameCount => Volatile.Read(ref _capturedFrameCount);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "ClipThat.DxgiFrameCapture",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void StopCapture()
    {
        _ = StopCaptureAndWaitForExit(timeoutMs: 3000);
    }

    private bool StopCaptureAndWaitForExit(int timeoutMs)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            try
            {
                _stopEvent.Set();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }

        // Only clear _captureThread after Join confirms exit so Dispose() never tears down COM
        // resources if an earlier StopCapture() call timed out while the loop was still running.
        Thread? thread = Volatile.Read(ref _captureThread);
        if (thread is not null && thread != Thread.CurrentThread)
        {
            bool exited = thread.Join(timeoutMs);
            if (exited)
            {
                Interlocked.CompareExchange(ref _captureThread, null, thread);
            }

            return exited;
        }

        return true;
    }

    private void CaptureLoop()
    {
        // DuplicateOutput1 requires Per-Monitor DPI Awareness V2 on the calling thread.
        TrySetThreadDpiAwarenessV2();

        // Request 1 ms system timer resolution while capturing so 60 FPS pacing and short sleeps
        // do not snap to Windows' default 15.6 ms timer tick.
        nint highResTimer = CreateWaitableTimerExW(
            nint.Zero,
            nint.Zero,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        try
        {
            CaptureLoopCore(highResTimer);
        }
        finally
        {
            if (highResTimer != nint.Zero)
            {
                CloseHandle(highResTimer);
            }
        }
    }

    private void CaptureLoopCore(nint highResTimer)
    {
        int consecutiveAccessLost = 0;
        int consecutiveErrors = 0;
        long nextSlotTicks = _sessionClock.Elapsed.Ticks;

        while (Volatile.Read(ref _stopped) == 0)
        {
            try
            {
                IDXGIOutputDuplication? duplication = _duplication;
                if (duplication is null)
                {
                    if (!TryRecreateDuplication())
                    {
                        if (++consecutiveAccessLost >= MaxConsecutiveAccessLostRetries)
                        {
                            EngineLogger.Warn(
                                "DxgiFrameCapture",
                                $"DXGI Desktop Duplication could not be reacquired after {consecutiveAccessLost} attempts; requesting pipeline recovery.");
                            TriggerSessionClosed();
                            return;
                        }

                        if (_stopEvent.Wait(AccessLostRetryDelayMs))
                        {
                            return;
                        }

                        continue;
                    }

                    consecutiveAccessLost = 0;
                    nextSlotTicks = _sessionClock.Elapsed.Ticks;
                    duplication = _duplication;
                    if (duplication is null)
                    {
                        continue;
                    }
                }

                // Phase-pace frame acquisition to target 60/30 FPS so high-refresh displays (144Hz/165Hz/240Hz)
                // and 1000Hz gaming mice coalesce cleanly inside DXGI without spinning the CPU or starving DWM.
                long nowTicks = _sessionClock.Elapsed.Ticks;
                long remainingTicks = nextSlotTicks - nowTicks;
                if (remainingTicks > TimeSpan.TicksPerMillisecond)
                {
                    long waitTicks = Math.Min(remainingTicks, 30 * TimeSpan.TicksPerMillisecond);
                    if (WaitForPacingSlot(highResTimer, waitTicks))
                    {
                        return;
                    }

                    continue;
                }

                // Wait up to 100 ms on a completely idle desktop (10 wakeups/s instead of 60);
                // AcquireNextFrame still returns immediately on any screen or hardware cursor change.
                const uint acquireTimeoutMs = 100u;
                Result hr = duplication.AcquireNextFrame(acquireTimeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? desktopResource);

                if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    desktopResource?.Dispose();
                    // AcquireNextFrame already waited a full frame interval; keep nextSlotTicks ready
                    // so the next desktop/cursor update wakes immediately without double latency.
                    nextSlotTicks = _sessionClock.Elapsed.Ticks;
                    continue;
                }

                if (hr == Vortice.DXGI.ResultCode.AccessLost ||
                    hr == Vortice.DXGI.ResultCode.SessionDisconnected)
                {
                    desktopResource?.Dispose();
                    EngineLogger.Info("DxgiFrameCapture", $"DXGI Desktop Duplication interrupted (HR=0x{hr.Code:X8}); reacquiring output.");
                    SafeDispose(_duplication);
                    _duplication = null;

                    if (_stopEvent.Wait(AccessLostRetryDelayMs))
                    {
                        return;
                    }

                    continue;
                }

                if (hr.Failure)
                {
                    desktopResource?.Dispose();
                    hr.CheckError();
                }

                bool hasDesktopUpdate = false;
                bool hasCursorUpdate = false;

                try
                {
                    using (desktopResource)
                    {
                        if (frameInfo.LastMouseUpdateTime != 0)
                        {
                            _cursorVisible = frameInfo.PointerPosition.Visible;
                            _cursorX = frameInfo.PointerPosition.Position.X;
                            _cursorY = frameInfo.PointerPosition.Position.Y;
                            hasCursorUpdate = true;

                            if (frameInfo.PointerShapeBufferSize > 0)
                            {
                                ReadPointerShape(duplication, frameInfo.PointerShapeBufferSize);
                            }
                        }

                        bool hasDesktopImage =
                            frameInfo.LastPresentTime != 0 ||
                            frameInfo.AccumulatedFrames > 0 ||
                            frameInfo.TotalMetadataBufferSize > 0;
                        // Avoid copying an uninitialized black surface on pure mouse-only updates before the first desktop present.
                        if (desktopResource is not null &&
                            (hasDesktopImage || (!_cursorCompositor.HasDesktopFrame && frameInfo.LastMouseUpdateTime == 0)))
                        {
                            using ID3D11Texture2D acquiredTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                            _cursorCompositor.UpdateDesktopTexture(acquiredTexture);
                            hasDesktopUpdate = true;
                        }
                    }
                }
                finally
                {
                    // Always release the DXGI desktop frame immediately before compositing or encoding
                    // so DWM / the display pipeline is never held back.
                    try
                    {
                        duplication.ReleaseFrame();
                    }
                    catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.AccessLost)
                    {
                        SafeDispose(_duplication);
                        _duplication = null;
                    }
                }

                consecutiveAccessLost = 0;
                consecutiveErrors = 0;

                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                // Advance pacing target smoothly without bursting after idle desktop periods.
                long afterAcquireTicks = _sessionClock.Elapsed.Ticks;
                nextSlotTicks += _frameIntervalTicks;
                if (afterAcquireTicks - nextSlotTicks > _frameIntervalTicks)
                {
                    nextSlotTicks = afterAcquireTicks + _frameIntervalTicks;
                }

                if (_cursorCompositor.HasDesktopFrame &&
                    (hasDesktopUpdate || hasCursorUpdate || Volatile.Read(ref _capturedFrameCount) == 0))
                {
                    ID3D11Texture2D outputFrame = _cursorCompositor.ComposeFrame(_cursorVisible, _cursorX, _cursorY, _isHdrPqOutput);

                    long timestamp100Ns = afterAcquireTicks;
                    if (timestamp100Ns <= _lastSubmittedTimestamp100Ns)
                    {
                        timestamp100Ns = _lastSubmittedTimestamp100Ns + 1;
                    }

                    _lastSubmittedTimestamp100Ns = timestamp100Ns;

                    if (Interlocked.Increment(ref _capturedFrameCount) == 1)
                    {
                        Texture2DDescription outDesc = outputFrame.Description;
                        EngineLogger.Info(
                            "DxgiFrameCapture",
                            $"First DXGI frame captured: Size={outDesc.Width}x{outDesc.Height}, " +
                            $"Timestamp100Ns={timestamp100Ns}, ActiveGPU=\"{AdapterName}\"");
                    }

                    _onFrameCaptured(outputFrame, timestamp100Ns, _frameIntervalTicks);
                }
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }

                EngineLogger.Warn("DxgiFrameCapture", $"Frame acquisition error: {ex.Message}", ex);
                SafeDispose(_duplication);
                _duplication = null;

                if (++consecutiveErrors >= MaxConsecutiveCaptureErrors)
                {
                    Interlocked.Exchange(ref _stopped, 1);
                    _onFrameError(ex);
                    return;
                }

                if (_stopEvent.Wait(AccessLostRetryDelayMs))
                {
                    return;
                }
            }
        }
    }

    private unsafe void ReadPointerShape(IDXGIOutputDuplication duplication, uint requiredSize)
    {
        if (requiredSize == 0 || requiredSize > 16 * 1024 * 1024)
        {
            return;
        }

        if (_pointerShapeBuffer.Length < requiredSize)
        {
            _pointerShapeBuffer = new byte[requiredSize];
        }

        fixed (byte* bufferPtr = _pointerShapeBuffer)
        {
            Result hr = duplication.GetFramePointerShape(
                (uint)_pointerShapeBuffer.Length,
                (nint)bufferPtr,
                out uint bytesWritten,
                out OutduplPointerShapeInfo shapeInfo);

            if (hr.Success && bytesWritten > 0)
            {
                _cursorCompositor.UpdateCursorShape(_pointerShapeBuffer, (int)bytesWritten, shapeInfo);
            }
        }
    }

    private bool TryRecreateDuplication()
    {
        try
        {
            nint currentTargetMonitor = _monitorHandle;
            if (!string.IsNullOrWhiteSpace(_monitorDeviceName))
            {
                try
                {
                    currentTargetMonitor = CaptureInterop.ResolveMonitorHandle(_monitorDeviceName);
                }
                catch
                {
                    // Fall back to the previously known monitor handle.
                }
            }

            using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();

            for (uint outputIndex = 0; ; outputIndex++)
            {
                Result enumHr = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                if (enumHr.Failure || output is null)
                {
                    output?.Dispose();
                    break;
                }

                using (output)
                {
                    nint outputMonitor = MonitorDeviceFactory.GetOutputMonitorHandle(output, out string outputDeviceName);
                    bool monitorMatched =
                        outputMonitor == currentTargetMonitor ||
                        outputMonitor == _monitorHandle ||
                        (!string.IsNullOrWhiteSpace(_monitorDeviceName) &&
                         string.Equals(outputDeviceName, _monitorDeviceName, StringComparison.OrdinalIgnoreCase));

                    if (!monitorMatched)
                    {
                        continue;
                    }

                    IDXGIOutputDuplication newDuplication = CreateDuplication(_device, output, out OutduplDescription duplDesc);
                    if (duplDesc.Rotation != ModeRotation.Identity && duplDesc.Rotation != ModeRotation.Unspecified)
                    {
                        newDuplication.Dispose();
                        return false;
                    }

                    _monitorHandle = outputMonitor;
                    _duplication = newDuplication;
                    Volatile.Write(ref _captureWidth, (int)duplDesc.ModeDescription.Width);
                    Volatile.Write(ref _captureHeight, (int)duplDesc.ModeDescription.Height);
                    _isHdrPqOutput = IsHdrPqOutput(output);
                    EngineLogger.Info(
                        "DxgiFrameCapture",
                        $"Reacquired DXGI Desktop Duplication ({duplDesc.ModeDescription.Width}x{duplDesc.ModeDescription.Height}, {duplDesc.ModeDescription.Format}, HdrPq={_isHdrPqOutput}).");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            EngineLogger.Warn("DxgiFrameCapture", $"Failed to reacquire DXGI Desktop Duplication: {ex.Message}");
        }

        return false;
    }

    private static IDXGIOutputDuplication CreateDuplication(
        ID3D11Device device,
        IDXGIOutput output,
        out OutduplDescription description)
    {
        nint previousDpi = TrySetThreadDpiAwarenessV2();
        try
        {
            using IDXGIOutput5? output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
            if (output5 is not null)
            {
                try
                {
                    IDXGIOutputDuplication dupl1 = output5.DuplicateOutput1(device, PreferredDuplicationFormats);
                    description = dupl1.Description;
                    return dupl1;
                }
                catch (SharpGenException)
                {
                    // Fall back to IDXGIOutput1.DuplicateOutput if DuplicateOutput1 is rejected on this driver/mode.
                }
            }

            using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
            IDXGIOutputDuplication dupl = output1.DuplicateOutput(device);
            description = dupl.Description;
            return dupl;
        }
        finally
        {
            if (previousDpi != nint.Zero)
            {
                TryRestoreThreadDpiAwareness(previousDpi);
            }
        }
    }

    private static bool IsHdrPqOutput(IDXGIOutput output)
    {
        try
        {
            using IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
            // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 == 12 (HDR10 ST.2084 PQ)
            return output6 is not null && (int)output6.Description1.ColorSpace == 12;
        }
        catch
        {
            return false;
        }
    }

    private void TriggerSessionClosed()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _onSessionClosed();
        }
    }

    private static nint TrySetThreadDpiAwarenessV2()
    {
        try
        {
            return SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
            return nint.Zero;
        }
    }

    private static void TryRestoreThreadDpiAwareness(nint previousContext)
    {
        try
        {
            SetThreadDpiAwarenessContext(previousContext);
        }
        catch
        {
            // Ignore DPI context restore errors.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool exitedCleanly = StopCaptureAndWaitForExit(timeoutMs: 3500);
        if (!exitedCleanly)
        {
            EngineLogger.Warn(
                "DxgiFrameCapture",
                "Capture thread did not exit within 3500 ms during Dispose; skipping unsafe COM teardown to prevent native crash.");
            return;
        }

        // Dispose in strict reverse dependency order.
        SafeDispose(_duplication);
        _duplication = null;
        SafeDispose(_cursorCompositor);
        SafeDispose(_immediateContext);
        SafeDispose(_device);
        _stopEvent.Dispose();
    }

    private static void SafeDispose(IDisposable? disposable)
        => EngineDisposeHelper.SafeDispose(disposable, "Błąd zwalniania zasobu DXGI/D3D11");

    private bool WaitForPacingSlot(nint highResTimer, long waitTicks100Ns)
    {
        if (highResTimer != nint.Zero && waitTicks100Ns > 0)
        {
            long dueTime = -waitTicks100Ns;
            if (SetWaitableTimer(highResTimer, ref dueTime, 0, nint.Zero, nint.Zero, false))
            {
                _ = WaitForSingleObject(highResTimer, 40u);
                return Volatile.Read(ref _stopped) != 0 || _stopEvent.IsSet;
            }
        }

        int sleepMs = (int)(waitTicks100Ns / TimeSpan.TicksPerMillisecond);
        return sleepMs > 0 && _stopEvent.Wait(sleepMs);
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002u;
    private const uint TimerAllAccess = 0x001F0003u;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(
        nint lpTimerAttributes,
        nint lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        nint hTimer,
        ref long lpDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
