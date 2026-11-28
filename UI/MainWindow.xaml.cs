using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClipThat.Engine;
using ClipThat.Services;
using ClipThat.UI.Dialogs;
using ClipThat.UI.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace ClipThat.UI;

/// <summary>
/// Main FluentWindow hosting Polish navigation tabs for Settings and Clips Browser,
/// plus a pinned bottom-rail in-app updater action.
/// Clicking the close button closes this window to free WPF memory while background capture stays in the system tray
/// (or hides it temporarily when a clip export is still running, auto-closing once export finishes).
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
    private readonly AppUpdateService _updateService;
    private CancellationTokenSource? _updateCheckCts;
    private bool _isCheckingForUpdates;
    private bool _allowClose;
    private bool _isClosed;

    public MainWindow(
        SettingsService settingsService,
        DisplayService displayService,
        AudioDeviceService audioDeviceService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        GlobalHotkeyService globalHotkeyService,
        IReplayEngine replayEngine,
        ClipEditorService clipEditorService,
        AppUpdateService? updateService = null)
    {
        InitializeComponent();

        _updateService = updateService ?? new AppUpdateService();
        _pageProvider = new WindowPageProvider(
            settingsService,
            displayService,
            audioDeviceService,
            resourceEstimatorService,
            startupService,
            globalHotkeyService,
            replayEngine,
            clipEditorService,
            OnClipsExportCompleted);

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

    private void OnClipsExportCompleted()
    {
        if (_isClosed || IsVisible || _pageProvider.IsExporting || _isCheckingForUpdates)
        {
            return;
        }

        // The window was only kept alive hidden in tray while a clip export finished;
        // close it now so WPF visual trees and Mica memory are reclaimed cleanly.
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosed && !IsVisible && !_pageProvider.IsExporting && !_isCheckingForUpdates)
            {
                Close();
            }
        });
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

    private void UpdateNavItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (UpdateNavItem.IsMouseCaptured)
        {
            UpdateNavItem.ReleaseMouseCapture();
        }

        e.Handled = true;
        _ = CheckForUpdatesFromUiAsync();
    }

    private void UpdateNavItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            _ = CheckForUpdatesFromUiAsync();
        }
    }

    private async Task CheckForUpdatesFromUiAsync()
    {
        if (_isCheckingForUpdates || _isClosed)
        {
            return;
        }

        _isCheckingForUpdates = true;
        UpdateNavItem.Content = "Sprawdzanie...";
        UpdateNavItem.IsEnabled = false;

        CancelUpdateCheckCts();
        var cts = new CancellationTokenSource();
        _updateCheckCts = cts;

        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(cts.Token);
            if (_isClosed || cts.IsCancellationRequested)
            {
                return;
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                {
                    string versionDetail = result.ShortRemoteSha is not null
                        ? $"Korzystasz już z najnowszej wersji ClipThat ({result.ShortRemoteSha})."
                        : "Korzystasz już z najnowszej wersji ClipThat.";

                    ShowStyledDialog(
                        title: "Wszystko jest aktualne",
                        message: versionDetail,
                        confirmText: "OK",
                        cancelText: null,
                        icon: SymbolRegular.CheckmarkCircle24,
                        tone: ConfirmDialogTone.Success);
                    break;
                }

                case UpdateCheckStatus.UpdateAvailable:
                {
                    if (_pageProvider.IsExporting)
                    {
                        ShowStyledDialog(
                            title: "Trwa eksport klipu",
                            message: "Dostępna jest aktualizacja, ale trwa obecnie eksport nagrania. Poczekaj na zakończenie eksportu i spróbuj ponownie.",
                            confirmText: "OK",
                            cancelText: null,
                            icon: SymbolRegular.ArrowDownload24,
                            tone: ConfirmDialogTone.Accent);
                        break;
                    }

                    if (result.IsGitRepo && result.HasUncommittedGitChanges)
                    {
                        ShowStyledDialog(
                            title: "Lokalne niezapisane zmiany",
                            message: "Dostępna jest nowa wersja ClipThat, ale w lokalnym repozytorium Git znajdują się niezapisane zmiany.\n\nZapisz je (git commit lub git stash) przed pobraniem aktualizacji, aby nie utracić swojej pracy.",
                            confirmText: "OK",
                            cancelText: null,
                            icon: SymbolRegular.Warning24,
                            tone: ConfirmDialogTone.Accent);
                        break;
                    }

                    string changeSummary = BuildUpdatePromptMessage(result);
                    bool confirmed = ShowStyledDialog(
                        title: "Dostępna aktualizacja",
                        message: changeSummary,
                        confirmText: "Aktualizuj",
                        cancelText: "Później",
                        icon: SymbolRegular.ArrowDownload24,
                        tone: ConfirmDialogTone.Accent);

                    if (!confirmed || _isClosed)
                    {
                        break;
                    }

                    try
                    {
                        AppUpdateService.LaunchDetachedUpdate(result, Environment.ProcessId);
                        if (Application.Current is App app)
                        {
                            await app.RequestExitForUpdateAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_isClosed)
                        {
                            ShowStyledDialog(
                                title: "Błąd aktualizacji",
                                message: $"Nie udało się uruchomić procesu aktualizacji:\n{ex.Message}",
                                confirmText: "OK",
                                cancelText: null,
                                icon: SymbolRegular.Warning24,
                                tone: ConfirmDialogTone.Destructive);
                        }
                    }

                    break;
                }

                case UpdateCheckStatus.Error:
                default:
                {
                    ShowStyledDialog(
                        title: "Nie udało się sprawdzić aktualizacji",
                        message: result.ErrorMessage ?? "Wystąpił nieznany błąd podczas sprawdzania aktualizacji.",
                        confirmText: "OK",
                        cancelText: null,
                        icon: SymbolRegular.Warning24,
                        tone: ConfirmDialogTone.Destructive);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed while checking for updates.
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ShowStyledDialog(
                    title: "Nie udało się sprawdzić aktualizacji",
                    message: $"Wystąpił nieoczekiwany błąd: {ex.Message}",
                    confirmText: "OK",
                    cancelText: null,
                    icon: SymbolRegular.Warning24,
                    tone: ConfirmDialogTone.Destructive);
            }
        }
        finally
        {
            if (!_isClosed)
            {
                UpdateNavItem.Content = "Aktualizacja";
                UpdateNavItem.IsEnabled = true;
            }

            _isCheckingForUpdates = false;
        }
    }

    private static string BuildUpdatePromptMessage(UpdateCheckResult result)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.CommitMessage))
        {
            string headline = result.CommitMessage.Length > 140
                ? result.CommitMessage[..137] + "..."
                : result.CommitMessage;
            details.Add($"Ostatnia zmiana: {headline}");
        }

        if (result.CommitDateUtc.HasValue)
        {
            string localDate = result.CommitDateUtc.Value
                .ToLocalTime()
                .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            details.Add($"Data publikacji: {localDate}");
        }

        if (result.ShortRemoteSha is not null)
        {
            details.Add($"Wersja: {result.ShortRemoteSha}");
        }

        string detailsBlock = details.Count > 0
            ? "\n\n" + string.Join("\n", details)
            : string.Empty;

        return $"Dostępna jest nowa wersja ClipThat.{detailsBlock}\n\nCzy chcesz teraz pobrać aktualizację? Aplikacja zamknie się na chwilę, zaktualizuje pliki i uruchomi ponownie.";
    }

    private bool ShowStyledDialog(
        string title,
        string message,
        string confirmText,
        string? cancelText,
        SymbolRegular icon,
        ConfirmDialogTone tone)
    {
        var dialog = new ConfirmDialog(
            title: title,
            message: message,
            confirmText: confirmText,
            cancelText: cancelText,
            icon: icon,
            tone: tone);

        if (IsVisible)
        {
            dialog.Owner = this;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    private void CancelUpdateCheckCts()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _updateCheckCts, null);
        if (cts is null)
        {
            return;
        }

        try { cts.Cancel(); } catch { /* ignore */ }
        cts.Dispose();
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
        _isClosed = true;
        CancelUpdateCheckCts();
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
        ClipEditorService clipEditorService,
        Action onClipsExportCompleted) : INavigationViewPageProvider
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
                if (_clipsPage is null)
                {
                    _clipsPage = new ClipsPage(settingsService, replayEngine, clipEditorService);
                    _clipsPage.ExportCompleted += onClipsExportCompleted;
                }

                return _clipsPage;
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
            if (_clipsPage is not null)
            {
                _clipsPage.ExportCompleted -= onClipsExportCompleted;
            }

            _settingsPage?.Detach();
            _clipsPage?.Detach();
            _settingsPage = null;
            _clipsPage = null;
        }
    }
}
