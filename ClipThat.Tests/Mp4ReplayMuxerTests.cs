using System.Buffers.Binary;
using System.IO;
using System.Text;
using ClipThat.Engine.Buffer;
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

    [Fact]
    public void PatchAudioTrackSelectionFlags_LeavesSingleAudioTrackUntouched()
    {
        string path = Path.Combine(_tempDir, "single_audio.mp4");
        byte[] moov = WrapBox("moov",
            BuildMvhdV0(1000, 5000),
            BuildTrak("vide", tkhdVersion: 0),
            BuildTrak("soun", tkhdVersion: 0));
        File.WriteAllBytes(path, Concat(WrapBox("ftyp", new byte[8]), moov));

        Mp4ReplayMuxer.PatchAudioTrackSelectionFlags(path);

        var tkhdList = FindAllTkhdPayloads(File.ReadAllBytes(path));
        Assert.Equal(2, tkhdList.Count);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x03 }, tkhdList[1][1..4]);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(tkhdList[1].AsSpan(34, 2)));
    }

    [Fact]
    public void WriteMpegTsIntermediate_PreservesExactVfrPtsAndProducesValid188BytePackets()
    {
        string tsPath = Path.Combine(_tempDir, "vfr_replay.ts");
        byte[] spsPps =
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x28,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xEE, 0x3C, 0x80
        };
        byte[] idrPayload = { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x11, 0x22 };
        byte[] pSlicePayload = { 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x33, 0x44 };
        byte[] aacPayload = { 0x21, 0x10, 0x04, 0x60, 0x8C, 0x1C };

        long baseTs = 10_000_000L; // 1.0 s base offset
        using var v0 = new EncodedPacket(MediaTrackType.Video, idrPayload, idrPayload.Length, baseTs, 166_667, isKeyframe: true, returnToArrayPool: false);
        using var a0 = new EncodedPacket(MediaTrackType.SystemAudio, aacPayload, aacPayload.Length, baseTs + 5_000_000L, 213_333, isKeyframe: true, returnToArrayPool: false);
        using var v1 = new EncodedPacket(MediaTrackType.Video, pSlicePayload, pSlicePayload.Length, baseTs + 25_000_000L, 166_667, isKeyframe: false, returnToArrayPool: false);

        var ordered = new List<Mp4ReplayMuxer.IndexedPacket>
        {
            new(v0, 0),
            new(a0, 1),
            new(v1, 2)
        };

        int audioStreamCount = Mp4ReplayMuxer.WriteMpegTsIntermediate(
            tsPath,
            ordered,
            baseTimestamp100Ns: baseTs,
            videoSequenceHeader: spsPps,
            hasSystemAudio: true,
            hasMicrophoneAudio: false,
            includeCombinedMix: false);

        Assert.Equal(1, audioStreamCount);

        byte[] tsBytes = File.ReadAllBytes(tsPath);
        Assert.True(tsBytes.Length >= 188 * 5);
        Assert.Equal(0, tsBytes.Length % 188);

        var videoPtsValues = new List<long>();
        var audioPtsValues = new List<long>();

        for (int offset = 0; offset < tsBytes.Length; offset += 188)
        {
            Assert.Equal((byte)0x47, tsBytes[offset]);

            bool pusi = (tsBytes[offset + 1] & 0x40) != 0;
            ushort pid = (ushort)(((tsBytes[offset + 1] & 0x1F) << 8) | tsBytes[offset + 2]);
            byte afc = (byte)((tsBytes[offset + 3] >> 4) & 0x03);

            if (offset == 0)
            {
                Assert.Equal((ushort)0x0000, pid); // PAT
                continue;
            }

            if (offset == 188)
            {
                Assert.Equal((ushort)0x1000, pid); // PMT
                continue;
            }

            if (!pusi || (pid != 0x0100 && pid != 0x0101))
            {
                continue;
            }

            int payloadStart = offset + 4;
            if ((afc & 0x02) != 0)
            {
                int adaptationLen = tsBytes[payloadStart];
                payloadStart += 1 + adaptationLen;
            }

            // PES start code 00 00 01 + stream_id + ... + 5-byte PTS at payloadStart + 9
            Assert.Equal((byte)0x00, tsBytes[payloadStart]);
            Assert.Equal((byte)0x00, tsBytes[payloadStart + 1]);
            Assert.Equal((byte)0x01, tsBytes[payloadStart + 2]);

            long pts90k = DecodePts90k(tsBytes.AsSpan(payloadStart + 9, 5));
            if (pid == 0x0100)
            {
                videoPtsValues.Add(pts90k);
            }
            else
            {
                audioPtsValues.Add(pts90k);
            }
        }

        // Exact VFR spacing: v0 at 0.0 s (0), v1 at +2.5 s (225000 ticks @ 90 kHz), audio at +0.5 s (45000 ticks @ 90 kHz)
        Assert.Equal(new long[] { 0L, 225_000L }, videoPtsValues);
        Assert.Equal(new long[] { 45_000L }, audioPtsValues);
    }

    private static long DecodePts90k(ReadOnlySpan<byte> ptsBytes)
    {
        long p32_30 = (ptsBytes[0] >> 1) & 0x07;
        long p29_22 = ptsBytes[1];
        long p21_15 = (ptsBytes[2] >> 1) & 0x7F;
        long p14_7 = ptsBytes[3];
        long p6_0 = (ptsBytes[4] >> 1) & 0x7F;
        return (p32_30 << 30) | (p29_22 << 22) | (p21_15 << 15) | (p14_7 << 7) | p6_0;
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
