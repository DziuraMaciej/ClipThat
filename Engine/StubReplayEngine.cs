using System.IO;
using ClipThat.Models;

namespace ClipThat.Engine;

/// <summary>
/// Thread-safe placeholder replay engine used until the native WGC/Direct3D11/WASAPI backend is wired in.
/// </summary>
public sealed class StubReplayEngine : IReplayEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _currentSettings;
    private volatile bool _isRecording;
    private int _disposed;

    public bool IsRecording => _isRecording;

    public event Action<string>? StatusChanged;

    public event Action<string>? ReplaySaved;

    public async Task StartAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string statusMessage;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _currentSettings = settings.Clone();
            _currentSettings.Normalize();
            _isRecording = true;
            statusMessage = $"Nagrywanie aktywne (tryb testowy · {_currentSettings.Resolution.ToString()[1..]}p {_currentSettings.Fps} FPS)";
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(statusMessage);
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool wasRecording;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            wasRecording = _isRecording;
            _isRecording = false;
        }
        finally
        {
            _gate.Release();
        }

        if (wasRecording)
        {
            StatusChanged?.Invoke("Nagrywanie wstrzymane");
        }
    }

    public async Task<string?> SaveReplayAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        string? outputFilePath = null;
        string statusMessage;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            if (!_isRecording || _currentSettings is null)
            {
                statusMessage = "Nagrywanie jest wstrzymane — brak bufora do zapisania";
            }
            else
            {
                Directory.CreateDirectory(_currentSettings.OutputDirectory);
                string fileName = $"ClipThat_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                outputFilePath = Path.Combine(_currentSettings.OutputDirectory, fileName);
                statusMessage = $"Zapisano powtórkę (symulacja): {fileName}";
            }
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(statusMessage);

        if (outputFilePath is not null)
        {
            ReplaySaved?.Invoke(outputFilePath);
        }

        return outputFilePath;
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string? statusMessage = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _currentSettings = settings.Clone();
            _currentSettings.Normalize();

            if (_isRecording)
            {
                statusMessage = $"Nagrywanie aktywne (tryb testowy · {_currentSettings.Resolution.ToString()[1..]}p {_currentSettings.Fps} FPS)";
            }
        }
        finally
        {
            _gate.Release();
        }

        if (statusMessage is not null)
        {
            StatusChanged?.Invoke(statusMessage);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _isRecording = false;
            _currentSettings = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
