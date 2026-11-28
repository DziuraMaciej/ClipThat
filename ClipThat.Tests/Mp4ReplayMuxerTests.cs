using System.Buffers.Binary;
using System.IO;
using System.Text;
using ClipThat.Engine.Muxing;
using Xunit;

namespace ClipThat.Tests;

public sealed class Mp4ReplayMuxerTests : IDisposable
{
    private readonly string _tempDir;

    public Mp4ReplayMuxerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClipThatTests_" + Guid.NewGuid().ToString("N"));
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
    public void TryReadMp4Metadata_ReadsVersion0DurationAndAudioTrackCount()
    {
        string path = Path.Combine(_tempDir, "v0.mp4");
        byte[] mvhd = BuildMvhdV0(timescale: 1000, duration: 12_500); // 12.5 s
        byte[] moov = WrapBox("moov",
            mvhd,
            BuildTrak("vide", tkhdVersion: 0),
            BuildTrak("soun", tkhdVersion: 0),
            BuildTrak("soun", tkhdVersion: 0),
            BuildTrak("soun", tkhdVersion: 0));
        File.WriteAllBytes(path, Concat(WrapBox("ftyp", new byte[8]), moov));

        bool ok = Mp4ReplayMuxer.TryReadMp4Metadata(path, out TimeSpan duration, out int audioTracks);

        Assert.True(ok);
        Assert.Equal(TimeSpan.FromSeconds(12.5), duration);
        Assert.Equal(3, audioTracks);
    }

    [Fact]
    public void TryReadMp4Metadata_ReadsVersion1Duration()
    {
        string path = Path.Combine(_tempDir, "v1.mp4");
        byte[] mvhd = BuildMvhdV1(timescale: 48_000, duration: 48_000UL * 300UL); // 300 s
        byte[] moov = WrapBox("moov",
            mvhd,
            BuildTrak("vide", tkhdVersion: 1),
            BuildTrak("soun", tkhdVersion: 1));
        File.WriteAllBytes(path, Concat(WrapBox("ftyp", new byte[8]), moov));

        bool ok = Mp4ReplayMuxer.TryReadMp4Metadata(path, out TimeSpan duration, out int audioTracks);

        Assert.True(ok);
        Assert.Equal(TimeSpan.FromSeconds(300), duration);
        Assert.Equal(1, audioTracks);
    }

    [Fact]
    public void TryReadMp4Metadata_IgnoresUnknownMaxValueDurationWithoutOverflow()
    {
        string pathV0 = Path.Combine(_tempDir, "unknown_v0.mp4");
        byte[] moovV0 = WrapBox("moov", BuildMvhdV0(timescale: 1000, duration: uint.MaxValue), BuildTrak("soun", 0));
        File.WriteAllBytes(pathV0, Concat(WrapBox("ftyp", new byte[8]), moovV0));

        Assert.True(Mp4ReplayMuxer.TryReadMp4Metadata(pathV0, out TimeSpan durV0, out int tracksV0));
        Assert.Equal(TimeSpan.Zero, durV0);
        Assert.Equal(1, tracksV0);

        string pathV1 = Path.Combine(_tempDir, "unknown_v1.mp4");
        byte[] moovV1 = WrapBox("moov", BuildMvhdV1(timescale: 1, duration: ulong.MaxValue), BuildTrak("soun", 1));
        File.WriteAllBytes(pathV1, Concat(WrapBox("ftyp", new byte[8]), moovV1));

        Assert.True(Mp4ReplayMuxer.TryReadMp4Metadata(pathV1, out TimeSpan durV1, out int tracksV1));
        Assert.Equal(TimeSpan.Zero, durV1);
        Assert.Equal(1, tracksV1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PatchAudioTrackSelectionFlags_SetsAlternateGroupAndEnablesOnlyLastAudioTrack(int tkhdVersion)
    {
        string path = Path.Combine(_tempDir, $"patch_v{tkhdVersion}.mp4");
        byte[] videoTrak = BuildTrak("vide", tkhdVersion);
        byte[] audioTrak0 = BuildTrak("soun", tkhdVersion);
        byte[] audioTrak1 = BuildTrak("soun", tkhdVersion);
        byte[] audioTrak2 = BuildTrak("soun", tkhdVersion);

        byte[] moov = WrapBox("moov",
            BuildMvhdV0(1000, 5000),
            videoTrak,
            audioTrak0,
            audioTrak1,
            audioTrak2);

        File.WriteAllBytes(path, Concat(WrapBox("ftyp", new byte[8]), moov));

        Mp4ReplayMuxer.PatchAudioTrackSelectionFlags(path);

        byte[] patched = File.ReadAllBytes(path);
        var tkhdList = FindAllTkhdPayloads(patched);
        Assert.Equal(4, tkhdList.Count);

        int altGroupRelOffset = tkhdVersion == 0 ? 34 : 46;

        // Track 0 (video): untouched (flags = 0x000003, alternate_group = 0)
        Assert.Equal(new byte[] { 0x00, 0x00, 0x03 }, tkhdList[0][1..4]);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(tkhdList[0].AsSpan(altGroupRelOffset, 2)));

        // Audio tracks 0 and 1: disabled-by-default (0x000002), alternate_group = 1
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02 }, tkhdList[1][1..4]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(tkhdList[1].AsSpan(altGroupRelOffset, 2)));

