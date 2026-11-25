namespace ClipThat.Models;

/// <summary>
/// Estimated video bitrate, RAM buffer footprint, and saved file size for the configured replay buffer.
/// </summary>
public sealed record ResourceEstimate(
    double VideoBitrateMbps,
    double EstimatedRamUsageMb,
    double EstimatedDiskSizeMb);
