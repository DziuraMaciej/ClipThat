using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;
using Microsoft.Win32;

namespace ClipThat.UI.Pages;

/// <summary>
/// Settings page allowing users to configure monitor selection, buffer parameters, folder, and startup behavior.
/// </summary>
public partial class SettingsPage : Page
{
    private readonly SettingsService _settingsService;
    private readonly DisplayService _displayService;
    private readonly ResourceEstimatorService _resourceEstimatorService;
    private readonly StartupService _startupService;
    private readonly IReplayEngine _replayEngine;

    private AppSettings _workingSettings;
    private bool _isInitializing = true;

    public SettingsPage(
        SettingsService settingsService,
        DisplayService displayService,
        ResourceEstimatorService resourceEstimatorService,
        StartupService startupService,
        IReplayEngine replayEngine)
    {
        _settingsService = settingsService;
        _displayService = displayService;
        _resourceEstimatorService = resourceEstimatorService;
        _startupService = startupService;
        _replayEngine = replayEngine;
        _workingSettings = _settingsService.Load();

        InitializeComponent();

        PopulateOptions();
        LoadSettingsIntoControls();

        _replayEngine.StatusChanged += OnEngineStatusChanged;
        UpdateEngineControls(_replayEngine.IsRecording, null);

        _isInitializing = false;
        UpdateResourceEstimate();
    }

    /// <summary>
    /// Unsubscribes from long-lived engine events when the parent window closes so the page can be collected.
    /// </summary>
    public void Detach()
    {
        _replayEngine.StatusChanged -= OnEngineStatusChanged;
    }

    private void PopulateOptions()
    {
        IReadOnlyList<DisplayDeviceInfo> displays = _displayService.GetDisplays();
        MonitorComboBox.ItemsSource = displays;

        ResolutionComboBox.ItemsSource = new[]
        {
            new ResolutionOption("240p", ResolutionPreset.P240),
            new ResolutionOption("360p", ResolutionPreset.P360),
            new ResolutionOption("480p", ResolutionPreset.P480),
            new ResolutionOption("720p (HD)", ResolutionPreset.P720),
            new ResolutionOption("1080p (Full HD)", ResolutionPreset.P1080)
        };

        FpsComboBox.ItemsSource = new[]
        {
            new FpsOption("30 FPS", 30),
            new FpsOption("60 FPS", 60)
        };
    }

    private void LoadSettingsIntoControls()
    {
        if (MonitorComboBox.ItemsSource is IReadOnlyList<DisplayDeviceInfo> displays && displays.Count > 0)
        {
            DisplayDeviceInfo? selected = _displayService.GetDisplayByDeviceName(_workingSettings.SelectedMonitorDeviceName);
            MonitorComboBox.SelectedItem = displays.FirstOrDefault(d =>
                selected is not null &&
                string.Equals(d.DeviceName, selected.DeviceName, StringComparison.OrdinalIgnoreCase))
                ?? displays[0];
        }

        DurationSlider.Value = _workingSettings.BufferDurationMinutes;
        DurationValueText.Text = $"{_workingSettings.BufferDurationMinutes} min";
        ResolutionComboBox.SelectedValue = _workingSettings.Resolution;
        FpsComboBox.SelectedValue = _workingSettings.Fps;
        OutputDirectoryTextBox.Text = _workingSettings.OutputDirectory;
        StartWithWindowsSwitch.IsChecked = _workingSettings.StartWithWindows || _startupService.IsEnabled();
        StartRecordingOnLaunchSwitch.IsChecked = _workingSettings.StartRecordingOnLaunch;
    }

    private void QualityInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        UpdateResourceEstimate();
    }

    private void UpdateResourceEstimate()
    {
        int durationMinutes = (int)Math.Round(DurationSlider.Value);
        DurationValueText.Text = $"{durationMinutes} min";

        ResolutionPreset resolution = ResolutionComboBox.SelectedValue is ResolutionPreset preset
            ? preset
            : ResolutionPreset.P1080;

        int fps = FpsComboBox.SelectedValue is int selectedFps ? selectedFps : 60;

        ResourceEstimate estimate = _resourceEstimatorService.Estimate(durationMinutes, resolution, fps);
        CultureInfo polishCulture = CultureInfo.GetCultureInfo("pl-PL");

        BitrateEstimateText.Text = string.Format(polishCulture, "{0:0.0} Mbps", estimate.VideoBitrateMbps);
        RamEstimateText.Text = string.Format(polishCulture, "{0:0} MB", estimate.EstimatedRamUsageMb);
        DiskEstimateText.Text = string.Format(polishCulture, "{0:0} MB", estimate.EstimatedDiskSizeMb);
    }

    private void BrowseOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Wybierz folder zapisu nagrań",
            InitialDirectory = OutputDirectoryTextBox.Text
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            OutputDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsButton.IsEnabled = false;
        SaveFeedbackText.Text = string.Empty;

        try
        {
            AppSettings updated = _workingSettings.Clone();
            updated.SelectedMonitorDeviceName = (MonitorComboBox.SelectedItem as DisplayDeviceInfo)?.DeviceName;
            updated.BufferDurationMinutes = (int)Math.Round(DurationSlider.Value);
            updated.Resolution = ResolutionComboBox.SelectedValue is ResolutionPreset preset
                ? preset
                : ResolutionPreset.P1080;
            updated.Fps = FpsComboBox.SelectedValue is int fps ? fps : 60;
            updated.OutputDirectory = OutputDirectoryTextBox.Text;
            updated.StartWithWindows = StartWithWindowsSwitch.IsChecked == true;
            updated.StartRecordingOnLaunch = StartRecordingOnLaunchSwitch.IsChecked == true;
            updated.Normalize();

            _settingsService.Save(updated);
            _startupService.SetEnabled(updated.StartWithWindows);
            await _replayEngine.UpdateSettingsAsync(updated);

            _workingSettings = updated;
            SaveFeedbackText.Text = "Zapisano ustawienia.";
        }
        catch (Exception ex)
        {
            SaveFeedbackText.Text = $"Błąd zapisu: {ex.Message}";
        }
        finally
        {
            SaveSettingsButton.IsEnabled = true;
        }
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
            SaveFeedbackText.Text = $"Błąd zapisu powtórki: {ex.Message}";
        }
        finally
        {
            SaveReplayButton.IsEnabled = _replayEngine.IsRecording;
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
            SaveFeedbackText.Text = $"Błąd zmiany stanu nagrywania: {ex.Message}";
            UpdateEngineControls(_replayEngine.IsRecording, null);
        }
        finally
        {
            ToggleRecordingButton.IsEnabled = true;
            SaveReplayButton.IsEnabled = _replayEngine.IsRecording;
        }
    }

    private void OnEngineStatusChanged(string status)
    {
        Dispatcher.InvokeAsync(() => UpdateEngineControls(_replayEngine.IsRecording, status));
    }

    private void UpdateEngineControls(bool isRecording, string? status)
    {
        EngineStatusText.Text = status ?? (isRecording
            ? "Nagrywanie aktywne"
            : "Nagrywanie wstrzymane");
        ToggleRecordingButton.Content = isRecording ? "Wstrzymaj nagrywanie" : "Wznów nagrywanie";
        SaveReplayButton.IsEnabled = isRecording;
    }

    private sealed record ResolutionOption(string Label, ResolutionPreset Value);

    private sealed record FpsOption(string Label, int Value);
}
