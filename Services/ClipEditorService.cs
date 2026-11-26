using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
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

    public TimeSpan StartTime { get; init; }

    public TimeSpan EndTime { get; init; }

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
public sealed partial class ClipEditorService
{
    private const string FfmpegEssentialsDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim FfmpegDownloadGate = new(1, 1);

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

            IEnumerable<FileInfo> files;
            try
            {
                files = directoryInfo.EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (IReadOnlyList<ClipItem>)Array.Empty<ClipItem>();
            }

            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!file.Exists)
                    {
                        continue;
                    }

                    long fileSizeBytes = file.Length;
                    DateTime recordedAt = ClipItem.ParseRecordedAt(file.FullName);

                    (TimeSpan duration, int audioTrackCount) = await ReadMediaMetadataAsync(
                        file.FullName,
                        cancellationToken).ConfigureAwait(false);

                    results.Add(new ClipItem(
                        file.FullName,
                        file.Name,
                        recordedAt,
                        duration,
                        fileSizeBytes,
                        audioTrackCount));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip files that are momentarily locked or removed during enumeration.
                    Debug.WriteLine($"Pominięto plik podczas skanowania nagrań '{file.FullName}': {ex.Message}");
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
    /// Resolves an installed or cached <c>ffmpeg.exe</c> from the application directory, <c>Tools\ffmpeg.exe</c>,
    /// user cache, or the system <c>PATH</c>. Returns <see langword="null"/> if not found.
    /// </summary>
    public string? ResolveFfmpegPath() => ResolveToolExecutable("ffmpeg.exe");

    /// <summary>
    /// Ensures <c>ffmpeg.exe</c> is available locally, downloading the lightweight Windows x64 essentials build
    /// once on demand if it is not installed on the machine.
    /// </summary>
    public async Task<string> EnsureFfmpegAvailableAsync(
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

                    while (true)
                    {
                        int bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (bytesRead <= 0)
                        {
                            break;
                        }

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
                        else
                        {
                            double downloadedMb = totalRead / (1024.0 * 1024.0);
                            progress?.Report(new ClipExportProgress(
                                string.Format(CultureInfo.GetCultureInfo("pl-PL"), "Pobieranie FFmpeg ({0:0.0} MB)...", downloadedMb),
                                null,
                                IsIndeterminate: true));
                        }
                    }
                }

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

            using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(() =>
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

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
    /// Exports a trimmed and volume-adjusted copy of the input MP4 using <c>-c:v copy</c> without video re-encoding,
    /// saving the result as <c>{originalNameWithoutExt}_edit.mp4</c> in the same folder.
    /// </summary>
    public async Task<ClipExportResult> ExportClipAsync(
        ClipExportOptions options,
        IProgress<ClipExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.InputFilePath) || !File.Exists(options.InputFilePath))
        {
            throw new FileNotFoundException("Wybrany plik nagrania nie istnieje.", options.InputFilePath);
        }

        TimeSpan startTime = options.StartTime < TimeSpan.Zero ? TimeSpan.Zero : options.StartTime;
        TimeSpan endTime = options.EndTime;
        if (endTime <= startTime)
        {
            throw new ArgumentException("Czas zakończenia musi być większy niż czas rozpoczęcia.");
        }

        double systemVolume = Math.Clamp(options.SystemVolume, 0.0, 2.0);
        double microphoneVolume = Math.Clamp(options.MicrophoneVolume, 0.0, 2.0);

        string ffmpegPath = await EnsureFfmpegAvailableAsync(progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ClipExportProgress(
            "Analizowanie ścieżek audio nagrania...",
            0,
            IsIndeterminate: true));

        (TimeSpan _, int audioTrackCount) = await ReadMediaMetadataAsync(
            options.InputFilePath,
            cancellationToken).ConfigureAwait(false);

        string outputPath = BuildEditOutputPath(options.InputFilePath);
        string tempOutputPath = outputPath + ".tmp";
        TryDeleteFile(tempOutputPath);

        bool usedSingleTrackFallback = audioTrackCount < 2;
        bool shouldPatchMultiTrackFlags =
            options.ExportMode == ClipExportMode.KeepSeparateAudioTracks && audioTrackCount >= 2;

        ProcessStartInfo startInfo = BuildFfmpegStartInfo(
            ffmpegPath,
            options.InputFilePath,
            tempOutputPath,
            startTime,
            endTime,
            systemVolume,
            microphoneVolume,
            options.ExportMode,
            audioTrackCount);

        TimeSpan expectedDuration = endTime - startTime;
        progress?.Report(new ClipExportProgress(
            "Eksportowanie przyciętego klipu...",
            0,
            IsIndeterminate: false));

        var recentErrorLines = new Queue<string>(16);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Nie udało się uruchomić procesu ffmpeg.exe.");
            }

            using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(() =>
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
                            $"Eksportowanie przyciętego klipu ({(int)percent}%)...",
                            percent,
                            IsIndeterminate: false));
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string detail = recentErrorLines.Count > 0
                    ? recentErrorLines.Last()
                    : $"Kod wyjścia: {process.ExitCode}";
                throw new InvalidOperationException($"FFmpeg zakończył się błędem: {detail}");
            }

            if (!File.Exists(tempOutputPath))
            {
                throw new InvalidOperationException("FFmpeg nie utworzył pliku wyjściowego.");
            }

            if (shouldPatchMultiTrackFlags)
            {
                Mp4ReplayMuxer.PatchAudioTrackSelectionFlags(tempOutputPath);
            }

            // Re-resolve a non-colliding final output path right before moving in case a file appeared during export.
            string finalOutputPath = BuildEditOutputPath(options.InputFilePath);
            File.Move(tempOutputPath, finalOutputPath, overwrite: false);

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

    private static ProcessStartInfo BuildFfmpegStartInfo(
        string ffmpegPath,
        string inputFilePath,
        string tempOutputPath,
        TimeSpan startTime,
        TimeSpan endTime,
        double systemVolume,
        double microphoneVolume,
        ClipExportMode exportMode,
        int audioTrackCount)
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
        string endArg = FormatFfmpegTimestamp(endTime);
        string sysVol = systemVolume.ToString("0.###", CultureInfo.InvariantCulture);
        string micVol = microphoneVolume.ToString("0.###", CultureInfo.InvariantCulture);

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(startArg);
        startInfo.ArgumentList.Add("-to");
        startInfo.ArgumentList.Add(endArg);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFilePath);

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
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("copy");
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
                startInfo.ArgumentList.Add("-c:v");
                startInfo.ArgumentList.Add("copy");
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
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("192k");
        }
        else
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
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

    private async Task<(TimeSpan Duration, int AudioTrackCount)> ReadMediaMetadataAsync(
        string filePath,
        CancellationToken cancellationToken)
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
            string? ffprobePath = ResolveToolExecutable("ffprobe.exe");
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

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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

    private static string? ResolveToolExecutable(string executableName)
    {
        string baseDirCandidate = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(baseDirCandidate))
        {
            return baseDirCandidate;
        }

        string appToolsCandidate = Path.Combine(AppContext.BaseDirectory, "Tools", executableName);
        if (File.Exists(appToolsCandidate))
        {
            return appToolsCandidate;
        }

        string localAppDataToolsCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipThat",
            "Tools",
            executableName);
        if (File.Exists(localAppDataToolsCandidate))
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
                    if (File.Exists(candidate))
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
        string tempPath = destinationPath + ".extracting";
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

    private static string BuildEditOutputPath(string inputFilePath)
    {
        string directory = Path.GetDirectoryName(inputFilePath) ?? AppSettings.DefaultOutputDirectory;
        string baseName = Path.GetFileNameWithoutExtension(inputFilePath);

        string candidate = Path.Combine(directory, $"{baseName}_edit.mp4");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 10_000; index++)
        {
            string numberedCandidate = Path.Combine(directory, $"{baseName}_edit_{index}.mp4");
            if (!File.Exists(numberedCandidate))
            {
                return numberedCandidate;
            }
        }

        return Path.Combine(directory, $"{baseName}_edit_{DateTime.Now:HHmmss}.mp4");
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

    private static bool TryParseFfmpegTime(string stderrLine, out TimeSpan parsedTime)
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
}
