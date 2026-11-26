using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipThat.Engine;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI.Controls;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace ClipThat.UI.Pages;

/// <summary>
/// Streamlined Polish Clips Browser and Quick Audio/Trim Editor page with collapsible clips panel,
/// live directory watching, tall muted MediaElement video preview, unified playback + range trim bar,
/// live dual-track NAudio preview mixer, and fast FFmpeg stream-copy export.
/// </summary>
public partial class ClipsPage : Page
{
    private const double MaxAudioVideoDriftMs = 250.0;
    private const int PreviewMixerSampleRate = 48000;
    private const int PreviewMixerChannels = 2;
    private const double ExpandedSidebarWidth = 290.0;
    private const double CollapsedSidebarWidth = 48.0;

    private static readonly Duration SidebarWidthAnimationDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration SidebarContentFadeOutDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration SidebarContentFadeInDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly CubicEase SidebarEaseOut = CreateFrozenEaseOut();

    private static readonly Brush NeutralStatusBrush = CreateFrozenBrush(0xFF, 0xA8, 0xA8, 0xA8);
    private static readonly Brush SuccessStatusBrush = CreateFrozenBrush(0xFF, 0x4A, 0xDE, 0x80);
    private static readonly Brush ErrorStatusBrush = CreateFrozenBrush(0xFF, 0xF8, 0x71, 0x71);

    private readonly SettingsService _settingsService;
    private readonly IReplayEngine _replayEngine;
    private readonly ClipEditorService _clipEditorService;

    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _watcherDebounceTimer;
    private readonly object _audioSyncLock = new();

    private FileSystemWatcher? _directoryWatcher;
    private string _watchedDirectory = string.Empty;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _audioExtractionCts;

    private WaveOutEvent? _waveOut;
    private MixingSampleProvider? _audioMixer;
    private WaveStream? _systemWaveStream;
    private WaveStream? _micWaveStream;
    private LockedSampleProvider? _systemSampleProvider;
    private LockedSampleProvider? _micSampleProvider;
    private VolumeSampleProvider? _systemVolumeProvider;
    private VolumeSampleProvider? _micVolumeProvider;
    private string? _tempSystemWavPath;
    private string? _tempMicWavPath;

    private ClipItem? _selectedClip;
    private double _clipDurationSeconds;
    private double _lastNonZeroSystemVolume = 100.0;
    private double _lastNonZeroMicVolume = 100.0;

    private bool _isClipsListCollapsed;
    private int _sidebarAnimationGeneration;
    private bool _isPlaying;
    private bool _isScrubbingTimeline;
    private bool _wasPlayingBeforeTimelineScrub;
    private bool _isDraggingTrimHandle;
    private bool _isRefreshingClipsList;
    private bool _isUpdatingVolumeSliders;
    private bool _isExtractingPreviewAudio;
    private bool _isPreviewAudioReady;
    private bool _isExporting;
    private bool _isDetached;

    public ClipsPage(
        SettingsService settingsService,
        IReplayEngine replayEngine,
        ClipEditorService clipEditorService)
    {
        _settingsService = settingsService;
        _replayEngine = replayEngine;
        _clipEditorService = clipEditorService;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        _watcherDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _watcherDebounceTimer.Tick += WatcherDebounceTimer_Tick;

        InitializeComponent();

        PreviewMediaElement.IsMuted = true;
        PreviewMediaElement.Volume = 0.0;

        _replayEngine.ReplaySaved += OnReplaySaved;
        Loaded += ClipsPage_Loaded;
        Unloaded += ClipsPage_Unloaded;
    }

    /// <summary>
    /// Releases watchers, timers, media handles, NAudio mixer objects, temporary preview files,
    /// and active background operations when the parent window closes.
    /// </summary>
    public void Detach()
    {
        if (_isDetached)
        {
            return;
        }

        _isDetached = true;

        _replayEngine.ReplaySaved -= OnReplaySaved;
        Loaded -= ClipsPage_Loaded;
        Unloaded -= ClipsPage_Unloaded;

        _playbackTimer.Stop();
        _playbackTimer.Tick -= PlaybackTimer_Tick;

        _watcherDebounceTimer.Stop();
        _watcherDebounceTimer.Tick -= WatcherDebounceTimer_Tick;

        DisposeDirectoryWatcher();
        CancelTokenSource(ref _refreshCts);
        CancelTokenSource(ref _exportCts);
        UnloadMediaPreview(clearSelectionField: true);
        ClipEditorService.CleanupPreviewTempDirectory();
    }

