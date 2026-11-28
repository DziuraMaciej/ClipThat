using System.Runtime.InteropServices;
using ClipThat.Models;
using WinForms = System.Windows.Forms;

namespace ClipThat.Services;

/// <summary>
/// Enumerates physical displays using WinForms screens combined with Win32 EnumDisplaySettings.
/// </summary>
public sealed class DisplayService
{
    private const int EnumCurrentSettings = -1;

    /// <summary>
    /// Returns all active physical monitors with exact virtual desktop coordinates and refresh rates.
    /// </summary>
    public IReadOnlyList<DisplayDeviceInfo> GetDisplays()
    {
        WinForms.Screen[] screens = WinForms.Screen.AllScreens;
        var results = new List<DisplayDeviceInfo>(screens.Length);

        for (int i = 0; i < screens.Length; i++)
        {
            WinForms.Screen screen = screens[i];
            int displayIndex = i + 1;

            var devMode = new DevModeW
            {
                dmSize = (short)Marshal.SizeOf<DevModeW>()
            };

            bool hasSettings = EnumDisplaySettingsW(screen.DeviceName, EnumCurrentSettings, ref devMode);

            int x = hasSettings ? devMode.dmPosition.x : screen.Bounds.X;
            int y = hasSettings ? devMode.dmPosition.y : screen.Bounds.Y;
            int width = hasSettings && devMode.dmPelsWidth > 0 ? devMode.dmPelsWidth : screen.Bounds.Width;
            int height = hasSettings && devMode.dmPelsHeight > 0 ? devMode.dmPelsHeight : screen.Bounds.Height;
            int refreshRate = hasSettings && devMode.dmDisplayFrequency > 1 ? devMode.dmDisplayFrequency : 60;

            string friendlyName = ResolveFriendlyName(screen.DeviceName, displayIndex);

            results.Add(new DisplayDeviceInfo
            {
                DeviceName = screen.DeviceName,
                FriendlyName = friendlyName,
                Bounds = new DisplayBounds(x, y, width, height),
                IsPrimary = screen.Primary,
                RefreshRate = refreshRate,
                DisplayIndex = displayIndex
            });
        }

        return results;
    }

    /// <summary>
    /// Finds a display by device name, falling back to the primary or first available display.
    /// </summary>
    public DisplayDeviceInfo? GetDisplayByDeviceName(string? deviceName)
    {
        IReadOnlyList<DisplayDeviceInfo> displays = GetDisplays();
        if (displays.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            DisplayDeviceInfo? match = displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
    }

    private static string ResolveFriendlyName(string deviceName, int displayIndex)
    {
        var displayDevice = new DisplayDeviceW
        {
            cb = Marshal.SizeOf<DisplayDeviceW>()
        };

        if (EnumDisplayDevicesW(deviceName, 0, ref displayDevice, 0) &&
            !string.IsNullOrWhiteSpace(displayDevice.DeviceString))
        {
            string trimmed = displayDevice.DeviceString.Trim();
            if (!string.Equals(trimmed, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(trimmed, "Generic Monitor", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(trimmed, "Rodzajowy monitor PnP", StringComparison.OrdinalIgnoreCase))
            {
                return $"Monitor {displayIndex} — {trimmed}";
            }
        }

        return $"Monitor {displayIndex}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettingsW(
        string? lpszDeviceName,
        int iModeNum,
        ref DevModeW lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDeviceW lpDisplayDevice,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public PointL dmPosition;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
