using ClipThat.Models;

namespace ClipThat.Services;

/// <summary>
/// Calculates live RAM usage and MP4 disk size estimates based on replay duration, resolution, and FPS.
/// </summary>
public sealed class ResourceEstimatorService
{
    public const double AudioBitrateMbps = 0.384;            // Three 128 kbps AAC audio tracks (System + Microphone + Combined Mix)
    public const double SingleTrackAudioBitrateMbps = 0.128; // Single 128 kbps AAC audio track (System only, microphone disabled)
    public const double BaseRamOverheadMb = 30.0;
    public const double RamBufferMultiplier = 1.15;
    private const double MegabitsPerMiB = 8.0 * 1.024 * 1.024; // 8 * 1,048,576 / 1,000,000 = 8.388608

    /// <summary>
    /// Returns the target video bitrate in Mbps for the given resolution preset and frame rate.
    /// </summary>
    public double GetVideoBitrateMbps(ResolutionPreset resolution, int fps)
    {
        bool isHighFps = fps >= 60;

        return resolution switch
        {
            ResolutionPreset.P240 => isHighFps ? 1.2 : 0.8,
            ResolutionPreset.P360 => isHighFps ? 2.2 : 1.5,
            ResolutionPreset.P480 => isHighFps ? 4.0 : 2.5,
            ResolutionPreset.P720 => isHighFps ? 8.0 : 5.0,
            ResolutionPreset.P1080 => isHighFps ? 16.0 : 10.0,
            _ => isHighFps ? 16.0 : 10.0
        };
    }

    /// <summary>
    /// Estimates video bitrate, disk file size (MiB, matching Explorer/ClipItem MB display),
    /// and ring-buffer RAM usage in megabytes.
    /// </summary>
    public ResourceEstimate Estimate(
        int bufferDurationMinutes,
        ResolutionPreset resolution,
        int fps,
        bool includeMicrophone = true)
    {
        int clampedMinutes = Math.Clamp(bufferDurationMinutes, 1, 10);
        double durationSeconds = clampedMinutes * 60.0;
        double videoBitrateMbps = GetVideoBitrateMbps(resolution, fps);
        double audioBitrateMbps = includeMicrophone ? AudioBitrateMbps : SingleTrackAudioBitrateMbps;
        double diskSizeMb = ((videoBitrateMbps + audioBitrateMbps) * durationSeconds) / MegabitsPerMiB;
        double ramUsageMb = BaseRamOverheadMb + (diskSizeMb * RamBufferMultiplier);

        return new ResourceEstimate(videoBitrateMbps, ramUsageMb, diskSizeMb);
    }

    /// <summary>
    /// Estimates resource consumption directly from an <see cref="AppSettings"/> instance.
    /// </summary>
    public ResourceEstimate Estimate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool includeMicrophone = !AudioDeviceService.IsDisabledDeviceId(settings.MicrophoneDeviceId);
        return Estimate(settings.BufferDurationMinutes, settings.Resolution, settings.Fps, includeMicrophone);
    }
}
