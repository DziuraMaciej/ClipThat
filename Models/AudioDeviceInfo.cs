namespace ClipThat.Models;

/// <summary>
/// Describes an active Windows WASAPI playback or capture endpoint selectable in settings.
/// </summary>
public sealed class AudioDeviceInfo
{
    /// <summary>
    /// Core Audio endpoint ID, or <see langword="null"/> for the system default device option.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Human-readable device name shown in UI dropdowns.
    /// </summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>
    /// Indicates whether this entry represents the dynamic system default sentinel option.
    /// </summary>
    public bool IsDefaultOption { get; init; }

    /// <summary>
    /// Indicates whether this entry represents the explicit disabled sentinel option (e.g. microphone off).
    /// </summary>
    public bool IsDisabledOption { get; init; }

    /// <summary>
    /// Indicates whether this concrete endpoint is currently Windows' active default multimedia endpoint.
    /// </summary>
    public bool IsSystemDefault { get; init; }

    public override string ToString()
    {
        if (IsDefaultOption || IsDisabledOption)
        {
            return FriendlyName;
        }

        return IsSystemDefault ? $"{FriendlyName} [Domyślne]" : FriendlyName;
    }
}
