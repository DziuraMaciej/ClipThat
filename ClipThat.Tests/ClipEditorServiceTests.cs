using System.Diagnostics;
using ClipThat.Services;
using Xunit;

namespace ClipThat.Tests;

public sealed class ClipEditorServiceTests
{
    [Fact]
    public void BuildFfmpegStartInfo_MixSingleTrackForDiscord_UsesStreamCopyAndAmixFilter()
    {
        ProcessStartInfo psi = ClipEditorService.BuildFfmpegStartInfo(
            ffmpegPath: "ffmpeg.exe",
            inputFilePath: @"C:\Videos\clip.mp4",
            tempOutputPath: @"C:\Videos\out.tmp",
            startTime: TimeSpan.FromSeconds(12.5),
            duration: TimeSpan.FromSeconds(30),
            systemVolume: 1.0,
            microphoneVolume: 0.75,
            exportMode: ClipExportMode.MixSingleTrackForDiscord,
            audioTrackCount: 3,
            reencodeVideo: false,
            hardwareVideoEncoder: null);

        List<string> args = psi.ArgumentList.ToList();
        Assert.Contains("-ss", args);
        Assert.Contains("00:00:12.500", args);
        Assert.Contains("-t", args);
        Assert.Contains("00:00:30.000", args);
        Assert.Contains("copy", args);
        Assert.Contains(args, a => a.Contains("amix=inputs=2:duration=longest:normalize=0[aout]", StringComparison.Ordinal) &&
                                   a.Contains("volume=0.75", StringComparison.Ordinal));
        Assert.Contains("[aout]", args);
        Assert.DoesNotContain("[sys_out]", args);
    }

    [Fact]
    public void BuildFfmpegStartInfo_KeepSeparateAudioTracks_MapsThreeAudioOutputs()
    {
        ProcessStartInfo psi = ClipEditorService.BuildFfmpegStartInfo(
            ffmpegPath: "ffmpeg.exe",
            inputFilePath: @"C:\Videos\clip.mp4",
            tempOutputPath: @"C:\Videos\out.tmp",
            startTime: TimeSpan.Zero,
            duration: TimeSpan.FromSeconds(15),
            systemVolume: 1.2,
            microphoneVolume: 0.5,
            exportMode: ClipExportMode.KeepSeparateAudioTracks,
            audioTrackCount: 2,
            reencodeVideo: false,
            hardwareVideoEncoder: null);

        List<string> args = psi.ArgumentList.ToList();
        Assert.Contains(args, a => a.Contains("asplit=2[sys_mix][sys_out]", StringComparison.Ordinal));
        Assert.Contains("[sys_out]", args);
        Assert.Contains("[mic_out]", args);
        Assert.Contains("[aout]", args);
    }

    [Fact]
    public void BuildFfmpegStartInfo_HandlesSingleTrackAndNoAudioClips()
    {
        ProcessStartInfo singleAudio = ClipEditorService.BuildFfmpegStartInfo(
            "ffmpeg.exe", @"C:\Videos\clip.mp4", @"C:\Videos\out.tmp",
            TimeSpan.Zero, TimeSpan.FromSeconds(10), 1.5, 1.0,
            ClipExportMode.KeepSeparateAudioTracks, audioTrackCount: 1,
            reencodeVideo: false, hardwareVideoEncoder: null);

        Assert.Contains(singleAudio.ArgumentList, a => a == "[0:a:0]volume=1.5[aout]");
        Assert.DoesNotContain("-an", singleAudio.ArgumentList);

        ProcessStartInfo noAudio = ClipEditorService.BuildFfmpegStartInfo(
            "ffmpeg.exe", @"C:\Videos\clip.mp4", @"C:\Videos\out.tmp",
            TimeSpan.Zero, TimeSpan.FromSeconds(10), 1.0, 1.0,
            ClipExportMode.MixSingleTrackForDiscord, audioTrackCount: 0,
            reencodeVideo: false, hardwareVideoEncoder: null);

        Assert.Contains("-an", noAudio.ArgumentList);
        Assert.DoesNotContain("-filter_complex", noAudio.ArgumentList);
    }

    [Theory]
    [InlineData("h264_nvenc", "-rc", "vbr")]
    [InlineData("h264_amf", "-rc", "cqp")]
    [InlineData("h264_qsv", "-global_quality", "19")]
    [InlineData(null, "-crf", "18")]
    public void BuildFfmpegStartInfo_ReencodeVideo_ConfiguresExpectedCodecFlags(
        string? hwEncoder,
        string expectedFlag,
        string expectedValue)
    {
        ProcessStartInfo psi = ClipEditorService.BuildFfmpegStartInfo(
            "ffmpeg.exe", @"C:\Videos\clip.mp4", @"C:\Videos\out.tmp",
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), 1.0, 1.0,
            ClipExportMode.MixSingleTrackForDiscord, audioTrackCount: 0,
            reencodeVideo: true, hardwareVideoEncoder: hwEncoder);

        List<string> args = psi.ArgumentList.ToList();
        Assert.DoesNotContain("copy", args);
        Assert.Contains(hwEncoder ?? "libx264", args);

        int flagIndex = args.IndexOf(expectedFlag);
        Assert.True(flagIndex >= 0 && flagIndex + 1 < args.Count);
        Assert.Equal(expectedValue, args[flagIndex + 1]);

        if (hwEncoder == "h264_nvenc")
        {
            int bitrateIndex = args.IndexOf("-b:v");
            Assert.True(bitrateIndex >= 0 && bitrateIndex + 1 < args.Count);
            Assert.Equal("0", args[bitrateIndex + 1]);
        }
    }

    [Fact]
    public void TryParseFfmpegTime_ParsesValidStderrLinesAndRejectsInvalidOnes()
    {
        Assert.True(ClipEditorService.TryParseFfmpegTime(
            "frame=  360 fps=120 q=19.0 size=   4096kB time=00:01:23.45 bitrate= 402.1kbits/s speed=2.0x",
            out TimeSpan parsed));
        Assert.Equal(new TimeSpan(0, 0, 1, 23, 450), parsed);

        Assert.True(ClipEditorService.TryParseFfmpegTime(
            "size= 12345kB time=01:05:09.125 bitrate=1234.5kbits/s",
            out TimeSpan longParsed));
        Assert.Equal(new TimeSpan(0, 1, 5, 9, 125), longParsed);

        Assert.False(ClipEditorService.TryParseFfmpegTime("frame=  10 fps=0.0 q=0.0 size=0kB time=N/A", out _));
        Assert.False(ClipEditorService.TryParseFfmpegTime(string.Empty, out _));
    }

    [Fact]
    public void TryParseSha256Text_ValidatesHexFormatStrictly()
    {
        const string validHex = "b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd";
        Assert.True(ClipEditorService.TryParseSha256Text(validHex + "  ffmpeg-release-essentials.zip\n", out string parsed));
        Assert.Equal(validHex.ToUpperInvariant(), parsed);

        Assert.False(ClipEditorService.TryParseSha256Text("too-short", out _));
        Assert.False(ClipEditorService.TryParseSha256Text(new string('g', 64), out _));
        Assert.False(ClipEditorService.TryParseSha256Text(null, out _));
    }
}
