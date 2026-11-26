using System.Diagnostics;
using Microsoft.Win32;

namespace ClipThat.Services;

/// <summary>
/// Manages automatic launch with Windows via the current user's Run registry key.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipThat";

    /// <summary>
    /// Returns true if ClipThat is currently registered to start with Windows.
    /// </summary>
    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        object? value = key?.GetValue(ValueName);
        return value is string command && !string.IsNullOrWhiteSpace(command);
    }

    /// <summary>
    /// Re-applies the current executable path (<see cref="Environment.ProcessPath"/>) to the Windows Run
    /// registry key so moving <c>ClipThat.exe</c> to another folder never breaks autostart.
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
    /// Adds or removes the ClipThat startup registry entry with the --minimized flag.
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

        return $"\"{executablePath}\" --minimized";
    }
}
