using ClipThat.Models;

namespace ClipThat.Engine;

/// <summary>
/// Defines the lifecycle and replay-saving contract for background screen capture engines.
/// </summary>
public interface IReplayEngine : IDisposable
{
    bool IsRecording { get; }

    Task StartAsync(AppSettings settings);

    Task StopAsync();

    Task<string?> SaveReplayAsync();

    Task UpdateSettingsAsync(AppSettings settings);

    event Action<string>? StatusChanged;

    event Action<string>? ReplaySaved;
}
