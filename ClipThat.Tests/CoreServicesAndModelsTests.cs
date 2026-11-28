using ClipThat.Engine.Video;
using ClipThat.Models;
using ClipThat.Services;
using Xunit;

namespace ClipThat.Tests;

public sealed class CoreServicesAndModelsTests
{
    [Theory]
    [InlineData("my_clip", "C:\\Videos\\orig.mp4", "my_clip")]
    [InlineData("my_clip.mp4", "C:\\Videos\\orig.mp4", "my_clip")]
    [InlineData("bad:name*with?chars<>|", "C:\\Videos\\orig.mp4", "badnamewithchars")]
    [InlineData("CON", "C:\\Videos\\orig.mp4", "orig_edited")]
    [InlineData("nul", "C:\\Videos\\orig.mp4", "orig_edited")]
    [InlineData("   ", "C:\\Videos\\Replay_01.mp4", "Replay_01_edited")]
    [InlineData(null, "C:\\Videos\\Replay_01.mp4", "Replay_01_edited")]
    public void SanitizeOutputBaseName_ProducesSafeFileName(string? customName, string inputPath, string expected)
    {
        string result = ClipEditorService.SanitizeOutputBaseName(customName, inputPath);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResourceEstimator_UsesMiBDivisorAndAdjustsForDisabledMicrophone()
    {
        var estimator = new ResourceEstimatorService();

        ResourceEstimate withMic = estimator.Estimate(5, ResolutionPreset.P1080, 60, includeMicrophone: true);
        ResourceEstimate noMic = estimator.Estimate(5, ResolutionPreset.P1080, 60, includeMicrophone: false);

        // 1080p60 = 16 Mbps video; 5 min = 300 s; MiB divisor = 8.388608
        double expectedWithMicDiskMb = ((16.0 + ResourceEstimatorService.AudioBitrateMbps) * 300.0) / 8.388608;
        double expectedNoMicDiskMb = ((16.0 + ResourceEstimatorService.SingleTrackAudioBitrateMbps) * 300.0) / 8.388608;

        Assert.Equal(16.0, withMic.VideoBitrateMbps);
        Assert.Equal(expectedWithMicDiskMb, withMic.EstimatedDiskSizeMb, precision: 6);
        Assert.Equal(expectedNoMicDiskMb, noMic.EstimatedDiskSizeMb, precision: 6);
        Assert.True(noMic.EstimatedDiskSizeMb < withMic.EstimatedDiskSizeMb);
        Assert.True(noMic.EstimatedRamUsageMb < withMic.EstimatedRamUsageMb);

        var settingsNoMic = new AppSettings
        {
            BufferDurationMinutes = 5,
            Resolution = ResolutionPreset.P1080,
            Fps = 60,
            MicrophoneDeviceId = AudioDeviceService.DisabledDeviceId
        };
        ResourceEstimate fromSettings = estimator.Estimate(settingsNoMic);
        Assert.Equal(noMic.EstimatedDiskSizeMb, fromSettings.EstimatedDiskSizeMb, precision: 6);
    }

    [Fact]
    public void AppSettingsNormalize_SanitizesInvalidValues()
    {
        var settings = new AppSettings
        {
            BufferDurationMinutes = 99,
            Fps = 144,
            Resolution = (ResolutionPreset)999,
            OutputDirectory = "bad\0path",
            HotkeyModifiers = 0xFFFFu,
            HotkeyVirtualKey = 0u,
            SystemAudioDeviceId = "   ",
            MicrophoneDeviceId = " NONE "
        };

        settings.Normalize();

        Assert.Equal(10, settings.BufferDurationMinutes);
        Assert.Equal(60, settings.Fps);
        Assert.Equal(ResolutionPreset.P1080, settings.Resolution);
        Assert.Equal(AppSettings.DefaultOutputDirectory, settings.OutputDirectory);
        Assert.Equal(AppSettings.DefaultHotkeyModifiers, settings.HotkeyModifiers);
        Assert.Equal(AppSettings.DefaultHotkeyVirtualKey, settings.HotkeyVirtualKey);
        Assert.Null(settings.SystemAudioDeviceId);
        Assert.Equal("none", settings.MicrophoneDeviceId);
    }

    [Fact]
    public void AppSettingsNormalize_MasksModifierBitsWhenVirtualKeyIsValid()
    {
        var settings = new AppSettings
        {
            BufferDurationMinutes = 0,
            Fps = 30,
            HotkeyModifiers = 0x00F3u, // should mask to 0x0003 (Alt + Ctrl)
            HotkeyVirtualKey = 0x78u   // VK_F9
        };

        settings.Normalize();

        Assert.Equal(1, settings.BufferDurationMinutes);
        Assert.Equal(30, settings.Fps);
        Assert.Equal(0x0003u, settings.HotkeyModifiers);
        Assert.Equal(0x78u, settings.HotkeyVirtualKey);
    }

    [Theory]
    [InlineData(ResolutionPreset.P1080, 1920, 1080, 1920, 1080)]
    [InlineData(ResolutionPreset.P720, 1920, 1080, 1280, 720)]
    [InlineData(ResolutionPreset.P1080, 3440, 1440, 2580, 1080)] // 21:9 ultrawide preserved
    [InlineData(ResolutionPreset.P1080, 2560, 1600, 1728, 1080)] // 16:10 preserved
    [InlineData(ResolutionPreset.P1080, 1080, 1920, 1080, 1920)] // Portrait preserved
    [InlineData(ResolutionPreset.P720, 1080, 1920, 720, 1280)]   // Portrait scaled cleanly
    [InlineData(ResolutionPreset.P1080, 5120, 1440, 3840, 1080)] // 32:9 super-ultrawide within 4096 cap
    public void GpuVideoProcessorGetTargetDimensions_PreservesAspectRatioWithEvenBounds(
        ResolutionPreset preset,
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        (int width, int height) = GpuVideoProcessor.GetTargetDimensions(preset, sourceWidth, sourceHeight);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
        Assert.InRange(width, 2, 4096);
        Assert.InRange(height, 2, 2304);
    }
}
