using System.Globalization;
using System.IO;

namespace ClipThat.Models;

/// <summary>
/// Represents a recorded MP4 replay clip discovered in the configured output directory.
/// </summary>
public sealed class ClipItem
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

    public ClipItem(
        string filePath,
        string fileName,
        DateTime recordedAt,
        TimeSpan duration,
        long fileSizeBytes,
        int audioTrackCount = 0)
    {
        FilePath = filePath;
        FileName = fileName;
        RecordedAt = recordedAt;
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        FileSizeBytes = Math.Max(0L, fileSizeBytes);
        AudioTrackCount = Math.Max(0, audioTrackCount);
    }

    public string FilePath { get; }

    public string FileName { get; }

    public DateTime RecordedAt { get; }

    public TimeSpan Duration { get; }

    public long FileSizeBytes { get; }

    public int AudioTrackCount { get; }

    public bool HasSeparateAudioTracks => AudioTrackCount >= 2;

    public string FormattedDate => RecordedAt.ToString("dd.MM.yyyy HH:mm:ss", PolishCulture);

    public string FormattedDuration => FormatDuration(Duration);

    public string FormattedSize => FormatFileSize(FileSizeBytes);

    public string AudioTrackBadge => AudioTrackCount switch
    {
        >= 3 => "3 ścieżki audio",
        2 => "2 ścieżki audio",
        1 => "1 ścieżka audio",
        _ => "Brak audio"
    };

    /// <summary>
    /// Parses the recording timestamp from ClipThat's <c>ssmmHHddMMyyyy.mp4</c> filename convention,
    /// falling back to the file creation timestamp when the filename does not match.
    /// </summary>
    public static DateTime ParseRecordedAt(string filePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        if (DateTime.TryParseExact(
                baseName,
                "ssmmHHddMMyyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsedTimestamp))
        {
            return parsedTimestamp;
        }

        try
        {
            return File.GetCreationTime(filePath);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// Formats a duration as <c>mm:ss</c> (or <c>hh:mm:ss</c> when 1 hour or longer).
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1.0)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
    }

    /// <summary>
    /// Formats a byte count using Polish decimal formatting (KB, MB, or GB).
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024L * 1024L)
        {
            double kilobytes = Math.Max(0L, bytes) / 1024.0;
            return string.Format(PolishCulture, "{0:0} KB", kilobytes);
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            double megabytes = bytes / (1024.0 * 1024.0);
            return string.Format(PolishCulture, "{0:0.0} MB", megabytes);
        }

        double gigabytes = bytes / (1024.0 * 1024.0 * 1024.0);
        return string.Format(PolishCulture, "{0:0.00} GB", gigabytes);
    }
}
