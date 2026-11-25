using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;

namespace ClipThat.UI.Pages;

/// <summary>
/// Clips browser page listing recorded MP4 replays from the configured output directory.
/// </summary>
public partial class ClipsPage : Page
{
    private readonly SettingsService _settingsService;
    private readonly IReplayEngine _replayEngine;

    public ClipsPage(SettingsService settingsService, IReplayEngine replayEngine)
    {
        _settingsService = settingsService;
        _replayEngine = replayEngine;

        InitializeComponent();

        _replayEngine.ReplaySaved += OnReplaySaved;
        RefreshClips();
    }

    /// <summary>
    /// Unsubscribes from engine events when the parent window closes.
    /// </summary>
    public void Detach()
    {
        _replayEngine.ReplaySaved -= OnReplaySaved;
    }

    private void RefreshClips()
    {
        AppSettings settings = _settingsService.Load();
        string outputDirectory = settings.OutputDirectory;
        FolderPathText.Text = outputDirectory;

        if (!Directory.Exists(outputDirectory))
        {
            ClipsListView.ItemsSource = Array.Empty<ClipItem>();
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        CultureInfo polishCulture = CultureInfo.GetCultureInfo("pl-PL");
        var clips = new DirectoryInfo(outputDirectory)
            .EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new ClipItem(
                file.FullName,
                file.Name,
                file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", polishCulture),
                FormatSize(file.Length, polishCulture)))
            .ToList();

        ClipsListView.ItemsSource = clips;
        EmptyStateText.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSize(long bytes, CultureInfo culture)
    {
        double megabytes = bytes / (1024.0 * 1024.0);
        return string.Format(culture, "{0:0.0} MB", megabytes);
    }

    private void OnReplaySaved(string _)
    {
        Dispatcher.InvokeAsync(RefreshClips);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshClips();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string outputDirectory = _settingsService.Load().OutputDirectory;
        Directory.CreateDirectory(outputDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = outputDirectory,
            UseShellExecute = true
        });
    }

    private void ClipsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipsListView.SelectedItem is not ClipItem clip || !File.Exists(clip.FullPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = clip.FullPath,
            UseShellExecute = true
        });
    }

    private sealed record ClipItem(string FullPath, string FileName, string CreatedAtText, string SizeText);
}
