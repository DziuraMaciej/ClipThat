using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Muxing;
using ClipThat.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ClipThat.Services;

/// <summary>
/// Controls how exported audio tracks are packaged in the trimmed MP4 output.
/// </summary>
public enum ClipExportMode
{
    MixSingleTrackForDiscord,
    KeepSeparateAudioTracks
}

/// <summary>
/// Parameters for fast stream-copy trimming and per-track audio volume adjustment.
/// </summary>
public sealed class ClipExportOptions
{
    public required string InputFilePath { get; init; }

    public string? OutputFileName { get; init; }

    public double SystemVolume { get; init; } = 1.0;

    public double MicrophoneVolume { get; init; } = 1.0;

    public ClipExportMode ExportMode { get; init; } = ClipExportMode.MixSingleTrackForDiscord;
}

/// <summary>
/// Progress payload emitted while downloading FFmpeg on demand or exporting a clip.
/// </summary>
public readonly record struct ClipExportProgress(
    string StatusMessage,
    double? Percent,
    bool IsIndeterminate);

/// <summary>
/// Result metadata returned after a clip export completes.
/// </summary>
public sealed record ClipExportResult(
    string OutputFilePath,
    int SourceAudioTrackCount,
    bool UsedSingleTrackFallback);

/// <summary>
/// Temporary PCM WAV file paths extracted from an MP4 clip for live dual-track NAudio preview playback.
/// </summary>
public sealed record PreviewAudioExtractionResult(
    string? SystemAudioWavPath,
    string? MicrophoneAudioWavPath,
    int ExtractedTrackCount);

/// <summary>
/// Discovers recorded MP4 replays, reads media metadata without blocking UI, resolves or downloads
/// FFmpeg on demand, and performs near-instant <c>-c:v copy</c> trimming and audio mixing.
/// </summary>
public sealed partial class ClipEditorService : IDisposable
{
    private const string FfmpegEssentialsDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FfmpegEssentialsSha256Url =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
    private static readonly string[] HardwareEncoderCandidates =
        { "h264_nvenc", "h264_amf", "h264_qsv" };
    private static readonly char[] Sha256Separators = { ' ', '\t', '\r', '\n' };
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim FfmpegDownloadGate = new(1, 1);
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly ConcurrentDictionary<string, CachedClipMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _hwEncoderProbeLock = new(1, 1);
    private readonly HashSet<string> _failedHardwareEncoders = new(StringComparer.Ordinal);
    private volatile string? _preferredHardwareVideoEncoder;

    /// <summary>
    /// Directory storing temporary PCM WAV files extracted for live dual-track preview playback.
    /// </summary>
    public static string PreviewTempDirectory => Path.Combine(Path.GetTempPath(), "ClipThat", "Preview");

