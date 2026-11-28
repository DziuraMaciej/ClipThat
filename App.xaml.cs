using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClipThat.Engine;
using ClipThat.Engine.Diagnostics;
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
    private const long MaxUiLogBytes = 2L * 1024L * 1024L;
    private const string SingleInstanceMutexName = "Local\\ClipThat_SingleInstance_Mutex";
    private const string ShowWindowEventName = "Local\\ClipThat_ShowWindow_Event";
    private const string ExitApplicationEventName = "Local\\ClipThat_Exit_Event";

    private static readonly object UiLogLock = new();

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

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWaitRegistration;
    private EventWaitHandle? _exitApplicationEvent;
    private RegisteredWaitHandle? _exitApplicationWaitRegistration;
    private MainWindow? _mainWindow;
    private GlobalHotkeyService? _globalHotkeyService;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _trayMenu;
    private WinForms.ToolStripMenuItem? _saveReplayMenuItem;
    private WinForms.ToolStripMenuItem? _toggleRecordingMenuItem;
    private Drawing.Icon? _recordingTrayIcon;
    private Drawing.Icon? _pausedTrayIcon;
    private long _lastHotkeyTriggerTickMs;
    private long _lastTrayErrorTickMs;
    private string? _lastTrayErrorMessage;
    private int _saveReplayInProgress;
    private int _toggleRecordingInProgress;
    private int _shuttingDown;
    private bool _userPreferenceEventsSubscribed;
    private bool _globalExceptionHandlersSubscribed;

    private bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            RequestExistingInstanceExitAndWait(timeoutMs: 5000);
            Shutdown();
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        SubscribeGlobalExceptionHandlers();

        try
        {
            ApplySystemAccent();

            if (!_userPreferenceEventsSubscribed)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _userPreferenceEventsSubscribed = true;
            }

            AppSettings settings = _settingsService.Load();

            if (settings.StartWithWindows)
            {
                try
                {
                    _startupService.EnsureStartupPathUpToDate();
                }
                catch (Exception ex)
                {
                    LogUiException("EnsureStartupPathUpToDate", ex);
                }
            }

            _replayEngine.StatusChanged += OnEngineStatusChanged;
            _replayEngine.ErrorOccurred += OnEngineErrorOccurred;
            _replayEngine.ReplaySaved += OnEngineReplaySaved;

            InitializeTrayIcon();
            InitializeGlobalHotkey(settings);

            bool startMinimized = e.Args.Any(arg =>
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            // Show the main window immediately on explicit manual launch so UI appears without waiting
            // for GPU/encoder/WASAPI hardware initialization to finish.
            if (!startMinimized)
            {
                ShowMainWindow();
            }

            SyncTrayState();

            if (startMinimized || settings.StartRecordingOnLaunch)
            {
                try
                {
                    await _replayEngine.StartAsync(settings);
                }
                catch (Exception ex)
                {
                    EngineLogger.Error("App", $"Fatal startup capture failure: {ex.Message}", ex);
                    NotifyTrayError(_replayEngine.LastError ?? $"Nie udało się uruchomić nagrywania: {ex.Message}");
                }
            }

            SyncTrayState();
        }
        catch (Exception ex)
        {
            EngineLogger.Error("App", $"Fatal application startup failure: {ex.Message}", ex);
            NotifyTrayError($"Nie udało się uruchomić aplikacji ClipThat: {ex.Message}");
            MessageBox.Show(
                $"Nie udało się uruchomić aplikacji ClipThat:\n{ex.Message}",
                "ClipThat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    private static void RequestExistingInstanceExitAndWait(int timeoutMs)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitApplicationEventName, out EventWaitHandle? exitEvent))
            {
                using (exitEvent)
                {
                    exitEvent.Set();
                }
            }
            else
            {
                return;
            }

            // Wait briefly until the running instance releases the single-instance mutex and exits cleanly.
            Stopwatch waitClock = Stopwatch.StartNew();
            while (waitClock.ElapsedMilliseconds < timeoutMs)
            {
                using var probe = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
                if (createdNew)
                {
                    try { probe.ReleaseMutex(); } catch { /* ignore */ }
                    return;
                }

                Thread.Sleep(50);
            }
        }
        catch
        {
            // Ignore cross-process signaling errors during clean-exit probe.
        }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out EventWaitHandle? existingEvent))
                    {
                        using (existingEvent)
                        {
                            existingEvent.Set();
                        }
                    }
                }
                catch
                {
                    // Ignore cross-process wake signaling errors.
                }

                return false;
            }

            _singleInstanceMutex = mutex;
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showWindowEvent,
                static (state, _) =>
                {
                    if (state is App app && !app.IsShuttingDown)
                    {
                        app.Dispatcher.InvokeAsync(app.ShowMainWindow);
                    }
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);

            _exitApplicationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitApplicationEventName);
            _exitApplicationWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _exitApplicationEvent,
                static (state, _) =>
                {
                    if (state is App app && !app.IsShuttingDown)
                    {
                        app.Dispatcher.InvokeAsync(async () => await app.ExitApplicationAsync());
                    }
                },
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);

            return true;
        }
        catch (Exception ex)
        {
            LogUiException("TryAcquireSingleInstance", ex);
            return true;
        }
    }

    private void SubscribeGlobalExceptionHandlers()
    {
        if (_globalExceptionHandlersSubscribed)
        {
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _globalExceptionHandlersSubscribed = true;
    }

    private void UnsubscribeGlobalExceptionHandlers()
    {
        if (!_globalExceptionHandlersSubscribed)
        {
            return;
        }

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _globalExceptionHandlersSubscribed = false;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        EngineLogger.Error("App", $"Unhandled UI dispatcher exception: {e.Exception.Message}", e.Exception);
        LogUiException("DispatcherUnhandledException", e.Exception);

        // Never swallow catastrophic runtime/memory corruption faults.
        if (e.Exception is OutOfMemoryException or AccessViolationException or StackOverflowException)
        {
            e.Handled = false;
            return;
        }

        NotifyTrayError($"Nieoczekiwany błąd interfejsu: {e.Exception.Message}");
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            EngineLogger.Error("App", $"Unhandled AppDomain exception (terminating={e.IsTerminating}): {ex.Message}", ex);
            LogUiException("AppDomain.UnhandledException", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        EngineLogger.Warn("App", $"Unobserved background task exception: {e.Exception.Message}", e.Exception);
        e.SetObserved();
    }

    private void InitializeTrayIcon()
    {
        _recordingTrayIcon = CreateTrayIcon(isRecording: true);
        _pausedTrayIcon = CreateTrayIcon(isRecording: false);

        _saveReplayMenuItem = new WinForms.ToolStripMenuItem("Zapisz powtórkę");
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
            Text = "ClipThat OFF",
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
        if (IsShuttingDown || !_replayEngine.IsRecording)
        {
            return;
        }

        long nowTickMs = Environment.TickCount64;
        long previousTickMs = Volatile.Read(ref _lastHotkeyTriggerTickMs);
        if (previousTickMs != 0 && nowTickMs - previousTickMs < HotkeyDebounceMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _saveReplayInProgress, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastHotkeyTriggerTickMs, nowTickMs);
        _ = SaveReplayCoordinatedAsync("SaveReplayFromHotkeyAsync");
    }

    private async Task SaveReplayCoordinatedAsync(string context)
    {
        try
        {
            await _replayEngine.SaveReplayAsync();
        }
        catch (Exception ex)
        {
            EngineLogger.Error("App", $"{context} failed: {ex.Message}", ex);
            NotifyTrayError(_replayEngine.LastError ?? $"Nie udało się zapisać powtórki: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _saveReplayInProgress, 0);
        }
    }

    private void ShowMainWindow()
    {
        if (IsShuttingDown)
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
                    _mainWindow.ForceClose();
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

        if (!IsShuttingDown)
        {
            // Gently reclaim WPF UI memory after closing to tray without stalling active background capture.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                if (!IsShuttingDown && Volatile.Read(ref _mainWindow) is null)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                }
            });
        }
    }

    private async Task SaveReplayFromTrayAsync()
    {
        if (IsShuttingDown || !_replayEngine.IsRecording)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _saveReplayInProgress, 1, 0) != 0)
        {
            return;
        }

        await SaveReplayCoordinatedAsync("SaveReplayFromTrayAsync");
    }

    private async Task ToggleRecordingAsync()
    {
        if (IsShuttingDown || Interlocked.CompareExchange(ref _toggleRecordingInProgress, 1, 0) != 0)
        {
            return;
        }

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
            EngineLogger.Error("App", $"ToggleRecordingAsync failed: {ex.Message}", ex);
            NotifyTrayError(_replayEngine.LastError ?? $"Błąd zmiany stanu nagrywania: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _toggleRecordingInProgress, 0);
            SyncTrayState();
        }
    }

    private void OnEngineStatusChanged(string _)
    {
        Dispatcher.InvokeAsync(SyncTrayState);
    }

    private void OnEngineErrorOccurred(string errorMessage)
    {
        NotifyTrayError(errorMessage);
        Dispatcher.InvokeAsync(SyncTrayState);
    }

    /// <summary>
    /// Surfaces a fatal startup, capture, or replay-save error in a Windows system tray balloon
    /// notification with short-window deduplication so failures are never silent or double-popped.
    /// </summary>
    public void NotifyTrayError(string message)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string trimmed = message.Trim();
        string displayText = trimmed.Length > 240 ? trimmed[..237] + "..." : trimmed;

        Dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || _notifyIcon is null)
            {
                return;
            }

            long nowMs = Environment.TickCount64;
            if (string.Equals(_lastTrayErrorMessage, displayText, StringComparison.Ordinal) &&
                nowMs - _lastTrayErrorTickMs < 2000)
            {
                return;
            }

            _lastTrayErrorMessage = displayText;
            _lastTrayErrorTickMs = nowMs;

            try
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "ClipThat — Błąd",
                    displayText,
                    WinForms.ToolTipIcon.Error);
            }
            catch
            {
                // Ignore tray notification failures during OS shell transitions.
            }
        });
    }

    private void OnEngineReplaySaved(string filePath)
    {
        if (IsShuttingDown)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown || _notifyIcon is null)
            {
                return;
            }

            string fileName = Path.GetFileName(filePath);
            _notifyIcon.ShowBalloonTip(
                2500,
                "ClipThat",
                $"Zapisano powtórkę: {fileName}",
                WinForms.ToolTipIcon.Info);
        });
    }

    private void SyncTrayState()
    {
        if (IsShuttingDown)
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
                ? "ClipThat ON"
                : "ClipThat OFF";
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

            resources["SystemAccentColorBrush"] = UiBrushHelper.CreateFrozenBrush(systemAccent);
            resources["SystemAccentColorPrimaryBrush"] = UiBrushHelper.CreateFrozenBrush(primaryAccent);
            resources["SystemAccentColorSecondaryBrush"] = UiBrushHelper.CreateFrozenBrush(secondaryAccent);
            resources["SystemAccentColorTertiaryBrush"] = UiBrushHelper.CreateFrozenBrush(tertiaryAccent);
            resources["SystemAccentTintBrush"] = UiBrushHelper.CreateFrozenBrush(
                Color.FromArgb(0x26, primaryAccent.R, primaryAccent.G, primaryAccent.B));
            resources["SystemAccentTintStrongBrush"] = UiBrushHelper.CreateFrozenBrush(
                Color.FromArgb(0x3D, primaryAccent.R, primaryAccent.G, primaryAccent.B));
            resources["SystemAccentBorderSubtleBrush"] = UiBrushHelper.CreateFrozenBrush(
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

        lock (UiLogLock)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxUiLogBytes)
                {
                    File.Move(logPath, logPath + ".old", overwrite: true);
                }

                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}\r\n{fullDetails}\r\n{new string('-', 80)}\r\n";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // Ignore file logging failures so error reporting itself never throws.
            }
        }

        return logPath;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsShuttingDown)
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
        UnsubscribeGlobalExceptionHandlers();
        UnsubscribeUserPreferenceChanged();
        ReleaseSingleInstanceHandles();
        base.OnExit(e);
    }

    private void ReleaseSingleInstanceHandles()
    {
        RegisteredWaitHandle? waitReg = Interlocked.Exchange(ref _showWindowWaitRegistration, null);
        if (waitReg is not null)
        {
            try
            {
                waitReg.Unregister(null);
            }
            catch
            {
                // Ignore wait handle unregister errors on exit.
            }
        }

        RegisteredWaitHandle? exitWaitReg = Interlocked.Exchange(ref _exitApplicationWaitRegistration, null);
        if (exitWaitReg is not null)
        {
            try
            {
                exitWaitReg.Unregister(null);
            }
            catch
            {
                // Ignore wait handle unregister errors on exit.
            }
        }

        EventWaitHandle? showEvent = Interlocked.Exchange(ref _showWindowEvent, null);
        showEvent?.Dispose();

        EventWaitHandle? exitEvent = Interlocked.Exchange(ref _exitApplicationEvent, null);
        exitEvent?.Dispose();

        Mutex? mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (mutex is not null)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
                // Mutex may not be owned on this thread during shutdown.
            }

            mutex.Dispose();
        }
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
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        UnsubscribeGlobalExceptionHandlers();
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
                _mainWindow.ForceClose();
                _mainWindow = null;
            }

            _replayEngine.StatusChanged -= OnEngineStatusChanged;
            _replayEngine.ErrorOccurred -= OnEngineErrorOccurred;
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
            _clipEditorService.Dispose();

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
