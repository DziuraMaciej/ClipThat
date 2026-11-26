using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClipThat;

/// <summary>
/// Application entry point managing background replay lifecycle and Windows system tray integration.
/// </summary>
public partial class App : Application
{
    private const long HotkeyDebounceMs = 1500;

    private readonly SettingsService _settingsService = new();
    private readonly DisplayService _displayService = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly ResourceEstimatorService _resourceEstimatorService = new();
    private readonly StartupService _startupService = new();
    private readonly ClipEditorService _clipEditorService = new();
    private readonly IReplayEngine _replayEngine;

    public App()
    {
        _replayEngine = new ReplayEngine(_resourceEstimatorService);
    }

    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _globalHotkeyService;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _saveReplayMenuItem;
    private WinForms.ToolStripMenuItem? _toggleRecordingMenuItem;
    private Drawing.Icon? _recordingTrayIcon;
    private Drawing.Icon? _pausedTrayIcon;
    private long _lastHotkeyTriggerTickMs;
    private int _hotkeySaveInProgress;
    private bool _isShuttingDown;
    private bool _userPreferenceEventsSubscribed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            ApplySystemAccent();

            if (!_userPreferenceEventsSubscribed)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _userPreferenceEventsSubscribed = true;
            }

            AppSettings settings = _settingsService.Load();

            _replayEngine.StatusChanged += OnEngineStatusChanged;
            _replayEngine.ReplaySaved += OnEngineReplaySaved;

            InitializeTrayIcon();
            InitializeGlobalHotkey(settings);

            bool startMinimized = e.Args.Any(arg =>
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (startMinimized || settings.StartRecordingOnLaunch)
            {
                try
                {
                    await _replayEngine.StartAsync(settings);
                }
                catch (Exception ex)
                {
                    _notifyIcon?.ShowBalloonTip(
                        4000,
                        "ClipThat",
                        $"Nie udało się uruchomić nagrywania: {ex.Message}",
                        WinForms.ToolTipIcon.Warning);
                }
            }

            if (!startMinimized && !settings.StartRecordingOnLaunch)
            {
                ShowMainWindow();
            }

            SyncTrayState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Nie udało się uruchomić aplikacji ClipThat:\n{ex.Message}",
                "ClipThat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    private void InitializeTrayIcon()
    {
        _recordingTrayIcon = CreateTrayIcon(isRecording: true);
        _pausedTrayIcon = CreateTrayIcon(isRecording: false);

        _saveReplayMenuItem = new WinForms.ToolStripMenuItem("Zapisz powtórkę teraz");
        _saveReplayMenuItem.Click += async (_, _) => await SaveReplayFromTrayAsync();

        _toggleRecordingMenuItem = new WinForms.ToolStripMenuItem("Wznów nagrywanie");
        _toggleRecordingMenuItem.Click += async (_, _) => await ToggleRecordingAsync();

        var openMenuItem = new WinForms.ToolStripMenuItem("Otwórz ClipThat");
        openMenuItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        var exitMenuItem = new WinForms.ToolStripMenuItem("Zamknij");
        exitMenuItem.Click += async (_, _) => await ExitApplicationAsync();

        _trayMenu = new WinForms.ContextMenuStrip();
        _trayMenu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _saveReplayMenuItem,
            _toggleRecordingMenuItem,
            new WinForms.ToolStripSeparator(),
            openMenuItem,
            new WinForms.ToolStripSeparator(),
            exitMenuItem
        });
        _trayMenu.Opening += (_, _) => SyncTrayState();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _pausedTrayIcon,
            Text = "ClipThat — Wstrzymano",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void InitializeGlobalHotkey(AppSettings settings)
    {
        _globalHotkeyService = new GlobalHotkeyService(Dispatcher);
        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;

        bool registered = _globalHotkeyService.UpdateHotkey(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
        if (!registered)
        {
            string hotkeyLabel = GlobalHotkeyService.FormatHotkey(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
            _notifyIcon?.ShowBalloonTip(
                4000,
                "ClipThat",
                $"Nie udało się zarejestrować skrótu globalnego {hotkeyLabel} — może być używany przez inną aplikację.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (_isShuttingDown || !_replayEngine.IsRecording)
        {
            return;
        }

        long nowTickMs = Environment.TickCount64;
        long previousTickMs = Volatile.Read(ref _lastHotkeyTriggerTickMs);
        if (previousTickMs != 0 && nowTickMs - previousTickMs < HotkeyDebounceMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _hotkeySaveInProgress, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastHotkeyTriggerTickMs, nowTickMs);
        _ = SaveReplayFromHotkeyAsync();
    }

    private async Task SaveReplayFromHotkeyAsync()
    {
        try
        {
            await _replayEngine.SaveReplayAsync();
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _notifyIcon?.ShowBalloonTip(
                    3000,
                    "ClipThat",
                    $"Nie udało się zapisać powtórki: {ex.Message}",
                    WinForms.ToolTipIcon.Error);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _hotkeySaveInProgress, 0);
        }
    }

    private void ShowMainWindow()
    {
        if (_isShuttingDown)
        {
            return;
        }

        try
        {
            if (_mainWindow is not null)
            {
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }

                _mainWindow.Show();
                _mainWindow.Activate();
                return;
            }

            if (_globalHotkeyService is null)
            {
                InitializeGlobalHotkey(_settingsService.Load());
            }

            var window = new MainWindow(
                _settingsService,
                _displayService,
                _audioDeviceService,
                _resourceEstimatorService,
                _startupService,
                _globalHotkeyService!,
                _replayEngine,
                _clipEditorService);

            _mainWindow = window;
            window.Closed += OnMainWindowClosed;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            if (_mainWindow is not null)
            {
                try
                {
                    _mainWindow.Closed -= OnMainWindowClosed;
                    _mainWindow.Close();
                }
                catch
                {
                    // Ignore cleanup errors for a partially initialized window.
                }

                _mainWindow = null;
            }

            string logPath = LogUiException("ShowMainWindow", ex);
            string innerDetails = ex.InnerException is not null
                ? $"\n\nPrzyczyna wewnętrzna ({ex.InnerException.GetType().Name}):\n{ex.InnerException.Message}"
                : string.Empty;

            MessageBox.Show(
                $"Nie udało się otworzyć okna głównego ClipThat:\n{ex.Message}{innerDetails}\n\nPełny zrzut błędu zapisano w:\n{logPath}",
                "ClipThat — Błąd otwierania okna",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }
    }

    private async Task SaveReplayFromTrayAsync()
    {
        try
        {
            await _replayEngine.SaveReplayAsync();
        }
        catch (Exception ex)
        {
            _notifyIcon?.ShowBalloonTip(
                3000,
                "ClipThat",
                $"Nie udało się zapisać powtórki: {ex.Message}",
                WinForms.ToolTipIcon.Error);
        }
    }

    private async Task ToggleRecordingAsync()
    {
        try
        {
            if (_replayEngine.IsRecording)
            {
                await _replayEngine.StopAsync();
            }
            else
            {
                AppSettings settings = _settingsService.Load();
                await _replayEngine.StartAsync(settings);
            }
        }
        catch (Exception ex)
        {
            _notifyIcon?.ShowBalloonTip(
                3000,
                "ClipThat",
                $"Błąd zmiany stanu nagrywania: {ex.Message}",
                WinForms.ToolTipIcon.Error);
        }
        finally
        {
            SyncTrayState();
        }
    }

    private void OnEngineStatusChanged(string _)
    {
        Dispatcher.InvokeAsync(SyncTrayState);
    }

    private void OnEngineReplaySaved(string filePath)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string fileName = Path.GetFileName(filePath);
            _notifyIcon?.ShowBalloonTip(
                2500,
                "ClipThat",
                $"Zapisano powtórkę: {fileName}",
                WinForms.ToolTipIcon.Info);
        });
    }

    private void SyncTrayState()
    {
        if (_isShuttingDown)
        {
            return;
        }

        bool isRecording = _replayEngine.IsRecording;

        if (_toggleRecordingMenuItem is not null)
        {
            _toggleRecordingMenuItem.Text = isRecording ? "Wstrzymaj nagrywanie" : "Wznów nagrywanie";
        }

        if (_saveReplayMenuItem is not null)
        {
            _saveReplayMenuItem.Enabled = isRecording;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Icon = isRecording ? _recordingTrayIcon : _pausedTrayIcon;
            _notifyIcon.Text = isRecording
                ? "ClipThat — Nagrywanie aktywne"
                : "ClipThat — Wstrzymano";
        }
    }

    public static void ApplySystemAccent()
    {
        try
        {
            ApplicationThemeManager.Apply(
                ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                updateAccent: true);
            ApplicationAccentColorManager.ApplySystemAccent();

            ResourceDictionary? resources = Current?.Resources;
            if (resources is null)
            {
                return;
            }

            Color systemAccent = ApplicationAccentColorManager.SystemAccent;
            Color primaryAccent = ApplicationAccentColorManager.SecondaryAccent;
            Color secondaryAccent = ApplicationAccentColorManager.TertiaryAccent;
            Color tertiaryAccent = BlendWithWhite(secondaryAccent, 0.35);

            resources["SystemAccentColorBrush"] = CreateFrozenBrush(systemAccent);
            resources["SystemAccentColorPrimaryBrush"] = CreateFrozenBrush(primaryAccent);
            resources["SystemAccentColorSecondaryBrush"] = CreateFrozenBrush(secondaryAccent);
            resources["SystemAccentColorTertiaryBrush"] = CreateFrozenBrush(tertiaryAccent);
            resources["SystemAccentTintBrush"] = CreateFrozenBrush(
                Color.FromArgb(0x26, primaryAccent.R, primaryAccent.G, primaryAccent.B));
            resources["SystemAccentTintStrongBrush"] = CreateFrozenBrush(
                Color.FromArgb(0x3D, primaryAccent.R, primaryAccent.G, primaryAccent.B));
            resources["SystemAccentBorderSubtleBrush"] = CreateFrozenBrush(
                Color.FromArgb(0x7A, primaryAccent.R, primaryAccent.G, primaryAccent.B));
        }
        catch (Exception ex)
        {
            LogUiException("ApplySystemAccent", ex);
        }
    }

    private static string LogUiException(string context, Exception ex)
    {
        string fullDetails = ex.ToString();
        Debug.WriteLine($"[ClipThat] {context} exception:\n{fullDetails}");
        Trace.WriteLine($"[ClipThat] {context} exception:\n{fullDetails}");

        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipThat");
        string logPath = Path.Combine(logDirectory, "ui-errors.log");

        try
        {
            Directory.CreateDirectory(logDirectory);
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}\r\n{fullDetails}\r\n{new string('-', 80)}\r\n";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Ignore file logging failures so error reporting itself never throws.
        }

        return logPath;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.InvokeAsync(ApplySystemAccent);
        }
    }

    private void UnsubscribeUserPreferenceChanged()
    {
        if (_userPreferenceEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _userPreferenceEventsSubscribed = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnsubscribeUserPreferenceChanged();
        base.OnExit(e);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color BlendWithWhite(Color color, double ratio)
    {
        byte r = (byte)Math.Clamp(Math.Round(color.R + ((255 - color.R) * ratio)), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(color.G + ((255 - color.G) * ratio)), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(color.B + ((255 - color.B) * ratio)), 0, 255);
        return Color.FromArgb(color.A, r, g, b);
    }

    private async Task ExitApplicationAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        UnsubscribeUserPreferenceChanged();

        try
        {
            if (_globalHotkeyService is not null)
            {
                _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;
                _globalHotkeyService.Dispose();
                _globalHotkeyService = null;
            }

            if (_mainWindow is not null)
            {
                _mainWindow.Closed -= OnMainWindowClosed;
                _mainWindow.Close();
                _mainWindow = null;
            }

            _replayEngine.StatusChanged -= OnEngineStatusChanged;
            _replayEngine.ReplaySaved -= OnEngineReplaySaved;

            try
            {
                await _replayEngine.StopAsync();
            }
            catch
            {
                // Ignore engine stop failures during application shutdown.
            }

            try
            {
                _replayEngine.Dispose();
            }
            catch
            {
                // Ignore engine disposal failures during application shutdown.
            }
        }
        finally
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _trayMenu?.Dispose();
            _trayMenu = null;

            _recordingTrayIcon?.Dispose();
            _recordingTrayIcon = null;

            _pausedTrayIcon?.Dispose();
            _pausedTrayIcon = null;

            _audioDeviceService.Dispose();

            Shutdown();
        }
    }

    private static Drawing.Icon CreateTrayIcon(bool isRecording)
    {
        using var bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);

            using var backgroundBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(24, 24, 27));
            using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(63, 63, 70), 2f);
            using var badgePath = CreateRoundedRectPath(new Drawing.RectangleF(2f, 2f, 28f, 28f), 7f);

            graphics.FillPath(backgroundBrush, badgePath);
            graphics.DrawPath(borderPen, badgePath);

            Drawing.Color accentColor = isRecording
                ? Drawing.Color.FromArgb(239, 68, 68)
                : Drawing.Color.FromArgb(156, 163, 175);

            using var outerRingPen = new Drawing.Pen(Drawing.Color.FromArgb(140, accentColor), 2f);
            graphics.DrawEllipse(outerRingPen, 7f, 7f, 18f, 18f);

            using var dotBrush = new Drawing.SolidBrush(accentColor);
            graphics.FillEllipse(dotBrush, 10f, 10f, 12f, 12f);
        }

        nint hIcon = bitmap.GetHicon();
        try
        {
            using Drawing.Icon tempIcon = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static Drawing.Drawing2D.GraphicsPath CreateRoundedRectPath(Drawing.RectangleF bounds, float radius)
    {
        float diameter = radius * 2f;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
