using System.IO;
using System.Windows.Threading;
using ClipThat.Models;
using ClipThat.Services;
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
    public void StartupService_ReadOnlyStatusQueryDoesNotThrow()
    {
        var service = new StartupService();
        _ = service.IsStartupEnabled();
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
}
