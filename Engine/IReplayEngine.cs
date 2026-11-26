using ClipThat.Models;

namespace ClipThat.Engine;

/// <summary>
/// Defines the lifecycle and replay-saving contract for background screen capture engines.
/// </summary>
public interface IReplayEngine : IDisposable
{
    bool IsRecording { get; }

    /// <summary>
    /// Most recent human-readable engine status message, if any.
    /// </summary>
    string? CurrentStatus { get; }

    /// <summary>
    /// Most recent fatal startup, pipeline, or replay-save error message, or <see langword="null"/>
    /// when the engine is healthy. Preserved across UI window opens so startup failures are never silent.
    /// </summary>
    string? LastError { get; }

    Task StartAsync(AppSettings settings);

    Task StopAsync();

    Task<string?> SaveReplayAsync();

    Task UpdateSettingsAsync(AppSettings settings);

    event Action<string>? StatusChanged;

    event Action<string>? ErrorOccurred;

    event Action<string>? ReplaySaved;
}
