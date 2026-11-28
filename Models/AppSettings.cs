using System.IO;
using ClipThat.Services;

namespace ClipThat.Models;

/// <summary>
/// Persisted user configuration for ClipThat replay recording and startup behavior.
/// </summary>
public sealed class AppSettings
{
    public const uint DefaultHotkeyModifiers = 0x0001u; // MOD_ALT
    public const uint DefaultHotkeyVirtualKey = 0x79u;  // VK_F10

    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "ClipThat");

    public string? SelectedMonitorDeviceName { get; set; }

    public int BufferDurationMinutes { get; set; } = 5;

    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.P1080;

    public int Fps { get; set; } = 60;

    public string OutputDirectory { get; set; } = DefaultOutputDirectory;

    public uint HotkeyModifiers { get; set; } = DefaultHotkeyModifiers;

    public uint HotkeyVirtualKey { get; set; } = DefaultHotkeyVirtualKey;

    public string? SystemAudioDeviceId { get; set; }

    public string? MicrophoneDeviceId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool StartRecordingOnLaunch { get; set; } = true;

    /// <summary>
    /// Clamps and sanitizes settings values to supported ranges and defaults.
    /// </summary>
    public void Normalize()
    {
        SelectedMonitorDeviceName = string.IsNullOrWhiteSpace(SelectedMonitorDeviceName)
            ? null
            : SelectedMonitorDeviceName.Trim();

        BufferDurationMinutes = Math.Clamp(BufferDurationMinutes, 1, 10);
        Fps = Fps == 30 ? 30 : 60;

        if (!Enum.IsDefined(Resolution))
        {
            Resolution = ResolutionPreset.P1080;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = DefaultOutputDirectory;
        }
        else
        {
            string trimmedDir = OutputDirectory.Trim();
            if (HasInvalidWindowsPathChars(trimmedDir))
            {
                OutputDirectory = DefaultOutputDirectory;
            }
            else
            {
                try
                {
                    OutputDirectory = Path.GetFullPath(trimmedDir);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    OutputDirectory = DefaultOutputDirectory;
                }
            }
        }

        HotkeyModifiers &= 0x000Fu;
        bool isFunctionKey = HotkeyVirtualKey is >= 0x70u and <= 0x87u;
        if (HotkeyVirtualKey == 0u || HotkeyVirtualKey > 0xFFu || (HotkeyModifiers == 0u && !isFunctionKey))
        {
            HotkeyModifiers = DefaultHotkeyModifiers;
            HotkeyVirtualKey = DefaultHotkeyVirtualKey;
        }

        SystemAudioDeviceId = string.IsNullOrWhiteSpace(SystemAudioDeviceId)
            ? null
            : SystemAudioDeviceId.Trim();

        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId))
        {
            MicrophoneDeviceId = null;
        }
        else if (string.Equals(MicrophoneDeviceId.Trim(), AudioDeviceService.DisabledDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            MicrophoneDeviceId = AudioDeviceService.DisabledDeviceId;
        }
        else
        {
            MicrophoneDeviceId = MicrophoneDeviceId.Trim();
        }
    }

    /// <summary>
    /// Creates an isolated copy of the current settings instance.
    /// </summary>
    public AppSettings Clone() => new()
    {
        SelectedMonitorDeviceName = SelectedMonitorDeviceName,
        BufferDurationMinutes = BufferDurationMinutes,
        Resolution = Resolution,
        Fps = Fps,
        OutputDirectory = OutputDirectory,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey,
        SystemAudioDeviceId = SystemAudioDeviceId,
        MicrophoneDeviceId = MicrophoneDeviceId,
        StartWithWindows = StartWithWindows,
        StartRecordingOnLaunch = StartRecordingOnLaunch
    };

    private static bool HasInvalidWindowsPathChars(string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c < 0x20 || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return true;
            }

            // Allow ':' only as a Windows drive specifier at index 1 (e.g. "C:\...").
            if (c == ':' && i != 1)
            {
                return true;
            }
        }

        return false;
    }
}
