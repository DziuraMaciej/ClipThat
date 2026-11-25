using System.Windows;
using ClipThat.Engine;
using ClipThat.Services;
using ClipThat.UI.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ClipThat.UI;

/// <summary>
/// Main FluentWindow hosting Polish navigation tabs for Settings and Clips Browser.
/// Closing this window destroys its UI tree to keep background RAM usage minimal.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly WindowPageProvider _pageProvider;

    public MainWindow(
        SettingsService settingsService,
        DisplayService displayService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        IReplayEngine replayEngine)
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);

        _pageProvider = new WindowPageProvider(
            settingsService,
            displayService,
            resourceEstimatorService,
            startupService,
            replayEngine);

        RootNavigation.SetPageProviderService(_pageProvider);
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        RootNavigation.Navigate(typeof(SettingsPage));
    }

    protected override void OnClosed(EventArgs e)
    {
        _pageProvider.DetachAll();
        base.OnClosed(e);
    }

    private sealed class WindowPageProvider(
        SettingsService settingsService,
        DisplayService displayService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        IReplayEngine replayEngine) : INavigationViewPageProvider
    {
        private SettingsPage? _settingsPage;
        private ClipsPage? _clipsPage;

        public object? GetPage(Type pageType)
        {
            if (pageType == typeof(SettingsPage))
            {
                return _settingsPage ??= new SettingsPage(
                    settingsService,
                    displayService,
                    resourceEstimatorService,
                    startupService,
                    replayEngine);
            }

            if (pageType == typeof(ClipsPage))
            {
                return _clipsPage ??= new ClipsPage(settingsService, replayEngine);
            }

            return null;
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