    [GeneratedRegex(@"time=(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})\.(?<f>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegTimeRegex();

    /// <summary>
    /// Scans <paramref name="outputDirectory"/> for <c>.mp4</c> clips, populates duration and track metadata,
    /// and returns them sorted newest first.
    /// </summary>
    public async Task<IReadOnlyList<ClipItem>> ScanClipsAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return Array.Empty<ClipItem>();
        }

        return await Task.Run(async () =>
        {
            var results = new List<ClipItem>();
            DirectoryInfo directoryInfo = new(outputDirectory);

            List<FileInfo> files;
            try
            {
                files = directoryInfo.EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (IReadOnlyList<ClipItem>)Array.Empty<ClipItem>();
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coldEntries = new List<(string FullName, string Name, long FileSizeBytes, long LastWriteTicksUtc, DateTime RecordedAt)>();

            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!file.Exists)
                    {
                        continue;
                    }

                    string fullName = file.FullName;
                    seenPaths.Add(fullName);

                    long fileSizeBytes = file.Length;
                    long lastWriteTicksUtc = file.LastWriteTimeUtc.Ticks;
                    DateTime recordedAt = ClipItem.ParseRecordedAt(fullName, file.CreationTime);

                    if (_metadataCache.TryGetValue(fullName, out CachedClipMetadata cached) &&
                        cached.LastWriteTicksUtc == lastWriteTicksUtc &&
                        cached.FileSizeBytes == fileSizeBytes)
                    {
                        results.Add(new ClipItem(
                            fullName,
                            file.Name,
                            recordedAt,
                            cached.Duration,
                            fileSizeBytes,
                            cached.AudioTrackCount));
                    }
                    else
                    {
                        coldEntries.Add((fullName, file.Name, fileSizeBytes, lastWriteTicksUtc, recordedAt));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip files that are momentarily locked or removed during enumeration.
                    EngineLogger.Warn("ClipEditorService", $"Pominięto plik podczas skanowania nagrań '{file.FullName}': {ex.Message}");
                }
            }

            if (coldEntries.Count > 0)
            {
                // Resolve ffprobe.exe once per scan instead of walking %PATH% inside every parallel worker.
                string? resolvedFfprobePath = ResolveToolExecutable("ffprobe.exe");
                var coldResults = new ConcurrentBag<ClipItem>();
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
                    CancellationToken = cancellationToken
                };

                await Parallel.ForEachAsync(coldEntries, parallelOptions, async (entry, ct) =>
                {
                    try
                    {
                        (TimeSpan duration, int audioTrackCount) = await ReadMediaMetadataAsync(
                            entry.FullName,
                            ct,
                            resolvedFfprobePath,
                            hasPreResolvedFfprobe: true).ConfigureAwait(false);

                        // Cache both positive and negative metadata lookups (keyed by size + write timestamp)
                        // so unreadable/zero-duration files do not re-run WinRT + ffprobe on every scan.
                        _metadataCache[entry.FullName] = new CachedClipMetadata(
                            entry.LastWriteTicksUtc,
                            entry.FileSizeBytes,
                            duration,
                            audioTrackCount);

                        coldResults.Add(new ClipItem(
                            entry.FullName,
                            entry.Name,
                            entry.RecordedAt,
                            duration,
                            entry.FileSizeBytes,
                            audioTrackCount));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        EngineLogger.Warn("ClipEditorService", $"Pominięto plik podczas odczytu metadanych '{entry.FullName}': {ex.Message}");
                    }
                }).ConfigureAwait(false);

                results.AddRange(coldResults);
            }

            foreach (string cachedPath in _metadataCache.Keys)
            {
                if (!seenPaths.Contains(cachedPath))
                {
                    _metadataCache.TryRemove(cachedPath, out _);
                }
            }

            results.Sort(static (a, b) =>
            {
                int cmp = b.RecordedAt.CompareTo(a.RecordedAt);
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(b.FileName, a.FileName);
            });

            return (IReadOnlyList<ClipItem>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves <c>ffmpeg.exe</c> using a unified deterministic lookup order:
    /// application directory, <c>Tools\ffmpeg.exe</c>, portable embedded extraction cache
    /// (<c>%LocalAppData%\ClipThat\bin\ffmpeg.exe</c>), user <c>Tools</c> cache, or system <c>PATH</c>.
    /// </summary>
    public string? ResolveFfmpegPath() => ResolveToolExecutable("ffmpeg.exe");

    /// <summary>
    /// Ensures <c>ffmpeg.exe</c> is available locally from the application directory, embedded portable
    /// extraction cache (<c>%LocalAppData%\ClipThat\bin\ffmpeg.exe</c>), system <c>PATH</c>, or one-time download.
    /// </summary>
    public async Task<string> EnsureFfmpegAvailableAsync(
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Prefer an adjacent copy next to the executable (AppContext.BaseDirectory or Tools\).
        string? localBundled = PortableFfmpegProvider.ResolveAdjacentExecutable("ffmpeg.exe");
        if (localBundled is not null)
        {
            return localBundled;
        }

        // 2. Otherwise extract embedded portable ffmpeg.exe (single-file publish mode).
        try
        {
            bool needsExtraction = !PortableFfmpegProvider.IsAlreadyExtracted();
            if (needsExtraction)
            {
                progress?.Report(new ClipExportProgress(
                    "Przygotowywanie wbudowanego FFmpeg...",
                    null,
                    IsIndeterminate: true));
            }

            string? portablePath = await PortableFfmpegProvider.TryEnsureExtractedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(portablePath) && File.Exists(portablePath))
            {
                return portablePath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "ClipEditorService",
                $"Failed to extract embedded portable ffmpeg.exe ({ex.Message}); checking fallback locations.",
                ex);
        }

        // 3. Fall back to LocalAppData cache or system PATH.
        string? existing = ResolveFfmpegPath();
        if (existing is not null)
        {
            return existing;
        }

        await FfmpegDownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveFfmpegPath();
            if (existing is not null)
            {
                return existing;
            }

            string targetDirectory = GetWritableToolsDirectory();
            string targetFfmpegPath = Path.Combine(targetDirectory, "ffmpeg.exe");
            string targetFfprobePath = Path.Combine(targetDirectory, "ffprobe.exe");
            string tempZipPath = Path.Combine(targetDirectory, $"ffmpeg_download_{Guid.NewGuid():N}.zip");

            progress?.Report(new ClipExportProgress(
                "Nie znaleziono ffmpeg.exe — rozpoczynanie jednorazowego pobierania FFmpeg...",
                null,
                IsIndeterminate: true));

            try
            {
                using HttpResponseMessage response = await SharedHttpClient.GetAsync(
                    FfmpegEssentialsDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                string downloadedSha256Hex;

                using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using (Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    await using (FileStream zipStream = new(
                        tempZipPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true))
                    {
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int lastReportedPercent = -1;
                        long lastReportedBytes = 0;

                        while (true)
                        {
                            int bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (bytesRead <= 0)
                            {
                                break;
                            }

                            sha256.AppendData(buffer, 0, bytesRead);
                            await zipStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                            totalRead += bytesRead;

                            if (totalBytes is > 0)
                            {
                                double percent = Math.Clamp((totalRead * 100.0) / totalBytes.Value, 0.0, 100.0);
                                int wholePercent = (int)percent;
                                if (wholePercent != lastReportedPercent)
                                {
                                    lastReportedPercent = wholePercent;
                                    progress?.Report(new ClipExportProgress(
                                        $"Pobieranie FFmpeg ({wholePercent}%)...",
                                        percent,
                                        IsIndeterminate: false));
                                }
                            }
                            else if (totalRead - lastReportedBytes >= 262_144)
                            {
                                lastReportedBytes = totalRead;
                                double downloadedMb = totalRead / (1024.0 * 1024.0);
                                progress?.Report(new ClipExportProgress(
                                    string.Format(PolishCulture, "Pobieranie FFmpeg ({0:0.0} MB)...", downloadedMb),
                                    null,
                                    IsIndeterminate: true));
                            }
                        }
                    }

                    downloadedSha256Hex = Convert.ToHexString(sha256.GetHashAndReset());
                }

                progress?.Report(new ClipExportProgress(
                    "Weryfikowanie sumy kontrolnej SHA-256 archiwum FFmpeg...",
                    null,
                    IsIndeterminate: true));

                await VerifyDownloadedArchiveSha256Async(downloadedSha256Hex, cancellationToken).ConfigureAwait(false);

                progress?.Report(new ClipExportProgress(
                    "Rozpakowywanie ffmpeg.exe...",
                    null,
                    IsIndeterminate: true));

                await Task.Run(() =>
                {
                    using ZipArchive archive = ZipFile.OpenRead(tempZipPath);
                    ZipArchiveEntry? ffmpegEntry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));

                    if (ffmpegEntry is null)
                    {
                        throw new InvalidOperationException("Pobrane archiwum nie zawiera pliku ffmpeg.exe.");
                    }

                    ExtractArchiveEntryAtomic(ffmpegEntry, targetFfmpegPath);

                    ZipArchiveEntry? ffprobeEntry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "ffprobe.exe", StringComparison.OrdinalIgnoreCase));
                    if (ffprobeEntry is not null && !File.Exists(targetFfprobePath))
                    {
                        try
                        {
                            ExtractArchiveEntryAtomic(ffprobeEntry, targetFfprobePath);
                        }
                        catch
                        {
                            // ffprobe.exe is optional because native MP4 box probing handles metadata.
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (!File.Exists(targetFfmpegPath))
                {
                    throw new InvalidOperationException("Nie udało się zapisać pliku ffmpeg.exe po rozpakowaniu.");
                }

                return targetFfmpegPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Nie udało się pobrać FFmpeg ({ex.Message}). Umieść plik ffmpeg.exe w folderze Tools obok aplikacji lub dodaj go do zmiennej środowiskowej PATH.",
                    ex);
            }
            finally
            {
                TryDeleteFile(tempZipPath);
            }
        }
        finally
        {
            FfmpegDownloadGate.Release();
        }
    }

