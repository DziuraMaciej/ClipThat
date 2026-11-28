using System.IO;
using ClipThat.Models;
using ClipThat.Services;
using Xunit;

namespace ClipThat.Tests;

public sealed class SettingsAndModelsTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsAndModelsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClipThatSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }

    [Fact]
    public void SettingsService_SavesAtomicallyAndReloadsPersistedValues()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var service = new SettingsService(path);

        AppSettings initial = service.Load();
        Assert.True(File.Exists(path));
        Assert.Equal(5, initial.BufferDurationMinutes);

        initial.BufferDurationMinutes = 8;
        initial.Fps = 30;
        initial.Resolution = ResolutionPreset.P720;
        initial.SelectedMonitorDeviceName = @"  \\.\DISPLAY2  ";
        initial.HotkeyModifiers = 0x0003u; // Ctrl + Alt
        initial.HotkeyVirtualKey = 0x7Bu;  // F12
        initial.MicrophoneDeviceId = "none";
        service.Save(initial);

        var freshReader = new SettingsService(path);
        AppSettings reloaded = freshReader.Load();

        Assert.Equal(8, reloaded.BufferDurationMinutes);
        Assert.Equal(30, reloaded.Fps);
        Assert.Equal(ResolutionPreset.P720, reloaded.Resolution);
        Assert.Equal(@"\\.\DISPLAY2", reloaded.SelectedMonitorDeviceName);
        Assert.Equal(0x0003u, reloaded.HotkeyModifiers);
        Assert.Equal(0x7Bu, reloaded.HotkeyVirtualKey);
        Assert.Equal("none", reloaded.MicrophoneDeviceId);
    }

    [Fact]
    public void SettingsService_BacksUpCorruptedJsonAndResetsToDefaults()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        const string brokenJson = "{ \"BufferDurationMinutes\": [not-valid-json";
        File.WriteAllText(path, brokenJson);

        var service = new SettingsService(path);
        AppSettings loaded = service.Load();

        Assert.Equal(5, loaded.BufferDurationMinutes);
        Assert.Equal(AppSettings.DefaultHotkeyModifiers, loaded.HotkeyModifiers);
        Assert.Equal(AppSettings.DefaultHotkeyVirtualKey, loaded.HotkeyVirtualKey);

        string backupPath = path + ".corrupt.bak";
        Assert.True(File.Exists(backupPath));
        Assert.Equal(brokenJson, File.ReadAllText(backupPath));

        // Main settings file on disk should be reset to clean defaults so future reloads stay healthy.
        string repairedJson = File.ReadAllText(path);
        Assert.NotEqual(brokenJson, repairedJson);
        var freshService = new SettingsService(path);
        Assert.Equal(5, freshService.Load().BufferDurationMinutes);
    }

    [Fact]
    public void SettingsService_LoadAndSave_ReturnIsolatedClonesAndAreThreadSafe()
    {
        string path = Path.Combine(_tempDir, "isolated_settings.json");
        var service = new SettingsService(path);

        AppSettings first = service.Load();
        first.BufferDurationMinutes = 10;
        first.Fps = 30;

        AppSettings second = service.Load();
        Assert.Equal(5, second.BufferDurationMinutes);
        Assert.Equal(60, second.Fps);

        second.BufferDurationMinutes = 7;
        service.Save(second);
        second.BufferDurationMinutes = 1;

        AppSettings third = service.Load();
        Assert.Equal(7, third.BufferDurationMinutes);

        Parallel.For(0, 16, i =>
        {
            AppSettings snapshot = service.Load();
            snapshot.BufferDurationMinutes = (i % 10) + 1;
            service.Save(snapshot);
        });

        AppSettings finalLoaded = service.Load();
        Assert.InRange(finalLoaded.BufferDurationMinutes, 1, 10);
    }

    [Fact]
    public void AppSettingsNormalize_RejectsUnmodifiedNonFunctionKeyAndAllowsFunctionKeys()
    {
        var bareLetter = new AppSettings
        {
            HotkeyModifiers = 0u,
            HotkeyVirtualKey = 0x41u, // 'A'
            SelectedMonitorDeviceName = "   "
        };
        bareLetter.Normalize();

        Assert.Null(bareLetter.SelectedMonitorDeviceName);
        Assert.Equal(AppSettings.DefaultHotkeyModifiers, bareLetter.HotkeyModifiers);
        Assert.Equal(AppSettings.DefaultHotkeyVirtualKey, bareLetter.HotkeyVirtualKey);

        var bareFunctionKey = new AppSettings
        {
            HotkeyModifiers = 0u,
            HotkeyVirtualKey = 0x7Bu, // VK_F12
            SelectedMonitorDeviceName = @" \\.\DISPLAY1 "
        };
        bareFunctionKey.Normalize();

        Assert.Equal(@"\\.\DISPLAY1", bareFunctionKey.SelectedMonitorDeviceName);
        Assert.Equal(0u, bareFunctionKey.HotkeyModifiers);
        Assert.Equal(0x7Bu, bareFunctionKey.HotkeyVirtualKey);
    }

    [Fact]
    public void ClipItem_ParseRecordedAt_ParsesExactConventionAndFallsBackToFileCreationTime()
    {
        DateTime parsed = ClipItem.ParseRecordedAt(@"C:\Videos\45301428112026.mp4");
        Assert.Equal(new DateTime(2026, 11, 28, 14, 30, 45), parsed);

        string fallbackFile = Path.Combine(_tempDir, "custom_highlight.mp4");
        File.WriteAllBytes(fallbackFile, Array.Empty<byte>());
        DateTime expectedCreation = new DateTime(2025, 5, 10, 9, 15, 0);
        File.SetCreationTime(fallbackFile, expectedCreation);

        DateTime fallbackParsed = ClipItem.ParseRecordedAt(fallbackFile);
        Assert.Equal(expectedCreation, fallbackParsed);

        string missingFile = Path.Combine(_tempDir, "does_not_exist.mp4");
        DateTime missingParsed = ClipItem.ParseRecordedAt(missingFile);
        Assert.True(missingParsed.Year >= 2020, $"Expected recent fallback date instead of Windows epoch {missingParsed:O}");
    }

    [Theory]
    [InlineData(-5, "00:00")]
    [InlineData(0, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3661, "01:01:01")]
    public void ClipItem_FormatDuration_FormatsShortAndLongDurations(int seconds, string expected)
    {
        Assert.Equal(expected, ClipItem.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ClipItem_FormatFileSizeAndAudioTrackBadges_AreConsistent()
    {
        Assert.Equal("0 KB", ClipItem.FormatFileSize(0));
        Assert.Equal("512 KB", ClipItem.FormatFileSize(512 * 1024));
        Assert.Equal("25,0 MB", ClipItem.FormatFileSize(25L * 1024L * 1024L));
        Assert.Equal("2,00 GB", ClipItem.FormatFileSize(2L * 1024L * 1024L * 1024L));

        var negative = new ClipItem("neg.mp4", "neg.mp4", DateTime.Now, TimeSpan.FromSeconds(-10), -500, audioTrackCount: -2);
        Assert.Equal(TimeSpan.Zero, negative.Duration);
        Assert.Equal(0L, negative.FileSizeBytes);
        Assert.Equal(0, negative.AudioTrackCount);

        var noAudio = new ClipItem("a.mp4", "a.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 0);
        var single = new ClipItem("b.mp4", "b.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 1);
        var dual = new ClipItem("c.mp4", "c.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 2);
        var triple = new ClipItem("d.mp4", "d.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 3);

        Assert.False(noAudio.HasSeparateAudioTracks);
        Assert.Equal("Brak audio", noAudio.AudioTrackBadge);
        Assert.False(single.HasSeparateAudioTracks);
        Assert.Equal("1 ścieżka audio", single.AudioTrackBadge);
        Assert.True(dual.HasSeparateAudioTracks);
        Assert.Equal("2 ścieżki audio", dual.AudioTrackBadge);
        Assert.True(triple.HasSeparateAudioTracks);
        Assert.Equal("3 ścieżki audio", triple.AudioTrackBadge);

        var quad = new ClipItem("e.mp4", "e.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 4);
        var five = new ClipItem("f.mp4", "f.mp4", DateTime.Now, TimeSpan.FromSeconds(10), 1024, audioTrackCount: 5);
        Assert.Equal("4 ścieżki audio", quad.AudioTrackBadge);
        Assert.Equal("5 ścieżek audio", five.AudioTrackBadge);
    }

    [Theory]
    [InlineData(0x0001u, 0x79u, "Alt + F10")]
    [InlineData(0x0002u | 0x0004u, 0x41u, "Ctrl + Shift + A")]
    [InlineData(0x0008u, 0x65u, "Win + Num 5")]
    [InlineData(0x0002u | 0x0001u, 0x2Cu, "Ctrl + Alt + PrintScreen")]
    [InlineData(0u, 0x7Bu, "F12")]
    [InlineData(0u, 0u, "Brak")]
    public void GlobalHotkeyService_FormatHotkey_FormatsExpectedLabel(uint modifiers, uint vk, string expected)
    {
        Assert.Equal(expected, GlobalHotkeyService.FormatHotkey(modifiers, vk));
    }
}
