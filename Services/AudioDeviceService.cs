using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClipThat.Services;

/// <summary>
/// Enumerates active Windows Core Audio (WASAPI) playback and microphone devices
/// via <see cref="MMDeviceEnumerator"/> and raises change notifications when endpoints
/// are plugged in, unplugged, or switched as default.
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    public const string DisabledDeviceId = "none";
    public const string DefaultRenderDeviceLabel = "Domyślne urządzenie systemowe (automatyczne)";
    public const string DefaultCaptureDeviceLabel = "Domyślny mikrofon systemowy (automatyczny)";
    public const string DisabledMicrophoneLabel = "Wyłączony";
    public const string DefaultDeviceLabel = DefaultRenderDeviceLabel;

    private readonly object _watcherLock = new();
    private MMDeviceEnumerator? _notificationEnumerator;
    private EndpointNotificationBridge? _notificationBridge;
    private int _disposed;

    public AudioDeviceService()
    {
        TryStartDeviceWatcher();
    }

    /// <summary>
    /// Raised on a background thread whenever Windows audio endpoints are added, removed,
    /// change active state, or when the default audio endpoint changes.
    /// </summary>
    public event Action? DevicesChanged;

    public static bool IsDisabledDeviceId(string? deviceId)
        => string.Equals(deviceId, DisabledDeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lists active playback endpoints (<see cref="DataFlow.Render"/>) usable for WASAPI system audio loopback.
    /// Always starts with the automatic system default (<see langword="null"/> ID) option.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
        => EnumerateDevices(DataFlow.Render, includeDisabledOption: false);

    /// <summary>
    /// Lists active recording endpoints (<see cref="DataFlow.Capture"/>) usable as microphone sources.
    /// Starts with the automatic system default (<see langword="null"/> ID) and explicit disabled (<c>"none"</c>)
    /// options, and handles systems with no microphone gracefully without throwing.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => EnumerateDevices(DataFlow.Capture, includeDisabledOption: true);

    /// <summary>
    /// Returns <see langword="true"/> when at least one active microphone capture device is available.
    /// </summary>
    public bool HasActiveCaptureDevice()
    {
        IReadOnlyList<AudioDeviceInfo> devices = GetCaptureDevices();
        return devices.Any(d => !d.IsDefaultOption && !d.IsDisabledOption);
    }

    /// <summary>
    /// Resolves a concrete active <see cref="MMDevice"/> for the specified <paramref name="dataFlow"/> and optional
    /// <paramref name="deviceId"/>. Returns <see langword="null"/> when capture is explicitly disabled (<c>"none"</c>).
    /// Uses <see cref="MMDeviceEnumerator.GetDevice"/> when a specific ID is configured, or
    /// <see cref="MMDeviceEnumerator.GetDefaultAudioEndpoint"/> (<see cref="Role.Multimedia"/>) when null/empty.
    /// Caller owns and must dispose the returned device.
    /// </summary>
    public static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, DataFlow dataFlow, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(enumerator);

        if (dataFlow == DataFlow.Capture && IsDisabledDeviceId(deviceId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                MMDevice specific = enumerator.GetDevice(deviceId);
                if (specific.State == DeviceState.Active)
                {
                    return specific;
                }

                specific.Dispose();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                Debug.WriteLine($"Wybrane urządzenie audio '{deviceId}' jest niedostępne ({ex.Message}); powrót do domyślnego.");
            }
        }

        MMDevice? defaultDevice = TryGetDefaultEndpoint(enumerator, dataFlow, Role.Multimedia)
            ?? TryGetDefaultEndpoint(enumerator, dataFlow, Role.Console)
            ?? TryGetDefaultEndpoint(enumerator, dataFlow, Role.Communications);

        if (defaultDevice is not null)
        {
            return defaultDevice;
        }

        // Last resort: pick the first active endpoint for the requested flow if default roles are unset.
        try
        {
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
            MMDevice? selected = null;
            for (int i = 0; i < endpoints.Count; i++)
            {
                MMDevice candidate = endpoints[i];
                if (selected is null)
                {
                    selected = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            return selected;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Nie udało się wyliczyć urządzeń audio ({dataFlow}): {ex.Message}");
            return null;
        }
    }

    private void TryStartDeviceWatcher()
    {
        lock (_watcherLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _notificationEnumerator = new MMDeviceEnumerator();
                _notificationBridge = new EndpointNotificationBridge(NotifyDevicesChanged);
                _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationBridge);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Nie udało się zarejestrować obserwatora urządzeń audio: {ex.Message}");
                CleanupWatcherLocked();
            }
        }
    }

    private void NotifyDevicesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Action? handler = DevicesChanged;
        if (handler is null)
        {
            return;
        }

        // Dispatch off the Core Audio RPC callback thread to avoid re-entrant MMDevApi deadlocks.
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((Action)state!).Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Błąd powiadomienia o zmianie urządzeń audio: {ex.Message}");
            }
        }, handler);
    }

    private static IReadOnlyList<AudioDeviceInfo> EnumerateDevices(DataFlow dataFlow, bool includeDisabledOption)
    {
        string defaultLabel = dataFlow == DataFlow.Capture
            ? DefaultCaptureDeviceLabel
            : DefaultRenderDeviceLabel;

        var results = new List<AudioDeviceInfo>
        {
            new()
            {
                DeviceId = null,
                FriendlyName = defaultLabel,
                IsDefaultOption = true,
                IsDisabledOption = false,
                IsSystemDefault = false
            }
        };

        if (includeDisabledOption)
        {
            results.Add(new AudioDeviceInfo
            {
                DeviceId = DisabledDeviceId,
                FriendlyName = DisabledMicrophoneLabel,
                IsDefaultOption = false,
                IsDisabledOption = true,
                IsSystemDefault = false
            });
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultDeviceId = null;
            using (MMDevice? defaultEndpoint = TryGetDefaultEndpoint(enumerator, dataFlow, Role.Multimedia)
                ?? TryGetDefaultEndpoint(enumerator, dataFlow, Role.Communications))
            {
                defaultDeviceId = defaultEndpoint?.ID;
            }

            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
            for (int i = 0; i < endpoints.Count; i++)
            {
                using MMDevice device = endpoints[i];
                try
                {
                    string id = device.ID;
                    string friendlyName = string.IsNullOrWhiteSpace(device.FriendlyName)
                        ? id
                        : device.FriendlyName.Trim();

                    results.Add(new AudioDeviceInfo
                    {
                        DeviceId = id,
                        FriendlyName = friendlyName,
                        IsDefaultOption = false,
                        IsDisabledOption = false,
                        IsSystemDefault = !string.IsNullOrEmpty(defaultDeviceId) &&
                                          string.Equals(id, defaultDeviceId, StringComparison.OrdinalIgnoreCase)
                    });
                }
                catch (COMException ex)
                {
                    Debug.WriteLine($"Pominięto nieodpowiadające urządzenie audio: {ex.Message}");
                }
            }
        }
        catch (COMException ex)
        {
            // Systems with no audio service or no microphone endpoints should degrade cleanly.
            Debug.WriteLine($"Brak dostępnych urządzeń audio dla {dataFlow}: {ex.Message}");
        }

        return results;
    }

    private static MMDevice? TryGetDefaultEndpoint(MMDeviceEnumerator enumerator, DataFlow dataFlow, Role role)
    {
        try
        {
            MMDevice device = enumerator.GetDefaultAudioEndpoint(dataFlow, role);
            if (device.State == DeviceState.Active)
            {
                return device;
            }

            device.Dispose();
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void CleanupWatcherLocked()
    {
        if (_notificationEnumerator is not null && _notificationBridge is not null)
        {
            try
            {
                _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationBridge);
            }
            catch
            {
                // Ignore COM unregister errors during teardown.
            }
        }

        _notificationBridge = null;

        if (_notificationEnumerator is not null)
        {
            try
            {
                _notificationEnumerator.Dispose();
            }
            catch
            {
                // Ignore COM enumerator disposal errors.
            }

            _notificationEnumerator = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_watcherLock)
        {
            CleanupWatcherLocked();
        }
    }

    private sealed class EndpointNotificationBridge(Action onChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChanged();

        public void OnDeviceAdded(string pwstrDeviceId) => onChanged();

        public void OnDeviceRemoved(string deviceId) => onChanged();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => onChanged();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Ignore frequent mixer/volume property updates.
        }
    }
}
