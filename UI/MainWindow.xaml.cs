using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClipThat.Engine;
using ClipThat.Services;
using ClipThat.UI.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace ClipThat.UI;

/// <summary>
/// Main FluentWindow hosting Polish navigation tabs for Settings and Clips Browser.
/// Clicking the close button closes this window to free WPF memory while background capture stays in the system tray
/// (or hides it temporarily when a clip export is still running).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private const string NavigationRailBackgroundTag = "ClipThatNavRailBackground";

    // Window Active — translucent Mica glass tints preserving relative brightness across every tier
    private static readonly SolidColorBrush ActiveRailBrush = UiBrushHelper.CreateFrozenBrush(0x88, 0x0A, 0x0A, 0x0A);
    private static readonly SolidColorBrush ActiveCardBrush = UiBrushHelper.CreateFrozenBrush(0x52, 0x18, 0x18, 0x18);
    private static readonly SolidColorBrush ActiveClipItemBrush = UiBrushHelper.CreateFrozenBrush(0x38, 0x24, 0x24, 0x24);
    private static readonly SolidColorBrush ActiveWorkspaceBrush = UiBrushHelper.CreateFrozenBrush(0x16, 0xFF, 0xFF, 0xFF);

    // Window Inactive — solid opaque 3-tier dark grays
    private static readonly SolidColorBrush InactiveRailBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x14, 0x14, 0x14);
    private static readonly SolidColorBrush InactiveCardBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x20, 0x20, 0x20);
    private static readonly SolidColorBrush InactiveClipItemBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x26, 0x26, 0x26);
    private static readonly SolidColorBrush InactiveWorkspaceBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x2B, 0x2B, 0x2B);

    private readonly WindowPageProvider _pageProvider;
    private bool _allowClose;

    public MainWindow(
        SettingsService settingsService,
        DisplayService displayService,
        AudioDeviceService audioDeviceService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        GlobalHotkeyService globalHotkeyService,
        IReplayEngine replayEngine,
        ClipEditorService clipEditorService)
    {
        InitializeComponent();

        App.ApplySystemAccent();

        _pageProvider = new WindowPageProvider(
            settingsService,
            displayService,
            audioDeviceService,
            resourceEstimatorService,
            startupService,
            globalHotkeyService,
            replayEngine,
            clipEditorService);

        RootNavigation.SetPageProviderService(_pageProvider);
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool isVisible = e.NewValue is true;
        _pageProvider.NotifyHostWindowVisibilityChanged(isVisible);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        EnsureNavigationPaneBackground();
        UpdateSurfaceBrushes(IsActive);
        RootNavigation.Navigate(typeof(SettingsPage));
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        UpdateSurfaceBrushes(IsActive);
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        UpdateSurfaceBrushes(IsActive);
    }

    private void EnsureNavigationPaneBackground()
    {
        RootNavigation.ApplyTemplate();

        if (VisualTreeHelper.GetChildrenCount(RootNavigation) == 0 ||
            VisualTreeHelper.GetChild(RootNavigation, 0) is not Grid templateRootGrid ||
            templateRootGrid.ColumnDefinitions.Count < 2)
        {
            return;
        }

        if (templateRootGrid.Children.OfType<Border>().Any(border => Equals(border.Tag, NavigationRailBackgroundTag)))
        {
            return;
        }

        // WPF-UI's LeftNavigationViewTemplate does not bind NavigationView.Background to its left pane,
        // so attach a background layer scoped strictly to column 0 (leaving column 1 transparent for Mica).
        var railBackground = new Border
        {
            Tag = NavigationRailBackgroundTag,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Grid.SetColumn(railBackground, 0);
        Panel.SetZIndex(railBackground, -1);
        railBackground.SetBinding(
            Border.BackgroundProperty,
            new Binding(nameof(Control.Background)) { Source = RootNavigation });

        templateRootGrid.Children.Insert(0, railBackground);
    }

    private static void UpdateSurfaceBrushes(bool isActive)
    {
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        if (isActive)
        {
            resources["AppRailBrush"] = ActiveRailBrush;
            resources["AppCardBrush"] = ActiveCardBrush;
            resources["AppClipItemBrush"] = ActiveClipItemBrush;
            resources["AppWorkspaceBrush"] = ActiveWorkspaceBrush;
        }
        else
        {
            resources["AppRailBrush"] = InactiveRailBrush;
            resources["AppCardBrush"] = InactiveCardBrush;
            resources["AppClipItemBrush"] = InactiveClipItemBrush;
            resources["AppWorkspaceBrush"] = InactiveWorkspaceBrush;
        }
    }

    /// <summary>
    /// Allows the window to close permanently during application shutdown or error recovery,
    /// bypassing the hide-to-tray behavior in <see cref="OnClosing"/>.
    /// </summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _pageProvider.IsExporting)
        {
            // Keep the window alive while a background clip export is actively reporting progress.
            e.Cancel = true;
            Hide();
            return;
        }

        // Otherwise allow MainWindow to close cleanly when sent to tray so WPF visual trees,
        // Mica backdrops, and pages release ~60-110 MB RAM while background capture continues.
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        Activated -= MainWindow_Activated;
        Deactivated -= MainWindow_Deactivated;
        IsVisibleChanged -= MainWindow_IsVisibleChanged;
        _pageProvider.DetachAll();
        base.OnClosed(e);
    }

    private sealed class WindowPageProvider(
        SettingsService settingsService,
        DisplayService displayService,
        AudioDeviceService audioDeviceService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        GlobalHotkeyService globalHotkeyService,
        IReplayEngine replayEngine,
        ClipEditorService clipEditorService) : INavigationViewPageProvider
    {
        private SettingsPage? _settingsPage;
        private ClipsPage? _clipsPage;

        public bool IsExporting => _clipsPage?.IsExporting == true;

        public object? GetPage(Type pageType)
        {
            if (pageType == typeof(SettingsPage))
            {
                return _settingsPage ??= new SettingsPage(
                    settingsService,
                    displayService,
                    audioDeviceService,
                    resourceEstimatorService,
                    startupService,
                    globalHotkeyService,
                    replayEngine);
            }

            if (pageType == typeof(ClipsPage))
            {
                return _clipsPage ??= new ClipsPage(settingsService, replayEngine, clipEditorService);
            }

            return null;
        }

        public void NotifyHostWindowVisibilityChanged(bool isVisible)
        {
            _settingsPage?.OnHostWindowVisibilityChanged(isVisible);
            _clipsPage?.OnHostWindowVisibilityChanged(isVisible);
        }

        public void DetachAll()
        {
            _settingsPage?.Detach();
            _clipsPage?.Detach();
            _settingsPage = null;
            _clipsPage = null;
        }
    }
}
