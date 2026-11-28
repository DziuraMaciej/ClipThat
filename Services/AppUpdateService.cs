using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClipThat.Engine.Diagnostics;

namespace ClipThat.Services;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? AppRootDirectory,
    string? LocalSha,
    string? RemoteSha,
    string? CommitMessage,
    DateTimeOffset? CommitDateUtc,
    bool IsGitRepo,
    bool HasUncommittedGitChanges,
    string? ErrorMessage)
{
    public string? ShortRemoteSha => FormatShortSha(RemoteSha);
    public string? ShortLocalSha => FormatShortSha(LocalSha);

    private static string? FormatShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }

        string trimmed = sha.Trim();
        return trimmed.Length > 7 ? trimmed[..7] : trimmed;
    }
}

/// <summary>
/// Checks GitHub for newer commits on the main branch and launches a detached, self-cleaning
/// same-volume PowerShell updater script that supports both Git repositories and ZIP downloads
/// while preserving <c>Assets\ffmpeg.exe</c> and user settings.
/// </summary>
public sealed class AppUpdateService
{
    public const string GitHubLatestCommitApiUrl = "https://api.github.com/repos/DziuraMaciej/ClipThat/commits/main";
    public const string GitHubMainZipUrl = "https://github.com/DziuraMaciej/ClipThat/archive/refs/heads/main.zip";
    public const string VersionFileName = ".clipthat_version";

