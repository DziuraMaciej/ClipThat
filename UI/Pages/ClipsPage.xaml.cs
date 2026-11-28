using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipThat.Engine;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Diagnostics;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI.Controls;
using ClipThat.UI.Dialogs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Controls;

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

    private static readonly SolidColorBrush NeutralStatusBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0xA8, 0xA8, 0xA8);
    private static readonly SolidColorBrush SuccessStatusBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0x4A, 0xDE, 0x80);
    private static readonly SolidColorBrush ErrorStatusBrush = UiBrushHelper.CreateFrozenBrush(0xFF, 0xF8, 0x71, 0x71);

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
    private long _lastTrimSeekTimestamp;
    private long _lastScrubSeekTimestamp;
    private double? _pendingTrimSeekSeconds;
    private double? _lastTrimDragSeconds;
    private double _suspendedPositionSeconds;
    private int _lastDisplayedCurrentSec = -1;
    private int _lastDisplayedTotalSec = -1;

    private bool _isClipsListCollapsed;
    private int _sidebarAnimationGeneration;
    private bool _isPlaying;
    private bool _isScrubbingTimeline;
    private bool _wasPlayingBeforeTimelineScrub;
    private bool _isDraggingTrimHandle;
    private bool _isRefreshingClipsList;
    private bool _isUpdatingVolumeSliders;
    private bool _isPopulatingExportFileName;
    private bool _userEditedExportFileName;
    private bool _isPreviewSuspendedForClip;
    private bool _restoreSuspendedPositionOnMediaOpened;
    private bool _isExtractingPreviewAudio;
    private bool _isPreviewAudioReady;
    private bool _isExporting;
    private bool _isDeletingClip;
    private bool _isDetached;

    public bool IsExporting => _isExporting;

    /// <summary>
    /// Raised on the UI thread when an active export finishes (succeeded, failed, or cancelled),
    /// allowing a hidden host window to close itself and reclaim WPF memory.
    /// </summary>
    public event Action? ExportCompleted;

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
        SuspendPreviewAndWatcher();
    }

    /// <summary>
    /// Called by <see cref="MainWindow"/> when the window is hidden to tray or restored,
    /// releasing file watchers, preview audio streams, and temp WAVs while minimized to tray.
    /// </summary>
    public void OnHostWindowVisibilityChanged(bool isVisible)
    {
        if (_isDetached)
        {
            return;
        }

        if (!isVisible)
        {
            SuspendPreviewAndWatcher();
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        string outputDirectory = GetConfiguredOutputDirectory();
        FolderPathText.Text = outputDirectory;
        FolderPathText.ToolTip = outputDirectory;
        ConfigureDirectoryWatcher(outputDirectory);
        _ = RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
    }

    private void SuspendPreviewAndWatcher()
    {
        _playbackTimer.Stop();
        _watcherDebounceTimer.Stop();
        DisposeDirectoryWatcher();
        CancelTokenSource(ref _refreshCts);

        if (_isPlaying)
        {
            PausePlayback();
        }

        // Close MediaElement while hidden in tray or navigated away so we release the MP4 file handle
        // and video decoder memory while keeping trim markers and playhead position ready to restore.
        if (_selectedClip is not null && PreviewMediaElement.Source is not null)
        {
            _suspendedPositionSeconds = _clipDurationSeconds > 0.0
                ? Math.Clamp(GetSafeMediaPosition().TotalSeconds, 0.0, _clipDurationSeconds)
                : 0.0;
            _isPreviewSuspendedForClip = true;

            try
            {
                PreviewMediaElement.Stop();
                PreviewMediaElement.Close();
                PreviewMediaElement.Source = null;
            }
            catch
            {
                // Ignore MediaElement teardown errors during suspend.
            }
        }

        // Dispose NAudio playback objects and delete temporary preview .wav files when hidden or navigated away.
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
            watcher.Error += OnDirectoryWatcherError;
            watcher.EnableRaisingEvents = true;

            _directoryWatcher = watcher;
        }
        catch (Exception ex)
        {
            EngineLogger.Warn("ClipsPage", $"Nie udało się uruchomić obserwatora folderu '{directoryPath}': {ex.Message}", ex);
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
            watcher.Error -= OnDirectoryWatcherError;
            watcher.Dispose();
        }
        catch
        {
            // Ignore disposal errors when tearing down FileSystemWatcher.
        }
    }

    private bool IsPageVisibleInHost()
    {
        if (_isDetached || !IsLoaded || !IsVisible)
        {
            return false;
        }

        Window? hostWindow = Window.GetWindow(this);
        return hostWindow is null || hostWindow.IsVisible;
    }

    private void OnDirectoryWatcherError(object sender, ErrorEventArgs e)
    {
        Exception? ex = e.GetException();
        EngineLogger.Warn("ClipsPage", $"Błąd obserwatora folderu nagrań (odtwarzanie): {ex?.Message ?? "nieznany"}", ex);

        if (_isDetached)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!IsPageVisibleInHost())
            {
                return;
            }

            ConfigureDirectoryWatcher(GetConfiguredOutputDirectory());
            ScheduleDebouncedRefresh();
        });
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
            if (!IsPageVisibleInHost())
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
        if (!IsPageVisibleInHost())
        {
            return;
        }

        await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
    }

    private async Task RefreshClipsAsync(string? preferredSelectionPath = null)
    {
        if (!IsPageVisibleInHost())
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

        if (!IsPageVisibleInHost() || cts.IsCancellationRequested)
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
                if (!_userEditedExportFileName && !ExportFileNameTextBox.IsKeyboardFocusWithin)
                {
                    PopulateDefaultExportFileName(matchingClip);
                }

                // Restore MediaElement source if it was closed while hidden in tray or navigated away.
                if (_isPreviewSuspendedForClip && PreviewMediaElement.Source is null && File.Exists(matchingClip.FilePath))
                {
                    _isPreviewSuspendedForClip = false;
                    _restoreSuspendedPositionOnMediaOpened = true;
                    try
                    {
                        if (_clipDurationSeconds > 0.0)
                        {
                            TrimRangeSlider.CurrentPosition = Math.Clamp(_suspendedPositionSeconds, 0.0, _clipDurationSeconds);
                        }

                        PreviewMediaElement.IsMuted = true;
                        PreviewMediaElement.Volume = 0.0;
                        PreviewMediaElement.Source = new Uri(matchingClip.FilePath, UriKind.Absolute);
                        PreviewMediaElement.Pause();
                    }
                    catch (Exception ex)
                    {
                        _restoreSuspendedPositionOnMediaOpened = false;
                        EngineLogger.Warn("ClipsPage", $"Nie udało się wznowić podglądu wideo: {ex.Message}", ex);
                    }
                }

                // Preview audio streams are initialized lazily on first Play to avoid allocating WaveOutEvent/MF handles while browsing.
                if (matchingClip.AudioTrackCount <= 0)
                {
                    _isPreviewAudioReady = true;
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
            _userEditedExportFileName = false;
            SetExportFileNameProgrammatically(string.Empty);
            SetEditorControlsEnabled(hasClip: false);
            ShowExportStatus("Wybrany plik nagrania nie istnieje już na dysku.", ErrorStatusBrush);
            return;
        }

        _selectedClip = clip;
        _userEditedExportFileName = false;
        _isPreviewSuspendedForClip = false;
        _restoreSuspendedPositionOnMediaOpened = false;
        _lastDisplayedCurrentSec = -1;
        _lastDisplayedTotalSec = -1;
        _clipDurationSeconds = Math.Max(0.0, clip.Duration.TotalSeconds);
        NoPreviewOverlay.Visibility = Visibility.Collapsed;

        UpdateSelectedClipSubtitleAndAudioHint(clip);
        PopulateDefaultExportFileName(clip);
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

        // Lazy-init preview audio (WaveOutEvent + MF IMFSourceReader) on first Play instead of eagerly on clip selection.
        _isPreviewAudioReady = clip.AudioTrackCount <= 0;
        HideAudioLoadingOverlay();
    }

    private bool IsCurrentSelectedClip(ClipItem clip)
        => _selectedClip is not null &&
           string.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase);

    private async Task PreparePreviewAudioAsync(ClipItem clip)
    {
        DisposeAudioMixerAndDeleteTempFiles();

        var cts = new CancellationTokenSource();
        _audioExtractionCts = cts;
        _isExtractingPreviewAudio = true;
        _isPreviewAudioReady = false;

        try
        {
            // Fast path: stream decoded PCM directly from the MP4 tracks via Media Foundation (zero temp disk I/O).
            (bool mfOpened, MfTrackAudioWaveStream? mfSys, MfTrackAudioWaveStream? mfMic) = await Task.Run(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return (false, (MfTrackAudioWaveStream?)null, (MfTrackAudioWaveStream?)null);
                }

                bool ok = MfTrackAudioWaveStream.TryOpenAudioTracks(
                    clip.FilePath,
                    clip.Duration,
                    out MfTrackAudioWaveStream? sys,
                    out MfTrackAudioWaveStream? mic);
                return (ok, sys, mic);
            }, cts.Token);

            if (!IsPageVisibleInHost() || cts.IsCancellationRequested || !IsCurrentSelectedClip(clip))
            {
                try { mfMic?.Dispose(); } catch { /* ignore */ }
                try { mfSys?.Dispose(); } catch { /* ignore */ }
                return;
            }

            if (mfOpened && (mfSys is not null || mfMic is not null))
            {
                InitializeAudioMixerFromStreams(mfSys, mfMic, tempSysWavPath: null, tempMicWavPath: null);
                return;
            }

            try { mfMic?.Dispose(); } catch { /* ignore */ }
            try { mfSys?.Dispose(); } catch { /* ignore */ }

            // Fallback path: extract temporary PCM WAV files via FFmpeg when Media Foundation cannot open the file.
            ShowAudioLoadingOverlay("Ładowanie ścieżek audio...");

            var progress = new Progress<ClipExportProgress>(update =>
            {
                if (_isDetached || cts.IsCancellationRequested || !IsCurrentSelectedClip(clip))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(update.StatusMessage))
                {
                    ShowAudioLoadingOverlay(update.StatusMessage);
                }
            });

            PreviewAudioExtractionResult extractionResult = await _clipEditorService.ExtractPreviewAudioTracksAsync(
                clip.FilePath,
                clip.AudioTrackCount,
                progress,
                cts.Token);

            if (!IsPageVisibleInHost() || cts.IsCancellationRequested || !IsCurrentSelectedClip(clip))
            {
                // Clip changed or page unloaded/hidden while ffmpeg was running; discard temp WAV files immediately.
                ClipEditorService.TryDeletePreviewFile(extractionResult.SystemAudioWavPath);
                ClipEditorService.TryDeletePreviewFile(extractionResult.MicrophoneAudioWavPath);
                return;
            }

            if (extractionResult.ExtractedTrackCount <= 0)
            {
                _isPreviewAudioReady = true;
                return;
            }

            WaveFileReader? sysReader = null;
            WaveFileReader? micReader = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(extractionResult.SystemAudioWavPath) &&
                    File.Exists(extractionResult.SystemAudioWavPath))
                {
                    sysReader = new WaveFileReader(extractionResult.SystemAudioWavPath);
                }

                if (!string.IsNullOrWhiteSpace(extractionResult.MicrophoneAudioWavPath) &&
                    File.Exists(extractionResult.MicrophoneAudioWavPath))
                {
                    micReader = new WaveFileReader(extractionResult.MicrophoneAudioWavPath);
                }
            }
            catch
            {
                try { sysReader?.Dispose(); } catch { /* ignore */ }
                try { micReader?.Dispose(); } catch { /* ignore */ }
                ClipEditorService.TryDeletePreviewFile(extractionResult.SystemAudioWavPath);
                ClipEditorService.TryDeletePreviewFile(extractionResult.MicrophoneAudioWavPath);
                throw;
            }

            InitializeAudioMixerFromStreams(
                sysReader,
                micReader,
                extractionResult.SystemAudioWavPath,
                extractionResult.MicrophoneAudioWavPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelled due to clip switch, hide-to-tray, or unload.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd przygotowania ścieżek audio do podglądu: {ex.Message}");
            if (!_isDetached && !cts.IsCancellationRequested && IsCurrentSelectedClip(clip))
            {
                AudioTracksHintText.Text = "Podgląd audio niedostępny";
            }
        }
        finally
        {
            if (ReferenceEquals(_audioExtractionCts, cts))
            {
                _isExtractingPreviewAudio = false;
                if (!_isDetached)
                {
                    HideAudioLoadingOverlay();
                }
            }
        }
    }

    private void InitializeAudioMixerFromStreams(
        WaveStream? sysStream,
        WaveStream? micStream,
        string? tempSysWavPath,
        string? tempMicWavPath)
    {
        WaveOutEvent? waveOut = null;
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

            if (sysStream is not null)
            {
                sysVolumeProvider = WrapStreamForPreviewMixer(sysStream, sysPercent, out sysProvider);
                mixer.AddMixerInput(sysVolumeProvider);
            }

            if (micStream is not null)
            {
                micVolumeProvider = WrapStreamForPreviewMixer(micStream, micPercent, out micProvider);
                mixer.AddMixerInput(micVolumeProvider);
            }

            if (sysVolumeProvider is null && micVolumeProvider is null)
            {
                ClipEditorService.TryDeletePreviewFile(tempSysWavPath);
                ClipEditorService.TryDeletePreviewFile(tempMicWavPath);
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
                _waveOut = waveOut;
                _tempSystemWavPath = tempSysWavPath;
                _tempMicWavPath = tempMicWavPath;
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

            ClipEditorService.TryDeletePreviewFile(tempSysWavPath);
            ClipEditorService.TryDeletePreviewFile(tempMicWavPath);

            AudioTracksHintText.Text = "Nie udało się uruchomić wyjścia audio podglądu";
        }
    }

    private VolumeSampleProvider WrapStreamForPreviewMixer(
        WaveStream stream,
        int volumePercent,
        out LockedSampleProvider lockedProvider)
    {
        lockedProvider = new LockedSampleProvider(stream, _audioSyncLock);
        ISampleProvider chain = lockedProvider;

        if (chain.WaveFormat.Channels == 1)
        {
            chain = new MonoToStereoSampleProvider(chain);
        }
        else if (chain.WaveFormat.Channels > PreviewMixerChannels)
        {
            chain = new DualAudioCaptureService.AudioPcmConverter.DownmixToStereoSampleProvider(chain);
        }

        if (chain.WaveFormat.SampleRate != PreviewMixerSampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, PreviewMixerSampleRate);
        }

        return new VolumeSampleProvider(chain)
        {
            Volume = volumePercent / 100f
        };
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
        _isPreviewSuspendedForClip = false;
        _restoreSuspendedPositionOnMediaOpened = false;
        _suspendedPositionSeconds = 0.0;
        _lastDisplayedCurrentSec = -1;
        _lastDisplayedTotalSec = -1;

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
            _userEditedExportFileName = false;
            _clipDurationSeconds = 0.0;
            NoPreviewOverlay.Visibility = Visibility.Visible;
            SelectedClipSubtitleText.Text = "Wybierz nagranie z listy po lewej stronie";
            AudioTracksHintText.Text = string.Empty;
            SetExportFileNameProgrammatically(string.Empty);
            InitializeTimelineAndTrimRange(0.0, resetRangeToFull: true);
            SetEditorControlsEnabled(hasClip: false);
        }
    }

    private void UpdateSelectedClipSubtitleAndAudioHint(ClipItem clip)
    {
        SelectedClipSubtitleText.Text = $"{clip.FileName} · {clip.FormattedDate} · {clip.FormattedSize}";
        AudioTracksHintText.Text = string.Empty;
    }

    private void PopulateDefaultExportFileName(ClipItem clip)
    {
        SetExportFileNameProgrammatically(Path.GetFileNameWithoutExtension(clip.FileName) + "_edited");
    }

    private void SetExportFileNameProgrammatically(string value)
    {
        _isPopulatingExportFileName = true;
        try
        {
            ExportFileNameTextBox.Text = value;
        }
        finally
        {
            _isPopulatingExportFileName = false;
        }
    }

    private void ExportFileNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isPopulatingExportFileName && _selectedClip is not null)
        {
            _userEditedExportFileName = true;
        }
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
        ClipsListBox.IsEnabled = !_isExporting;
        TrimRangeSlider.IsEnabled = canInteract && _clipDurationSeconds > 0.0;

        SystemVolumeSlider.IsEnabled = canInteract && hasAudio;
        SystemMuteButton.IsEnabled = canInteract && hasAudio;
        MicVolumeSlider.IsEnabled = canInteract && hasSeparateMic;
        MicMuteButton.IsEnabled = canInteract && hasSeparateMic;

        MixSingleTrackRadio.IsEnabled = canInteract;
        KeepSeparateTracksRadio.IsEnabled = canInteract && hasSeparateMic;
        ExportFileNameTextBox.IsEnabled = canInteract;
        ExportButton.IsEnabled = canInteract && _clipDurationSeconds > 0.0;
    }

    private void InitializeTimelineAndTrimRange(
        double durationSeconds,
        bool resetRangeToFull,
        double? initialPositionSeconds = null)
    {
        double safeMax = durationSeconds > 0.0 ? durationSeconds : 1.0;

        double targetStart = resetRangeToFull
            ? 0.0
            : Math.Clamp(TrimRangeSlider.StartValue, 0.0, safeMax);

        double targetEnd = resetRangeToFull
            ? (durationSeconds > 0.0 ? durationSeconds : 1.0)
            : Math.Clamp(TrimRangeSlider.EndValue, targetStart, safeMax);

        double targetPosition = initialPositionSeconds.HasValue
            ? Math.Clamp(initialPositionSeconds.Value, 0.0, safeMax)
            : resetRangeToFull
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

        bool restoreSuspendedPosition = _restoreSuspendedPositionOnMediaOpened;
        _restoreSuspendedPositionOnMediaOpened = false;

        if (PreviewMediaElement.NaturalDuration.HasTimeSpan &&
            PreviewMediaElement.NaturalDuration.TimeSpan > TimeSpan.Zero)
        {
            double naturalSeconds = PreviewMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
            bool hadValidDuration = _clipDurationSeconds > 0.0;
            bool startAtBeginning = !hadValidDuration || TrimRangeSlider.StartValue <= 0.01;
            bool endAtFullDuration = !hadValidDuration ||
                                     Math.Abs(TrimRangeSlider.EndValue - _clipDurationSeconds) < 0.25;
            _clipDurationSeconds = naturalSeconds;
            double? restoredPosition = restoreSuspendedPosition
                ? Math.Clamp(_suspendedPositionSeconds, 0.0, naturalSeconds)
                : null;

            if (startAtBeginning && endAtFullDuration)
            {
                InitializeTimelineAndTrimRange(naturalSeconds, resetRangeToFull: true, initialPositionSeconds: restoredPosition);
            }
            else
            {
                double preservedStart = Math.Clamp(TrimRangeSlider.StartValue, 0.0, naturalSeconds);
                double preservedEnd = endAtFullDuration
                    ? naturalSeconds
                    : Math.Clamp(TrimRangeSlider.EndValue, preservedStart, naturalSeconds);
                double preservedPos = restoredPosition ?? Math.Clamp(TrimRangeSlider.CurrentPosition, 0.0, naturalSeconds);

                TrimRangeSlider.SetRange(
                    minimum: 0.0,
                    maximum: naturalSeconds,
                    startValue: preservedStart,
                    endValue: preservedEnd,
                    currentPosition: preservedPos);
                UpdateTimeReadout(TimeSpan.FromSeconds(preservedPos));
                UpdateTrimDisplay();
            }

            SetEditorControlsEnabled(hasClip: true);
        }

        double initialSeconds = _clipDurationSeconds > 0.0
            ? Math.Clamp(TrimRangeSlider.CurrentPosition, 0.0, _clipDurationSeconds)
            : 0.0;
        TimeSpan initialPosition = TimeSpan.FromSeconds(initialSeconds);

        TryPauseWaveOut();
        PreviewMediaElement.Position = initialPosition;
        PreviewMediaElement.Pause();
        SeekAudioStreams(initialPosition);
        _isPlaying = false;
        _playbackTimer.Stop();
        UpdatePlayPauseButtonVisual();
        SyncPlayheadUi(initialSeconds, syncTrimPlayhead: true);
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
        string errorMessage = e.ErrorException?.Message ?? "Nieznany błąd odtwarzania";
        ShowExportStatus($"Nie udało się odtworzyć podglądu: {errorMessage}", ErrorStatusBrush);
    }

    private void VideoSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectedClip is not null)
        {
            TogglePlayPause();
        }
    }

    private void EditorScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VideoPreviewBorder is null || EditorScrollViewer is null || EditorScrollViewer.ViewportHeight <= 0.0)
        {
            return;
        }

        double targetHeight = Math.Clamp(EditorScrollViewer.ViewportHeight * 0.50, 260.0, 560.0);
        if (Math.Abs(VideoPreviewBorder.Height - targetHeight) >= 2.0)
        {
            VideoPreviewBorder.Height = targetHeight;
        }
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

            _isPlaying = true;
            _playbackTimer.Start();
            UpdatePlayPauseButtonVisual();

            if (_selectedClip.AudioTrackCount > 0 && !_isPreviewAudioReady && !_isExtractingPreviewAudio)
            {
                _ = PreparePreviewAudioAsync(_selectedClip);
            }
            else
            {
                TryStartWaveOut();
            }
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
        // Skip resync once video approaches or passes the audio stream's end to avoid 30 Hz EOF seek loops.
        if (_isPlaying && _waveOut?.PlaybackState == PlaybackState.Playing)
        {
            (TimeSpan? audioPosition, TimeSpan? audioTotalTime) = GetAudioTiming();
            if (audioPosition.HasValue && audioTotalTime.HasValue)
            {
                TimeSpan eofGuard = audioTotalTime.Value - TimeSpan.FromMilliseconds(MaxAudioVideoDriftMs);
                if (videoPosition < eofGuard &&
                    Math.Abs((videoPosition - audioPosition.Value).TotalMilliseconds) > MaxAudioVideoDriftMs)
                {
                    SeekAudioStreams(videoPosition);
                }
            }
        }

        SyncPlayheadUi(currentSec, syncTrimPlayhead: true);
    }

    private void TrimRangeSlider_ThumbDragStarted(object? sender, EventArgs e)
    {
        _isDraggingTrimHandle = true;
        _pendingTrimSeekSeconds = null;
        _lastTrimDragSeconds = null;
        _lastTrimSeekTimestamp = 0;
        if (_isPlaying)
        {
            PausePlayback();
        }
    }

    private void TrimRangeSlider_ThumbDragCompleted(object? sender, EventArgs e)
    {
        _isDraggingTrimHandle = false;
        double? finalTarget = _pendingTrimSeekSeconds ?? _lastTrimDragSeconds;
        _pendingTrimSeekSeconds = null;
        _lastTrimDragSeconds = null;

        if (finalTarget.HasValue && _selectedClip is not null && _clipDurationSeconds > 0.0)
        {
            SeekMediaAndAudioToSeconds(finalTarget.Value, syncTrimPlayhead: false);
        }
    }

    private void TrimRangeSlider_StartChanged(object? sender, double newStartSeconds)
    {
        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        UpdateTrimDisplay();
        SeekForTrimChange(newStartSeconds);
    }

    private void TrimRangeSlider_EndChanged(object? sender, double newEndSeconds)
    {
        if (_selectedClip is null || _clipDurationSeconds <= 0.0)
        {
            return;
        }

        UpdateTrimDisplay();
        SeekForTrimChange(newEndSeconds);
    }

    private void SeekForTrimChange(double targetSeconds)
    {
        if (!_isDraggingTrimHandle)
        {
            SeekMediaAndAudioToSeconds(targetSeconds, syncTrimPlayhead: false);
            return;
        }

        _lastTrimDragSeconds = targetSeconds;
        long now = Stopwatch.GetTimestamp();
        if (_lastTrimSeekTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastTrimSeekTimestamp, now).TotalMilliseconds < 50.0)
        {
            _pendingTrimSeekSeconds = targetSeconds;
            return;
        }

        _lastTrimSeekTimestamp = now;
        _pendingTrimSeekSeconds = null;
        SeekVideoOnlyToSeconds(targetSeconds);
        SyncPlayheadUi(targetSeconds, syncTrimPlayhead: false);
    }

    private void TrimRangeSlider_PositionSeekStarted(object? sender, EventArgs e)
    {
        _isScrubbingTimeline = true;
        _lastScrubSeekTimestamp = 0;
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
            // Throttle live MediaElement seeks while dragging on high-polling-rate mice;
            // exact final position and audio streams resync cleanly on PositionSeekCompleted.
            long now = Stopwatch.GetTimestamp();
            if (_lastScrubSeekTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastScrubSeekTimestamp, now).TotalMilliseconds >= 35.0)
            {
                _lastScrubSeekTimestamp = now;
                SeekVideoOnlyToSeconds(clamped);
            }
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
            SeekWaveStreamLocked(_systemWaveStream, _systemSampleProvider, position);
            SeekWaveStreamLocked(_micWaveStream, _micSampleProvider, position);
        }
    }

    private (TimeSpan? CurrentTime, TimeSpan? TotalTime) GetAudioTiming()
    {
        // Read lock-free cached timing snapshots updated on the NAudio playback thread and on seek
        // so the 30 Hz UI PlaybackTimer_Tick never contends with _audioSyncLock.
        LockedSampleProvider? sysProvider = Volatile.Read(ref _systemSampleProvider);
        LockedSampleProvider? micProvider = Volatile.Read(ref _micSampleProvider);

        LockedSampleProvider? activeProvider = sysProvider;
        if (activeProvider is null)
        {
            activeProvider = micProvider;
        }
        else if (micProvider is not null && micProvider.TotalTime > activeProvider.TotalTime)
        {
            activeProvider = micProvider;
        }

        return activeProvider is null
            ? (null, null)
            : (activeProvider.LastCurrentTime, activeProvider.TotalTime);
    }

    private static void SeekWaveStreamLocked(
        WaveStream? stream,
        LockedSampleProvider? provider,
        TimeSpan position)
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
            provider?.UpdateCachedPosition(stream.CurrentTime);
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
        int currentWholeSec = Math.Max(0, (int)Math.Floor(currentPosition.TotalSeconds));
        int totalWholeSec = Math.Max(0, (int)Math.Floor(Math.Max(0.0, _clipDurationSeconds)));
        if (currentWholeSec == _lastDisplayedCurrentSec && totalWholeSec == _lastDisplayedTotalSec)
        {
            return;
        }

        _lastDisplayedCurrentSec = currentWholeSec;
        _lastDisplayedTotalSec = totalWholeSec;
        string currentText = ClipItem.FormatDuration(TimeSpan.FromSeconds(currentWholeSec));
        string totalText = ClipItem.FormatDuration(TimeSpan.FromSeconds(totalWholeSec));
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
        => ToggleTrackMute(SystemVolumeSlider, ref _lastNonZeroSystemVolume);

    private void MicMuteButton_Click(object sender, RoutedEventArgs e)
        => ToggleTrackMute(MicVolumeSlider, ref _lastNonZeroMicVolume);

    private void ToggleTrackMute(Slider slider, ref double lastNonZeroVolume)
    {
        _isUpdatingVolumeSliders = true;
        try
        {
            if (slider.Value > 0.5)
            {
                lastNonZeroVolume = slider.Value;
                slider.Value = 0.0;
            }
            else
            {
                slider.Value = lastNonZeroVolume > 0.5 ? lastNonZeroVolume : 100.0;
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

        SymbolRegular sysSymbol = sysPercent == 0 ? SymbolRegular.SpeakerMute24 : SymbolRegular.Speaker224;
        if (SystemMuteButton.Icon is SymbolIcon sysIcon)
        {
            sysIcon.Symbol = sysSymbol;
        }
        else
        {
            SystemMuteButton.Icon = new SymbolIcon(sysSymbol);
        }

        SymbolRegular micSymbol = micPercent == 0 ? SymbolRegular.MicOff24 : SymbolRegular.Mic24;
        if (MicMuteButton.Icon is SymbolIcon micIcon)
        {
            micIcon.Symbol = micSymbol;
        }
        else
        {
            MicMuteButton.Icon = new SymbolIcon(micSymbol);
        }
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
            OutputFileName = ExportFileNameTextBox.Text,
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
            ClipExportResult result = await _clipEditorService.ExportTrimmedClipAsync(
                options,
                startTime,
                endTime,
                progress,
                cts.Token);

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
            _watcherDebounceTimer.Stop();
            if (IsPageVisibleInHost())
            {
                await RefreshClipsAsync(preferredSelectionPath: _selectedClip?.FilePath);
            }
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
            bool shouldNotifyExportCompleted = false;
            if (!_isDetached)
            {
                _isExporting = false;
                CancelExportButton.Visibility = Visibility.Collapsed;
                ExportProgressBar.Visibility = Visibility.Collapsed;
                ExportProgressBar.IsIndeterminate = false;
                SetEditorControlsEnabled(hasClip: _selectedClip is not null);
                shouldNotifyExportCompleted = true;
            }

            if (shouldNotifyExportCompleted)
            {
                ExportCompleted?.Invoke();
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
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{outputDirectory}\"")
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

            using Process? _ = Process.Start(new ProcessStartInfo
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

        if (_isExporting || _isDeletingClip || _isDetached || sender is not FrameworkElement { Tag: ClipItem clip })
        {
            return;
        }

        _isDeletingClip = true;
        try
        {
            Window? owner = Window.GetWindow(this);
            var confirmDialog = new ConfirmDialog(
                title: "Usuń nagranie",
                message: $"Czy na pewno chcesz trwale usunąć nagranie \"{clip.FileName}\"? Tej operacji nie można cofnąć.",
                confirmText: "Usuń",
                cancelText: "Anuluj",
                icon: SymbolRegular.Delete24);

            if (owner is { IsVisible: true })
            {
                confirmDialog.Owner = owner;
            }
            else
            {
                confirmDialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            if (confirmDialog.ShowDialog() != true || _isDetached)
            {
                return;
            }

            bool deletingSelectedClip =
                (_selectedClip is not null &&
                 string.Equals(_selectedClip.FilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                (PreviewMediaElement.Source is { IsFile: true } sourceUri &&
                 string.Equals(sourceUri.LocalPath, clip.FilePath, StringComparison.OrdinalIgnoreCase));

            if (deletingSelectedClip)
            {
                // Stop playback and release MediaElement source + NAudio .wav streams before deleting the file.
                UnloadMediaPreview(clearSelectionField: true);
            }

            try
            {
                await DeleteFileWithRetryAsync(clip.FilePath);
                if (!_isDetached)
                {
                    ShowExportStatus($"Usunięto nagranie: {clip.FileName}", NeutralStatusBrush);
                }
            }
            catch (Exception ex)
            {
                if (!_isDetached)
                {
                    ShowExportStatus($"Nie udało się usunąć pliku \"{clip.FileName}\": {ex.Message}", ErrorStatusBrush);
                }
            }

            if (!_isDetached)
            {
                await RefreshClipsAsync(preferredSelectionPath: deletingSelectedClip ? null : _selectedClip?.FilePath);
            }
        }
        finally
        {
            _isDeletingClip = false;
        }
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
        private readonly WaveStream _waveStream;
        private readonly ISampleProvider _inner;
        private readonly object _syncLock;
        private readonly long _totalTimeTicks;
        private long _lastCurrentTimeTicks;
        private volatile bool _isStopped;

        public LockedSampleProvider(WaveStream waveStream, object syncLock)
        {
            _waveStream = waveStream;
            _inner = waveStream.ToSampleProvider();
            _syncLock = syncLock;
            WaveFormat = _inner.WaveFormat;
            _totalTimeTicks = waveStream.TotalTime.Ticks;
            _lastCurrentTimeTicks = waveStream.CurrentTime.Ticks;
        }

        public WaveFormat WaveFormat { get; }

        public TimeSpan LastCurrentTime => TimeSpan.FromTicks(Volatile.Read(ref _lastCurrentTimeTicks));

        public TimeSpan TotalTime => TimeSpan.FromTicks(_totalTimeTicks);

        public void MarkStopped() => _isStopped = true;

        public void UpdateCachedPosition(TimeSpan currentTime)
            => Volatile.Write(ref _lastCurrentTimeTicks, currentTime.Ticks);

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
                    Volatile.Write(ref _lastCurrentTimeTicks, _waveStream.CurrentTime.Ticks);
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