        Assert.Equal(new byte[] { 0x00, 0x00, 0x02 }, tkhdList[2][1..4]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(tkhdList[2].AsSpan(altGroupRelOffset, 2)));

        // Last audio track (CombinedMix): enabled (0x000003), alternate_group = 1
        Assert.Equal(new byte[] { 0x00, 0x00, 0x03 }, tkhdList[3][1..4]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(tkhdList[3].AsSpan(altGroupRelOffset, 2)));
    }

    private static byte[] BuildMvhdV0(uint timescale, uint duration)
    {
        var payload = new byte[24];
        payload[0] = 0; // version 0
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), duration);
        return WrapBox("mvhd", payload);
    }

    private static byte[] BuildMvhdV1(uint timescale, ulong duration)
    {
        var payload = new byte[36];
        payload[0] = 1; // version 1
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), timescale);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(24, 8), duration);
        return WrapBox("mvhd", payload);
    }

    private static byte[] BuildTrak(string handlerFourCc, int tkhdVersion)
    {
        int tkhdPayloadLen = tkhdVersion == 0 ? 84 : 96;
        var tkhdPayload = new byte[tkhdPayloadLen];
        tkhdPayload[0] = (byte)tkhdVersion;
        // Start with default enabled flags 0x000003 and alternate_group = 0
        tkhdPayload[1] = 0x00;
        tkhdPayload[2] = 0x00;
        tkhdPayload[3] = 0x03;

        var hdlrPayload = new byte[24];
        Encoding.ASCII.GetBytes(handlerFourCc, hdlrPayload.AsSpan(8, 4));

        byte[] tkhd = WrapBox("tkhd", tkhdPayload);
        byte[] mdia = WrapBox("mdia", WrapBox("hdlr", hdlrPayload));
        return WrapBox("trak", tkhd, mdia);
    }

    private static byte[] WrapBox(string fourCc, params byte[][] children)
    {
        int payloadLen = children.Sum(c => c.Length);
        var box = new byte[8 + payloadLen];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(fourCc, box.AsSpan(4, 4));
        int pos = 8;
        foreach (byte[] child in children)
        {
            Buffer.BlockCopy(child, 0, box, pos, child.Length);
            pos += child.Length;
        }

        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, pos, part.Length);
            pos += part.Length;
        }

        return output;
    }

    private static List<byte[]> FindAllTkhdPayloads(byte[] mp4)
    {
        var results = new List<byte[]>();
        Scan(mp4, 0, mp4.Length, results);
        return results;

        static void Scan(byte[] data, int start, int end, List<byte[]> acc)
        {
            int offset = start;
            while (offset + 8 <= end)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                if (size < 8 || offset + size > end)
                {
                    break;
                }

                string type = Encoding.ASCII.GetString(data, offset + 4, 4);
                if (type == "tkhd")
                {
                    acc.Add(data[(offset + 8)..(offset + size)]);
                }
                else if (type is "moov" or "trak")
                {
                    Scan(data, offset + 8, offset + size, acc);
                }

                offset += size;
            }
        }
    }
}
