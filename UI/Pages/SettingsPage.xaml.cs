using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI.Controls;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClipThat.UI.Pages;

/// <summary>
/// Complete Polish settings view with visual monitor arrangement, live RAM/disk estimator,
/// audio device selectors, interactive hotkey recorder, output folder picker, and startup toggles.
/// </summary>
public partial class SettingsPage : Page
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly SolidColorBrush RecordingDotBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x22, 0xC5, 0x5E);
    private static readonly SolidColorBrush PausedDotBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x71, 0x71, 0x7A);
    private static readonly SolidColorBrush ErrorDotBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0xEF, 0x44, 0x44);

    private readonly SettingsService _settingsService;
    private readonly DisplayService _displayService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly StartupService _startupService;
    private readonly GlobalHotkeyService _globalHotkeyService;
    private readonly IReplayEngine _replayEngine;

    private readonly DispatcherTimer _sliderDebounceTimer;
    private readonly DispatcherTimer _audioDevicesDebounceTimer;

    private AppSettings _workingSettings;
    private bool _isInitializing = true;
    private bool _isRefreshingAudioDevices;
    private bool _isSyncingStartupToggle;
    private bool _isApplyingEngineUpdate;
    private bool _hasPendingEngineUpdate;
    private bool _hasPendingSliderSave;
    private bool _isSavingReplay;
    private bool _isTogglingRecording;
    private bool _isDetached;

    public SettingsPage(
        SettingsService settingsService,
        DisplayService displayService,
        AudioDeviceService audioDeviceService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        GlobalHotkeyService globalHotkeyService,
        IReplayEngine replayEngine)
    {
        _settingsService = settingsService;
        _displayService = displayService;
        _audioDeviceService = audioDeviceService;
        _resourceEstimatorService = resourceEstimatorService;
        _startupService = startupService;
        _globalHotkeyService = globalHotkeyService;
        _replayEngine = replayEngine;
        _workingSettings = _settingsService.Load();

        _sliderDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _sliderDebounceTimer.Tick += SliderDebounceTimer_Tick;

        _audioDevicesDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _audioDevicesDebounceTimer.Tick += AudioDevicesDebounceTimer_Tick;

        InitializeComponent();

        LoadSettingsIntoControls();

        MonitorLayout.MonitorSelected += MonitorLayout_MonitorSelected;
        SaveHotkeyBox.HotkeyCommitted += SaveHotkeyBox_HotkeyCommitted;
        _replayEngine.StatusChanged += OnEngineStatusChanged;
        _replayEngine.ErrorOccurred += OnEngineErrorOccurred;
        _audioDeviceService.DevicesChanged += OnAudioDevicesChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;

        UpdateEngineControls(_replayEngine.IsRecording, _replayEngine.LastError, _replayEngine.CurrentStatus);
        UpdateFilenamePreview();
        UpdateResourceEstimate();

        _isInitializing = false;
    }

    /// <summary>
    /// Unsubscribes from long-lived engine, control, and system events when the parent window closes.
    /// </summary>
    public void Detach()
    {
        if (_isDetached)
        {
            return;
        }

        _isDetached = true;

        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Tick -= SliderDebounceTimer_Tick;

        _audioDevicesDebounceTimer.Stop();
        _audioDevicesDebounceTimer.Tick -= AudioDevicesDebounceTimer_Tick;

        if (_hasPendingSliderSave)
        {
            _hasPendingSliderSave = false;
            try
            {
                AppSettings finalSnapshot = _workingSettings.Clone();
                finalSnapshot.Normalize();
                _settingsService.Save(finalSnapshot);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _replayEngine.UpdateSettingsAsync(finalSnapshot).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore best-effort engine sync errors on window close.
                    }
                });
            }
            catch
            {
                // Ignore best-effort flush errors on window close.
            }
        }

        MonitorLayout.MonitorSelected -= MonitorLayout_MonitorSelected;
        MonitorLayout.Cleanup();

        SaveHotkeyBox.HotkeyCommitted -= SaveHotkeyBox_HotkeyCommitted;
        SaveHotkeyBox.Cleanup();

        _replayEngine.StatusChanged -= OnEngineStatusChanged;
        _replayEngine.ErrorOccurred -= OnEngineErrorOccurred;
        _audioDeviceService.DevicesChanged -= OnAudioDevicesChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Loaded -= SettingsPage_Loaded;
        Unloaded -= SettingsPage_Unloaded;
    }

    /// <summary>
    /// Pauses background UI timers when the host window is hidden to the system tray and resumes them when restored.
    /// </summary>
    public void OnHostWindowVisibilityChanged(bool isVisible)
    {
        if (_isDetached)
        {
            return;
        }

        if (!isVisible)
        {
            _audioDevicesDebounceTimer.Stop();
        }
        else if (IsLoaded)
        {
            UpdateEngineControls(_replayEngine.IsRecording, _replayEngine.LastError, _replayEngine.CurrentStatus);
            UpdateFilenamePreview();
            ScheduleAudioDevicesRefresh();
        }
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDetached)
        {
            return;
        }

        UpdateEngineControls(_replayEngine.IsRecording, _replayEngine.LastError, _replayEngine.CurrentStatus);
        UpdateFilenamePreview();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _audioDevicesDebounceTimer.Stop();
    }

    private void LoadSettingsIntoControls()
    {
        // Keep null SelectedMonitorDeviceName ("automatic primary monitor") intact until the user
        // explicitly clicks a monitor tile so unrelated setting edits never trigger a capture restart.
        MonitorLayout.Initialize(_displayService, _workingSettings.SelectedMonitorDeviceName);

        _ = RefreshAudioDeviceListsAsync();

        DurationSlider.Value = _workingSettings.BufferDurationMinutes;
        DurationValueText.Text = $"{_workingSettings.BufferDurationMinutes} min";

        SelectResolutionRadio(_workingSettings.Resolution);
        SelectFpsRadio(_workingSettings.Fps);

        OutputDirectoryTextBox.Text = _workingSettings.OutputDirectory;
        SaveHotkeyBox.Initialize(_globalHotkeyService, _workingSettings.HotkeyModifiers, _workingSettings.HotkeyVirtualKey);

        bool startupEnabled = _startupService.IsStartupEnabled();
        if (_workingSettings.StartWithWindows != startupEnabled)
        {
            _workingSettings.StartWithWindows = startupEnabled;
            SaveWorkingSettingsOnly();
        }

        StartWithWindowsSwitch.IsChecked = startupEnabled;
        StartRecordingOnLaunchSwitch.IsChecked = _workingSettings.StartRecordingOnLaunch;
    }

    private void SelectResolutionRadio(ResolutionPreset resolution)
    {
        RadioButton target = resolution switch
        {
            ResolutionPreset.P240 => Res240Radio,
            ResolutionPreset.P360 => Res360Radio,
            ResolutionPreset.P480 => Res480Radio,
            ResolutionPreset.P720 => Res720Radio,
            _ => Res1080Radio
        };
        target.IsChecked = true;
    }

    private void SelectFpsRadio(int fps)
    {
        if (fps == 30)
        {
            Fps30Radio.IsChecked = true;
        }
        else
        {
            Fps60Radio.IsChecked = true;
        }
    }

    private int _audioRefreshGeneration;

    private async Task RefreshAudioDeviceListsAsync()
    {
        if (_isDetached)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _audioRefreshGeneration);
        string? configuredSystemId = _workingSettings.SystemAudioDeviceId;
        string? configuredMicId = _workingSettings.MicrophoneDeviceId;

        (IReadOnlyList<AudioDeviceInfo> rawPlayback, IReadOnlyList<AudioDeviceInfo> rawCapture) =
            await Task.Run(_audioDeviceService.GetPlaybackAndCaptureDevices).ConfigureAwait(true);

        if (_isDetached || Volatile.Read(ref _audioRefreshGeneration) != generation)
        {
            return;
        }

        _isRefreshingAudioDevices = true;
        try
        {
            IReadOnlyList<AudioDeviceInfo> playbackDevices = EnsureConfiguredDevicePresent(
                rawPlayback,
                _workingSettings.SystemAudioDeviceId ?? configuredSystemId,
                disconnectedLabel: "Odłączone urządzenie audio (zapisane)");
            SystemAudioComboBox.ItemsSource = playbackDevices;
            SystemAudioComboBox.SelectedItem = FindMatchingAudioDevice(playbackDevices, _workingSettings.SystemAudioDeviceId);

            IReadOnlyList<AudioDeviceInfo> captureDevices = EnsureConfiguredDevicePresent(
                rawCapture,
                _workingSettings.MicrophoneDeviceId ?? configuredMicId,
                disconnectedLabel: "Odłączony mikrofon (zapisany)");
            MicrophoneComboBox.ItemsSource = captureDevices;
            MicrophoneComboBox.SelectedItem = FindMatchingAudioDevice(captureDevices, _workingSettings.MicrophoneDeviceId);
        }
        finally
        {
            _isRefreshingAudioDevices = false;
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> EnsureConfiguredDevicePresent(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? configuredDeviceId,
        string disconnectedLabel)
    {
        if (string.IsNullOrWhiteSpace(configuredDeviceId) ||
            AudioDeviceService.IsDisabledDeviceId(configuredDeviceId))
        {
            return devices;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(devices[i].DeviceId) &&
                string.Equals(devices[i].DeviceId, configuredDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return devices;
            }
        }

        // Preserve a temporarily unplugged device ID as a visible placeholder so changing the other
        // audio selector never silently overwrites this saved device selection.
        var augmented = new List<AudioDeviceInfo>(devices.Count + 1);
        augmented.AddRange(devices);
        augmented.Add(new AudioDeviceInfo
        {
            DeviceId = configuredDeviceId,
            FriendlyName = disconnectedLabel,
            IsDefaultOption = false,
            IsDisabledOption = false,
            IsSystemDefault = false
        });
        return augmented;
    }

    private static AudioDeviceInfo? FindMatchingAudioDevice(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? configuredDeviceId)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuredDeviceId))
        {
            return devices.FirstOrDefault(d => d.IsDefaultOption) ?? devices[0];
        }

        if (AudioDeviceService.IsDisabledDeviceId(configuredDeviceId))
        {
            return devices.FirstOrDefault(d => d.IsDisabledOption) ?? devices[0];
        }

        return devices.FirstOrDefault(d =>
                   !string.IsNullOrWhiteSpace(d.DeviceId) &&
                   string.Equals(d.DeviceId, configuredDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(d => d.IsDefaultOption)
               ?? devices[0];
    }

    private async void MonitorLayout_MonitorSelected(object? sender, DisplayDeviceInfo display)
    {
        if (_isInitializing || _isDetached)
        {
            return;
        }

        _workingSettings.SelectedMonitorDeviceName = display.DeviceName;
        await SaveAndApplySettingsAsync();
    }

    private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || _isDetached)
        {
            return;
        }

        int durationMinutes = Math.Clamp((int)Math.Round(e.NewValue), 1, 10);
        DurationValueText.Text = $"{durationMinutes} min";
        _workingSettings.BufferDurationMinutes = durationMinutes;
        _hasPendingSliderSave = true;

        UpdateResourceEstimate();

        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Start();
    }

    private async void SliderDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _sliderDebounceTimer.Stop();
        if (_isDetached)
        {
            return;
        }

        _hasPendingSliderSave = false;
        await SaveAndApplySettingsAsync();
    }

    private async void ResolutionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isDetached || sender is not RadioButton { Tag: ResolutionPreset preset })
        {
            return;
        }

        if (_workingSettings.Resolution == preset)
        {
            return;
        }

        _workingSettings.Resolution = preset;
        UpdateResourceEstimate();
        await SaveAndApplySettingsAsync();
    }

    private async void FpsRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isDetached || sender is not RadioButton radio)
        {
            return;
        }

        int fps = string.Equals(radio.Tag?.ToString(), "30", StringComparison.Ordinal) ? 30 : 60;
        if (_workingSettings.Fps == fps)
        {
            return;
        }

        _workingSettings.Fps = fps;
        UpdateResourceEstimate();
        await SaveAndApplySettingsAsync();
    }

    private async void AudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingAudioDevices || _isDetached)
        {
            return;
        }

        string? selectedSystemAudioId = (SystemAudioComboBox.SelectedItem as AudioDeviceInfo)?.DeviceId;
        string? selectedMicrophoneId = (MicrophoneComboBox.SelectedItem as AudioDeviceInfo)?.DeviceId;

        bool changed =
            !string.Equals(_workingSettings.SystemAudioDeviceId, selectedSystemAudioId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_workingSettings.MicrophoneDeviceId, selectedMicrophoneId, StringComparison.OrdinalIgnoreCase);

        if (!changed)
        {
            return;
        }

        _workingSettings.SystemAudioDeviceId = selectedSystemAudioId;
        _workingSettings.MicrophoneDeviceId = selectedMicrophoneId;
        UpdateResourceEstimate();
        await SaveAndApplySettingsAsync();
    }

    private async void BrowseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Wybierz folder zapisu nagrań"
        };

        string currentDir = OutputDirectoryTextBox.Text;
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            if (Directory.Exists(currentDir))
            {
                dialog.InitialDirectory = currentDir;
            }
            else
            {
                string? parent = Path.GetDirectoryName(currentDir);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    dialog.InitialDirectory = parent;
                }
            }
        }

        if (_isDetached)
        {
            return;
        }

        Window? owner = Window.GetWindow(this);
        bool? confirmed = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (!_isDetached && confirmed == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            OutputDirectoryTextBox.Text = dialog.FolderName;
            _workingSettings.OutputDirectory = dialog.FolderName;
            await SaveAndApplySettingsAsync();
        }
    }

    private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string folderPath = string.IsNullOrWhiteSpace(_workingSettings.OutputDirectory)
            ? AppSettings.DefaultOutputDirectory
            : _workingSettings.OutputDirectory;

        try
        {
            Directory.CreateDirectory(folderPath);
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatusError($"Nie udało się otworzyć folderu: {ex.Message}");
        }
    }

    private void SaveHotkeyBox_HotkeyCommitted(object? sender, HotkeyChangedEventArgs e)
    {
        if (_isInitializing || _isDetached)
        {
            return;
        }

        _workingSettings.HotkeyModifiers = e.Modifiers;
        _workingSettings.HotkeyVirtualKey = e.VirtualKey;
        SaveWorkingSettingsOnly();
    }

    private void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isSyncingStartupToggle || _isDetached)
        {
            return;
        }

        bool enabled = StartWithWindowsSwitch.IsChecked == true;
        try
        {
            _startupService.SetEnabled(enabled);
            _workingSettings.StartWithWindows = enabled;
            SaveWorkingSettingsOnly();
        }
        catch (Exception ex)
        {
            _isSyncingStartupToggle = true;
            try
            {
                StartWithWindowsSwitch.IsChecked = !enabled;
            }
            finally
            {
                _isSyncingStartupToggle = false;
            }

            ShowStatusError($"Błąd zmiany autostartu Windows: {ex.Message}");
        }
    }

    private void StartRecordingOnLaunchSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isDetached)
        {
            return;
        }

        _workingSettings.StartRecordingOnLaunch = StartRecordingOnLaunchSwitch.IsChecked == true;
        SaveWorkingSettingsOnly();
    }

    private void SaveWorkingSettingsOnly()
    {
        try
        {
            _workingSettings.Normalize();
            _settingsService.Save(_workingSettings);
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                ShowStatusError($"Błąd zapisu ustawień: {ex.Message}");
            }
        }
    }

    private Task SaveAndApplySettingsAsync()
    {
        SaveWorkingSettingsOnly();
        return ApplyEngineSettingsAsync();
    }

    private async Task ApplyEngineSettingsAsync()
    {
        if (_isDetached)
        {
            return;
        }

        if (_isApplyingEngineUpdate)
        {
            _hasPendingEngineUpdate = true;
            return;
        }

        _isApplyingEngineUpdate = true;
        try
        {
            do
            {
                _hasPendingEngineUpdate = false;
                AppSettings snapshot = _workingSettings.Clone();
                snapshot.Normalize();
                await _replayEngine.UpdateSettingsAsync(snapshot);
            }
            while (_hasPendingEngineUpdate);
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                string errorMessage = _replayEngine.LastError ?? $"Błąd stosowania ustawień: {ex.Message}";
                UpdateEngineControls(_replayEngine.IsRecording, errorMessage);
                if (string.IsNullOrWhiteSpace(_replayEngine.LastError))
                {
                    (Application.Current as App)?.NotifyTrayError(errorMessage);
                }
            }
        }
        finally
        {
            _isApplyingEngineUpdate = false;
        }
    }

    private void UpdateResourceEstimate()
    {
        int durationMinutes = Math.Clamp((int)Math.Round(DurationSlider.Value), 1, 10);
        DurationValueText.Text = $"{durationMinutes} min";

        ResolutionPreset resolution = _workingSettings.Resolution;
        int fps = _workingSettings.Fps;
        bool includeMicrophone = !AudioDeviceService.IsDisabledDeviceId(_workingSettings.MicrophoneDeviceId);

        ResourceEstimate estimate = _resourceEstimatorService.Estimate(durationMinutes, resolution, fps, includeMicrophone);

        RamEstimateText.Text = string.Format(PolishCulture, "~{0:0} MB", estimate.EstimatedRamUsageMb);
        DiskEstimateText.Text = string.Format(PolishCulture, "~{0:0} MB", estimate.EstimatedDiskSizeMb);
        BitrateEstimateText.Text = string.Format(PolishCulture, "{0:0.0} Mbps", estimate.VideoBitrateMbps);
    }

    private void UpdateFilenamePreview()
    {
        string exampleName = DateTime.Now.ToString("ssmmHHddMMyyyy", CultureInfo.InvariantCulture) + ".mp4";
        FilenamePreviewText.Text = $"Format nazwy: sekmingodzdzienmiesrok.mp4 (np. {exampleName})";
    }

    private async void SaveReplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingReplay || _isTogglingRecording || _isDetached)
        {
            return;
        }

        _isSavingReplay = true;
        SaveReplayButton.IsEnabled = false;
        try
        {
            if (Application.Current is App app)
            {
                await app.RequestSaveReplayAsync("SaveReplayFromSettingsAsync");
            }
            else
            {
                await _replayEngine.SaveReplayAsync();
            }
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                string errorMessage = _replayEngine.LastError ?? $"Błąd zapisu powtórki: {ex.Message}";
                ShowStatusError(errorMessage);
                if (string.IsNullOrWhiteSpace(_replayEngine.LastError))
                {
                    (Application.Current as App)?.NotifyTrayError(errorMessage);
                }
            }
        }
        finally
        {
            _isSavingReplay = false;
            if (!_isDetached)
            {
                SaveReplayButton.IsEnabled = _replayEngine.IsRecording && !_isTogglingRecording;
            }
        }
    }

    private async void ToggleRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTogglingRecording || _isDetached)
        {
            return;
        }

        _isTogglingRecording = true;
        ToggleRecordingButton.IsEnabled = false;
        SaveReplayButton.IsEnabled = false;
        try
        {
            if (_hasPendingSliderSave)
            {
                _sliderDebounceTimer.Stop();
                _hasPendingSliderSave = false;
                SaveWorkingSettingsOnly();
            }

            if (Application.Current is App app)
            {
                await app.RequestToggleRecordingAsync();
            }
            else if (_replayEngine.IsRecording)
            {
                await _replayEngine.StopAsync();
            }
            else
            {
                AppSettings snapshot = _workingSettings.Clone();
                snapshot.Normalize();
                await _replayEngine.StartAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                string errorMessage = _replayEngine.LastError ?? $"Błąd zmiany stanu nagrywania: {ex.Message}";
                UpdateEngineControls(_replayEngine.IsRecording, errorMessage);
                if (string.IsNullOrWhiteSpace(_replayEngine.LastError))
                {
                    (Application.Current as App)?.NotifyTrayError(errorMessage);
                }
            }
        }
        finally
        {
            _isTogglingRecording = false;
            if (!_isDetached)
            {
                ToggleRecordingButton.IsEnabled = true;
                SaveReplayButton.IsEnabled = _replayEngine.IsRecording && !_isSavingReplay;
            }
        }
    }

    private void OnAudioDevicesChanged()
    {
        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(ScheduleAudioDevicesRefresh);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_isDetached || e.Mode != PowerModes.Resume)
        {
            return;
        }

        Dispatcher.InvokeAsync(ScheduleAudioDevicesRefresh);
    }

    private void ScheduleAudioDevicesRefresh()
    {
        if (_isDetached || !IsLoaded || !IsVisible)
        {
            return;
        }

        _audioDevicesDebounceTimer.Stop();
        _audioDevicesDebounceTimer.Start();
    }

    private void AudioDevicesDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _audioDevicesDebounceTimer.Stop();
        if (!_isDetached && IsLoaded && IsVisible)
        {
            _ = RefreshAudioDeviceListsAsync();
        }
    }

    private void OnEngineStatusChanged(string status)
    {
        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (_isDetached)
            {
                return;
            }

            EngineStatusLevel level = _replayEngine.StatusLevel;
            bool isError = level == EngineStatusLevel.Error;
            string? currentStatus = _replayEngine.CurrentStatus ?? status;
            string? errorDetail = isError ? currentStatus : _replayEngine.LastError;
            string? infoDetail = isError || level == EngineStatusLevel.Paused ? null : currentStatus;
            UpdateEngineControls(_replayEngine.IsRecording, errorDetail, infoDetail);
        });
    }

    private void OnEngineErrorOccurred(string errorMessage)
    {
        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!_isDetached)
            {
                UpdateEngineControls(_replayEngine.IsRecording, errorMessage);
            }
        });
    }

    private void UpdateEngineControls(bool isRecording, string? errorMessage, string? statusDetail = null)
    {
        bool hasError = !string.IsNullOrWhiteSpace(errorMessage);
        StatusDot.Fill = isRecording
            ? RecordingDotBrush
            : (hasError ? ErrorDotBrush : PausedDotBrush);

        EngineStatusTitleText.Text = isRecording ? "Nagrywanie w tle aktywne" : "Wstrzymano";

        bool isErrorDuplicate = hasError && string.Equals(statusDetail, errorMessage, StringComparison.Ordinal);
        bool showDetail = !string.IsNullOrWhiteSpace(statusDetail) &&
                          !isErrorDuplicate &&
                          _replayEngine.StatusLevel != EngineStatusLevel.Paused &&
                          _replayEngine.StatusLevel != EngineStatusLevel.Error;

        if (showDetail)
        {
            EngineStatusDetailText.Text = statusDetail;
            EngineStatusDetailText.Visibility = Visibility.Visible;
        }
        else
        {
            EngineStatusDetailText.Text = string.Empty;
            EngineStatusDetailText.Visibility = Visibility.Collapsed;
        }

        if (hasError)
        {
            ShowStatusError(errorMessage!);
        }
        else
        {
            StatusErrorBannerText.Text = string.Empty;
            StatusErrorBanner.Visibility = Visibility.Collapsed;
        }

        ToggleRecordingButton.Content = isRecording ? "Wstrzymaj" : "Wznów";
        ToggleRecordingButton.IsEnabled = !_isTogglingRecording;
        SymbolRegular toggleSymbol = isRecording ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        if (ToggleRecordingButton.Icon is SymbolIcon existingIcon)
        {
            existingIcon.Symbol = toggleSymbol;
        }
        else
        {
            ToggleRecordingButton.Icon = new SymbolIcon(toggleSymbol);
        }

        SaveReplayButton.IsEnabled = isRecording && !_isSavingReplay && !_isTogglingRecording;
    }

    private void ShowStatusError(string message)
    {
        StatusErrorBannerText.Text = message;
        StatusErrorBanner.Visibility = Visibility.Visible;
        if (!_replayEngine.IsRecording)
        {
            StatusDot.Fill = ErrorDotBrush;
        }
    }
}