    private static readonly TimeSpan ZipTimestampTolerance = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> TrackedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".bat"
    };
    private static readonly HashSet<string> TrackedRootFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClipThat.csproj", "Start_ClipThat.bat", ".gitattributes", "README.md"
    };
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "ReleaseBuild", ".git", ".vs"
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly string? _overrideAppRoot;

    public AppUpdateService(string? overrideAppRoot = null)
    {
        _overrideAppRoot = overrideAppRoot;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClipThat", "1.0"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public string? ResolveAppRootDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_overrideAppRoot))
        {
            return Directory.Exists(_overrideAppRoot) ? Path.GetFullPath(_overrideAppRoot) : null;
        }

        return FindAppRootDirectory(AppContext.BaseDirectory);
    }

    internal static string? FindAppRootDirectory(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(startDirectory);
        }
        catch
        {
            return null;
        }

        for (int depth = 0; depth < 7 && current is not null; depth++)
        {
            string csprojPath = Path.Combine(current.FullName, "ClipThat.csproj");
            string startBatPath = Path.Combine(current.FullName, "Start_ClipThat.bat");
            if (File.Exists(csprojPath) || File.Exists(startBatPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        string? appRoot = ResolveAppRootDirectory();
        if (string.IsNullOrWhiteSpace(appRoot))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                AppRootDirectory: null,
                LocalSha: null,
                RemoteSha: null,
                CommitMessage: null,
                CommitDateUtc: null,
                IsGitRepo: false,
                HasUncommittedGitChanges: false,
                ErrorMessage: "Nie znaleziono folderu źródłowego aplikacji (ClipThat.csproj / Start_ClipThat.bat).");
        }

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestCommitApiUrl);
            using HttpResponseMessage response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    appRoot,
                    LocalSha: null,
                    RemoteSha: null,
                    CommitMessage: null,
                    CommitDateUtc: null,
                    IsGitRepo: false,
                    HasUncommittedGitChanges: false,
                    ErrorMessage: "Przekroczono chwilowy limit zapytań GitHub API. Spróbuj ponownie za kilka minut.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    appRoot,
                    LocalSha: null,
                    RemoteSha: null,
                    CommitMessage: null,
                    CommitDateUtc: null,
                    IsGitRepo: false,
                    HasUncommittedGitChanges: false,
                    ErrorMessage: $"Serwer GitHub zwrócił błąd HTTP {(int)response.StatusCode}.");
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                appRoot,
                LocalSha: null,
                RemoteSha: null,
                CommitMessage: null,
                CommitDateUtc: null,
                IsGitRepo: false,
                HasUncommittedGitChanges: false,
                ErrorMessage: "Przekroczono limit czasu połączenia z GitHub. Sprawdź połączenie z internetem.");
        }
        catch (HttpRequestException ex)
        {
            EngineLogger.Warn("AppUpdateService", $"Update check HTTP error: {ex.Message}", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                appRoot,
                LocalSha: null,
                RemoteSha: null,
                CommitMessage: null,
                CommitDateUtc: null,
                IsGitRepo: false,
                HasUncommittedGitChanges: false,
                ErrorMessage: "Nie udało się połączyć z GitHub. Sprawdź połączenie z internetem i spróbuj ponownie.");
        }
        catch (Exception ex)
        {
            EngineLogger.Warn("AppUpdateService", $"Update check failed: {ex.Message}", ex);
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                appRoot,
                LocalSha: null,
                RemoteSha: null,
                CommitMessage: null,
                CommitDateUtc: null,
                IsGitRepo: false,
                HasUncommittedGitChanges: false,
                ErrorMessage: $"Błąd podczas sprawdzania aktualizacji: {ex.Message}");
        }

        if (!TryParseGitHubCommitJson(json, out string remoteSha, out string commitMessage, out DateTimeOffset? commitDateUtc))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                appRoot,
                LocalSha: null,
                RemoteSha: null,
                CommitMessage: null,
                CommitDateUtc: null,
                IsGitRepo: false,
                HasUncommittedGitChanges: false,
                ErrorMessage: "Otrzymano nieprawidłową odpowiedź z GitHub API.");
        }

        return await Task.Run(
            () => InspectLocalVersionAndCompare(appRoot, remoteSha, commitMessage, commitDateUtc),
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryParseGitHubCommitJson(
        string json,
        out string sha,
        out string commitHeadline,
        out DateTimeOffset? commitDateUtc)
    {
        sha = string.Empty;
        commitHeadline = string.Empty;
        commitDateUtc = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("sha", out JsonElement shaElement) ||
                shaElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string rawSha = (shaElement.GetString() ?? string.Empty).Trim();
            if (!IsValidCommitSha(rawSha))
            {
                return false;
            }

            sha = rawSha.ToLowerInvariant();

            if (root.TryGetProperty("commit", out JsonElement commitElement) &&
                commitElement.ValueKind == JsonValueKind.Object)
            {
                if (commitElement.TryGetProperty("message", out JsonElement msgElement) &&
                    msgElement.ValueKind == JsonValueKind.String)
                {
                    string fullMessage = (msgElement.GetString() ?? string.Empty).Trim();
                    int newlineIndex = fullMessage.IndexOfAny(['\r', '\n']);
                    commitHeadline = newlineIndex >= 0
                        ? fullMessage[..newlineIndex].Trim()
                        : fullMessage;
                }

                commitDateUtc = TryReadCommitDate(commitElement, "committer")
                    ?? TryReadCommitDate(commitElement, "author");
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset? TryReadCommitDate(JsonElement commitElement, string personProperty)
    {
        if (!commitElement.TryGetProperty(personProperty, out JsonElement person) ||
            person.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!person.TryGetProperty("date", out JsonElement dateElement) ||
            dateElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? dateStr = dateElement.GetString();
        if (string.IsNullOrWhiteSpace(dateStr))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            dateStr,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    internal static bool IsValidCommitSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return false;
        }

        string trimmed = sha.Trim();
        if (trimmed.Length != 40)
        {
            return false;
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            bool isHex = (ch >= '0' && ch <= '9') ||
                         (ch >= 'a' && ch <= 'f') ||
                         (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static UpdateCheckResult InspectLocalVersionAndCompare(
        string appRoot,
        string remoteSha,
        string commitMessage,
        DateTimeOffset? commitDateUtc)
    {
        bool hasGitFolder = Directory.Exists(Path.Combine(appRoot, ".git"));
        if (hasGitFolder && TryRunGitCommand(appRoot, "rev-parse HEAD", out string headOutput) &&
            IsValidCommitSha(headOutput))
        {
            string localSha = headOutput.Trim().ToLowerInvariant();
            bool hasUncommitted = TryRunGitCommand(appRoot, "status --porcelain", out string statusOutput) &&
                                  !string.IsNullOrWhiteSpace(statusOutput);

            bool isUpToDate = string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase);
            if (!isUpToDate)
            {
                // If the local branch is ahead of GitHub (remote commit is already an ancestor of HEAD),
                // do not nag the author to pull an older remote commit.
                isUpToDate = TryRunGitCommand(appRoot, $"merge-base --is-ancestor {remoteSha} HEAD", out _);
            }

            return new UpdateCheckResult(
                isUpToDate ? UpdateCheckStatus.UpToDate : UpdateCheckStatus.UpdateAvailable,
                appRoot,
                localSha,
                remoteSha,
                commitMessage,
                commitDateUtc,
                IsGitRepo: true,
                HasUncommittedGitChanges: hasUncommitted,
                ErrorMessage: null);
        }

        // ZIP installation path: check stored .clipthat_version SHA first, then fallback to source mtime.
        string? savedVersionSha = ReadSavedVersionSha(appRoot);
        DateTimeOffset? latestSourceUtc = savedVersionSha is null
            ? GetLatestTrackedSourceWriteTimeUtc(appRoot)
            : null;

        UpdateCheckStatus status = EvaluateZipInstallStatus(
            savedVersionSha,
            latestSourceUtc,
            remoteSha,
            commitDateUtc);

        return new UpdateCheckResult(
            status,
            appRoot,
            LocalSha: savedVersionSha,
            RemoteSha: remoteSha,
            CommitMessage: commitMessage,
            CommitDateUtc: commitDateUtc,
            IsGitRepo: false,
            HasUncommittedGitChanges: false,
            ErrorMessage: null);
    }

    internal static UpdateCheckStatus EvaluateZipInstallStatus(
        string? savedVersionSha,
        DateTimeOffset? latestLocalSourceUtc,
        string remoteSha,
        DateTimeOffset? remoteCommitDateUtc)
    {
        if (IsValidCommitSha(savedVersionSha))
        {
            return string.Equals(savedVersionSha!.Trim(), remoteSha.Trim(), StringComparison.OrdinalIgnoreCase)
                ? UpdateCheckStatus.UpToDate
                : UpdateCheckStatus.UpdateAvailable;
        }

        if (remoteCommitDateUtc.HasValue && latestLocalSourceUtc.HasValue)
        {
            return remoteCommitDateUtc.Value <= latestLocalSourceUtc.Value + ZipTimestampTolerance
                ? UpdateCheckStatus.UpToDate
                : UpdateCheckStatus.UpdateAvailable;
        }

        return UpdateCheckStatus.UpdateAvailable;
    }

    internal static string? ReadSavedVersionSha(string appRoot)
    {
        string versionFilePath = Path.Combine(appRoot, VersionFileName);
        if (!File.Exists(versionFilePath))
        {
            return null;
        }

        try
        {
            string raw = File.ReadAllText(versionFilePath).Trim();
            return IsValidCommitSha(raw) ? raw.ToLowerInvariant() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static DateTimeOffset? GetLatestTrackedSourceWriteTimeUtc(string appRoot)
    {
        if (!Directory.Exists(appRoot))
        {
            return null;
        }

        DateTime maxUtc = DateTime.MinValue;
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(appRoot);

        while (pendingDirs.Count > 0)
        {
            string currentDir = pendingDirs.Pop();
            bool isRoot = string.Equals(currentDir, appRoot, StringComparison.OrdinalIgnoreCase);

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (ExcludedDirectoryNames.Contains(dirName) ||
                        dirName.StartsWith(".clipthat_update_backup_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pendingDirs.Push(subDir);
                }

                foreach (string filePath in Directory.EnumerateFiles(currentDir))
                {
                    string fileName = Path.GetFileName(filePath);
                    string ext = Path.GetExtension(filePath);
                    bool tracked = TrackedSourceExtensions.Contains(ext) ||
                                   (isRoot && TrackedRootFileNames.Contains(fileName));
                    if (!tracked)
                    {
                        continue;
                    }

                    DateTime writeUtc = File.GetLastWriteTimeUtc(filePath);
                    if (writeUtc > maxUtc)
                    {
                        maxUtc = writeUtc;
                    }
                }
            }
            catch
            {
                // Ignore inaccessible subdirectories during timestamp scan.
            }
        }

        return maxUtc > DateTime.MinValue ? new DateTimeOffset(maxUtc, TimeSpan.Zero) : null;
    }

    private static bool TryRunGitCommand(string workingDirectory, string arguments, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            if (!process.Start())
            {
                return false;
            }

            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }

            stdout = outTask.GetAwaiter().GetResult().Trim();
            _ = errTask.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a self-cleaning PowerShell updater script into <c>%TEMP%</c> and launches it in a visible
    /// progress console window. The caller should initiate graceful application shutdown immediately after.
    /// </summary>
    public static void LaunchDetachedUpdate(UpdateCheckResult checkResult, int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(checkResult);

        if (string.IsNullOrWhiteSpace(checkResult.AppRootDirectory) || !Directory.Exists(checkResult.AppRootDirectory))
        {
            throw new InvalidOperationException("Nie znaleziono docelowego folderu instalacji ClipThat.");
        }

        if (string.IsNullOrWhiteSpace(checkResult.RemoteSha))
        {
            throw new InvalidOperationException("Brak identyfikatora docelowej wersji aktualizacji.");
        }

        string scriptId = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"ClipThat-Updater-{scriptId}.ps1");
        string scriptContent = BuildUpdaterPowerShellScript(
            checkResult.AppRootDirectory,
            checkResult.RemoteSha,
            checkResult.IsGitRepo,
            currentProcessId,
            scriptId);

        // Windows PowerShell 5.1 requires UTF-8 with BOM to parse Polish diacritics cleanly.
        File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
            throw new InvalidOperationException("Nie udało się uruchomić procesu aktualizacji PowerShell.");
        }
    }

    internal static string BuildUpdaterPowerShellScript(
        string appRootDirectory,
        string remoteSha,
        bool isGitRepo,
        int currentProcessId,
        string updateId)
    {
        string escapedAppRoot = EscapePowerShellSingleQuoted(appRootDirectory);
        string escapedRemoteSha = EscapePowerShellSingleQuoted(remoteSha);
        string escapedZipUrl = EscapePowerShellSingleQuoted(GitHubMainZipUrl);
        string escapedVersionFile = EscapePowerShellSingleQuoted(VersionFileName);
        string escapedUpdateId = EscapePowerShellSingleQuoted(updateId);
        string isGitLiteral = isGitRepo ? "$true" : "$false";

        return $$"""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { $Host.UI.RawUI.WindowTitle = 'ClipThat - Aktualizacja' } catch {}

$appDir = '{{escapedAppRoot}}'
$remoteSha = '{{escapedRemoteSha}}'
$zipUrl = '{{escapedZipUrl}}'
$versionFile = '{{escapedVersionFile}}'
$updateId = '{{escapedUpdateId}}'
$isGitRepo = {{isGitLiteral}}
$targetPid = {{currentProcessId}}
$selfScript = $PSCommandPath

$zipPath = Join-Path $env:TEMP ('ClipThat-Update-' + $updateId + '.zip')
$extractDir = Join-Path $env:TEMP ('ClipThat-Update-Extract-' + $updateId)
$backupDir = Join-Path $appDir ('.clipthat_update_backup_' + $updateId)

function Wait-ClipThatExit {
    Write-Host 'Zamykanie działającej instancji ClipThat...' -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $parentAlive = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        $anyAlive = Get-Process -Name 'ClipThat' -ErrorAction SilentlyContinue
        if (-not $parentAlive -and -not $anyAlive) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    $remaining = Get-Process -Name 'ClipThat' -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Host 'Wymuszanie zamknięcia procesu ClipThat...' -ForegroundColor Yellow
        $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
    }
}

function Ensure-BatchCrlf([string]$path) {
    if (Test-Path -LiteralPath $path) {
        $text = [System.IO.File]::ReadAllText($path)
        $normalized = [System.Text.RegularExpressions.Regex]::Replace($text, "`r?`n", "`r`n")
        [System.IO.File]::WriteAllText($path, $normalized, (New-Object System.Text.UTF8Encoding($false)))
    }
}

try {
    Wait-ClipThatExit

    if ($isGitRepo) {
        Write-Host 'Pobieranie najnowszych zmian z GitHub (git pull)...' -ForegroundColor Cyan
        & git -C $appDir pull --ff-only
        if ($LASTEXITCODE -ne 0) {
            throw 'Polecenie git pull zakończyło się błędem. Sprawdź stan repozytorium.'
        }
    }
    else {
        Write-Host 'Pobieranie najnowszej wersji ClipThat z GitHub...' -ForegroundColor Cyan
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

        Write-Host 'Rozpakowywanie archiwum aktualizacji...' -ForegroundColor Cyan
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

        $extractedRoot = Get-ChildItem -LiteralPath $extractDir -Directory | Select-Object -First 1
        if (-not $extractedRoot -or -not (Test-Path -LiteralPath (Join-Path $extractedRoot.FullName 'ClipThat.csproj'))) {
            throw 'Pobrane archiwum nie zawiera poprawnego projektu ClipThat.'
        }

        $sourceDir = $extractedRoot.FullName
        $dirsToReplace = @('Engine', 'Models', 'Services', 'UI', 'ClipThat.Tests')
        $movedDirs = New-Object System.Collections.Generic.List[string]
        $backedUpRootFiles = New-Object System.Collections.Generic.List[string]
        $backedUpAssets = New-Object System.Collections.Generic.List[string]

        Write-Host 'Tworzenie bezpiecznej kopii zapasowej (ten sam dysk)...' -ForegroundColor Cyan
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $backupRootFilesDir = Join-Path $backupDir '_root_files'
        $backupAssetsDir = Join-Path $backupDir '_assets'
        New-Item -ItemType Directory -Path $backupRootFilesDir -Force | Out-Null
        New-Item -ItemType Directory -Path $backupAssetsDir -Force | Out-Null

        try {
            foreach ($dir in $dirsToReplace) {
                $destDir = Join-Path $appDir $dir
                if (Test-Path -LiteralPath $destDir) {
                    Move-Item -LiteralPath $destDir -Destination (Join-Path $backupDir $dir) -Force
                    $movedDirs.Add($dir)
                }
            }

            foreach ($existingFile in (Get-ChildItem -LiteralPath $appDir -File -Force)) {
                if ($existingFile.Name -eq $versionFile) {
                    continue
                }
                Copy-Item -LiteralPath $existingFile.FullName -Destination (Join-Path $backupRootFilesDir $existingFile.Name) -Force
                $backedUpRootFiles.Add($existingFile.Name)
            }

            $existingAssetsDir = Join-Path $appDir 'Assets'
            if (Test-Path -LiteralPath $existingAssetsDir) {
                foreach ($assetFile in (Get-ChildItem -LiteralPath $existingAssetsDir -File -Force)) {
                    if ($assetFile.Name -ieq 'ffmpeg.exe') {
                        continue
                    }
                    Copy-Item -LiteralPath $assetFile.FullName -Destination (Join-Path $backupAssetsDir $assetFile.Name) -Force
                    $backedUpAssets.Add($assetFile.Name)
                }
            }

            Write-Host 'Instalowanie zaktualizowanych plików (z zachowaniem Assets\ffmpeg.exe)...' -ForegroundColor Cyan

            $obsoleteUpdater = Join-Path $appDir 'Aktualizuj_ClipThat.bat'
            if (Test-Path -LiteralPath $obsoleteUpdater) {
                Remove-Item -LiteralPath $obsoleteUpdater -Force -ErrorAction SilentlyContinue
            }

            foreach ($dir in $dirsToReplace) {
                $srcSub = Join-Path $sourceDir $dir
                if (Test-Path -LiteralPath $srcSub) {
                    Copy-Item -LiteralPath $srcSub -Destination $appDir -Recurse -Force
                }
            }

            foreach ($srcRootFile in (Get-ChildItem -LiteralPath $sourceDir -File -Force)) {
                Copy-Item -LiteralPath $srcRootFile.FullName -Destination (Join-Path $appDir $srcRootFile.Name) -Force
            }

            $srcAssetsDir = Join-Path $sourceDir 'Assets'
            if (Test-Path -LiteralPath $srcAssetsDir) {
                $destAssetsDir = Join-Path $appDir 'Assets'
                New-Item -ItemType Directory -Path $destAssetsDir -Force | Out-Null
                foreach ($srcAsset in (Get-ChildItem -LiteralPath $srcAssetsDir -File -Force)) {
                    if ($srcAsset.Name -ieq 'ffmpeg.exe') {
                        continue
                    }
                    Copy-Item -LiteralPath $srcAsset.FullName -Destination (Join-Path $destAssetsDir $srcAsset.Name) -Force
                }
            }

            Ensure-BatchCrlf (Join-Path $appDir 'Start_ClipThat.bat')

            [System.IO.File]::WriteAllText((Join-Path $appDir $versionFile), $remoteSha, (New-Object System.Text.UTF8Encoding($false)))

            Get-ChildItem -LiteralPath $appDir -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(bin|obj|\.clipthat_update_backup_)' } |
                Unblock-File -ErrorAction SilentlyContinue

            Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        catch {
            Write-Host 'Wystąpił błąd podczas kopiowania - przywracanie poprzedniej wersji...' -ForegroundColor Yellow
            foreach ($dir in $movedDirs) {
                $destDir = Join-Path $appDir $dir
                $bakSub = Join-Path $backupDir $dir
                if (Test-Path -LiteralPath $destDir) {
                    Remove-Item -LiteralPath $destDir -Recurse -Force -ErrorAction SilentlyContinue
                }
                if (Test-Path -LiteralPath $bakSub) {
                    Move-Item -LiteralPath $bakSub -Destination $destDir -Force -ErrorAction SilentlyContinue
                }
            }
            foreach ($fileName in $backedUpRootFiles) {
                $bakFile = Join-Path $backupRootFilesDir $fileName
                if (Test-Path -LiteralPath $bakFile) {
                    Copy-Item -LiteralPath $bakFile -Destination (Join-Path $appDir $fileName) -Force -ErrorAction SilentlyContinue
                }
            }
            foreach ($assetName in $backedUpAssets) {
                $bakAsset = Join-Path $backupAssetsDir $assetName
                if (Test-Path -LiteralPath $bakAsset) {
                    Copy-Item -LiteralPath $bakAsset -Destination (Join-Path (Join-Path $appDir 'Assets') $assetName) -Force -ErrorAction SilentlyContinue
                }
            }
            Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
            throw
        }
    }

    Write-Host ''
    Write-Host 'Aktualizacja pobrana. Budowanie i uruchamianie nowej wersji ClipThat...' -ForegroundColor Green
    $startBat = Join-Path $appDir 'Start_ClipThat.bat'
    if (-not (Test-Path -LiteralPath $startBat)) {
        throw 'Nie znaleziono pliku Start_ClipThat.bat po aktualizacji.'
    }

    & $startBat --rebuild
    if ($LASTEXITCODE -ne 0) {
        throw "Budowanie aplikacji zakończyło się kodem błędu $LASTEXITCODE."
    }
}
catch {
    Write-Host ''
    Write-Host ('Błąd aktualizacji ClipThat: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    Read-Host 'Naciśnij Enter, aby zamknąć to okno'
    exit 1
}
finally {
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($selfScript -and (Test-Path -LiteralPath $selfScript)) {
        Remove-Item -LiteralPath $selfScript -Force -ErrorAction SilentlyContinue
    }
}
""";
    }

    private static string EscapePowerShellSingleQuoted(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
