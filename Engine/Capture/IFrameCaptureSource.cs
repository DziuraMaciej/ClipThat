using ClipThat.Engine.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClipThat.Engine.Capture;

/// <summary>
/// Common contract for GPU desktop capture backends (<see cref="DxgiFrameCapture"/> primary and
/// <see cref="WgcFrameCapture"/> fallback) bound to a monitor-matched <see cref="ID3D11Device"/>.
/// </summary>
public interface IFrameCaptureSource : IDisposable
{
    ID3D11Device Device { get; }

    ID3D11DeviceContext ImmediateContext { get; }

    nint MonitorHandle { get; }

    string AdapterName { get; }

    uint AdapterVendorId { get; }

    uint AdapterDeviceId { get; }

    Luid AdapterLuid { get; }

    bool MatchedMonitorAdapter { get; }

    string CaptureBackendName { get; }

    int CapturedFrameCount { get; }

    void Start();

    void StopCapture();
}

/// <summary>
/// Shared DXGI adapter/output matching and Direct3D 11 device creation used by both
/// <see cref="DxgiFrameCapture"/> and <see cref="WgcFrameCapture"/>.
/// </summary>
internal static class MonitorDeviceFactory
{
    private static readonly FeatureLevel[] SupportedFeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    /// <summary>
    /// Enumerates DXGI adapters via <see cref="IDXGIFactory1.EnumAdapters1"/> and each adapter's
    /// <see cref="IDXGIAdapter.EnumOutputs"/> to locate the exact <see cref="IDXGIAdapter1"/> whose
    /// output <c>HMONITOR</c> matches <paramref name="targetMonitorHandle"/>.
    /// </summary>
    public static IDXGIAdapter1? FindAdapterForMonitor(nint targetMonitorHandle, string logTag)
    {
        if (!TryFindAdapterAndOutput(targetMonitorHandle, logTag, out IDXGIAdapter1? adapter, out IDXGIOutput? output))
        {
            return null;
        }

        output?.Dispose();
        return adapter;
    }

    /// <summary>
    /// Locates both the exact <see cref="IDXGIAdapter1"/> and <see cref="IDXGIOutput"/> driving
    /// <paramref name="targetMonitorHandle"/>. The caller owns and must dispose both returned objects.
    /// </summary>
    public static bool TryFindAdapterAndOutput(
        nint targetMonitorHandle,
        string logTag,
        out IDXGIAdapter1? matchedAdapter,
        out IDXGIOutput? matchedOutput)
    {
        matchedAdapter = null;
        matchedOutput = null;

        if (targetMonitorHandle == nint.Zero)
        {
            return false;
        }

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                Result enumAdapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
                if (enumAdapterResult.Failure || adapter is null)
                {
                    adapter?.Dispose();
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
                            output?.Dispose();
                            break;
                        }

                        bool outputMatched = false;
                        try
                        {
                            nint outputMonitor = GetOutputMonitorHandle(output, out string outputDeviceName);
                            if (outputMonitor == targetMonitorHandle)
                            {
                                EngineLogger.Info(
                                    logTag,
                                    $"Matched HMONITOR=0x{targetMonitorHandle:X} ({outputDeviceName}) -> " +
                                    $"DXGI Adapter[{adapterIndex}] \"{adapterName}\" " +
                                    $"(VendorId=0x{adapterDesc.VendorId:X4}, LUID={adapterDesc.Luid.HighPart:X8}:{adapterDesc.Luid.LowPart:X8})");
                                matchedAdapter = adapter;
                                matchedOutput = output;
                                outputMatched = true;
                                matched = true;
                                return true;
                            }
                        }
                        finally
                        {
                            if (!outputMatched)
                            {
                                output.Dispose();
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
                logTag,
                $"DXGI adapter/output enumeration failed for HMONITOR=0x{targetMonitorHandle:X}.",
                ex);
        }

        EngineLogger.Warn(
            logTag,
            $"No DXGI output matched HMONITOR=0x{targetMonitorHandle:X}.");
        return false;
    }

    public static MatchedDeviceInfo CreateD3D11DeviceForMonitor(
        nint targetMonitorHandle,
        string? monitorDeviceName,
        string logTag,
        bool requireExactMonitorMatch,
        out IDXGIOutput? matchedOutput)
    {
        matchedOutput = null;
        IDXGIAdapter1? matchedAdapter = null;
        IDXGIOutput? candidateOutput = null;

        bool foundExact = TryFindAdapterAndOutput(targetMonitorHandle, logTag, out matchedAdapter, out candidateOutput);

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        bool usedMatchedAdapter = false;

        try
        {
            if (foundExact && matchedAdapter is not null)
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
                        logTag,
                        $"Matched GPU adapter \"{SanitizeNativeString(matchedAdapter.Description1.Description, "Unknown GPU")}\" rejected VideoSupport flag; initialized with BgraSupport only.");
                }
                else
                {
                    EngineLogger.Warn(
                        logTag,
                        $"D3D11CreateDevice failed on monitor-matched adapter \"{SanitizeNativeString(matchedAdapter.Description1.Description, "Unknown GPU")}\".");
                }
            }

            if ((device is null || context is null) && requireExactMonitorMatch)
            {
                throw new InvalidOperationException(
                    $"Nie znaleziono wyjścia DXGI dla monitora '{monitorDeviceName ?? "domyślnego"}'.");
            }

            if (device is null || context is null)
            {
                candidateOutput?.Dispose();
                candidateOutput = null;

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
                        logTag,
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
                throw new InvalidOperationException(
                    $"Nie udało się utworzyć sprzętowego urządzenia Direct3D11 dla monitora '{monitorDeviceName ?? "domyślnego"}'.");
            }

            using ID3D11Multithread? multithread = device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);

            (string adapterName, uint vendorId, uint deviceId, Luid luid) = QueryActiveAdapterInfo(device);

            matchedOutput = candidateOutput;
            candidateOutput = null;

            var info = new MatchedDeviceInfo(
                device,
                context,
                adapterName,
                vendorId,
                deviceId,
                luid,
                usedMatchedAdapter);

            device = null;
            context = null;
            return info;
        }
        finally
        {
            SafeDispose(candidateOutput);
            SafeDispose(matchedAdapter);
            SafeDispose(context);
            SafeDispose(device);
        }
    }

    public static nint GetOutputMonitorHandle(IDXGIOutput output, out string deviceName)
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

    public static string SanitizeNativeString(string? raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        int nullIdx = raw.IndexOf('\0');
        string cleaned = (nullIdx >= 0 ? raw[..nullIdx] : raw).Trim();
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
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
        catch
        {
            // Ignore best-effort cleanup errors.
        }
    }

    internal readonly record struct MatchedDeviceInfo(
        ID3D11Device Device,
        ID3D11DeviceContext ImmediateContext,
        string AdapterName,
        uint VendorId,
        uint DeviceId,
        Luid Luid,
        bool MatchedMonitorOutput);
}
