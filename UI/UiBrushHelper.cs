using System.Windows.Media;

namespace ClipThat.UI;

/// <summary>
/// Shared helper for creating thread-safe frozen <see cref="SolidColorBrush"/> instances.
/// </summary>
internal static class UiBrushHelper
{
    public static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        => CreateFrozenBrush(Color.FromRgb(r, g, b));

    public static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        => CreateFrozenBrush(Color.FromArgb(a, r, g, b));
}
