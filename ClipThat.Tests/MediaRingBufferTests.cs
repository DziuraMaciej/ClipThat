using System.Buffers;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using Xunit;

namespace ClipThat.Tests;

public sealed class MediaRingBufferTests
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private static EncodedPacket CreateVideoPacket(long timestampSeconds, bool isKeyframe, int size = 256, byte fill = 0xAA)
    {
        byte[] data = new byte[size];
        Array.Fill(data, fill);
        return new EncodedPacket(
            MediaTrackType.Video,
            data,
            size,
            timestampSeconds * TicksPerSecond,
            TicksPerSecond / 60,
            isKeyframe,
            returnToArrayPool: false);
    }

    private static EncodedPacket CreateAudioPacket(long timestampSeconds, MediaTrackType track = MediaTrackType.SystemAudio, int size = 128)
    {
        byte[] data = new byte[size];
        Array.Fill(data, (byte)0x55);
        return new EncodedPacket(
            track,
            data,
            size,
            timestampSeconds * TicksPerSecond,
            213_333,
            isKeyframe: true,
            returnToArrayPool: false);
    }

    [Fact]
    public void DropsLeadingNonKeyframeVideoPackets()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 1);

        buffer.AddPacket(CreateVideoPacket(0, isKeyframe: false));
        buffer.AddPacket(CreateVideoPacket(1, isKeyframe: false));

        Assert.Equal(0, buffer.PacketCount);
        Assert.Equal(0, buffer.BufferedBytes);

        buffer.AddPacket(CreateVideoPacket(2, isKeyframe: true, size: 512));
        buffer.AddPacket(CreateVideoPacket(3, isKeyframe: false, size: 128));

        Assert.Equal(2, buffer.PacketCount);
        Assert.Equal(640, buffer.BufferedBytes);
    }

    [Fact]
    public void EvictsOldestGopWhenWindowExceededAndKeepsLeadingVideoKeyframe()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 1); // 60 s window

        // GOP 1: t = 0..29s
        buffer.AddPacket(CreateVideoPacket(0, isKeyframe: true, size: 1000));
        buffer.AddPacket(CreateVideoPacket(10, isKeyframe: false, size: 400));
        buffer.AddPacket(CreateAudioPacket(15, MediaTrackType.SystemAudio, size: 200));

        // GOP 2: t = 30..55s (still within 60s window -> nothing evicted yet)
        buffer.AddPacket(CreateVideoPacket(30, isKeyframe: true, size: 1000));
        buffer.AddPacket(CreateVideoPacket(55, isKeyframe: false, size: 400));
        Assert.Equal(5, buffer.PacketCount);

        // GOP 3: t = 65s -> (65 - 0) > 60s, so GOP 1 is evicted cleanly up to GOP 2's keyframe at t = 30s
        buffer.AddPacket(CreateVideoPacket(65, isKeyframe: true, size: 1000));
        Assert.Equal(3, buffer.PacketCount);

        using EncodedPacketSnapshot snapshot = buffer.SnapshotPackets();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(MediaTrackType.Video, snapshot[0].TrackType);
        Assert.True(snapshot[0].IsKeyframe);
        Assert.Equal(30 * TicksPerSecond, snapshot[0].Timestamp100Ns);

        long expectedBytes = snapshot.TotalBytes;
        Assert.Equal(expectedBytes, buffer.BufferedBytes);
    }

    [Fact]
    public void StaticDesktopEvictsExpiredAudioWhenSingleVideoKeyframePresent()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 1); // 60 s window

        // Single video keyframe at t = 0s (simulates completely static desktop)
        buffer.AddPacket(CreateVideoPacket(0, isKeyframe: true, size: 1024));

        // Stream 120 seconds of audio packets (1 packet per second) with no new video frames
        for (int sec = 1; sec <= 120; sec++)
        {
            buffer.AddPacket(CreateAudioPacket(sec, MediaTrackType.SystemAudio, size: 256));
        }

        using EncodedPacketSnapshot snapshot = buffer.SnapshotPackets();

        // The single video keyframe must remain at index 0
        Assert.Equal(MediaTrackType.Video, snapshot[0].TrackType);
        Assert.True(snapshot[0].IsKeyframe);
        Assert.Equal(0L, snapshot[0].Timestamp100Ns);

        // Expired audio older than (120 - 60 = 60s) must have been trimmed to prevent unbounded RAM growth
        var audioPackets = snapshot.Where(p => p.TrackType == MediaTrackType.SystemAudio).ToList();
        Assert.NotEmpty(audioPackets);
        Assert.All(audioPackets, p => Assert.True(p.Timestamp100Ns >= 60 * TicksPerSecond));
        Assert.True(audioPackets.Count <= 61);
    }

    [Fact]
    public void SnapshotPacketsRetainsIndependentPayloadsAfterBufferClear()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 1);
        buffer.AddPacket(CreateVideoPacket(0, isKeyframe: true, size: 64, fill: 0x3C));
        buffer.AddPacket(CreateAudioPacket(1, MediaTrackType.MicrophoneAudio, size: 32));

        using EncodedPacketSnapshot snapshot = buffer.SnapshotPackets();
        buffer.Clear();

        Assert.Equal(0, buffer.PacketCount);
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(64, snapshot[0].Length);
        Assert.Equal(0x3C, snapshot[0].Buffer[0]);
        Assert.Equal(0x3C, snapshot[0].Buffer[63]);
    }

    [Fact]
    public async Task ConcurrentAddAndDisposeDoesNotThrowObjectDisposedException()
    {
        var buffer = new MediaRingBuffer(bufferDurationMinutes: 1);
        using var startGate = new ManualResetEventSlim(false);
        Exception? workerException = null;

        Task[] writers = Enumerable.Range(0, 4).Select(workerId => Task.Run(() =>
        {
            startGate.Wait();
            try
            {
                for (int i = 0; i < 400; i++)
                {
                    buffer.AddPacket(CreateVideoPacket(i, isKeyframe: i % 10 == 0, size: 64));
                    buffer.AddPacket(CreateAudioPacket(i, MediaTrackType.SystemAudio, size: 32));
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerException, ex, null);
            }
        })).ToArray();

        startGate.Set();
        await Task.Delay(5);
        buffer.Dispose();

        await Task.WhenAll(writers);

        Assert.Null(workerException);
        Assert.Equal(0, buffer.PacketCount);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public unsafe void CreateFromNativeMemory_UsesSlabPoolAndDedicatedSlabsSafelyAcrossSnapshotAndClear()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 2);

        const int smallCount = 250;
        const int smallSize = 16_384; // 250 * 16 KB = ~4 MB -> spans multiple 2 MiB slabs
        const int oversizedSize = 600_000; // > 512 KB -> dedicated slab path

        void* smallSrc = NativeMemory.Alloc(smallSize);
        void* largeSrc = NativeMemory.Alloc(oversizedSize);
        try
        {
            for (int i = 0; i < smallCount; i++)
            {
                byte fill = (byte)((i * 37) & 0xFF);
                new Span<byte>(smallSrc, smallSize).Fill(fill);
                EncodedPacket packet = EncodedPacket.CreateFromNativeMemory(
                    MediaTrackType.Video,
                    (nint)smallSrc,
                    smallSize,
                    timestamp100Ns: i * (TicksPerSecond / 10),
                    duration100Ns: TicksPerSecond / 10,
                    isKeyframe: i % 30 == 0);
                buffer.AddPacket(packet);
            }

            new Span<byte>(largeSrc, oversizedSize).Fill(0xAB);
            EncodedPacket largeKeyframe = EncodedPacket.CreateFromNativeMemory(
                MediaTrackType.Video,
                (nint)largeSrc,
                oversizedSize,
                timestamp100Ns: smallCount * (TicksPerSecond / 10),
                duration100Ns: TicksPerSecond / 10,
                isKeyframe: true);
            buffer.AddPacket(largeKeyframe);
        }
        finally
        {
            NativeMemory.Free(smallSrc);
            NativeMemory.Free(largeSrc);
        }

        using EncodedPacketSnapshot snapshot = buffer.SnapshotPackets();
        buffer.Clear();

        Assert.Equal(0, buffer.PacketCount);
        Assert.Equal(smallCount + 1, snapshot.Count);

        for (int i = 0; i < smallCount; i++)
        {
            byte expected = (byte)((i * 37) & 0xFF);
            ReadOnlySpan<byte> data = snapshot[i].Data;
            Assert.Equal(smallSize, data.Length);
            Assert.Equal(expected, data[0]);
            Assert.Equal(expected, data[^1]);
        }

        ReadOnlySpan<byte> largeData = snapshot[^1].Data;
        Assert.Equal(oversizedSize, largeData.Length);
        Assert.Equal((byte)0xAB, largeData[0]);
        Assert.Equal((byte)0xAB, largeData[^1]);
    }

    [Fact]
    public void AddAudioPacket_ThrowsOnInvalidLengthAndInvalidTrackTypeWithoutLeaking()
    {
        using var buffer = new MediaRingBuffer(bufferDurationMinutes: 1);
        byte[] rented1 = ArrayPool<byte>.Shared.Rent(64);
        Assert.Throws<ArgumentException>(() =>
            buffer.AddAudioPacket(MediaTrackType.Video, rented1, 32, 0, 213_333));

        byte[] rented2 = ArrayPool<byte>.Shared.Rent(64);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            buffer.AddAudioPacket(MediaTrackType.SystemAudio, rented2, rented2.Length + 10, 0, 213_333));
    }
}
