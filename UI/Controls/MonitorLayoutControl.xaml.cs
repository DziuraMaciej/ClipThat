using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipThat.Models;
using ClipThat.Services;
using Microsoft.Win32;

namespace ClipThat.UI.Controls;

/// <summary>
/// Interactive Windows 11-style proportional monitor arrangement canvas with live display hot-plug updates
/// and temporary on-screen display identification badges.
/// </summary>
public partial class MonitorLayoutControl : UserControl
{
    private const double CanvasPadding = 16.0;
    private const double IdentifyBadgeDipSize = 132.0;
    private const double IdentifyBadgeCornerMarginDip = 24.0;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010u;
    private const uint SwpShowWindow = 0x0040u;
    private const uint MonitorDefaultToNearest = 2u;
    private const int MdtEffectiveDpi = 0;
    private static readonly nint HwndTopMost = new(-1);

    private static readonly SolidColorBrush DefaultFillBrush = UiBrushHelper.CreateFrozenBrush(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush DefaultBorderBrush = UiBrushHelper.CreateFrozenBrush(0x45, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush HoverFillBrush = UiBrushHelper.CreateFrozenBrush(0x34, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush HoverBorderBrush = UiBrushHelper.CreateFrozenBrush(0x70, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush IdentifyOverlayFillBrush = UiBrushHelper.CreateFrozenBrush(0xDD, 0x18, 0x18, 0x1B);
    private static readonly SolidColorBrush PrimaryTextBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0xF3, 0xF3, 0xF3);
    private static readonly SolidColorBrush SecondaryTextBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0xA8, 0xA8, 0xA8);

    private readonly List<Window> _identifyWindows = new();
    private DisplayService? _displayService;
    private IReadOnlyList<DisplayDeviceInfo> _displays = Array.Empty<DisplayDeviceInfo>();
    private DispatcherTimer? _identifyTimer;
    private bool _displayEventsSubscribed;

    public MonitorLayoutControl()
    {
        InitializeComponent();
        Loaded += MonitorLayoutControl_Loaded;
        Unloaded += MonitorLayoutControl_Unloaded;
    }

    /// <summary>
    /// Raised when the user clicks a monitor tile to select it for capture.
    /// </summary>
    public event EventHandler<DisplayDeviceInfo>? MonitorSelected;

    public string? SelectedMonitorDeviceName { get; private set; }

    public void Initialize(DisplayService displayService, string? selectedMonitorDeviceName)
    {
        ArgumentNullException.ThrowIfNull(displayService);
        _displayService = displayService;
        SelectedMonitorDeviceName = selectedMonitorDeviceName;
        RefreshDisplays();
    }

    public void RefreshDisplays()
    {
        if (_displayService is null)
        {
            return;
        }

        _displays = _displayService.GetDisplays();
        IdentifyButton.IsEnabled = _displays.Count > 0;

        DisplayDeviceInfo? activeSelection = ResolveSelectedDisplay();
        SelectedMonitorDeviceName = activeSelection?.DeviceName;
        UpdateSummaryText(activeSelection);
        RenderMonitorTiles();
    }

    public void IdentifyDisplays()
    {
        CloseIdentifyOverlays();

        IReadOnlyList<DisplayDeviceInfo> displays = _displayService is not null
            ? _displayService.GetDisplays()
            : _displays;

        if (displays.Count == 0)
        {
            return;
        }

        foreach (DisplayDeviceInfo display in displays)
        {
            Window overlay = CreateIdentifyOverlayWindow(display);
            _identifyWindows.Add(overlay);
            overlay.Show();
            PositionOverlayBottomLeft(overlay, display.Bounds);
        }

        _identifyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _identifyTimer.Tick += OnIdentifyTimerTick;
        _identifyTimer.Start();
    }

    public void Cleanup()
    {
        UnsubscribeDisplayEvents();
        CloseIdentifyOverlays();
    }

    private DisplayDeviceInfo? ResolveSelectedDisplay()
    {
        if (_displays.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(SelectedMonitorDeviceName))
        {
            DisplayDeviceInfo? match = _displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, SelectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays[0];
    }

    private void UpdateSummaryText(DisplayDeviceInfo? selected)
    {
        if (selected is null)
        {
            SelectedMonitorSummaryText.Text = "Brak aktywnych monitorów.";
            return;
        }

        string primaryLabel = selected.IsPrimary ? " · Ekran główny" : string.Empty;
        SelectedMonitorSummaryText.Text =
            $"Wybrany: Monitor {selected.DisplayIndex} · {selected.Bounds.Width}×{selected.Bounds.Height} • {selected.RefreshRate} Hz{primaryLabel}";
    }

    private void RenderMonitorTiles()
    {
        LayoutCanvas.Children.Clear();

        if (_displays.Count == 0)
        {
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;

        double canvasWidth = LayoutCanvas.ActualWidth;
        double canvasHeight = LayoutCanvas.ActualHeight;
        if (canvasWidth <= CanvasPadding * 2 || canvasHeight <= CanvasPadding * 2)
        {
            return;
        }

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        foreach (DisplayDeviceInfo display in _displays)
        {
            minX = Math.Min(minX, display.Bounds.X);
            minY = Math.Min(minY, display.Bounds.Y);
            maxX = Math.Max(maxX, display.Bounds.X + display.Bounds.Width);
            maxY = Math.Max(maxY, display.Bounds.Y + display.Bounds.Height);
        }

        double unionWidth = Math.Max(1, maxX - minX);
        double unionHeight = Math.Max(1, maxY - minY);

        double availableWidth = Math.Max(1.0, canvasWidth - (CanvasPadding * 2));
        double availableHeight = Math.Max(1.0, canvasHeight - (CanvasPadding * 2));
        double scale = Math.Min(availableWidth / unionWidth, availableHeight / unionHeight);

        double scaledTotalWidth = unionWidth * scale;
        double scaledTotalHeight = unionHeight * scale;
        double offsetX = (canvasWidth - scaledTotalWidth) / 2.0;
        double offsetY = (canvasHeight - scaledTotalHeight) / 2.0;

        foreach (DisplayDeviceInfo display in _displays)
        {
            double rawLeft = offsetX + ((display.Bounds.X - minX) * scale);
            double rawTop = offsetY + ((display.Bounds.Y - minY) * scale);
            double rawWidth = Math.Max(28.0, display.Bounds.Width * scale);
            double rawHeight = Math.Max(28.0, display.Bounds.Height * scale);

            double gapX = rawWidth > 36.0 ? 2.0 : 0.0;
            double gapY = rawHeight > 36.0 ? 2.0 : 0.0;

            double tileLeft = rawLeft + gapX;
            double tileTop = rawTop + gapY;
            double tileWidth = Math.Max(24.0, rawWidth - (gapX * 2.0));
            double tileHeight = Math.Max(24.0, rawHeight - (gapY * 2.0));

            bool isSelected = string.Equals(
                display.DeviceName,
                SelectedMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase);

            Border tile = CreateMonitorTile(display, tileWidth, tileHeight, isSelected);
            Canvas.SetLeft(tile, tileLeft);
            Canvas.SetTop(tile, tileTop);
            LayoutCanvas.Children.Add(tile);
        }
    }

    private Border CreateMonitorTile(DisplayDeviceInfo display, double tileWidth, double tileHeight, bool isSelected)
    {
        var contentStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        double indexFontSize = Math.Clamp(tileHeight * 0.28, 16.0, 30.0);
        contentStack.Children.Add(new TextBlock
        {
            Text = display.DisplayIndex.ToString(CultureInfo.InvariantCulture),
            FontSize = indexFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = PrimaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        if (tileWidth >= 78.0 && tileHeight >= 52.0)
        {
            contentStack.Children.Add(new TextBlock
            {
                Text = $"{display.Bounds.Width}×{display.Bounds.Height} • {display.RefreshRate} Hz",
                FontSize = Math.Clamp(tileHeight * 0.095, 10.0, 12.0),
                Foreground = SecondaryTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        if (display.IsPrimary && tileWidth >= 88.0 && tileHeight >= 76.0)
        {
            var primaryBadgeText = new TextBlock
            {
                Text = "Ekran główny",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold
            };
            primaryBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "SystemAccentColorSecondaryBrush");

            var primaryBadge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = primaryBadgeText
            };
            primaryBadge.SetResourceReference(Border.BackgroundProperty, "SystemAccentTintStrongBrush");

            contentStack.Children.Add(primaryBadge);
        }

        string primaryNote = display.IsPrimary ? "\nEkran główny" : string.Empty;
        var tile = new Border
        {
            Width = tileWidth,
            Height = tileHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = isSelected ? new Thickness(2.5) : new Thickness(1.2),
            Cursor = Cursors.Hand,
            Padding = new Thickness(6),
            ToolTip = $"{display.FriendlyName}\n{display.Bounds.Width}×{display.Bounds.Height} • {display.RefreshRate} Hz{primaryNote}",
            Child = contentStack
        };

        if (isSelected)
        {
            tile.SetResourceReference(Border.BackgroundProperty, "SystemAccentTintBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "SystemAccentColorPrimaryBrush");
        }
        else
        {
            tile.Background = DefaultFillBrush;
            tile.BorderBrush = DefaultBorderBrush;
        }

        if (!isSelected)
        {
            tile.MouseEnter += (_, _) =>
            {
                tile.Background = HoverFillBrush;
                tile.BorderBrush = HoverBorderBrush;
            };
            tile.MouseLeave += (_, _) =>
            {
                tile.Background = DefaultFillBrush;
                tile.BorderBrush = DefaultBorderBrush;
            };
        }

        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SelectDisplay(display);
        };

        return tile;
    }

    private void SelectDisplay(DisplayDeviceInfo display)
    {
        bool changed = !string.Equals(SelectedMonitorDeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase);
        SelectedMonitorDeviceName = display.DeviceName;
        UpdateSummaryText(display);
        RenderMonitorTiles();

        if (changed)
        {
            MonitorSelected?.Invoke(this, display);
        }
    }

    private static Window CreateIdentifyOverlayWindow(DisplayDeviceInfo display)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = display.DisplayIndex.ToString(CultureInfo.InvariantCulture),
            FontSize = 60,
            FontWeight = FontWeights.Bold,
            Foreground = PrimaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineHeight = 64
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{display.Bounds.Width}×{display.Bounds.Height}",
            FontSize = 11.5,
            Foreground = SecondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var container = new Border
        {
            Background = IdentifyOverlayFillBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Child = stack
        };
        container.SetResourceReference(Border.BorderBrushProperty, "SystemAccentColorPrimaryBrush");

        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
            Width = IdentifyBadgeDipSize,
            Height = IdentifyBadgeDipSize,
            Left = -20000,
            Top = -20000,
            Content = container
        };
    }

    private static void PositionOverlayBottomLeft(Window overlay, DisplayBounds bounds)
    {
        nint hwnd = new WindowInteropHelper(overlay).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        nint currentExStyle = GetWindowLongPtrCompat(hwnd, GwlExStyle);
        nint targetExStyle = (nint)((long)currentExStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
        SetWindowLongPtrCompat(hwnd, GwlExStyle, targetExStyle);

        double dpi = GetMonitorDpi(bounds);
        double scale = dpi / 96.0;

        int physWidth = (int)Math.Round(IdentifyBadgeDipSize * scale);
        int physHeight = (int)Math.Round(IdentifyBadgeDipSize * scale);
        int margin = (int)Math.Round(IdentifyBadgeCornerMarginDip * scale);

        int physX = bounds.X + margin;
        int physY = bounds.Y + bounds.Height - physHeight - margin;

        SetWindowPos(hwnd, HwndTopMost, physX, physY, physWidth, physHeight, SwpNoActivate | SwpShowWindow);
    }

    private static double GetMonitorDpi(DisplayBounds bounds)
    {
        try
        {
            var pt = new PointNative
            {
                X = bounds.X + (bounds.Width / 2),
                Y = bounds.Y + (bounds.Height / 2)
            };

            nint hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (hMonitor != nint.Zero &&
                GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX;
            }
        }
        catch
        {
            // Fall back to standard 96 DPI when shcore.dll is unavailable.
        }

        return 96.0;
    }

    private void OnIdentifyTimerTick(object? sender, EventArgs e)
    {
        CloseIdentifyOverlays();
    }

    private void CloseIdentifyOverlays()
    {
        if (_identifyTimer is not null)
        {
            _identifyTimer.Stop();
            _identifyTimer.Tick -= OnIdentifyTimerTick;
            _identifyTimer = null;
        }

        if (_identifyWindows.Count == 0)
        {
            return;
        }

        foreach (Window window in _identifyWindows)
        {
            try
            {
                window.Close();
            }
            catch
            {
                // Ignore window teardown errors.
            }
        }

        _identifyWindows.Clear();
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderMonitorTiles();
    }

    private void IdentifyButton_Click(object sender, RoutedEventArgs e)
    {
        IdentifyDisplays();
    }

    private void MonitorLayoutControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_displayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
            _displayEventsSubscribed = true;
        }

        RefreshDisplays();
    }

    private void MonitorLayoutControl_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    private void UnsubscribeDisplayEvents()
    {
        if (_displayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
            _displayEventsSubscribed = false;
        }
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(RefreshDisplays);
    }

    private static nint GetWindowLongPtrCompat(nint hwnd, int index)
        => nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new nint(GetWindowLong32(hwnd, index));

    private static nint SetWindowLongPtrCompat(nint hwnd, int index, nint newValue)
        => nint.Size == 8 ? SetWindowLongPtr64(hwnd, index, newValue) : new nint(SetWindowLong32(hwnd, index, newValue.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }
}
