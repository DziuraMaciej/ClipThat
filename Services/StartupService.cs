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
    private const string StartupApprovedRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ClipThat";
    private const string LauncherScriptFileName = "Start_ClipThat.bat";
    private const string ProjectFileName = "ClipThat.csproj";

    /// <summary>
    /// Returns true if ClipThat is currently registered to start with Windows and has not been
    /// explicitly disabled in Windows Task Manager's Startup Apps list.
    /// </summary>
    public bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            if (value is not string command || string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            if (IsDisabledInStartupApproved())
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDisabledInStartupApproved()
    {
        try
        {
            using RegistryKey? approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: false);
            if (approvedKey?.GetValue(ValueName) is byte[] data && data.Length > 0)
            {
                // Windows encodes enabled entries with an even status byte (0x02/0x06) and disabled entries with an odd byte (0x03/0x07).
                return (data[0] & 0x01) != 0;
            }
        }
        catch
        {
            // Ignore StartupApproved query failures.
        }

        return false;
    }

    private static void ClearStartupApprovedOverride()
    {
        try
        {
            using RegistryKey? approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            approvedKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Ignore best-effort cleanup of Task Manager StartupApproved override.
        }
    }

    /// <summary>
    /// Re-applies the current startup command to the Windows Run registry key so moving
    /// <c>ClipThat.exe</c> or switching between standalone and <c>Start_ClipThat.bat</c> never breaks autostart.
    /// </summary>
    public void EnsureStartupPathUpToDate()
    {
        if (IsDisabledInStartupApproved())
        {
            return;
        }

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
            ClearStartupApprovedOverride();
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

    internal static string BuildDotnetStartupCommand(string dotnetPath, string? startBaseDir = null)
    {
        string? launcherBatPath = null;
        string? projectFilePath = null;

        string baseDir = startBaseDir ?? AppContext.BaseDirectory;
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
            // Invoke inner "cmd.exe /c" so Start_ClipThat.bat (which uses exit /b 0) closes the minimized
            // console immediately instead of leaving an idle cmd /K window open on the taskbar.
            return $"cmd.exe /c start \"\" /min cmd.exe /c \"\"{launcherBatPath}\" --minimized\"";
        }

        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            return $"cmd.exe /c start \"\" /min \"{dotnetPath}\" run --project \"{projectFilePath}\" -- --minimized";
        }

        string fallbackDll = Path.Combine(baseDir, "ClipThat.dll");
        if (File.Exists(fallbackDll))
        {
            return $"\"{dotnetPath}\" \"{fallbackDll}\" --minimized";
        }

        throw new InvalidOperationException("Nie udało się zlokalizować pliku uruchomieniowego ClipThat do autostartu.");
    }
}
