using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipThat.Engine.Diagnostics;
using ClipThat.Models;

namespace ClipThat.Services;

/// <summary>
/// Thread-safe JSON persistence for application settings under %AppData%\ClipThat\settings.json.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncLock = new();
    private AppSettings? _cachedSettings;

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipThat",
            "settings.json"))
    {
    }

    public SettingsService(string settingsFilePath)
    {
        SettingsFilePath = settingsFilePath;
    }

    public string SettingsFilePath { get; }

    /// <summary>
    /// Loads settings from disk or returns cached defaults, normalizing invalid values.
    /// </summary>
    public AppSettings Load()
    {
        lock (_syncLock)
        {
            if (_cachedSettings is not null)
            {
                return _cachedSettings.Clone();
            }

            AppSettings loaded = ReadFromDiskOrDefault(out bool shouldPersistDefaults, out bool shouldCache);
            loaded.Normalize();

            if (shouldCache)
            {
                _cachedSettings = loaded;
            }

            if (shouldPersistDefaults)
            {
                TryWriteAtomic(loaded);
            }

            return loaded.Clone();
        }
    }

    /// <summary>
    /// Normalizes and atomically persists the provided settings to disk.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings copy = settings.Clone();
        copy.Normalize();

        lock (_syncLock)
        {
            WriteAtomic(copy);
            _cachedSettings = copy;
        }
    }

    private AppSettings ReadFromDiskOrDefault(out bool shouldPersistDefaults, out bool shouldCache)
    {
        shouldPersistDefaults = false;
        shouldCache = true;

        if (!File.Exists(SettingsFilePath))
        {
            shouldPersistDefaults = true;
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            EngineLogger.Warn("SettingsService", $"Corrupted settings file '{SettingsFilePath}': {ex.Message}; backing up and resetting to defaults.", ex);
            TryBackupCorruptSettingsFile();
            shouldPersistDefaults = true;
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EngineLogger.Warn("SettingsService", $"Failed to read settings file '{SettingsFilePath}': {ex.Message}", ex);
            // Do not poison the in-memory cache with defaults on a transient file lock / I/O error.
            shouldCache = false;
            return new AppSettings();
        }
    }

    private void TryBackupCorruptSettingsFile()
    {
        try
        {
            string backupPath = SettingsFilePath + ".corrupt.bak";
            File.Copy(SettingsFilePath, backupPath, overwrite: true);
        }
        catch
        {
            // Ignore best-effort backup failures.
        }
    }

    private void TryWriteAtomic(AppSettings settings)
    {
        try
        {
            WriteAtomic(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup can proceed with in-memory defaults if AppData is temporarily unavailable.
        }
    }

    private void WriteAtomic(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{SettingsFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure.
            }

            throw;
        }
    }
}
