using System.IO;

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

    public bool StartWithWindows { get; set; } = false;

    public bool StartRecordingOnLaunch { get; set; } = true;

    /// <summary>
    /// Clamps and sanitizes settings values to supported ranges and defaults.
    /// </summary>
    public void Normalize()
    {
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

        SystemAudioDeviceId = string.IsNullOrWhiteSpace(SystemAudioDeviceId)
            ? null
            : SystemAudioDeviceId.Trim();

        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId))
        {
            MicrophoneDeviceId = null;
        }
        else if (string.Equals(MicrophoneDeviceId.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            MicrophoneDeviceId = "none";
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
}
