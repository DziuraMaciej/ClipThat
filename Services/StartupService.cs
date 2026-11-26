using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClipThat.Services;

/// <summary>
/// Manages automatic launch with Windows via the current user's Run registry key,
/// supporting both standalone <c>ClipThat.exe</c> and <c>dotnet run</c> / <c>Start_ClipThat.bat</c> launches.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipThat";
    private const string LauncherScriptFileName = "Start_ClipThat.bat";
    private const string ProjectFileName = "ClipThat.csproj";

    /// <summary>
    /// Returns true if ClipThat is currently registered to start with Windows
    /// (whether registered as standalone <c>ClipThat.exe</c> or via <c>Start_ClipThat.bat</c> / <c>dotnet.exe</c>).
    /// </summary>
    public bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            return value is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Alias for <see cref="IsStartupEnabled"/> used by existing settings UI callers.
    /// </summary>
    public bool IsEnabled() => IsStartupEnabled();

    /// <summary>
    /// Re-applies the current startup command to the Windows Run registry key so moving
    /// <c>ClipThat.exe</c> or switching between standalone and <c>Start_ClipThat.bat</c> never breaks autostart.
    /// </summary>
    public void EnsureStartupPathUpToDate()
    {
        string desiredCommand = BuildStartupCommand();

        using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (readKey?.GetValue(ValueName) is string existingCommand &&
            readKey.GetValueKind(ValueName) == RegistryValueKind.String &&
            string.Equals(existingCommand, desiredCommand, StringComparison.Ordinal))
        {
            return;
        }

        using RegistryKey writeKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Nie można otworzyć klucza rejestru autostartu.");

        writeKey.SetValue(ValueName, desiredCommand, RegistryValueKind.String);
    }

    /// <summary>
    /// Adds or removes the ClipThat startup registry entry under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Nie można otworzyć klucza rejestru autostartu.");

        if (enabled)
        {
            key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildStartupCommand()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            using Process currentProcess = Process.GetCurrentProcess();
            executablePath = currentProcess.MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Nie udało się ustalić ścieżki pliku wykonywalnego ClipThat.");
        }

        if (IsDotnetHost(executablePath))
        {
            return BuildDotnetStartupCommand(executablePath);
        }

        return $"\"{executablePath}\" --minimized";
    }

    private static bool IsDotnetHost(string executablePath)
    {
        string fileName = Path.GetFileName(executablePath);
        return string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDotnetStartupCommand(string dotnetPath)
    {
        string? launcherBatPath = null;
        string? projectFilePath = null;

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            DirectoryInfo? current = new DirectoryInfo(baseDir);
            while (current is not null)
            {
                string candidateBat = Path.Combine(current.FullName, LauncherScriptFileName);
                if (File.Exists(candidateBat))
                {
                    launcherBatPath = candidateBat;
                    break;
                }

                if (projectFilePath is null)
                {
                    string candidateCsproj = Path.Combine(current.FullName, ProjectFileName);
                    if (File.Exists(candidateCsproj))
                    {
                        projectFilePath = candidateCsproj;
                    }
                }

                current = current.Parent;
            }
        }

        if (!string.IsNullOrWhiteSpace(launcherBatPath))
        {
            return $"cmd.exe /c start \"\" /min \"{launcherBatPath}\"";
        }

        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            string? projectDir = Path.GetDirectoryName(projectFilePath);
            if (!string.IsNullOrWhiteSpace(projectDir))
            {
                string expectedBatPath = Path.Combine(projectDir, LauncherScriptFileName);
                if (File.Exists(expectedBatPath))
                {
                    return $"cmd.exe /c start \"\" /min \"{expectedBatPath}\"";
                }
            }

            return $"cmd.exe /c start \"\" /min \"{dotnetPath}\" run --project \"{projectFilePath}\" -- --minimized";
        }

        string fallbackDll = Path.Combine(AppContext.BaseDirectory, "ClipThat.dll");
        if (File.Exists(fallbackDll))
        {
            return $"\"{dotnetPath}\" \"{fallbackDll}\" --minimized";
        }

        return $"\"{dotnetPath}\" --minimized";
    }
}
