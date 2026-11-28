using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ClipThat.Models;
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
        Assert.Contains("-nostdin", args);
        int ssIndex = args.IndexOf("-ss");
        int inputIndex = args.IndexOf("-i");
        int durationIndex = args.IndexOf("-t");
        int videoCodecIndex = args.IndexOf("-c:v");
        int audioCodecIndex = args.IndexOf("-c:a");

        Assert.True(ssIndex >= 0 && inputIndex > ssIndex, "-ss should precede -i for fast input seeking");
        Assert.Equal("00:00:12.500", args[ssIndex + 1]);
        Assert.True(durationIndex > inputIndex, "-t should follow -i");
        Assert.Equal("00:00:30.000", args[durationIndex + 1]);
        Assert.True(videoCodecIndex >= 0 && args[videoCodecIndex + 1] == "copy");
        Assert.True(audioCodecIndex >= 0 && args[audioCodecIndex + 1] == "aac");
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

    [Fact]
    public void BuildFfmpegStartInfo_UsesInvariantDecimalSeparatorUnderPolishCulture()
    {
        CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            ProcessStartInfo psi = ClipEditorService.BuildFfmpegStartInfo(
                "ffmpeg.exe", @"C:\Videos\clip.mp4", @"C:\Videos\out.tmp",
                TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5.25),
                systemVolume: 1.25, microphoneVolume: 0.75,
                ClipExportMode.MixSingleTrackForDiscord, audioTrackCount: 2,
                reencodeVideo: false, hardwareVideoEncoder: null);

            string filterArg = Assert.Single(psi.ArgumentList, a => a.Contains("amix=", StringComparison.Ordinal));
            Assert.Contains("volume=1.25", filterArg, StringComparison.Ordinal);
            Assert.Contains("volume=0.75", filterArg, StringComparison.Ordinal);
            Assert.DoesNotContain("1,25", filterArg, StringComparison.Ordinal);
            Assert.DoesNotContain("0,75", filterArg, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task ScanClipsAsync_SortsNewestFirstAndRefreshesMetadataWhenFileChanges()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClipEditorScanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string olderPath = Path.Combine(tempDir, "00001028112026.mp4");
            string newerPath = Path.Combine(tempDir, "30001028112026.mp4");
            File.WriteAllBytes(olderPath, CreateMinimalMp4(durationMs: 5_000, audioTracks: 1));
            File.WriteAllBytes(newerPath, CreateMinimalMp4(durationMs: 12_000, audioTracks: 3));

            using var service = new ClipEditorService();
            IReadOnlyList<ClipItem> firstScan = await service.ScanClipsAsync(tempDir);

            Assert.Equal(2, firstScan.Count);
            Assert.Equal("30001028112026.mp4", firstScan[0].FileName);
            Assert.Equal(TimeSpan.FromSeconds(12), firstScan[0].Duration);
            Assert.Equal(3, firstScan[0].AudioTrackCount);
            Assert.Equal("00001028112026.mp4", firstScan[1].FileName);
            Assert.Equal(TimeSpan.FromSeconds(5), firstScan[1].Duration);
            Assert.Equal(1, firstScan[1].AudioTrackCount);

            // Modify the older file (different length + timestamp) and delete the newer file.
            File.WriteAllBytes(olderPath, CreateMinimalMp4(durationMs: 25_000, audioTracks: 2));
            File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddSeconds(5));
            File.Delete(newerPath);

            IReadOnlyList<ClipItem> secondScan = await service.ScanClipsAsync(tempDir);
            ClipItem updated = Assert.Single(secondScan);
            Assert.Equal("00001028112026.mp4", updated.FileName);
            Assert.Equal(TimeSpan.FromSeconds(25), updated.Duration);
            Assert.Equal(2, updated.AudioTrackCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ExportTrimmedClipAsync_ValidatesArgumentsBeforeLaunchingFfmpeg()
    {
        using var service = new ClipEditorService();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ExportTrimmedClipAsync(null!, TimeSpan.Zero, TimeSpan.FromSeconds(5)));

        var missingInput = new ClipExportOptions { InputFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4") };
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExportTrimmedClipAsync(missingInput, TimeSpan.Zero, TimeSpan.FromSeconds(5)));

        string tempFile = Path.Combine(Path.GetTempPath(), "clip_validate_" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(tempFile, Array.Empty<byte>());
        try
        {
            var validPath = new ClipExportOptions { InputFilePath = tempFile };
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ExportTrimmedClipAsync(validPath, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ExportTrimmedClipAsync(validPath, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2)));
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    private static byte[] CreateMinimalMp4(uint durationMs, int audioTracks)
    {
        var mvhdPayload = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(12, 4), 1000u);
        BinaryPrimitives.WriteUInt32BigEndian(mvhdPayload.AsSpan(16, 4), durationMs);

        var children = new List<byte[]>
        {
            WrapBox("mvhd", mvhdPayload),
            CreateTrakBox("vide")
        };
        for (int i = 0; i < audioTracks; i++)
        {
            children.Add(CreateTrakBox("soun"));
        }

        byte[] ftyp = WrapBox("ftyp", new byte[8]);
        byte[] moov = WrapBox("moov", children.ToArray());
        var result = new byte[ftyp.Length + moov.Length];
        Buffer.BlockCopy(ftyp, 0, result, 0, ftyp.Length);
        Buffer.BlockCopy(moov, 0, result, ftyp.Length, moov.Length);
        return result;
    }

    private static byte[] CreateTrakBox(string handler)
    {
        var hdlrPayload = new byte[24];
        Encoding.ASCII.GetBytes(handler, hdlrPayload.AsSpan(8, 4));
        return WrapBox("trak", WrapBox("mdia", WrapBox("hdlr", hdlrPayload)));
    }

    private static byte[] WrapBox(string fourCc, params byte[][] children)
    {
        int payloadLen = children.Sum(c => c.Length);
        var box = new byte[8 + payloadLen];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(fourCc, box.AsSpan(4, 4));
        int offset = 8;
        foreach (byte[] child in children)
        {
            Buffer.BlockCopy(child, 0, box, offset, child.Length);
            offset += child.Length;
        }

        return box;
    }
}
