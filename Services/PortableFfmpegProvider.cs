using System.IO;
using System.Reflection;
using ClipThat.Engine.Diagnostics;

namespace ClipThat.Services;

/// <summary>
/// Extracts the embedded static <c>ffmpeg.exe</c> resource once on demand to
/// <c>%LocalAppData%\ClipThat\bin\ffmpeg.exe</c> so replay saving and clip trimming
/// work out-of-the-box on any Windows PC without manual FFmpeg installation.
/// </summary>
public static class PortableFfmpegProvider
{
    public const string EmbeddedResourceLogicalName = "ClipThat.Assets.ffmpeg.exe";

    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);

    public static string BinDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipThat",
        "bin");

    public static string ExtractedFfmpegPath => Path.Combine(BinDirectory, "ffmpeg.exe");

    /// <summary>
    /// Returns <see langword="true"/> when an embedded <c>ffmpeg.exe</c> resource exists and is already
    /// validly extracted to <see cref="ExtractedFfmpegPath"/>.
    /// </summary>
    public static bool IsAlreadyExtracted()
    {
        Assembly assembly = typeof(PortableFfmpegProvider).Assembly;
        string? resourceName = ResolveEmbeddedResourceName(assembly);
        return resourceName is not null && IsExtractedFileValid(assembly, resourceName, ExtractedFfmpegPath);
    }

    /// <summary>
    /// Ensures the embedded <c>ffmpeg.exe</c> is extracted to <c>%LocalAppData%\ClipThat\bin\ffmpeg.exe</c>
    /// and returns its path, or <see langword="null"/> if no embedded resource is present in the assembly.
    /// </summary>
    public static string? TryEnsureExtracted()
    {
        string? adjacentPath = ResolveAdjacentFfmpegPath();
        if (adjacentPath is not null)
        {
            return adjacentPath;
        }

        Assembly assembly = typeof(PortableFfmpegProvider).Assembly;
        string? resourceName = ResolveEmbeddedResourceName(assembly);
        if (resourceName is null)
        {
            return null;
        }

        string targetPath = ExtractedFfmpegPath;
        if (IsExtractedFileValid(assembly, resourceName, targetPath))
        {
            return targetPath;
        }

        ExtractionGate.Wait();
        try
        {
            if (IsExtractedFileValid(assembly, resourceName, targetPath))
            {
                return targetPath;
            }

            ExtractAtomically(assembly, resourceName, targetPath);
            return targetPath;
        }
        finally
        {
            ExtractionGate.Release();
        }
    }

    private static string? ResolveAdjacentFfmpegPath()
    {
        string baseDirCandidate = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(baseDirCandidate))
        {
            return baseDirCandidate;
        }

        string toolsCandidate = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
        if (File.Exists(toolsCandidate))
        {
            return toolsCandidate;
        }

        return null;
    }

    /// <summary>
    /// Asynchronously ensures the embedded <c>ffmpeg.exe</c> is extracted once on demand to
    /// <c>%LocalAppData%\ClipThat\bin\ffmpeg.exe</c>.
    /// </summary>
    public static async Task<string?> TryEnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        Assembly assembly = typeof(PortableFfmpegProvider).Assembly;
        string? resourceName = ResolveEmbeddedResourceName(assembly);
        if (resourceName is null)
        {
            return null;
        }

        string targetPath = ExtractedFfmpegPath;
        if (IsExtractedFileValid(assembly, resourceName, targetPath))
        {
            return targetPath;
        }

        await ExtractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsExtractedFileValid(assembly, resourceName, targetPath))
            {
                return targetPath;
            }

            await ExtractAtomicallyAsync(assembly, resourceName, targetPath, cancellationToken)
                .ConfigureAwait(false);
            return targetPath;
        }
        finally
        {
            ExtractionGate.Release();
        }
    }

    private static string? ResolveEmbeddedResourceName(Assembly assembly)
    {
        if (assembly.GetManifestResourceInfo(EmbeddedResourceLogicalName) is not null)
        {
            return EmbeddedResourceLogicalName;
        }

        string[] names = assembly.GetManifestResourceNames();
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                return names[i];
            }
        }

        return null;
    }

    private static bool IsExtractedFileValid(Assembly assembly, string resourceName, string targetPath)
    {
        try
        {
            if (!File.Exists(targetPath))
            {
                return false;
            }

            using Stream? resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null || resourceStream.Length <= 0)
            {
                return false;
            }

            var fileInfo = new FileInfo(targetPath);
            return fileInfo.Length == resourceStream.Length;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractAtomically(Assembly assembly, string resourceName, string targetPath)
    {
        string directory = Path.GetDirectoryName(targetPath) ?? BinDirectory;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"ffmpeg.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using Stream resourceStream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Nie znaleziono osadzonego zasobu '{resourceName}'.");

            long expectedLength = resourceStream.Length;
            using (var fileStream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 131072,
                       useAsync: false))
            {
                resourceStream.CopyTo(fileStream);
                fileStream.Flush();
            }

            CommitExtractedFile(tempPath, targetPath, expectedLength);
            EngineLogger.Info("PortableFfmpegProvider", $"Extracted embedded ffmpeg.exe ({expectedLength:N0} B) to \"{targetPath}\".");
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static async Task ExtractAtomicallyAsync(
        Assembly assembly,
        string resourceName,
        string targetPath,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(targetPath) ?? BinDirectory;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"ffmpeg.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using Stream resourceStream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Nie znaleziono osadzonego zasobu '{resourceName}'.");

            long expectedLength = resourceStream.Length;
            await using (var fileStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 131072,
                             useAsync: true))
            {
                await resourceStream.CopyToAsync(fileStream, 131072, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            CommitExtractedFile(tempPath, targetPath, expectedLength);
            EngineLogger.Info("PortableFfmpegProvider", $"Extracted embedded ffmpeg.exe ({expectedLength:N0} B) to \"{targetPath}\".");
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void CommitExtractedFile(string tempPath, string targetPath, long expectedLength)
    {
        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(targetPath) && new FileInfo(targetPath).Length == expectedLength)
        {
            // Another instance/process extracted the exact same file concurrently or is actively executing it.
            TryDeleteFile(tempPath);
        }
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
            // Best-effort temp cleanup.
        }
    }
}
