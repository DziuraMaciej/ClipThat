using System.Runtime.InteropServices;
using ClipThat.Engine.Audio;
using Xunit;

namespace ClipThat.Tests;

public sealed class TrackPcmTimelineBufferTests
{
    private const int SampleRate = DualAudioCaptureService.TargetSampleRate; // 48000
    private const int Channels = DualAudioCaptureService.TargetChannels;     // 2

    private static long FramesToTicks100Ns(long frames)
        => (long)Math.Round(frames * (double)TimeSpan.TicksPerSecond / SampleRate);

    private static byte[] CreateConstantPcm(int frameCount, short leftValue, short rightValue)
    {
        var samples = new short[frameCount * Channels];
        for (int i = 0; i < frameCount; i++)
        {
            samples[i * 2] = leftValue;
            samples[i * 2 + 1] = rightValue;
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    [Fact]
    public void SequentialWriteAndReadPreservesSamplesAndUpdatesHasUnreadData()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        Assert.False(timeline.HasData);
        Assert.False(timeline.HasUnreadData);

        const int frames = 1024;
        byte[] pcm = CreateConstantPcm(frames, 1234, -5678);
        timeline.Write(pcm, startTimestamp100Ns: 0);

        Assert.True(timeline.HasData);
        Assert.True(timeline.HasUnreadData);
        Assert.Equal(0, timeline.StartFrame);
        Assert.Equal(frames, timeline.EndFrame);

        var output = new short[frames * Channels];
        timeline.ReadOrZero(0, frames, output);

        for (int i = 0; i < frames; i++)
        {
            Assert.Equal((short)1234, output[i * 2]);
            Assert.Equal((short)-5678, output[i * 2 + 1]);
        }

        Assert.False(timeline.HasUnreadData);
        Assert.Equal(timeline.EndFrame, timeline.StartFrame);
    }

    [Fact]
    public void SmallJitterSnapsContiguouslyWithoutGap()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        const int block = 1024;

        timeline.Write(CreateConstantPcm(block, 100, 200), FramesToTicks100Ns(0));
        // +2 frames jitter (within +-4 frame snap threshold)
        timeline.Write(CreateConstantPcm(block, 300, 400), FramesToTicks100Ns(block + 2));

        Assert.Equal(0, timeline.StartFrame);
        Assert.Equal(block * 2, timeline.EndFrame);

        var secondBlock = new short[block * Channels];
        timeline.ReadOrZero(block, block, secondBlock);
        Assert.Equal((short)300, secondBlock[0]);
        Assert.Equal((short)400, secondBlock[1]);
    }

    [Fact]
    public void ModerateGapZeroFillsMissingFrames()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        const int block = 512;
        const int gap = 512;

        timeline.Write(CreateConstantPcm(block, 111, 222), FramesToTicks100Ns(0));
        timeline.Write(CreateConstantPcm(block, 333, 444), FramesToTicks100Ns(block + gap));

        Assert.Equal(0, timeline.StartFrame);
        Assert.Equal(block * 2 + gap, timeline.EndFrame);

        var gapOut = new short[gap * Channels];
        timeline.ReadOrZero(block, gap, gapOut);
        Assert.All(gapOut, s => Assert.Equal((short)0, s));

        var secondOut = new short[block * Channels];
        timeline.ReadOrZero(block + gap, block, secondOut);
        Assert.Equal((short)333, secondOut[0]);
        Assert.Equal((short)444, secondOut[1]);
    }

    [Fact]
    public void LargeTimestampJumpResetsTimelineCleanly()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        const int block = 1024;
        long jumpFrame = SampleRate * 10L; // 10 seconds jump (> half of 2s capacity)

        timeline.Write(CreateConstantPcm(block, 10, 20), FramesToTicks100Ns(0));
        timeline.Write(CreateConstantPcm(block, 777, -888), FramesToTicks100Ns(jumpFrame));

        Assert.Equal(jumpFrame, timeline.StartFrame);
        Assert.Equal(jumpFrame + block, timeline.EndFrame);

        var outSamples = new short[block * Channels];
        timeline.ReadOrZero(jumpFrame, block, outSamples);
        Assert.Equal((short)777, outSamples[0]);
        Assert.Equal((short)-888, outSamples[1]);
    }

    [Fact]
    public void ResetClearsStateAndHasUnreadData()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        timeline.Write(CreateConstantPcm(1024, 50, 60), 0);

        Assert.True(timeline.HasData);
        Assert.True(timeline.HasUnreadData);

        timeline.Reset();

        Assert.False(timeline.HasData);
        Assert.False(timeline.HasUnreadData);
        Assert.Equal(0, timeline.StartFrame);
        Assert.Equal(0, timeline.EndFrame);
    }

    [Fact]
    public void CircularWrapAroundPreservesSamplesBeyondCapacity()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        const int block = 1024;
        var readBuf = new short[block * Channels];

        // 120 * 1024 = 122,880 frames (> 96,000-frame 2-second ring capacity)
        for (int i = 0; i < 120; i++)
        {
            short expectedLeft = (short)(100 + i);
            short expectedRight = (short)(-100 - i);
            long frameStart = (long)i * block;

            timeline.Write(CreateConstantPcm(block, expectedLeft, expectedRight), FramesToTicks100Ns(frameStart));
            timeline.ReadOrZero(frameStart, block, readBuf);

            Assert.Equal(expectedLeft, readBuf[0]);
            Assert.Equal(expectedRight, readBuf[1]);
            Assert.Equal(expectedLeft, readBuf[(block - 1) * 2]);
            Assert.Equal(expectedRight, readBuf[(block - 1) * 2 + 1]);
        }
    }

    [Fact]
    public void SlightlyBackwardTimestampSnapsWithoutCorruptingTimeline()
    {
        var timeline = new DualAudioCaptureService.CombinedAudioMixPipeline.TrackPcmTimelineBuffer();
        const int block = 1024;

        timeline.Write(CreateConstantPcm(block, 111, 222), FramesToTicks100Ns(0));
        // -2 frames jitter (within +-4 frame snap threshold)
        timeline.Write(CreateConstantPcm(block, 333, 444), FramesToTicks100Ns(block - 2));

        Assert.Equal(0, timeline.StartFrame);
        Assert.Equal(block * 2, timeline.EndFrame);

        var outSecond = new short[block * Channels];
        timeline.ReadOrZero(block, block, outSecond);
        Assert.Equal((short)333, outSecond[0]);
        Assert.Equal((short)444, outSecond[1]);
    }
}
