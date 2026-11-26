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
    private static readonly Brush RecordingDotBrush = CreateFrozenBrush(0xFF, 0x22, 0xC5, 0x5E);
    private static readonly Brush PausedDotBrush = CreateFrozenBrush(0xFF, 0x71, 0x71, 0x7A);

    private readonly SettingsService _settingsService;
    private readonly DisplayService _displayService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly StartupService _startupService;
    private readonly GlobalHotkeyService _globalHotkeyService;
    private readonly IReplayEngine _replayEngine;

    private readonly DispatcherTimer _sliderDebounceTimer;
    private readonly DispatcherTimer _filenamePreviewTimer;

    private AppSettings _workingSettings;
    private bool _isInitializing = true;
    private bool _isRefreshingAudioDevices;
    private bool _isSyncingStartupToggle;
    private bool _isApplyingEngineUpdate;
    private bool _hasPendingEngineUpdate;
    private bool _hasPendingSliderSave;
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

        _filenamePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _filenamePreviewTimer.Tick += FilenamePreviewTimer_Tick;

        InitializeComponent();

        LoadSettingsIntoControls();

        MonitorLayout.MonitorSelected += MonitorLayout_MonitorSelected;
        SaveHotkeyBox.HotkeyCommitted += SaveHotkeyBox_HotkeyCommitted;
        _replayEngine.StatusChanged += OnEngineStatusChanged;
        _audioDeviceService.DevicesChanged += OnAudioDevicesChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;

        UpdateEngineControls(_replayEngine.IsRecording, null);
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

        _filenamePreviewTimer.Stop();
        _filenamePreviewTimer.Tick -= FilenamePreviewTimer_Tick;

        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Tick -= SliderDebounceTimer_Tick;

        if (_hasPendingSliderSave)
        {
            _hasPendingSliderSave = false;
            try
            {
                _settingsService.Save(_workingSettings);
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
        _audioDeviceService.DevicesChanged -= OnAudioDevicesChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Loaded -= SettingsPage_Loaded;
        Unloaded -= SettingsPage_Unloaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDetached)
        {
            return;
        }

        UpdateFilenamePreview();
        _filenamePreviewTimer.Start();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _filenamePreviewTimer.Stop();
    }

    private void LoadSettingsIntoControls()
    {
        MonitorLayout.Initialize(_displayService, _workingSettings.SelectedMonitorDeviceName);
        if (!string.IsNullOrWhiteSpace(MonitorLayout.SelectedMonitorDeviceName) &&
            string.IsNullOrWhiteSpace(_workingSettings.SelectedMonitorDeviceName))
        {
            _workingSettings.SelectedMonitorDeviceName = MonitorLayout.SelectedMonitorDeviceName;
        }

        RefreshAudioDeviceLists();

        DurationSlider.Value = _workingSettings.BufferDurationMinutes;
        DurationValueText.Text = $"{_workingSettings.BufferDurationMinutes} min";

        SelectResolutionRadio(_workingSettings.Resolution);
        SelectFpsRadio(_workingSettings.Fps);

        OutputDirectoryTextBox.Text = _workingSettings.OutputDirectory;
        SaveHotkeyBox.Initialize(_globalHotkeyService, _workingSettings.HotkeyModifiers, _workingSettings.HotkeyVirtualKey);

        bool startupEnabled = _workingSettings.StartWithWindows || _startupService.IsEnabled();
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

    private void RefreshAudioDeviceLists()
    {
        if (_isDetached)
        {
            return;
        }

        _isRefreshingAudioDevices = true;
        try
        {
            IReadOnlyList<AudioDeviceInfo> playbackDevices = _audioDeviceService.GetPlaybackDevices();
            SystemAudioComboBox.ItemsSource = playbackDevices;
            SystemAudioComboBox.SelectedItem = FindMatchingAudioDevice(playbackDevices, _workingSettings.SystemAudioDeviceId);

            IReadOnlyList<AudioDeviceInfo> captureDevices = _audioDeviceService.GetCaptureDevices();
            MicrophoneComboBox.ItemsSource = captureDevices;
            MicrophoneComboBox.SelectedItem = FindMatchingAudioDevice(captureDevices, _workingSettings.MicrophoneDeviceId);
        }
        finally
        {
            _isRefreshingAudioDevices = false;
        }
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
        await SaveAndApplySettingsAsync();
    }

    private async void BrowseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Wybierz folder zapisu nagrań",
            InitialDirectory = OutputDirectoryTextBox.Text
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
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
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"")
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
            while (_hasPendingEngineUpdate && !_isDetached);
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                UpdateEngineControls(_replayEngine.IsRecording, $"Błąd stosowania ustawień: {ex.Message}");
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

        ResourceEstimate estimate = _resourceEstimatorService.Estimate(durationMinutes, resolution, fps);
        CultureInfo polishCulture = CultureInfo.GetCultureInfo("pl-PL");

        RamEstimateText.Text = string.Format(polishCulture, "~{0:0} MB", estimate.EstimatedRamUsageMb);
        DiskEstimateText.Text = string.Format(polishCulture, "~{0:0} MB", estimate.EstimatedDiskSizeMb);
        BitrateEstimateText.Text = string.Format(polishCulture, "{0:0.0} Mbps", estimate.VideoBitrateMbps);
    }

    private void FilenamePreviewTimer_Tick(object? sender, EventArgs e)
    {
        UpdateFilenamePreview();
    }

    private void UpdateFilenamePreview()
    {
        string exampleName = DateTime.Now.ToString("ssmmHHddMMyyyy", CultureInfo.InvariantCulture) + ".mp4";
        FilenamePreviewText.Text = $"Format nazwy: sekmingodzdzienmiesrok.mp4 (np. {exampleName})";
    }

    private async void SaveReplayButton_Click(object sender, RoutedEventArgs e)
    {
        SaveReplayButton.IsEnabled = false;
        try
        {
            await _replayEngine.SaveReplayAsync();
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                ShowStatusError($"Błąd zapisu powtórki: {ex.Message}");
            }
        }
        finally
        {
            if (!_isDetached)
            {
                SaveReplayButton.IsEnabled = _replayEngine.IsRecording;
            }
        }
    }

    private async void ToggleRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleRecordingButton.IsEnabled = false;
        try
        {
            if (_replayEngine.IsRecording)
            {
                await _replayEngine.StopAsync();
            }
            else
            {
                await _replayEngine.StartAsync(_settingsService.Load());
            }
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                UpdateEngineControls(_replayEngine.IsRecording, $"Błąd zmiany stanu nagrywania: {ex.Message}");
            }
        }
        finally
        {
            if (!_isDetached)
            {
                ToggleRecordingButton.IsEnabled = true;
                SaveReplayButton.IsEnabled = _replayEngine.IsRecording;
            }
        }
    }

    private void OnAudioDevicesChanged()
    {
        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(RefreshAudioDeviceLists);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_isDetached || e.Mode != PowerModes.Resume)
        {
            return;
        }

        Dispatcher.InvokeAsync(RefreshAudioDeviceLists);
    }

    private void OnEngineStatusChanged(string status)
    {
        if (_isDetached)
        {
            return;
        }

        string? errorDetail = IsErrorStatusMessage(status) ? status : null;
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isDetached)
            {
                UpdateEngineControls(_replayEngine.IsRecording, errorDetail);
            }
        });
    }

    private void UpdateEngineControls(bool isRecording, string? errorMessage)
    {
        StatusDot.Fill = isRecording ? RecordingDotBrush : PausedDotBrush;
        EngineStatusTitleText.Text = isRecording ? "Nagrywanie w tle aktywne" : "Wstrzymano";

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            ShowStatusError(errorMessage);
        }
        else
        {
            EngineStatusDetailText.Text = string.Empty;
            EngineStatusDetailText.Visibility = Visibility.Collapsed;
        }

        ToggleRecordingButton.Content = isRecording ? "Wstrzymaj" : "Wznów";
        ToggleRecordingButton.Icon = new SymbolIcon(isRecording ? SymbolRegular.Pause24 : SymbolRegular.Play24);
        SaveReplayButton.IsEnabled = isRecording;
    }

    private void ShowStatusError(string message)
    {
        EngineStatusDetailText.Text = message;
        EngineStatusDetailText.Visibility = Visibility.Visible;
    }

    private static bool IsErrorStatusMessage(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.StartsWith("Błąd", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("Nie udało", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("Przechwytywanie wstrzymane", StringComparison.OrdinalIgnoreCase);
    }

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
