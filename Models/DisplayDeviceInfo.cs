namespace ClipThat.Models;

/// <summary>
/// Represents pixel coordinates and dimensions of a physical display in virtual desktop space.
/// </summary>
public readonly record struct DisplayBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Describes an enumerated physical monitor available for capture.
/// </summary>
public sealed class DisplayDeviceInfo
{
    public string DeviceName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    public DisplayBounds Bounds { get; init; }

    public bool IsPrimary { get; init; }

    public int RefreshRate { get; init; }

    public int DisplayIndex { get; init; }

    public override string ToString()
    {
        string primarySuffix = IsPrimary ? " [Główny]" : string.Empty;
        return $"{DisplayIndex}. {FriendlyName} ({Bounds.Width}x{Bounds.Height} @ {RefreshRate} Hz){primarySuffix}";
    }
}
