using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Models;
using NAudio.CoreAudioApi;

namespace ClipThat.Services;

/// <summary>
/// Enumerates active Windows Core Audio (WASAPI) playback and microphone devices
/// via <see cref="MMDeviceEnumerator"/> with graceful fallback when no capture devices exist.
/// </summary>
public sealed class AudioDeviceService
{
    public const string DefaultDeviceLabel = "Domyślne urządzenie systemowe";

    /// <summary>
    /// Lists active playback endpoints (<see cref="DataFlow.Render"/>) usable for WASAPI system audio loopback.
    /// Always starts with the <see cref="DefaultDeviceLabel"/> (<see langword="null"/> ID) option.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices()
        => EnumerateDevices(DataFlow.Render);

    /// <summary>
    /// Lists active recording endpoints (<see cref="DataFlow.Capture"/>) usable as microphone sources.
    /// Always starts with the <see cref="DefaultDeviceLabel"/> (<see langword="null"/> ID) option and handles
    /// systems with no microphone gracefully without throwing.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => EnumerateDevices(DataFlow.Capture);

    /// <summary>
    /// Returns <see langword="true"/> when at least one active microphone capture device is available.
    /// </summary>
    public bool HasActiveCaptureDevice()
    {
        IReadOnlyList<AudioDeviceInfo> devices = GetCaptureDevices();
        return devices.Count > 1;
    }

    /// <summary>
    /// Resolves a concrete active <see cref="MMDevice"/> for the specified <paramref name="dataFlow"/> and optional
    /// <paramref name="deviceId"/>, falling back to the system default multimedia endpoint if the ID is null/empty
    /// or the previously configured device is disconnected. Caller owns and must dispose the returned device.
    /// </summary>
    public static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, DataFlow dataFlow, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(enumerator);

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

    private static IReadOnlyList<AudioDeviceInfo> EnumerateDevices(DataFlow dataFlow)
    {
        var results = new List<AudioDeviceInfo>
        {
            new()
            {
                DeviceId = null,
                FriendlyName = DefaultDeviceLabel,
                IsDefaultOption = true,
                IsSystemDefault = false
            }
        };

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
            if (!enumerator.HasDefaultAudioEndpoint(dataFlow, role))
            {
                return null;
            }

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
}