    /// <summary>
    /// Extracts isolated audio tracks (<c>0:a:0</c> System Audio and <c>0:a:1</c> Microphone Audio,
    /// handling 1-, 2-, and 3-track MP4 files gracefully) into temporary 48 kHz 16-bit Stereo PCM
    /// <c>.wav</c> files under <c>%TEMP%\ClipThat\Preview\</c> for live dual-track NAudio playback.
    /// </summary>
    public async Task<PreviewAudioExtractionResult> ExtractPreviewAudioTracksAsync(
        string inputFilePath,
        int knownAudioTrackCount,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath) || !File.Exists(inputFilePath))
        {
            return new PreviewAudioExtractionResult(null, null, 0);
        }

        int audioTrackCount = knownAudioTrackCount;
        if (audioTrackCount <= 0)
        {
            (_, audioTrackCount) = await ReadMediaMetadataAsync(inputFilePath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (audioTrackCount <= 0)
        {
            return new PreviewAudioExtractionResult(null, null, 0);
        }

        string ffmpegPath = await EnsureFfmpegAvailableAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        string previewDir = PreviewTempDirectory;
        Directory.CreateDirectory(previewDir);

        string sessionId = Guid.NewGuid().ToString("N");
        string sysWavPath = Path.Combine(previewDir, $"{sessionId}_sys.wav");
        string micWavPath = Path.Combine(previewDir, $"{sessionId}_mic.wav");

        bool extractMicTrack = audioTrackCount >= 2;
        if (extractMicTrack)
        {
            bool dualSucceeded = await RunAudioExtractionProcessAsync(
                ffmpegPath,
                inputFilePath,
                sysWavPath,
                micWavPath,
                cancellationToken).ConfigureAwait(false);

            if (dualSucceeded && IsValidWavFile(sysWavPath))
            {
                bool hasMicWav = IsValidWavFile(micWavPath);
                if (!hasMicWav)
                {
                    TryDeleteFile(micWavPath);
                    return new PreviewAudioExtractionResult(sysWavPath, null, 1);
                }

                return new PreviewAudioExtractionResult(sysWavPath, micWavPath, 2);
            }

            // Clean up any partial dual-track outputs before attempting single-track fallback.
            TryDeleteFile(sysWavPath);
            TryDeleteFile(micWavPath);
        }

        bool singleSucceeded = await RunAudioExtractionProcessAsync(
            ffmpegPath,
            inputFilePath,
            sysWavPath,
            micWavPath: null,
            cancellationToken).ConfigureAwait(false);

        if (singleSucceeded && IsValidWavFile(sysWavPath))
        {
            return new PreviewAudioExtractionResult(sysWavPath, null, 1);
        }

        TryDeleteFile(sysWavPath);
        return new PreviewAudioExtractionResult(null, null, 0);
    }

    /// <summary>
    /// Best-effort deletion of a single temporary preview audio file.
    /// </summary>
    public static void TryDeletePreviewFile(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            TryDeleteFile(filePath);
        }
    }

    /// <summary>
    /// Best-effort cleanup of leftover temporary <c>.wav</c> files in <c>%TEMP%\ClipThat\Preview\</c>.
    /// </summary>
    public static void CleanupPreviewTempDirectory()
    {
        try
        {
            string previewDir = PreviewTempDirectory;
            if (!Directory.Exists(previewDir))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(previewDir, "*.wav", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(file);
            }
        }
        catch
        {
            // Ignore best-effort cleanup errors.
        }
    }

    private static async Task<bool> RunAudioExtractionProcessAsync(
        string ffmpegPath,
        string inputFilePath,
        string sysWavPath,
        string? micWavPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFilePath);

        // Stream 0:a:0 -> Isolated System Audio (48 kHz, Stereo, 16-bit PCM WAV)
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add(sysWavPath);

        if (!string.IsNullOrWhiteSpace(micWavPath))
        {
            // Stream 0:a:1 -> Isolated Microphone Audio (48 kHz, Stereo, 16-bit PCM WAV)
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:1");
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("48000");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add(micWavPath);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return false;
            }

            using CancellationTokenRegistration cancelRegistration =
                RegisterProcessTermination(process, cancellationToken);

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await EnsureProcessExitedAfterKillAsync(process).ConfigureAwait(false);
                await ObserveTaskSafeAsync(stderrTask).ConfigureAwait(false);
                throw;
            }

            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"Ekstrakcja audio podglądu nie powiodła się (kod {process.ExitCode}): {stderr}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(sysWavPath);
            if (!string.IsNullOrWhiteSpace(micWavPath))
            {
                TryDeleteFile(micWavPath);
            }

            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Błąd uruchamiania ffmpeg dla podglądu audio: {ex.Message}");
            TryDeleteFile(sysWavPath);
            if (!string.IsNullOrWhiteSpace(micWavPath))
            {
                TryDeleteFile(micWavPath);
            }

            return false;
        }
    }

    private static bool IsValidWavFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 44;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Exports a trimmed and volume-adjusted copy of the input MP4 using <c>-c:v copy</c> without video re-encoding
    /// (falling back automatically to frame-accurate H.264 re-encoding if sparse keyframes cause the stream-copied
    /// output duration to exceed the requested <paramref name="startTime"/>..<paramref name="endTime"/> range),
    /// saving the result as <c>{sanitizedName}.mp4</c> in the same folder (falling back to
    /// <c>{originalNameWithoutExt}_edited.mp4</c> when no custom output name is provided).
    /// </summary>
    public async Task<ClipExportResult> ExportTrimmedClipAsync(
        ClipExportOptions options,
        TimeSpan startTime,
        TimeSpan endTime,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.InputFilePath) || !File.Exists(options.InputFilePath))
        {
            throw new FileNotFoundException("Wybrany plik nagrania nie istnieje.", options.InputFilePath);
        }

        TimeSpan normalizedStart = startTime < TimeSpan.Zero ? TimeSpan.Zero : startTime;
        TimeSpan normalizedEnd = endTime;
        if (normalizedEnd <= normalizedStart)
        {
            throw new ArgumentException("Czas zakończenia musi być większy niż czas rozpoczęcia.");
        }

        TimeSpan duration = normalizedEnd - normalizedStart;
        double systemVolume = Math.Clamp(options.SystemVolume, 0.0, 2.0);
        double microphoneVolume = Math.Clamp(options.MicrophoneVolume, 0.0, 2.0);

        string ffmpegPath = await EnsureFfmpegAvailableAsync(progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ClipExportProgress(
            "Analizowanie ścieżek audio nagrania...",
            0,
            IsIndeterminate: true));

        (TimeSpan sourceDuration, int audioTrackCount) = await ReadMediaMetadataAsync(
            options.InputFilePath,
            cancellationToken).ConfigureAwait(false);

        TimeSpan expectedExportDuration = sourceDuration > normalizedStart
            ? TimeSpan.FromTicks(Math.Min(duration.Ticks, (sourceDuration - normalizedStart).Ticks))
            : duration;

        string sanitizedBaseName = SanitizeOutputBaseName(options.OutputFileName, options.InputFilePath);
        string outputDirectory = ResolveOutputDirectory(options.InputFilePath);
        string tempOutputPath = Path.Combine(outputDirectory, $".clipthat_export_{Guid.NewGuid():N}.tmp");

        bool usedSingleTrackFallback = audioTrackCount < 2;
        bool shouldPatchMultiTrackFlags =
            options.ExportMode == ClipExportMode.KeepSeparateAudioTracks && audioTrackCount >= 2;

        ProcessStartInfo startInfo = BuildFfmpegStartInfo(
            ffmpegPath,
            options.InputFilePath,
            tempOutputPath,
            normalizedStart,
            duration,
            systemVolume,
            microphoneVolume,
            options.ExportMode,
            audioTrackCount,
            reencodeVideo: false,
            hardwareVideoEncoder: null);

        progress?.Report(new ClipExportProgress(
            "Eksportowanie przyciętego klipu...",
            0,
            IsIndeterminate: false));

        try
        {
            await RunFfmpegExportProcessAsync(
                startInfo,
                expectedExportDuration,
                "Eksportowanie przyciętego klipu",
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!File.Exists(tempOutputPath))
            {
                throw new InvalidOperationException("FFmpeg nie utworzył pliku wyjściowego.");
            }

            // Verify exported clip duration against EndValue - StartValue. If stream copy snapped back
            // to an earlier keyframe (causing excess pre-roll / A-V offset), fall back to frame-accurate export.
            (TimeSpan exportedDuration, _) = await ReadMediaMetadataAsync(
                tempOutputPath,
                cancellationToken).ConfigureAwait(false);

            double maxAllowedDriftSeconds = Math.Min(0.5, Math.Max(0.25, expectedExportDuration.TotalSeconds * 0.15));
            bool durationMismatch = exportedDuration > TimeSpan.Zero &&
                                    Math.Abs((exportedDuration - expectedExportDuration).TotalSeconds) > maxAllowedDriftSeconds;

            if (durationMismatch)
            {
                TryDeleteFile(tempOutputPath);
                progress?.Report(new ClipExportProgress(
                    "Dopasowywanie dokładnego zakresu przycinania...",
                    0,
                    IsIndeterminate: false));

                bool hwSucceeded = false;
                while (!hwSucceeded)
                {
                    string? hwEncoder = await ResolveWorkingHardwareVideoEncoderAsync(ffmpegPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(hwEncoder))
                    {
                        break;
                    }

                    try
                    {
                        ProcessStartInfo hwStartInfo = BuildFfmpegStartInfo(
                            ffmpegPath,
                            options.InputFilePath,
                            tempOutputPath,
                            normalizedStart,
                            duration,
                            systemVolume,
                            microphoneVolume,
                            options.ExportMode,
                            audioTrackCount,
                            reencodeVideo: true,
                            hardwareVideoEncoder: hwEncoder);

                        await RunFfmpegExportProcessAsync(
                            hwStartInfo,
                            expectedExportDuration,
                            "Dopasowywanie dokładnego zakresu przycinania (GPU)",
                            progress,
                            cancellationToken).ConfigureAwait(false);

                        hwSucceeded = File.Exists(tempOutputPath);
                        if (!hwSucceeded)
                        {
                            await MarkHardwareVideoEncoderFailedAsync(hwEncoder, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception hwEx)
                    {
                        EngineLogger.Warn(
                            "ClipEditorService",
                            $"Hardware FFmpeg re-encode ({hwEncoder}) failed ({hwEx.Message}); trying next encoder.");
                        await MarkHardwareVideoEncoderFailedAsync(hwEncoder, cancellationToken).ConfigureAwait(false);
                        TryDeleteFile(tempOutputPath);
                    }
                }

                if (!hwSucceeded)
                {
                    ProcessStartInfo accurateStartInfo = BuildFfmpegStartInfo(
                        ffmpegPath,
                        options.InputFilePath,
                        tempOutputPath,
                        normalizedStart,
                        duration,
                        systemVolume,
                        microphoneVolume,
                        options.ExportMode,
                        audioTrackCount,
                        reencodeVideo: true,
                        hardwareVideoEncoder: null);

                    await RunFfmpegExportProcessAsync(
                        accurateStartInfo,
                        expectedExportDuration,
                        "Dopasowywanie dokładnego zakresu przycinania",
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!File.Exists(tempOutputPath))
                {
                    throw new InvalidOperationException("FFmpeg nie utworzył pliku wyjściowego.");
                }
            }

            if (shouldPatchMultiTrackFlags)
            {
                Mp4ReplayMuxer.PatchAudioTrackSelectionFlags(tempOutputPath);
            }

            // Atomically commit the temporary export to a non-colliding final .mp4 path (no TOCTOU race).
            string finalOutputPath = CommitExportedFile(tempOutputPath, options.InputFilePath, sanitizedBaseName);

            progress?.Report(new ClipExportProgress(
                $"Wyeksportowano: {Path.GetFileName(finalOutputPath)}",
                100.0,
                IsIndeterminate: false));

            return new ClipExportResult(finalOutputPath, audioTrackCount, usedSingleTrackFallback);
        }
        catch
        {
            TryDeleteFile(tempOutputPath);
            throw;
        }
    }

    private static async Task RunFfmpegExportProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan expectedDuration,
        string progressLabel,
        IProgress<ClipExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var recentErrorLines = new Queue<string>(16);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Nie udało się uruchomić procesu ffmpeg.exe.");
        }

        using CancellationTokenRegistration cancelRegistration =
            RegisterProcessTermination(process, cancellationToken);

        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (recentErrorLines.Count >= 16)
                    {
                        recentErrorLines.Dequeue();
                    }

                    recentErrorLines.Enqueue(line.Trim());

                    if (TryParseFfmpegTime(line, out TimeSpan processedTime) && expectedDuration.TotalSeconds > 0.05)
                    {
                        double percent = Math.Clamp(
                            (processedTime.TotalSeconds / expectedDuration.TotalSeconds) * 100.0,
                            0.0,
                            99.0);
                        progress?.Report(new ClipExportProgress(
                            $"{progressLabel} ({(int)percent}%)...",
                            percent,
                            IsIndeterminate: false));
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EnsureProcessExitedAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail = recentErrorLines.Count > 0
                ? string.Join(" | ", recentErrorLines.TakeLast(3))
                : $"Kod wyjścia: {process.ExitCode}";
            throw new InvalidOperationException($"FFmpeg zakończył się błędem: {detail}");
        }
    }

    internal static ProcessStartInfo BuildFfmpegStartInfo(
        string ffmpegPath,
        string inputFilePath,
        string tempOutputPath,
        TimeSpan startTime,
        TimeSpan duration,
        double systemVolume,
        double microphoneVolume,
        ClipExportMode exportMode,
        int audioTrackCount,
        bool reencodeVideo,
        string? hardwareVideoEncoder)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        string startArg = FormatFfmpegTimestamp(startTime);
        string durationArg = FormatFfmpegTimestamp(duration);
        string sysVol = systemVolume.ToString("0.###", CultureInfo.InvariantCulture);
        string micVol = microphoneVolume.ToString("0.###", CultureInfo.InvariantCulture);

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-y");
        // Place -ss before -i for fast input seeking and -t duration after -i so exported length equals EndValue - StartValue.
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(startArg);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFilePath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(durationArg);

        if (audioTrackCount >= 2)
        {
            // ClipThat MP4 layout: 0:a:0 = System Audio, 0:a:1 = Microphone Audio (and 0:a:2 = Combined Mix when 3 tracks exist).
            if (exportMode == ClipExportMode.MixSingleTrackForDiscord)
            {
                startInfo.ArgumentList.Add("-filter_complex");
                startInfo.ArgumentList.Add(
                    $"[0:a:0]volume={sysVol}[a0];[0:a:1]volume={micVol}[a1];[a0][a1]amix=inputs=2:duration=longest:normalize=0[aout]");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("0:v:0");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("[aout]");
                AddVideoCodecArguments(startInfo, reencodeVideo, hardwareVideoEncoder);
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("aac");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add("192k");
            }
            else
            {
                // Preserve ClipThat's Windows Media Foundation-compatible 3-track layout:
                // 0:a:0 = Isolated System Audio, 0:a:1 = Isolated Microphone Audio,
                // 0:a:2 (LAST) = Combined Mix Audio (enabled default track after tkhd patching).
                startInfo.ArgumentList.Add("-filter_complex");
                startInfo.ArgumentList.Add(
                    $"[0:a:0]volume={sysVol},asplit=2[sys_mix][sys_out];[0:a:1]volume={micVol},asplit=2[mic_mix][mic_out];[sys_mix][mic_mix]amix=inputs=2:duration=longest:normalize=0[aout]");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("0:v:0");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("[sys_out]");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("[mic_out]");
                startInfo.ArgumentList.Add("-map");
                startInfo.ArgumentList.Add("[aout]");
                AddVideoCodecArguments(startInfo, reencodeVideo, hardwareVideoEncoder);
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("aac");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add("192k");
            }
        }
        else if (audioTrackCount == 1)
        {
            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add($"[0:a:0]volume={sysVol}[aout]");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("[aout]");
            AddVideoCodecArguments(startInfo, reencodeVideo, hardwareVideoEncoder);
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("192k");
        }
        else
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            AddVideoCodecArguments(startInfo, reencodeVideo, hardwareVideoEncoder);
            startInfo.ArgumentList.Add("-an");
        }

        startInfo.ArgumentList.Add("-avoid_negative_ts");
        startInfo.ArgumentList.Add("make_zero");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(tempOutputPath);

        return startInfo;
    }

    private static void AddVideoCodecArguments(
        ProcessStartInfo startInfo,
        bool reencodeVideo,
        string? hardwareVideoEncoder)
    {
        if (!reencodeVideo)
        {
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            return;
        }

        if (string.Equals(hardwareVideoEncoder, "h264_nvenc", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("h264_nvenc");
            startInfo.ArgumentList.Add("-preset");
            startInfo.ArgumentList.Add("p4");
            startInfo.ArgumentList.Add("-rc");
            startInfo.ArgumentList.Add("vbr");
            startInfo.ArgumentList.Add("-cq");
            startInfo.ArgumentList.Add("19");
            startInfo.ArgumentList.Add("-b:v");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("yuv420p");
            return;
        }

        if (string.Equals(hardwareVideoEncoder, "h264_amf", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("h264_amf");
            startInfo.ArgumentList.Add("-quality");
            startInfo.ArgumentList.Add("balanced");
            startInfo.ArgumentList.Add("-rc");
            startInfo.ArgumentList.Add("cqp");
            startInfo.ArgumentList.Add("-qp_i");
            startInfo.ArgumentList.Add("19");
            startInfo.ArgumentList.Add("-qp_p");
            startInfo.ArgumentList.Add("21");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("yuv420p");
            return;
        }

        if (string.Equals(hardwareVideoEncoder, "h264_qsv", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("h264_qsv");
            startInfo.ArgumentList.Add("-global_quality");
            startInfo.ArgumentList.Add("19");
            startInfo.ArgumentList.Add("-pix_fmt");
            startInfo.ArgumentList.Add("nv12");
            return;
        }

        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("veryfast");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("18");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
    }

    private async Task<string?> ResolveWorkingHardwareVideoEncoderAsync(
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        string? cached = _preferredHardwareVideoEncoder;
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await _hwEncoderProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_preferredHardwareVideoEncoder))
            {
                return _preferredHardwareVideoEncoder;
            }

            foreach (string encoderName in HardwareEncoderCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_failedHardwareEncoders.Contains(encoderName))
                {
                    continue;
                }

                if (await ProbeHardwareVideoEncoderAsync(ffmpegPath, encoderName, cancellationToken).ConfigureAwait(false))
                {
                    _preferredHardwareVideoEncoder = encoderName;
                    EngineLogger.Info("ClipEditorService", $"Detected working FFmpeg hardware video encoder: {encoderName}");
                    return encoderName;
                }

                _failedHardwareEncoders.Add(encoderName);
            }

            _preferredHardwareVideoEncoder = null;
            return null;
        }
        finally
        {
            _hwEncoderProbeLock.Release();
        }
    }

    private async Task MarkHardwareVideoEncoderFailedAsync(
        string encoderName,
        CancellationToken cancellationToken)
    {
        await _hwEncoderProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _failedHardwareEncoders.Add(encoderName);
            if (string.Equals(_preferredHardwareVideoEncoder, encoderName, StringComparison.Ordinal))
            {
                _preferredHardwareVideoEncoder = null;
            }
        }
        finally
        {
            _hwEncoderProbeLock.Release();
        }
    }

    private static async Task<bool> ProbeHardwareVideoEncoderAsync(
        string ffmpegPath,
        string encoderName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("color=c=black:s=256x256:d=0.04:r=25");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add(encoderName);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return false;
            }

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(4));

            using CancellationTokenRegistration cancelReg =
                RegisterProcessTermination(process, probeCts.Token);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await EnsureProcessExitedAfterKillAsync(process).ConfigureAwait(false);
                await ObserveTaskSafeAsync(stdoutTask).ConfigureAwait(false);
                await ObserveTaskSafeAsync(stderrTask).ConfigureAwait(false);
                throw;
            }

            _ = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(TimeSpan Duration, int AudioTrackCount)> ReadMediaMetadataAsync(
        string filePath,
        CancellationToken cancellationToken,
        string? preResolvedFfprobePath = null,
        bool hasPreResolvedFfprobe = false)
    {
        bool parsedBox = Mp4ReplayMuxer.TryReadMp4Metadata(
            filePath,
            out TimeSpan duration,
            out int audioTrackCount);

        if (parsedBox && duration > TimeSpan.Zero)
        {
            return (duration, audioTrackCount);
        }

        TimeSpan shellDuration = await TryReadDurationViaWindowsShellAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        if (shellDuration > TimeSpan.Zero)
        {
            duration = shellDuration;
        }

        if (!parsedBox || duration <= TimeSpan.Zero)
        {
            string? ffprobePath = hasPreResolvedFfprobe
                ? preResolvedFfprobePath
                : ResolveToolExecutable("ffprobe.exe");
            if (ffprobePath is not null)
            {
                (TimeSpan probeDuration, int probeAudioCount) = await TryProbeWithFfprobeAsync(
                    ffprobePath,
                    filePath,
                    cancellationToken).ConfigureAwait(false);

                if (duration <= TimeSpan.Zero && probeDuration > TimeSpan.Zero)
                {
                    duration = probeDuration;
                }

                if (!parsedBox && probeAudioCount > 0)
                {
                    audioTrackCount = probeAudioCount;
                }
            }
        }

        return (duration, audioTrackCount);
    }

    private static async Task<TimeSpan> TryReadDurationViaWindowsShellAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(filePath)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            VideoProperties videoProperties = await storageFile.Properties.GetVideoPropertiesAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            return videoProperties.Duration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static async Task<(TimeSpan Duration, int AudioTrackCount)> TryProbeWithFfprobeAsync(
        string ffprobePath,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=duration:stream=codec_type");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=0");
            startInfo.ArgumentList.Add(filePath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return (TimeSpan.Zero, 0);
            }

            using CancellationTokenRegistration cancelRegistration =
                RegisterProcessTermination(process, cancellationToken);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await EnsureProcessExitedAfterKillAsync(process).ConfigureAwait(false);
                await ObserveTaskSafeAsync(stdoutTask).ConfigureAwait(false);
                await ObserveTaskSafeAsync(stderrTask).ConfigureAwait(false);
                throw;
            }

            string output = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return (TimeSpan.Zero, 0);
            }

            TimeSpan duration = TimeSpan.Zero;
            int audioTracks = 0;

            foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (rawLine.StartsWith("duration=", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(
                        rawLine["duration=".Length..],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double seconds) &&
                    seconds > 0)
                {
                    duration = TimeSpan.FromSeconds(seconds);
                }
                else if (string.Equals(rawLine, "codec_type=audio", StringComparison.OrdinalIgnoreCase))
                {
                    audioTracks++;
                }
            }

            return (duration, audioTracks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (TimeSpan.Zero, 0);
        }
    }

    private static async Task VerifyDownloadedArchiveSha256Async(
        string actualHex,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage shaResponse = await SharedHttpClient.GetAsync(
            FfmpegEssentialsSha256Url,
            cancellationToken).ConfigureAwait(false);
        shaResponse.EnsureSuccessStatusCode();

        string shaText = await shaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!TryParseSha256Text(shaText, out string expectedHex))
        {
            throw new InvalidOperationException("Nie udało się odczytać poprawnej sumy kontrolnej SHA-256 archiwum FFmpeg.");
        }

        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Weryfikacja integralności SHA-256 pobranego archiwum FFmpeg nie powiodła się.");
        }
    }

    internal static bool TryParseSha256Text(string? content, out string normalizedHex)
    {
        normalizedHex = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string token = content.Trim().Split(Sha256Separators, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length != 64)
        {
            return false;
        }

        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        normalizedHex = token.ToUpperInvariant();
        return true;
    }

    private static string? ResolveToolExecutable(string executableName)
    {
        string? adjacentCandidate = PortableFfmpegProvider.ResolveAdjacentExecutable(executableName);
        if (adjacentCandidate is not null)
        {
            return adjacentCandidate;
        }

        string portableBinCandidate = Path.Combine(PortableFfmpegProvider.BinDirectory, executableName);
        if (IsNonEmptyFile(portableBinCandidate))
        {
            return portableBinCandidate;
        }

        string localAppDataToolsCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipThat",
            "Tools",
            executableName);
        if (IsNonEmptyFile(localAppDataToolsCandidate))
        {
            return localAppDataToolsCandidate;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string cleanedDir = rawDir.Trim('"');
                    if (string.IsNullOrWhiteSpace(cleanedDir))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(cleanedDir, executableName);
                    if (IsNonEmptyFile(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWritableToolsDirectory()
    {
        string appToolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (TryEnsureDirectoryWritable(appToolsDir))
        {
            return appToolsDir;
        }

        string localToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipThat",
            "Tools");
        Directory.CreateDirectory(localToolsDir);
        return localToolsDir;
    }

    private static bool TryEnsureDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probePath = Path.Combine(directory, $".write_probe_{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractArchiveEntryAtomic(ZipArchiveEntry entry, string destinationPath)
    {
        string tempPath = $"{destinationPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.extracting";
        TryDeleteFile(tempPath);
        try
        {
            entry.ExtractToFile(tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Strips a trailing <c>.mp4</c> extension (if entered), removes any invalid Windows filename characters,
    /// clamps length safely below NTFS limits, and falls back to <c>{originalName}_edited</c> when empty or reserved.
    /// </summary>
    public static string SanitizeOutputBaseName(string? customOutputFileName, string inputFilePath)
    {
        const int MaxBaseNameLength = 160;

        string originalName = Path.GetFileNameWithoutExtension(inputFilePath);
        string fallbackBaseName = string.IsNullOrWhiteSpace(originalName)
            ? "clip_edited"
            : $"{originalName}_edited";

        if (fallbackBaseName.Length > MaxBaseNameLength)
        {
            fallbackBaseName = fallbackBaseName[..MaxBaseNameLength].TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(fallbackBaseName))
            {
                fallbackBaseName = "clip_edited";
            }
        }

        if (string.IsNullOrWhiteSpace(customOutputFileName))
        {
            return fallbackBaseName;
        }

        string cleaned = StripMp4Extension(customOutputFileName);

        Span<char> buffer = cleaned.Length <= 256 ? stackalloc char[cleaned.Length] : new char[cleaned.Length];
        int written = 0;
        foreach (char ch in cleaned)
        {
            if (!InvalidFileNameChars.Contains(ch))
            {
                buffer[written++] = ch;
            }
        }

        cleaned = StripMp4Extension(new string(buffer[..written]));
        if (cleaned.Length > MaxBaseNameLength)
        {
            cleaned = StripMp4Extension(cleaned[..MaxBaseNameLength]);
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallbackBaseName;
        }

        int firstDotIndex = cleaned.IndexOf('.');
        string deviceProbe = firstDotIndex >= 0 ? cleaned[..firstDotIndex] : cleaned;
        if (ReservedWindowsDeviceNames.Contains(deviceProbe))
        {
            return fallbackBaseName;
        }

        return cleaned;
    }

    private static string ResolveOutputDirectory(string inputFilePath)
    {
        string? directory = Path.GetDirectoryName(inputFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        string fullPath = Path.GetFullPath(inputFilePath);
        string? fullDirectory = Path.GetDirectoryName(fullPath);
        return !string.IsNullOrWhiteSpace(fullDirectory)
            ? fullDirectory
            : AppSettings.DefaultOutputDirectory;
    }

    private static string StripMp4Extension(string value)
    {
        string result = value.Trim().TrimEnd('.', ' ');
        while (result.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^4].Trim().TrimEnd('.', ' ');
        }

        return result;
    }

    private static string CommitExportedFile(string tempOutputPath, string inputFilePath, string sanitizedBaseName)
    {
        string directory = ResolveOutputDirectory(inputFilePath);
        Directory.CreateDirectory(directory);

        for (int index = 1; index < 10_000; index++)
        {
            string candidate = index == 1
                ? Path.Combine(directory, $"{sanitizedBaseName}.mp4")
                : Path.Combine(directory, $"{sanitizedBaseName}_{index}.mp4");

            try
            {
                File.Move(tempOutputPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another concurrent export or user file claimed this name; advance atomically.
            }
        }

        string fallbackCandidate = Path.Combine(directory, $"{sanitizedBaseName}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
        File.Move(tempOutputPath, fallbackCandidate, overwrite: false);
        return fallbackCandidate;
    }

    private static string FormatFfmpegTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)value.TotalHours,
            value.Minutes,
            value.Seconds,
            value.Milliseconds);
    }

    internal static bool TryParseFfmpegTime(string stderrLine, out TimeSpan parsedTime)
    {
        Match match = FfmpegTimeRegex().Match(stderrLine);
        if (match.Success &&
            int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) &&
            int.TryParse(match.Groups["m"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) &&
            int.TryParse(match.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            string fracText = match.Groups["f"].Value;
            int milliseconds = 0;
            if (double.TryParse("0." + fracText, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction))
            {
                milliseconds = (int)Math.Round(fraction * 1000.0);
            }

            parsedTime = new TimeSpan(0, hours, minutes, seconds, milliseconds);
            return true;
        }

        parsedTime = TimeSpan.Zero;
        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipThat/1.0");
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore best-effort temporary file cleanup errors.
        }
    }

    private static CancellationTokenRegistration RegisterProcessTermination(
        Process process,
        CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have already exited.
            }
        });
    }

    private static async Task EnsureProcessExitedAfterKillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have already exited.
        }

        try
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout/exit wait errors during forced teardown.
        }
    }

    private static async Task ObserveTaskSafeAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Observe stream-read cancellation/IO exceptions so TaskScheduler.UnobservedTaskException is never raised.
        }
    }

    public void Dispose()
    {
        _hwEncoderProbeLock.Dispose();
    }

    private readonly record struct CachedClipMetadata(
        long LastWriteTicksUtc,
        long FileSizeBytes,
        TimeSpan Duration,
        int AudioTrackCount);
}
