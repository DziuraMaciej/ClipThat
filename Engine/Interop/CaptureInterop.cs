using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClipThat.Engine.Interop;

/// <summary>
/// Native WinRT interop interface for creating a <see cref="GraphicsCaptureItem"/> from an HWND or HMONITOR.
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow(
        [In] nint window,
        [In] ref Guid iid);

    nint CreateForMonitor(
        [In] nint monitor,
        [In] ref Guid iid);
}

/// <summary>
/// Native WinRT/DXGI interop interface for extracting an underlying DXGI/D3D11 resource pointer
/// from a WinRT <see cref="IDirect3DSurface"/> without copying pixels to CPU memory.
/// </summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] ref Guid iid);
}

/// <summary>
/// COM and WinRT interop helpers bridging Windows.Graphics.Capture with Vortice Direct3D11.
/// </summary>
internal static class CaptureInterop
{
    private const uint MonitorInfoPrimaryFlag = 0x00000001u;
    private const uint MonitorDefaultToPrimary = 0x00000001u;

    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>
    /// Native COM interface identifier (IID) for <c>ID3D11Texture2D</c>.
    /// </summary>
    internal static readonly Guid Direct3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>
    /// Resolves a Win32 <c>HMONITOR</c> handle for the requested display device name
    /// (for example <c>\\.\DISPLAY1</c>), defaulting to the primary monitor when unavailable.
    /// </summary>
    public static nint ResolveMonitorHandle(string? deviceName)
    {
        nint matchedMonitor = nint.Zero;
        nint primaryMonitor = nint.Zero;
        nint firstMonitor = nint.Zero;

        MonitorEnumProc callback = (nint hMonitor, nint _, nint _, nint _) =>
        {
            if (firstMonitor == nint.Zero)
            {
                firstMonitor = hMonitor;
            }

            var info = new MonitorInfoExW
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfoExW>()
            };

            if (GetMonitorInfoW(hMonitor, ref info))
            {
                if ((info.dwFlags & MonitorInfoPrimaryFlag) != 0 && primaryMonitor == nint.Zero)
                {
                    primaryMonitor = hMonitor;
                }

                if (!string.IsNullOrWhiteSpace(deviceName) &&
                    string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedMonitor = hMonitor;
                    return false;
                }
            }

            return true;
        };

        EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);

        if (matchedMonitor != nint.Zero)
        {
            return matchedMonitor;
        }

        if (primaryMonitor != nint.Zero)
        {
            return primaryMonitor;
        }

        if (firstMonitor != nint.Zero)
        {
            return firstMonitor;
        }

        nint fallback = MonitorFromPoint(default, MonitorDefaultToPrimary);
        if (fallback == nint.Zero)
        {
            throw new InvalidOperationException("Nie znaleziono żadnego aktywnego monitora do przechwytywania.");
        }

        return fallback;
    }

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> targeting the specified monitor handle.
    /// </summary>
    public static GraphicsCaptureItem CreateCaptureItemForMonitor(nint monitorHandle)
    {
        if (monitorHandle == nint.Zero)
        {
            throw new ArgumentException("Nieprawidłowy uchwyt monitora (HMONITOR).", nameof(monitorHandle));
        }

        using IObjectReference factoryRef = ActivationFactory.Get(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            GraphicsCaptureItemInteropIid);

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetUniqueObjectForIUnknown(factoryRef.ThisPtr);
        nint itemAbi = nint.Zero;
        try
        {
            Guid itemIid = GraphicsCaptureItemIid;
            itemAbi = interop.CreateForMonitor(monitorHandle, ref itemIid);
            if (itemAbi == nint.Zero)
            {
                throw new InvalidOperationException("Nie udało się utworzyć obiektu GraphicsCaptureItem dla monitora.");
            }

            GraphicsCaptureItem? item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemAbi);
            return item ?? throw new InvalidOperationException("Nie udało się zmarshalować obiektu GraphicsCaptureItem.");
        }
        finally
        {
            if (itemAbi != nint.Zero)
            {
                Marshal.Release(itemAbi);
            }

            Marshal.ReleaseComObject(interop);
        }
    }

    /// <summary>
    /// Resolves the requested display device name and creates a <see cref="GraphicsCaptureItem"/> for it.
    /// </summary>
    public static GraphicsCaptureItem CreateCaptureItemForDisplay(string? deviceName)
    {
        nint monitorHandle = ResolveMonitorHandle(deviceName);
        return CreateCaptureItemForMonitor(monitorHandle);
    }

    /// <summary>
    /// Creates a WinRT <see cref="IDirect3DDevice"/> bound to the supplied Vortice <see cref="ID3D11Device"/>
    /// for use with <see cref="Direct3D11CaptureFramePool"/>.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        ArgumentNullException.ThrowIfNull(d3dDevice);

        using IDXGIDevice dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        nint graphicsDeviceAbi = nint.Zero;
        try
        {
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out graphicsDeviceAbi);
            Marshal.ThrowExceptionForHR(hr);

            if (graphicsDeviceAbi == nint.Zero)
            {
                throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice zwróciło pusty wskaźnik.");
            }

            IDirect3DDevice? device = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi);
            return device ?? throw new InvalidOperationException("Nie udało się utworzyć obiektu WinRT IDirect3DDevice.");
        }
        finally
        {
            if (graphicsDeviceAbi != nint.Zero)
            {
                Marshal.Release(graphicsDeviceAbi);
            }
        }
    }

    /// <summary>
    /// Extracts the underlying GPU <see cref="ID3D11Texture2D"/> from a WinRT capture frame surface
    /// without copying pixel data to CPU RAM. The caller owns and must dispose the returned texture.
    /// </summary>
    public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        nint surfaceAbi = nint.Zero;
        nint accessAbi = nint.Zero;
        IDirect3DDxgiInterfaceAccess? access = null;

        try
        {
            surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            if (surfaceAbi == nint.Zero)
            {
                throw new InvalidOperationException("Powierzchnia przechwytywania (IDirect3DSurface) jest pusta.");
            }

            Guid accessIid = Direct3DDxgiInterfaceAccessIid;
            int hr = Marshal.QueryInterface(surfaceAbi, ref accessIid, out accessAbi);
            Marshal.ThrowExceptionForHR(hr);

            access = (IDirect3DDxgiInterfaceAccess)Marshal.GetUniqueObjectForIUnknown(accessAbi);
            Guid textureIid = Direct3D11Texture2DIid;
            nint texturePtr = access.GetInterface(ref textureIid);
            if (texturePtr == nint.Zero)
            {
                throw new InvalidOperationException("Nie udało się uzyskać wskaźnika ID3D11Texture2D z powierzchni WinRT.");
            }

            // Vortice ComObject(IntPtr) takes ownership of the COM reference returned by GetInterface.
            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            if (access is not null)
            {
                Marshal.ReleaseComObject(access);
            }

            if (accessAbi != nint.Zero)
            {
                Marshal.Release(accessAbi);
            }

            if (surfaceAbi != nint.Zero)
            {
                Marshal.Release(surfaceAbi);
            }
        }
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint lprcClip,
        MonitorEnumProc lpfnEnum,
        nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoExW lpmi);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoExW
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