    private async void ClipsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDetached)
        {
            return;
        }

        string outputDirectory = GetConfiguredOutputDirectory();
        FolderPathText.Text = outputDirectory;
        FolderPathText.ToolTip = outputDirectory;

        ConfigureDirectoryWatcher(outputDirectory);
        await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
    }

    private void ClipsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _playbackTimer.Stop();
        _watcherDebounceTimer.Stop();
        DisposeDirectoryWatcher();
        CancelTokenSource(ref _refreshCts);

        if (_isPlaying)
        {
            PausePlayback();
        }

        // Dispose NAudio playback objects and delete temporary preview .wav files when leaving ClipsPage.
        DisposeAudioMixerAndDeleteTempFiles();
        HideAudioLoadingOverlay();
    }

    private string GetConfiguredOutputDirectory()
    {
        string dir = _settingsService.Load().OutputDirectory;
        return string.IsNullOrWhiteSpace(dir) ? AppSettings.DefaultOutputDirectory : dir;
    }

    private void ConfigureDirectoryWatcher(string directoryPath)
    {
        DisposeDirectoryWatcher();
        _watchedDirectory = directoryPath;

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directoryPath, "*.mp4")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            watcher.Created += OnDirectoryFileChanged;
            watcher.Deleted += OnDirectoryFileChanged;
            watcher.Renamed += OnDirectoryFileRenamed;
            watcher.Changed += OnDirectoryFileChanged;
            watcher.EnableRaisingEvents = true;

            _directoryWatcher = watcher;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się uruchomić obserwatora folderu '{directoryPath}': {ex.Message}");
        }
    }

    private void DisposeDirectoryWatcher()
    {
        FileSystemWatcher? watcher = _directoryWatcher;
        _directoryWatcher = null;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnDirectoryFileChanged;
            watcher.Deleted -= OnDirectoryFileChanged;
            watcher.Renamed -= OnDirectoryFileRenamed;
            watcher.Changed -= OnDirectoryFileChanged;
            watcher.Dispose();
        }
        catch
        {
            // Ignore disposal errors when tearing down FileSystemWatcher.
        }
    }

    private void OnDirectoryFileChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleDebouncedRefresh();
    }

    private void OnDirectoryFileRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleDebouncedRefresh();
    }

    private void OnReplaySaved(string _)
    {
        ScheduleDebouncedRefresh();
    }

    private void ScheduleDebouncedRefresh()
    {
        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (_isDetached || !IsLoaded)
            {
                return;
            }

            _watcherDebounceTimer.Stop();
            _watcherDebounceTimer.Start();
        });
    }

    private async void WatcherDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _watcherDebounceTimer.Stop();
        if (_isDetached || !IsLoaded)
        {
            return;
        }

        await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
    }

    private async Task RefreshClipsAsync(string? preferredSelectionPath = null)
    {
        if (_isDetached)
        {
            return;
        }

        string outputDirectory = GetConfiguredOutputDirectory();
        FolderPathText.Text = outputDirectory;
        FolderPathText.ToolTip = outputDirectory;

        if (!string.Equals(_watchedDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase) ||
            (_directoryWatcher is null && Directory.Exists(outputDirectory)))
        {
            ConfigureDirectoryWatcher(outputDirectory);
        }

        CancelTokenSource(ref _refreshCts);
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        IReadOnlyList<ClipItem> clips;
        try
        {
            clips = await _clipEditorService.ScanClipsAsync(outputDirectory, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd podczas odświeżania listy nagrań: {ex.Message}");
            clips = Array.Empty<ClipItem>();
        }

        if (_isDetached || cts.IsCancellationRequested)
        {
            return;
        }

        string? targetPath = preferredSelectionPath ?? _selectedClip?.FilePath;
        ClipItem? matchingClip = !string.IsNullOrWhiteSpace(targetPath)
            ? clips.FirstOrDefault(c => string.Equals(c.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
            : null;

        _isRefreshingClipsList = true;
        try
        {
            ClipsListBox.ItemsSource = clips;
            EmptyStatePanel.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (matchingClip is not null)
            {
                ClipsListBox.SelectedItem = matchingClip;
            }
        }
        finally
        {
            _isRefreshingClipsList = false;
        }

        if (matchingClip is not null)
        {
            bool sameLoadedClip = _selectedClip is not null &&
                                  string.Equals(_selectedClip.FilePath, matchingClip.FilePath, StringComparison.OrdinalIgnoreCase);

            if (sameLoadedClip)
            {
                // Keep playback and trim state undisturbed while keeping metadata fresh.
                _selectedClip = matchingClip;
                UpdateSelectedClipSubtitleAndAudioHint(matchingClip);

                // If preview audio was cleaned up when navigating away from ClipsPage, re-extract it cleanly.
                if (!_isPreviewAudioReady && !_isExtractingPreviewAudio && matchingClip.AudioTrackCount > 0)
                {
                    _ = PreparePreviewAudioAsync(matchingClip);
                }
            }
            else
            {
                LoadClipIntoEditor(matchingClip);
            }
        }
        else
        {
            UnloadMediaPreview(clearSelectionField: true);
            if (clips.Count > 0)
            {
                ClipsListBox.SelectedItem = clips[0];
            }
        }
    }

    private void ClipsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDetached || _isRefreshingClipsList)
        {
            return;
        }

        if (ClipsListBox.SelectedItem is not ClipItem clip)
        {
            if (_selectedClip is not null)
            {
                UnloadMediaPreview(clearSelectionField: true);
            }

            return;
        }

        if (_selectedClip is not null &&
            string.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoadClipIntoEditor(clip);
    }

    private void LoadClipIntoEditor(ClipItem clip)
    {
        UnloadMediaPreview(clearSelectionField: false);

        if (!File.Exists(clip.FilePath))
        {
            _selectedClip = null;
            ShowExportStatus("Wybrany plik nagrania nie istnieje już na dysku.", ErrorStatusBrush);
            return;
        }

        _selectedClip = clip;
        _clipDurationSeconds = Math.Max(0.0, clip.Duration.TotalSeconds);
        NoPreviewOverlay.Visibility = Visibility.Collapsed;

        UpdateSelectedClipSubtitleAndAudioHint(clip);
        ResetAudioSlidersForClip(clip);
        InitializeTimelineAndTrimRange(_clipDurationSeconds, resetRangeToFull: true);
        SetEditorControlsEnabled(hasClip: true);

        if (!_isExporting)
        {
            HideExportStatus();
        }

        try
        {
            // Keep MediaElement muted so it renders video frames only while NAudio handles live dual-track audio.
            PreviewMediaElement.IsMuted = true;
            PreviewMediaElement.Volume = 0.0;
            PreviewMediaElement.Source = new Uri(clip.FilePath, UriKind.Absolute);
            PreviewMediaElement.Position = TimeSpan.Zero;
            PreviewMediaElement.Pause();
            _isPlaying = false;
            UpdatePlayPauseButtonVisual();
        }
        catch (Exception ex)
        {
            ShowExportStatus($"Nie udało się załadować podglądu wideo: {ex.Message}", ErrorStatusBrush);
        }

        if (clip.AudioTrackCount > 0)
        {
            _ = PreparePreviewAudioAsync(clip);
        }
        else
        {
            _isPreviewAudioReady = true;
            HideAudioLoadingOverlay();
        }
    }

    private async Task PreparePreviewAudioAsync(ClipItem clip)
    {
        DisposeAudioMixerAndDeleteTempFiles();

        var cts = new CancellationTokenSource();
        _audioExtractionCts = cts;
        _isExtractingPreviewAudio = true;
        _isPreviewAudioReady = false;

        ShowAudioLoadingOverlay("Ładowanie ścieżek audio...");

        var progress = new Progress<ClipExportProgress>(update =>
        {
            if (_isDetached || cts.IsCancellationRequested || !ReferenceEquals(_selectedClip, clip))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(update.StatusMessage))
            {
                ShowAudioLoadingOverlay(update.StatusMessage);
            }
        });

        PreviewAudioExtractionResult? extractionResult = null;
        try
        {
            extractionResult = await _clipEditorService.ExtractPreviewAudioTracksAsync(
                clip.FilePath,
                clip.AudioTrackCount,
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd ekstrakcji ścieżek audio do podglądu: {ex.Message}");
            if (!_isDetached && !cts.IsCancellationRequested && ReferenceEquals(_selectedClip, clip))
            {
                _isExtractingPreviewAudio = false;
                HideAudioLoadingOverlay();
                AudioTracksHintText.Text = "Podgląd audio niedostępny (brak ffmpeg)";
            }

            return;
        }

        if (_isDetached || cts.IsCancellationRequested || !ReferenceEquals(_selectedClip, clip))
        {
            // Clip changed or page unloaded while ffmpeg was running; discard temp WAV files immediately.
            ClipEditorService.TryDeletePreviewFile(extractionResult.SystemAudioWavPath);
            ClipEditorService.TryDeletePreviewFile(extractionResult.MicrophoneAudioWavPath);
            return;
        }

        _isExtractingPreviewAudio = false;
        HideAudioLoadingOverlay();

        if (extractionResult.ExtractedTrackCount <= 0)
        {
            _isPreviewAudioReady = true;
            return;
        }

        InitializeAudioMixer(extractionResult);
    }

    private void InitializeAudioMixer(PreviewAudioExtractionResult extraction)
    {
        WaveOutEvent? waveOut = null;
        WaveFileReader? sysStream = null;
        WaveFileReader? micStream = null;
        LockedSampleProvider? sysProvider = null;
        LockedSampleProvider? micProvider = null;
        VolumeSampleProvider? sysVolumeProvider = null;
        VolumeSampleProvider? micVolumeProvider = null;

        try
        {
            WaveFormat mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                PreviewMixerSampleRate,
                PreviewMixerChannels);

            var mixer = new MixingSampleProvider(mixerFormat)
            {
                ReadFully = true
            };

            int sysPercent = (int)Math.Round(SystemVolumeSlider.Value);
            int micPercent = (int)Math.Round(MicVolumeSlider.Value);

            if (!string.IsNullOrWhiteSpace(extraction.SystemAudioWavPath) &&
                File.Exists(extraction.SystemAudioWavPath))
            {
                sysStream = new WaveFileReader(extraction.SystemAudioWavPath);
                sysProvider = new LockedSampleProvider(sysStream, _audioSyncLock);
                sysVolumeProvider = new VolumeSampleProvider(sysProvider)
                {
                    Volume = sysPercent / 100f
                };
                mixer.AddMixerInput(sysVolumeProvider);
            }

            if (!string.IsNullOrWhiteSpace(extraction.MicrophoneAudioWavPath) &&
                File.Exists(extraction.MicrophoneAudioWavPath))
            {
                micStream = new WaveFileReader(extraction.MicrophoneAudioWavPath);
                micProvider = new LockedSampleProvider(micStream, _audioSyncLock);
                micVolumeProvider = new VolumeSampleProvider(micProvider)
                {
                    Volume = micPercent / 100f
                };
                mixer.AddMixerInput(micVolumeProvider);
            }

            if (sysVolumeProvider is null && micVolumeProvider is null)
            {
                ClipEditorService.TryDeletePreviewFile(extraction.SystemAudioWavPath);
                ClipEditorService.TryDeletePreviewFile(extraction.MicrophoneAudioWavPath);
                _isPreviewAudioReady = true;
                return;
            }

            waveOut = new WaveOutEvent
            {
                DesiredLatency = 75,
                NumberOfBuffers = 3
            };
            waveOut.Init(mixer.ToWaveProvider16());

            lock (_audioSyncLock)
            {
                _systemWaveStream = sysStream;
                _micWaveStream = micStream;
                _systemSampleProvider = sysProvider;
                _micSampleProvider = micProvider;
                _systemVolumeProvider = sysVolumeProvider;
                _micVolumeProvider = micVolumeProvider;
                _audioMixer = mixer;
                _waveOut = waveOut;
                _tempSystemWavPath = extraction.SystemAudioWavPath;
                _tempMicWavPath = extraction.MicrophoneAudioWavPath;
            }

            _isPreviewAudioReady = true;

            // Synchronize audio streams to the current video playhead position.
            TimeSpan currentVideoPos = GetSafeMediaPosition();
            SeekAudioStreams(currentVideoPos);
            ApplyLiveAudioVolumes();

            if (_isPlaying && !_isScrubbingTimeline && !_isDraggingTrimHandle)
            {
                TryStartWaveOut();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nie udało się zainicjować miksera NAudio dla podglądu: {ex.Message}");

            try { waveOut?.Stop(); } catch { /* ignore */ }
            try { waveOut?.Dispose(); } catch { /* ignore */ }
            try { sysStream?.Dispose(); } catch { /* ignore */ }
            try { micStream?.Dispose(); } catch { /* ignore */ }

            ClipEditorService.TryDeletePreviewFile(extraction.SystemAudioWavPath);
            ClipEditorService.TryDeletePreviewFile(extraction.MicrophoneAudioWavPath);

            AudioTracksHintText.Text = "Nie udało się uruchomić wyjścia audio podglądu";
        }
    }

    private void DisposeAudioMixerAndDeleteTempFiles()
    {
        CancelTokenSource(ref _audioExtractionCts);
        _isExtractingPreviewAudio = false;
        _isPreviewAudioReady = false;

        WaveOutEvent? waveOut;
        WaveStream? sysStream;
        WaveStream? micStream;
        string? sysTempPath;
        string? micTempPath;

        lock (_audioSyncLock)
        {
            _systemSampleProvider?.MarkStopped();
            _micSampleProvider?.MarkStopped();

            waveOut = _waveOut;
            _waveOut = null;
        }

        // Stop and dispose WaveOut outside _audioSyncLock so its callback thread cannot deadlock.
        if (waveOut is not null)
        {
            try
            {
                waveOut.Stop();
            }
            catch
            {
                // Ignore stop errors during teardown.
            }

            try
            {
                waveOut.Dispose();
            }
            catch
            {
                // Ignore dispose errors during teardown.
            }
        }

        lock (_audioSyncLock)
        {
            sysStream = _systemWaveStream;
            micStream = _micWaveStream;
            sysTempPath = _tempSystemWavPath;
            micTempPath = _tempMicWavPath;

            _systemWaveStream = null;
            _micWaveStream = null;
            _systemSampleProvider = null;
            _micSampleProvider = null;
            _systemVolumeProvider = null;
            _micVolumeProvider = null;
            _audioMixer = null;
            _tempSystemWavPath = null;
            _tempMicWavPath = null;
        }

        try { sysStream?.Dispose(); } catch { /* ignore */ }
        try { micStream?.Dispose(); } catch { /* ignore */ }

        ClipEditorService.TryDeletePreviewFile(sysTempPath);
        ClipEditorService.TryDeletePreviewFile(micTempPath);
    }

    private void UnloadMediaPreview(bool clearSelectionField)
    {
        _playbackTimer.Stop();
        _isPlaying = false;
        _isScrubbingTimeline = false;
        _wasPlayingBeforeTimelineScrub = false;
        _isDraggingTrimHandle = false;

        DisposeAudioMixerAndDeleteTempFiles();
        HideAudioLoadingOverlay();

        try
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Close();
            PreviewMediaElement.Source = null;
        }
        catch
        {
            // Ignore teardown errors when closing MediaElement.
        }

        UpdatePlayPauseButtonVisual();

        if (clearSelectionField)
        {
            _selectedClip = null;
            _clipDurationSeconds = 0.0;
            NoPreviewOverlay.Visibility = Visibility.Visible;
            SelectedClipSubtitleText.Text = "Wybierz nagranie z listy po lewej stronie";
            AudioTracksHintText.Text = string.Empty;
            InitializeTimelineAndTrimRange(0.0, resetRangeToFull: true);
            SetEditorControlsEnabled(hasClip: false);
        }
    }

    private void UpdateSelectedClipSubtitleAndAudioHint(ClipItem clip)
    {
        SelectedClipSubtitleText.Text = $"{clip.FileName} · {clip.FormattedDate} · {clip.FormattedSize}";

        AudioTracksHintText.Text = clip.AudioTrackCount switch
        {
            >= 3 => "3 ścieżki (System + Mikrofon + Miks)",
            2 => "2 ścieżki (System + Mikrofon)",
            1 => "1 ścieżka audio (suwak mikrofonu nieaktywny)",
            _ => "Brak wykrytych ścieżek audio"
        };
    }

    private void ResetAudioSlidersForClip(ClipItem clip)
    {
        _isUpdatingVolumeSliders = true;
        try
        {
            _lastNonZeroSystemVolume = 100.0;
            _lastNonZeroMicVolume = 100.0;

            SystemVolumeSlider.Value = 100.0;
            MicVolumeSlider.Value = 100.0;
            UpdateVolumeReadoutsAndIcons();
            ApplyLiveAudioVolumes();
        }
        finally
        {
            _isUpdatingVolumeSliders = false;
        }

        bool hasAudio = clip.AudioTrackCount > 0;
        bool hasSeparateMic = clip.HasSeparateAudioTracks;

        SystemVolumeSlider.IsEnabled = hasAudio && !_isExporting;
        SystemMuteButton.IsEnabled = hasAudio && !_isExporting;
        MicVolumeSlider.IsEnabled = hasSeparateMic && !_isExporting;
        MicMuteButton.IsEnabled = hasSeparateMic && !_isExporting;
        KeepSeparateTracksRadio.IsEnabled = hasSeparateMic && !_isExporting;

        if (!hasSeparateMic)
        {
            MixSingleTrackRadio.IsChecked = true;
        }
    }

    private void SetEditorControlsEnabled(bool hasClip)
    {
        bool canInteract = hasClip && !_isExporting;
        bool hasAudio = hasClip && (_selectedClip?.AudioTrackCount ?? 0) > 0;
        bool hasSeparateMic = hasClip && (_selectedClip?.HasSeparateAudioTracks ?? false);

        OpenExternalPlayerButton.IsEnabled = hasClip;
        PlayPauseButton.IsEnabled = hasClip;
        TrimRangeSlider.IsEnabled = canInteract && _clipDurationSeconds > 0.0;

        SystemVolumeSlider.IsEnabled = canInteract && hasAudio;
        SystemMuteButton.IsEnabled = canInteract && hasAudio;
        MicVolumeSlider.IsEnabled = canInteract && hasSeparateMic;
        MicMuteButton.IsEnabled = canInteract && hasSeparateMic;

        MixSingleTrackRadio.IsEnabled = canInteract;
        KeepSeparateTracksRadio.IsEnabled = canInteract && hasSeparateMic;
        ExportButton.IsEnabled = canInteract && _clipDurationSeconds > 0.0;
    }

    private void InitializeTimelineAndTrimRange(double durationSeconds, bool resetRangeToFull)
    {
        double safeMax = durationSeconds > 0.0 ? durationSeconds : 1.0;

        double targetStart = resetRangeToFull
            ? 0.0
            : Math.Clamp(TrimRangeSlider.StartValue, 0.0, safeMax);

        double targetEnd = resetRangeToFull
            ? (durationSeconds > 0.0 ? durationSeconds : 1.0)
            : Math.Clamp(TrimRangeSlider.EndValue, targetStart, safeMax);

        double targetPosition = resetRangeToFull
            ? 0.0
            : Math.Clamp(TrimRangeSlider.CurrentPosition, 0.0, safeMax);

        TrimRangeSlider.SetRange(
            minimum: 0.0,
            maximum: safeMax,
            startValue: targetStart,
            endValue: targetEnd,
            currentPosition: targetPosition);

        UpdateTimeReadout(TimeSpan.FromSeconds(targetPosition));
        UpdateTrimDisplay();
    }

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_isDetached || _selectedClip is null)
        {
            return;
        }

        PreviewMediaElement.IsMuted = true;
        PreviewMediaElement.Volume = 0.0;

        if (PreviewMediaElement.NaturalDuration.HasTimeSpan &&
            PreviewMediaElement.NaturalDuration.TimeSpan > TimeSpan.Zero)
        {
            double naturalSeconds = PreviewMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            bool shouldResetEnd = _clipDurationSeconds <= 0.0 ||
                                  Math.Abs(TrimRangeSlider.EndValue - _clipDurationSeconds) < 0.25;
            _clipDurationSeconds = naturalSeconds;

            InitializeTimelineAndTrimRange(naturalSeconds, resetRangeToFull: shouldResetEnd);
            SetEditorControlsEnabled(hasClip: true);
        }

        TryPauseWaveOut();
        PreviewMediaElement.Position = TimeSpan.Zero;
        PreviewMediaElement.Pause();
        SeekAudioStreams(TimeSpan.Zero);
        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlayPauseButtonVisual();
        SyncPlayheadUi(0.0, syncTrimPlayhead: true);
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isDetached)
        {
            return;
        }

        PauseAtTrimEnd();
    }

    private void PreviewMediaElement_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_isDetached)
        {
            return;
        }

        PausePlayback();
        ShowExportStatus($"Nie udało się odtworzyć podglądu: {e.ErrorException.Message}", ErrorStatusBrush);
    }

    private void VideoSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectedClip is not null)
        {
            TogglePlayPause();
        }
    }

    private void EditorScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || EditorScrollViewer is null)
        {
            return;
        }

        double targetOffset = Math.Clamp(
            EditorScrollViewer.VerticalOffset - e.Delta,
            0.0,
            EditorScrollViewer.ScrollableHeight);
        EditorScrollViewer.ScrollToVerticalOffset(targetOffset);
        e.Handled = true;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_selectedClip is null || PreviewMediaElement.Source is null)
        {
            return;
        }

        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            double startSec = _clipDurationSeconds > 0.0
                ? Math.Clamp(TrimRangeSlider.StartValue, 0.0, _clipDurationSeconds)
                : 0.0;
            double endSec = _clipDurationSeconds > 0.0
                ? Math.Clamp(TrimRangeSlider.EndValue, startSec, _clipDurationSeconds)
                : 0.0;

            double currentSec = _clipDurationSeconds > 0.0
                ? Math.Clamp(TrimRangeSlider.CurrentPosition, 0.0, _clipDurationSeconds)
                : 0.0;

            // Play-from-Start: if paused at/before StartValue or at/after EndValue (outside (StartValue, EndValue - 0.05)),
            // automatically seek both MediaElement and dual NAudio streams to StartValue before starting playback.
            if (_clipDurationSeconds > 0.0 &&
                (currentSec <= startSec || currentSec >= endSec - 0.05))
            {
                SeekMediaAndAudioToSeconds(startSec, syncTrimPlayhead: true);
            }
            else
            {
                if (Math.Abs(GetSafeMediaPosition().TotalSeconds - currentSec) > 0.1)
                {
                    SeekVideoOnlyToSeconds(currentSec);
                }

                SeekAudioStreams(TimeSpan.FromSeconds(currentSec));
            }

            ApplyLiveAudioVolumes();
            PreviewMediaElement.IsMuted = true;
            PreviewMediaElement.Volume = 0.0;
            PreviewMediaElement.Play();
            TryStartWaveOut();

            _isPlaying = true;
            _playbackTimer.Start();
            UpdatePlayPauseButtonVisual();
        }
    }

    private void PausePlayback()
    {
        TryPauseWaveOut();

        try
        {
            PreviewMediaElement.Pause();
        }
        catch
        {
            // Ignore pause errors if media is unloaded.
        }

        TimeSpan pausedPos = GetSafeMediaPosition();
        SeekAudioStreams(pausedPos);

        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlayPauseButtonVisual();
        SyncPlayheadUi(pausedPos.TotalSeconds, syncTrimPlayhead: true);
    }

    private void PauseAtTrimEnd()
    {
        TryPauseWaveOut();

        try
        {
            PreviewMediaElement.Pause();
        }
        catch
        {
            // Ignore pause errors if media is unloaded.
        }

        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlayPauseButtonVisual();

        double endSec = _clipDurationSeconds > 0.0
            ? Math.Clamp(TrimRangeSlider.EndValue, 0.0, _clipDurationSeconds)
            : 0.0;

        SeekMediaAndAudioToSeconds(endSec, syncTrimPlayhead: true);
    }

    private void UpdatePlayPauseButtonVisual()
    {
        SymbolRegular symbol = _isPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        if (PlayPauseButton.Icon is SymbolIcon symbolIcon)
        {
            symbolIcon.Symbol = symbol;
        }
        else
        {
            PlayPauseButton.Icon = new SymbolIcon(symbol);
        }

        PlayPauseButton.ToolTip = _isPlaying ? "Pauza" : "Odtwórz";
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDetached || _isScrubbingTimeline || _isDraggingTrimHandle || _selectedClip is null)
        {
            return;
        }

        TimeSpan videoPosition = GetSafeMediaPosition();
        double currentSec = _clipDurationSeconds > 0.0
            ? Math.Clamp(videoPosition.TotalSeconds, 0.0, _clipDurationSeconds)
            : 0.0;

        double endSec = _clipDurationSeconds > 0.0
            ? Math.Clamp(TrimRangeSlider.EndValue, 0.0, _clipDurationSeconds)
            : 0.0;

        // Automatically pause when playback reaches EndValue.
        if (_clipDurationSeconds > 0.0 && endSec > 0.0 && videoPosition.TotalSeconds >= endSec)
        {
            PauseAtTrimEnd();
            return;
        }

        // Re-sync NAudio WaveStreams if MediaElement ever stalls and drifts noticeably.
        if (_isPlaying && _waveOut?.PlaybackState == PlaybackState.Playing)
        {
            TimeSpan? audioPosition = GetAudioPosition();
            if (audioPosition.HasValue &&
                Math.Abs((videoPosition - audioPosition.Value).TotalMilliseconds) > MaxAudioVideoDriftMs)
            {
                SeekAudioStreams(videoPosition);
            }
        }

        SyncPlayheadUi(currentSec, syncTrimPlayhead: true);
    }

    private void TrimRangeSlider_ThumbDragStarted(object? sender, EventArgs e)
    {
        _isDraggingTrimHandle = true;
        if (_isPlaying)
        {
            PausePlayback();
        }
    }

    private void TrimRangeSlider_ThumbDragCompleted(object? sender, EventArgs e)
    {
        _isDraggingTrimHandle = false;
    }

    private void TrimRangeSlider_StartChanged(object? sender, double newStartSeconds)
    {
        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        UpdateTrimDisplay();
        SeekMediaAndAudioToSeconds(newStartSeconds, syncTrimPlayhead: false);
    }

    private void TrimRangeSlider_EndChanged(object? sender, double newEndSeconds)
    {
        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        UpdateTrimDisplay();
        SeekMediaAndAudioToSeconds(newEndSeconds, syncTrimPlayhead: false);
    }

    private void TrimRangeSlider_PositionSeekStarted(object? sender, EventArgs e)
    {
        _isScrubbingTimeline = true;
        _wasPlayingBeforeTimelineScrub = _isPlaying;

        if (_isPlaying)
        {
            TryPauseWaveOut();

            try
            {
                PreviewMediaElement.Pause();
            }
            catch
            {
                // Ignore pause errors while beginning a scrub.
            }
        }
    }

    private void TrimRangeSlider_PositionSeekRequested(object? sender, double targetSeconds)
    {
        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        double clamped = Math.Clamp(targetSeconds, 0.0, _clipDurationSeconds);
        UpdateTimeReadout(TimeSpan.FromSeconds(clamped));

        if (_isScrubbingTimeline)
        {
            // While scrubbing the unified bar, update video frames live; audio resyncs cleanly on release.
            SeekVideoOnlyToSeconds(clamped);
        }
        else
        {
            SeekMediaAndAudioToSeconds(clamped, syncTrimPlayhead: false);
        }
    }

    private void TrimRangeSlider_PositionSeekCompleted(object? sender, double targetSeconds)
    {
        _isScrubbingTimeline = false;
        bool shouldResume = _wasPlayingBeforeTimelineScrub && _isPlaying;
        _wasPlayingBeforeTimelineScrub = false;

        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        double clamped = Math.Clamp(targetSeconds, 0.0, _clipDurationSeconds);
        SeekMediaAndAudioToSeconds(clamped, syncTrimPlayhead: false);

        if (shouldResume)
        {
            double endSec = Math.Clamp(TrimRangeSlider.EndValue, 0.0, _clipDurationSeconds);
            if (clamped >= endSec - 0.05)
            {
                _isPlaying = false;
                _playbackTimer.Stop();
                UpdatePlayPauseButtonVisual();
            }
            else
            {
                PreviewMediaElement.Play();
                TryStartWaveOut();
            }
        }
    }

    private void SeekMediaAndAudioToSeconds(double seconds, bool syncTrimPlayhead)
    {
        if (_selectedClip is null || PreviewMediaElement.Source is null)
        {
            return;
        }

        double clamped = _clipDurationSeconds > 0.0
            ? Math.Clamp(seconds, 0.0, _clipDurationSeconds)
            : 0.0;

        TimeSpan targetPos = TimeSpan.FromSeconds(clamped);
        SeekVideoOnlyToSeconds(clamped);
        SeekAudioStreams(targetPos);
        SyncPlayheadUi(clamped, syncTrimPlayhead);
    }

    private void SeekVideoOnlyToSeconds(double seconds)
    {
        if (_selectedClip is null || PreviewMediaElement.Source is null)
        {
            return;
        }

        double clamped = _clipDurationSeconds > 0.0
            ? Math.Clamp(seconds, 0.0, _clipDurationSeconds)
            : 0.0;

        try
        {
            PreviewMediaElement.Position = TimeSpan.FromSeconds(clamped);
        }
        catch
        {
            // Ignore seek failures if media has not opened yet.
        }
    }

    private void SeekAudioStreams(TimeSpan position)
    {
        lock (_audioSyncLock)
        {
            SeekWaveStreamLocked(_systemWaveStream, position);
            SeekWaveStreamLocked(_micWaveStream, position);
        }
    }

    private TimeSpan? GetAudioPosition()
    {
        lock (_audioSyncLock)
        {
            WaveStream? activeStream = _systemWaveStream ?? _micWaveStream;
            if (activeStream is null)
            {
                return null;
            }

            try
            {
                return activeStream.CurrentTime;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void SeekWaveStreamLocked(WaveStream? stream, TimeSpan position)
    {
        if (stream is null)
        {
            return;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        try
        {
            int avgBytesPerSecond = stream.WaveFormat.AverageBytesPerSecond;
            int blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
            if (avgBytesPerSecond <= 0)
            {
                return;
            }

            long targetByte = (long)(position.TotalSeconds * avgBytesPerSecond);
            targetByte -= targetByte % blockAlign;
            long maxValidByte = Math.Max(0L, stream.Length - (stream.Length % blockAlign));
            stream.Position = Math.Clamp(targetByte, 0L, maxValidByte);
        }
        catch
        {
            // Ignore seek errors if the wave stream is being torn down.
        }
    }

    private void TryStartWaveOut()
    {
        WaveOutEvent? waveOut = _waveOut;
        if (waveOut is null)
        {
            return;
        }

        try
        {
            if (waveOut.PlaybackState != PlaybackState.Playing)
            {
                waveOut.Play();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd uruchomienia odtwarzania NAudio: {ex.Message}");
        }
    }

    private void TryPauseWaveOut()
    {
        WaveOutEvent? waveOut = _waveOut;
        if (waveOut is null)
        {
            return;
        }

        try
        {
            if (waveOut.PlaybackState == PlaybackState.Playing)
            {
                waveOut.Pause();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd pauzowania odtwarzania NAudio: {ex.Message}");
        }
    }

    private TimeSpan GetSafeMediaPosition()
    {
        try
        {
            return PreviewMediaElement.Position;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private void SyncPlayheadUi(double seconds, bool syncTrimPlayhead)
    {
        double clamped = _clipDurationSeconds > 0.0
            ? Math.Clamp(seconds, 0.0, _clipDurationSeconds)
            : 0.0;

        if (syncTrimPlayhead)
        {
            TrimRangeSlider.CurrentPosition = clamped;
        }

        UpdateTimeReadout(TimeSpan.FromSeconds(clamped));
    }

    private void UpdateTimeReadout(TimeSpan currentPosition)
    {
        string currentText = ClipItem.FormatDuration(currentPosition);
        string totalText = ClipItem.FormatDuration(TimeSpan.FromSeconds(_clipDurationSeconds));
        TimeReadoutText.Text = $"{currentText} / {totalText}";
    }

    private void UpdateTrimDisplay()
    {
        double startSec = _clipDurationSeconds > 0.0
            ? Math.Clamp(TrimRangeSlider.StartValue, 0.0, _clipDurationSeconds)
            : 0.0;
        double endSec = _clipDurationSeconds > 0.0
            ? Math.Clamp(TrimRangeSlider.EndValue, startSec, _clipDurationSeconds)
            : 0.0;
        double selectedSec = Math.Max(0.0, endSec - startSec);

        TrimStartText.Text = ClipItem.FormatDuration(TimeSpan.FromSeconds(startSec));
        TrimEndText.Text = ClipItem.FormatDuration(TimeSpan.FromSeconds(endSec));
        SelectedLengthText.Text = $"Wybrana długość: {ClipItem.FormatDuration(TimeSpan.FromSeconds(selectedSec))}";
    }

    private void SystemVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSliders)
        {
            return;
        }

        if (e.NewValue > 0.5)
        {
            _lastNonZeroSystemVolume = e.NewValue;
        }

        UpdateVolumeReadoutsAndIcons();
        ApplyLiveAudioVolumes();
    }

    private void MicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSliders)
        {
            return;
        }

        if (e.NewValue > 0.5)
        {
            _lastNonZeroMicVolume = e.NewValue;
        }

        UpdateVolumeReadoutsAndIcons();
        ApplyLiveAudioVolumes();
    }

    private void SystemMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isUpdatingVolumeSliders = true;
        try
        {
            if (SystemVolumeSlider.Value > 0.5)
            {
                _lastNonZeroSystemVolume = SystemVolumeSlider.Value;
                SystemVolumeSlider.Value = 0.0;
            }
            else
            {
                SystemVolumeSlider.Value = _lastNonZeroSystemVolume > 0.5 ? _lastNonZeroSystemVolume : 100.0;
            }

            UpdateVolumeReadoutsAndIcons();
            ApplyLiveAudioVolumes();
        }
        finally
        {
            _isUpdatingVolumeSliders = false;
        }
    }

    private void MicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isUpdatingVolumeSliders = true;
        try
        {
            if (MicVolumeSlider.Value > 0.5)
            {
                _lastNonZeroMicVolume = MicVolumeSlider.Value;
                MicVolumeSlider.Value = 0.0;
            }
            else
            {
                MicVolumeSlider.Value = _lastNonZeroMicVolume > 0.5 ? _lastNonZeroMicVolume : 100.0;
            }

            UpdateVolumeReadoutsAndIcons();
            ApplyLiveAudioVolumes();
        }
        finally
        {
            _isUpdatingVolumeSliders = false;
        }
    }

    private void ApplyLiveAudioVolumes()
    {
        if (SystemVolumeSlider is null || MicVolumeSlider is null)
        {
            return;
        }

        int sysPercent = (int)Math.Round(SystemVolumeSlider.Value);
        int micPercent = (int)Math.Round(MicVolumeSlider.Value);

        VolumeSampleProvider? sysProvider = _systemVolumeProvider;
        if (sysProvider is not null)
        {
            sysProvider.Volume = sysPercent / 100f;
        }

        VolumeSampleProvider? micProvider = _micVolumeProvider;
        if (micProvider is not null)
        {
            micProvider.Volume = micPercent / 100f;
        }
    }

    private void UpdateVolumeReadoutsAndIcons()
    {
        if (SystemVolumeText is null ||
            MicVolumeText is null ||
            SystemMuteButton is null ||
            MicMuteButton is null)
        {
            return;
        }

        int sysPercent = (int)Math.Round(SystemVolumeSlider.Value);
        int micPercent = (int)Math.Round(MicVolumeSlider.Value);

        SystemVolumeText.Text = $"{sysPercent}%";
        MicVolumeText.Text = $"{micPercent}%";

        SystemMuteButton.Icon = new SymbolIcon(
            sysPercent == 0 ? SymbolRegular.SpeakerMute24 : SymbolRegular.Speaker224);
        MicMuteButton.Icon = new SymbolIcon(
            micPercent == 0 ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting || _selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        ClipItem clipToExport = _selectedClip;
        TimeSpan startTime = TimeSpan.FromSeconds(Math.Clamp(TrimRangeSlider.StartValue, 0.0, _clipDurationSeconds));
        TimeSpan endTime = TimeSpan.FromSeconds(Math.Clamp(TrimRangeSlider.EndValue, 0.0, _clipDurationSeconds));
        if (endTime <= startTime)
        {
            ShowExportStatus("Zakres przycinania musi być dłuższy niż 0 sekund.", ErrorStatusBrush);
            return;
        }

        ClipExportMode exportMode = KeepSeparateTracksRadio.IsChecked == true
            ? ClipExportMode.KeepSeparateAudioTracks
            : ClipExportMode.MixSingleTrackForDiscord;

        var options = new ClipExportOptions
        {
            InputFilePath = clipToExport.FilePath,
            StartTime = startTime,
            EndTime = endTime,
            SystemVolume = Math.Round(SystemVolumeSlider.Value) / 100.0,
            MicrophoneVolume = Math.Round(MicVolumeSlider.Value) / 100.0,
            ExportMode = exportMode
        };

        _isExporting = true;
        SetEditorControlsEnabled(hasClip: true);
        CancelExportButton.Visibility = Visibility.Visible;
        ExportProgressBar.Visibility = Visibility.Visible;
        ExportProgressBar.IsIndeterminate = true;
        ExportProgressBar.Value = 0;
        ShowExportStatus("Przygotowywanie eksportu...", NeutralStatusBrush);

        CancelTokenSource(ref _exportCts);
        var cts = new CancellationTokenSource();
        _exportCts = cts;

        var progress = new Progress<ClipExportProgress>(update =>
        {
            if (_isDetached)
            {
                return;
            }

            ExportProgressBar.IsIndeterminate = update.IsIndeterminate;
            if (update.Percent.HasValue)
            {
                ExportProgressBar.Value = update.Percent.Value;
            }

            ShowExportStatus(update.StatusMessage, NeutralStatusBrush);
        });

        try
        {
            ClipExportResult result = await _clipEditorService.ExportClipAsync(options, progress, cts.Token);

            if (_isDetached)
            {
                return;
            }

            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = 100;

            string exportedName = Path.GetFileName(result.OutputFilePath);
            string successMessage = result.UsedSingleTrackFallback && result.SourceAudioTrackCount == 1
                ? $"Wyeksportowano klip: {exportedName} (użyto pojedynczej ścieżki audio)."
                : $"Wyeksportowano klip: {exportedName}";

            ShowExportStatus(successMessage, SuccessStatusBrush);
            await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
        }
        catch (OperationCanceledException)
        {
            if (!_isDetached)
            {
                ShowExportStatus("Eksport został anulowany.", NeutralStatusBrush);
            }
        }
        catch (Exception ex)
        {
            if (!_isDetached)
            {
                ShowExportStatus($"Błąd eksportu: {ex.Message}", ErrorStatusBrush);
            }
        }
        finally
        {
            if (!_isDetached)
            {
                _isExporting = false;
                CancelExportButton.Visibility = Visibility.Collapsed;
                ExportProgressBar.Visibility = Visibility.Collapsed;
                ExportProgressBar.IsIndeterminate = false;
                SetEditorControlsEnabled(hasClip: _selectedClip is not null);
            }
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e)
    {
        CancelTokenSource(ref _exportCts);
    }

    private void ToggleClipsListButton_Click(object sender, RoutedEventArgs e)
    {
        _isClipsListCollapsed = !_isClipsListCollapsed;
        ApplyClipsListCollapsedState();
    }

    private void ApplyClipsListCollapsedState()
    {
        int animationGeneration = ++_sidebarAnimationGeneration;

        double targetWidth = _isClipsListCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        if (double.IsNaN(ClipsListContainer.Width))
        {
            ClipsListContainer.Width = ClipsListContainer.ActualWidth > 0.0
                ? ClipsListContainer.ActualWidth
                : ExpandedSidebarWidth;
        }

        var widthAnimation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = SidebarWidthAnimationDuration,
            EasingFunction = SidebarEaseOut
        };
        ClipsListContainer.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);

        SymbolRegular toggleSymbol = _isClipsListCollapsed
            ? SymbolRegular.ChevronRight20
            : SymbolRegular.ChevronLeft20;

        if (ToggleClipsListButton.Icon is SymbolIcon toggleIcon)
        {
            toggleIcon.Symbol = toggleSymbol;
        }
        else
        {
            ToggleClipsListButton.Icon = new SymbolIcon(toggleSymbol);
        }

        ToggleClipsListButton.ToolTip = _isClipsListCollapsed
            ? "Rozwiń listę nagrań"
            : "Zwiń listę nagrań";

        if (_isClipsListCollapsed)
        {
            ClipsListTitleText.IsHitTestVisible = false;
            ClipsListBodyGrid.IsHitTestVisible = false;

            var titleFadeOut = new DoubleAnimation
            {
                To = 0.0,
                Duration = SidebarContentFadeOutDuration,
                EasingFunction = SidebarEaseOut
            };

            var bodyFadeOut = new DoubleAnimation
            {
                To = 0.0,
                Duration = SidebarContentFadeOutDuration,
                EasingFunction = SidebarEaseOut
            };

            bodyFadeOut.Completed += (_, _) =>
            {
                if (_isDetached || animationGeneration != _sidebarAnimationGeneration || !_isClipsListCollapsed)
                {
                    return;
                }

                ClipsListTitleText.Visibility = Visibility.Collapsed;
                ClipsListBodyGrid.Visibility = Visibility.Collapsed;
            };

            ClipsListTitleText.BeginAnimation(UIElement.OpacityProperty, titleFadeOut);
            ClipsListBodyGrid.BeginAnimation(UIElement.OpacityProperty, bodyFadeOut);
        }
        else
        {
            ClipsListTitleText.Visibility = Visibility.Visible;
            ClipsListBodyGrid.Visibility = Visibility.Visible;
            ClipsListTitleText.IsHitTestVisible = true;
            ClipsListBodyGrid.IsHitTestVisible = true;

            var titleFadeIn = new DoubleAnimation
            {
                To = 1.0,
                Duration = SidebarContentFadeInDuration,
                EasingFunction = SidebarEaseOut
            };

            var bodyFadeIn = new DoubleAnimation
            {
                To = 1.0,
                Duration = SidebarContentFadeInDuration,
                EasingFunction = SidebarEaseOut
            };

            ClipsListTitleText.BeginAnimation(UIElement.OpacityProperty, titleFadeIn);
            ClipsListBodyGrid.BeginAnimation(UIElement.OpacityProperty, bodyFadeIn);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string outputDirectory = GetConfiguredOutputDirectory();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{outputDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowExportStatus($"Nie udało się otworzyć folderu: {ex.Message}", ErrorStatusBrush);
        }
    }

    private void OpenExternalPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip is null || !File.Exists(_selectedClip.FilePath))
        {
            return;
        }

        try
        {
            if (_isPlaying)
            {
                PausePlayback();
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _selectedClip.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowExportStatus($"Nie udało się otworzyć pliku: {ex.Message}", ErrorStatusBrush);
        }
    }

    private async void DeleteClipButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: ClipItem clip })
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"Czy na pewno chcesz trwale usunąć nagranie \"{clip.FileName}\"?",
            "Usuń nagranie",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool deletingSelectedClip = _selectedClip is not null &&
                                    string.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase);

        if (deletingSelectedClip)
        {
            UnloadMediaPreview(clearSelectionField: true);
        }

        try
        {
            await DeleteFileWithRetryAsync(clip.FilePath);
            ShowExportStatus($"Usunięto nagranie: {clip.FileName}", NeutralStatusBrush);
        }
        catch (Exception ex)
        {
            ShowExportStatus($"Nie udało się usunąć pliku \"{clip.FileName}\": {ex.Message}", ErrorStatusBrush);
        }

        await RefreshClipsAsync(preferredSelectionPath: deletingSelectedClip ? null : _selectedClip?.FilePath);
    }

    private static async Task DeleteFileWithRetryAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(filePath);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Give WPF MediaElement / Media Foundation a brief moment to release the closed file handle.
                await Task.Delay(100 * attempt);
            }
        }
    }

    private void ShowAudioLoadingOverlay(string message)
    {
        AudioLoadingText.Text = message;
        AudioLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideAudioLoadingOverlay()
    {
        AudioLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowExportStatus(string message, Brush foreground)
    {
        ExportStatusText.Text = message;
        ExportStatusText.Foreground = foreground;
        ExportStatusText.Visibility = Visibility.Visible;
    }

    private void HideExportStatus()
    {
        ExportStatusText.Text = string.Empty;
        ExportStatusText.Visibility = Visibility.Collapsed;
    }

    private static void CancelTokenSource(ref CancellationTokenSource? cts)
    {
        CancellationTokenSource? existing = Interlocked.Exchange(ref cts, null);
        if (existing is null)
        {
            return;
        }

        try
        {
            existing.Cancel();
        }
        catch
        {
            // Ignore cancellation errors on teardown.
        }
        finally
        {
            existing.Dispose();
        }
    }

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static CubicEase CreateFrozenEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// Wraps a <see cref="WaveStream"/> into an <see cref="ISampleProvider"/> protected by a shared lock
    /// so concurrent seeking from the UI thread is glitch-free and reaching EOF outputs silence instead of
    /// causing <see cref="MixingSampleProvider"/> to remove the track permanently.
    /// </summary>
    private sealed class LockedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private readonly object _syncLock;
        private volatile bool _isStopped;

        public LockedSampleProvider(WaveStream waveStream, object syncLock)
        {
            _inner = waveStream.ToSampleProvider();
            _syncLock = syncLock;
            WaveFormat = _inner.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public void MarkStopped() => _isStopped = true;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_isStopped)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            lock (_syncLock)
            {
                if (_isStopped)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                int samplesRead;
                try
                {
                    samplesRead = _inner.Read(buffer, offset, count);
                }
                catch
                {
                    samplesRead = 0;
                }

                if (samplesRead < count)
                {
                    Array.Clear(buffer, offset + samplesRead, count - samplesRead);
                }

                return count;
            }
        }
    }
}
