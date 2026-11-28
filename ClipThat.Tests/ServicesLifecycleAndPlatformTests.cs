using System.IO;
using System.Windows.Threading;
using ClipThat.Models;
using ClipThat.Services;
using ClipThat.UI;
using NAudio.CoreAudioApi;
using Xunit;

namespace ClipThat.Tests;

public sealed class ServicesLifecycleAndPlatformTests
{
    [Fact]
    public void PortableFfmpegProvider_PathsAndResolutionAreSafe()
    {
        Assert.EndsWith(Path.Combine("ClipThat", "bin"), PortableFfmpegProvider.BinDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ffmpeg.exe", PortableFfmpegProvider.ExtractedFfmpegPath, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PortableFfmpegProvider.ResolveAdjacentExecutable("nonexistent_tool_" + Guid.NewGuid().ToString("N") + ".exe"));

        string? resolved = PortableFfmpegProvider.TryEnsureExtracted();
        if (resolved is not null)
        {
            Assert.True(File.Exists(resolved));
        }
    }

    [Fact]
    public void AudioDeviceService_HandlesDisabledIdEnumerationAndDoubleDispose()
    {
        Assert.True(AudioDeviceService.IsDisabledDeviceId("none"));
        Assert.True(AudioDeviceService.IsDisabledDeviceId("NONE"));
        Assert.False(AudioDeviceService.IsDisabledDeviceId(null));
        Assert.False(AudioDeviceService.IsDisabledDeviceId("{0.0.1.00000000}.{test}"));

        using (var enumerator = new MMDeviceEnumerator())
        {
            Assert.Null(AudioDeviceService.ResolveDevice(enumerator, DataFlow.Capture, AudioDeviceService.DisabledDeviceId));
            Assert.Null(AudioDeviceService.ResolveDevice(enumerator, DataFlow.Capture, "NONE"));
        }

        var service = new AudioDeviceService();
        IReadOnlyList<AudioDeviceInfo> playback = service.GetPlaybackDevices();
        Assert.NotEmpty(playback);
        Assert.Null(playback[0].DeviceId);

        IReadOnlyList<AudioDeviceInfo> capture = service.GetCaptureDevices();
        Assert.True(capture.Count >= 2);
        Assert.Null(capture[0].DeviceId);
        Assert.Equal(AudioDeviceService.DisabledDeviceId, capture[1].DeviceId);

        service.Dispose();
        service.Dispose(); // Must be idempotent
    }

    [Fact]
    public void DisplayService_ReturnsValidPhysicalDisplays()
    {
        var service = new DisplayService();
        IReadOnlyList<DisplayDeviceInfo> displays = service.GetDisplays();

        Assert.NotEmpty(displays);
        for (int i = 0; i < displays.Count; i++)
        {
            DisplayDeviceInfo display = displays[i];
            Assert.Equal(i + 1, display.DisplayIndex);
            Assert.False(string.IsNullOrWhiteSpace(display.DeviceName));
            Assert.False(string.IsNullOrWhiteSpace(display.FriendlyName));
            Assert.True(display.Bounds.Width > 0);
            Assert.True(display.Bounds.Height > 0);
            Assert.True(display.RefreshRate >= 24);
        }
    }

    [Fact]
    public void StartupService_ReadOnlyStatusQueryAndCommandBuilderAreSafe()
    {
        var service = new StartupService();
        _ = service.IsStartupEnabled();

        string tempRoot = Path.Combine(Path.GetTempPath(), "ClipThatStartupTest_" + Guid.NewGuid().ToString("N"));
        string nestedBin = Path.Combine(tempRoot, "bin", "Release");
        Directory.CreateDirectory(nestedBin);
        try
        {
            string batPath = Path.Combine(tempRoot, "Start_ClipThat.bat");
            File.WriteAllText(batPath, "@echo off\r\n");

            string cmd = StartupService.BuildDotnetStartupCommand(@"C:\Program Files\dotnet\dotnet.exe", nestedBin);
            Assert.Contains("cmd.exe /c start \"\" /min cmd.exe /c", cmd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(batPath, cmd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--minimized", cmd, StringComparison.OrdinalIgnoreCase);

            string emptyDir = Path.Combine(Path.GetTempPath(), "ClipThatEmpty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    StartupService.BuildDotnetStartupCommand(@"C:\Program Files\dotnet\dotnet.exe", emptyDir));
            }
            finally
            {
                try { Directory.Delete(emptyDir, recursive: true); } catch { /* ignore */ }
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void AppUpdateService_ParsesGitHubJsonEvaluatesVersionsAndBuildsSafeUpdaterScript()
    {
        const string sampleSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
        const string otherSha = "1111111111111111111111111111111111111111";
        string sampleJson = $$"""
            {
              "sha": "{{sampleSha}}",
              "commit": {
                "message": "Naprawiono aktualizacje\n\nSzczegoly zmiany",
                "committer": {
                  "date": "2026-11-28T10:15:30Z"
                }
              }
            }
            """;

        Assert.True(AppUpdateService.TryParseGitHubCommitJson(
            sampleJson,
            out string parsedSha,
            out string headline,
            out DateTimeOffset? commitDateUtc));
        Assert.Equal(sampleSha, parsedSha);
        Assert.Equal("Naprawiono aktualizacje", headline);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 10, 15, 30, TimeSpan.Zero), commitDateUtc);

        Assert.False(AppUpdateService.TryParseGitHubCommitJson("{\"sha\":\"short\"}", out _, out _, out _));
        Assert.False(AppUpdateService.TryParseGitHubCommitJson("not-json", out _, out _, out _));

        // ZIP install evaluation: exact saved SHA wins over timestamps.
        Assert.Equal(
            UpdateCheckStatus.UpToDate,
            AppUpdateService.EvaluateZipInstallStatus(sampleSha, null, sampleSha, commitDateUtc));
        Assert.Equal(
            UpdateCheckStatus.UpdateAvailable,
            AppUpdateService.EvaluateZipInstallStatus(otherSha, commitDateUtc, sampleSha, commitDateUtc));

        // ZIP install without .clipthat_version compares source timestamp with 2-minute tolerance.
        Assert.Equal(
            UpdateCheckStatus.UpToDate,
            AppUpdateService.EvaluateZipInstallStatus(null, commitDateUtc!.Value.AddSeconds(-60), sampleSha, commitDateUtc));
        Assert.Equal(
            UpdateCheckStatus.UpdateAvailable,
            AppUpdateService.EvaluateZipInstallStatus(null, commitDateUtc.Value.AddMinutes(-10), sampleSha, commitDateUtc));

        // Root resolution, .clipthat_version reading, and source timestamp scan (ignoring bin/obj).
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClipThatUpdateTest_" + Guid.NewGuid().ToString("N"));
        string nestedDir = Path.Combine(tempRoot, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(nestedDir);
        try
        {
            string csproj = Path.Combine(tempRoot, "ClipThat.csproj");
            File.WriteAllText(csproj, "<Project />");
            DateTime sourceTime = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csproj, sourceTime);

            // Newer file inside bin/ must be ignored by source mtime scan.
            string binFile = Path.Combine(nestedDir, "dummy.cs");
            File.WriteAllText(binFile, "// ignored");
            File.SetLastWriteTimeUtc(binFile, new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(tempRoot, AppUpdateService.FindAppRootDirectory(nestedDir));
            Assert.Equal(new DateTimeOffset(sourceTime, TimeSpan.Zero), AppUpdateService.GetLatestTrackedSourceWriteTimeUtc(tempRoot));

            File.WriteAllText(Path.Combine(tempRoot, AppUpdateService.VersionFileName), sampleSha + "\n");
            Assert.Equal(sampleSha, AppUpdateService.ReadSavedVersionSha(tempRoot));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }

        // Verify generated PowerShell script escapes apostrophes, keeps backup on same volume, preserves ffmpeg.exe, and removes obsolete bat.
        string script = AppUpdateService.BuildUpdaterPowerShellScript(
            @"E:\O'Connor\ClipThat",
            sampleSha,
            isGitRepo: false,
            currentProcessId: 12345,
            updateId: "abc123");

        Assert.Contains("$appDir = 'E:\\O''Connor\\ClipThat'", script, StringComparison.Ordinal);
        Assert.Contains("$backupDir = Join-Path $appDir ('.clipthat_update_backup_' + $updateId)", script, StringComparison.Ordinal);
        Assert.Contains("ffmpeg.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aktualizuj_ClipThat.bat", script, StringComparison.Ordinal);
        Assert.Contains("& $startBat --rebuild", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MfTrackAudioWaveStream_ValidatesArgumentsAndHandlesMissingFilesGracefully()
    {
        Assert.Throws<FileNotFoundException>(() => new MfTrackAudioWaveStream("", 0, TimeSpan.FromSeconds(1)));

        string existingFile = Path.Combine(Path.GetTempPath(), "mf_validate_" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(existingFile, Array.Empty<byte>());
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MfTrackAudioWaveStream(existingFile, -1, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            try { File.Delete(existingFile); } catch { /* ignore */ }
        }

        string missingFile = Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N") + ".mp4");
        bool opened = MfTrackAudioWaveStream.TryOpenAudioTracks(
            missingFile,
            TimeSpan.FromSeconds(5),
            out MfTrackAudioWaveStream? sysStream,
            out MfTrackAudioWaveStream? micStream);

        Assert.False(opened);
        Assert.Null(sysStream);
        Assert.Null(micStream);
    }

    [Fact]
    public void GlobalHotkeyService_LifecycleInvalidKeyAndDoubleDisposeWorkOnStaDispatcher()
    {
        Exception? threadError = null;
        var done = new ManualResetEventSlim(false);

        var staThread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                var service = new GlobalHotkeyService(dispatcher);

                Assert.False(service.IsRegistered);
                Assert.False(service.UpdateHotkey(0x0001u, virtualKey: 0u));
                Assert.False(service.IsRegistered);

                service.Unregister();
                service.Dispose();
                service.Dispose(); // Must be idempotent

                Assert.Throws<ObjectDisposedException>(() => service.UpdateHotkey(0x0001u, 0x79u));
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
            finally
            {
                done.Set();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Null(threadError);
    }

    [Fact]
    public void SmoothScrollBehavior_ComputesWheelDeltaAndConvergesFrameRateIndependently()
    {
        // Standard Windows 3 lines per notch -> 144 px per 120 delta, negative delta scrolls down (+offset)
        Assert.Equal(144.0, SmoothScrollBehavior.ComputeWheelDeltaPixels(-120, 3, 600.0), precision: 6);
        Assert.Equal(-144.0, SmoothScrollBehavior.ComputeWheelDeltaPixels(120, 3, 600.0), precision: 6);
        // Fractional high-precision touchpad delta scales linearly
        Assert.Equal(36.0, SmoothScrollBehavior.ComputeWheelDeltaPixels(-30, 3, 600.0), precision: 6);
        // Page-at-a-time (-1) scrolls 85% of viewport height
        Assert.Equal(510.0, SmoothScrollBehavior.ComputeWheelDeltaPixels(-120, -1, 600.0), precision: 6);
        Assert.Equal(0.0, SmoothScrollBehavior.ComputeWheelDeltaPixels(0, 3, 600.0), precision: 6);

        // Frame-rate independence: two 1/120 s steps should match one 1/60 s step closely.
        (double oneStep60Hz, bool done60) = SmoothScrollBehavior.AdvanceTowardsTarget(0.0, 200.0, 1.0 / 60.0);
        (double half1, _) = SmoothScrollBehavior.AdvanceTowardsTarget(0.0, 200.0, 1.0 / 120.0);
        (double twoSteps120Hz, _) = SmoothScrollBehavior.AdvanceTowardsTarget(half1, 200.0, 1.0 / 120.0);
        Assert.False(done60);
        Assert.InRange(Math.Abs(oneStep60Hz - twoSteps120Hz), 0.0, 0.01);

        // Convergence: reaches exact target cleanly within < 1 second at 60 Hz.
        double pos = 0.0;
        bool reached = false;
        for (int frame = 0; frame < 60 && !reached; frame++)
        {
            (pos, reached) = SmoothScrollBehavior.AdvanceTowardsTarget(pos, 200.0, 1.0 / 60.0);
        }

        Assert.True(reached);
        Assert.Equal(200.0, pos);
    }

    [Fact]
    public void AppStartupFlagParser_DetectsMinimizedVariantsFromStartupOrProcessArgs()
    {
        Assert.True(App.ShouldStartMinimized(new[] { "--minimized" }, Array.Empty<string>()));
        Assert.True(App.ShouldStartMinimized(Array.Empty<string>(), new[] { "  \"--MINIMIZED\"  " }));
        Assert.True(App.ShouldStartMinimized(new[] { "-minimized" }, null));
        Assert.True(App.ShouldStartMinimized(null, new[] { "/minimized" }));
        Assert.False(App.ShouldStartMinimized(Array.Empty<string>(), Array.Empty<string>()));
        Assert.False(App.ShouldStartMinimized(new[] { "--exit" }, new[] { "--other" }));
    }
}
