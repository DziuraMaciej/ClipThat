using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClipThat;

/// <summary>
/// Application entry point managing background replay lifecycle and Windows system tray integration.
/// </summary>
public partial class App : Application
{
    private readonly SettingsService _settingsService = new();
    private readonly DisplayService _displayService = new();
    private readonly ResourceEstimatorService _resourceEstimatorService = new();
    private readonly StartupService _startupService = new();
    private readonly IReplayEngine _replayEngine = new StubReplayEngine();

    private MainWindow? _mainWindow;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _saveReplayMenuItem;
    private WinForms.ToolStripMenuItem? _toggleRecordingMenuItem;
    private Drawing.Icon? _recordingTrayIcon;
    private Drawing.Icon? _pausedTrayIcon;
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            AppSettings settings = _settingsService.Load();

            _replayEngine.StatusChanged += OnEngineStatusChanged;
            _replayEngine.ReplaySaved += OnEngineReplaySaved;

            InitializeTrayIcon();

            bool startMinimized = e.Args.Any(arg =>
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (startMinimized || settings.StartRecordingOnLaunch)
            {
                await _replayEngine.StartAsync(settings);
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

    private void ShowMainWindow()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (_mainWindow is not null)
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            return;
        }

        _mainWindow = new MainWindow(
            _settingsService,
            _displayService,
            _resourceEstimatorService,
            _startupService,
            _replayEngine);
        _mainWindow.Closed += OnMainWindowClosed;
        _mainWindow.Show();
        _mainWindow.Activate();
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

    private async Task ExitApplicationAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        try
        {
            if (_mainWindow is not null)
            {
                _mainWindow.Closed -= OnMainWindowClosed;
                _mainWindow.Close();
                _mainWindow = null;
            }

            _replayEngine.StatusChanged -= OnEngineStatusChanged;
            _replayEngine.ReplaySaved -= OnEngineReplaySaved;

            await _replayEngine.StopAsync();
            _replayEngine.Dispose();
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
