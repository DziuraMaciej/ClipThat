using System.Runtime.InteropServices;
using ClipThat.Engine.Audio;
using ClipThat.Engine.Encoding;
using ClipThat.Engine.Muxing;
using NAudio.Wave;
using Xunit;

namespace ClipThat.Tests;

public sealed class AudioAndCodecPipelineTests
{
    [Fact]
    public void DownmixToStereoSampleProvider_5Point1PreservesCenterLfeAndSurround()
    {
        // 5.1 layout: FL, FR, FC, LFE, RL, RR
        float[] input =
        {
            // Frame 0: center-only dialogue -> both L and R receive CenterWeight (0.7071)
            0f, 0f, 1.0f, 0f, 0f, 0f,
            // Frame 1: LFE + rear channels -> L gets LFE + RL, R gets LFE + RR
            0f, 0f, 0f, 1.0f, 0.5f, -0.5f
        };

        var source = new StaticFloatSampleProvider(input, channels: 6, sampleRate: 48000);
        var downmixer = new DualAudioCaptureService.AudioPcmConverter.DownmixToStereoSampleProvider(source);

        Assert.Equal(2, downmixer.WaveFormat.Channels);
        Assert.Equal(48000, downmixer.WaveFormat.SampleRate);

        float[] output = new float[4];
        int read = downmixer.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.InRange(output[0], 0.70f, 0.72f);
        Assert.InRange(output[1], 0.70f, 0.72f);
        Assert.True(output[2] > 0.6f, "Left channel should include LFE and Rear-Left");
        Assert.InRange(output[3], -0.05f, 0.05f); // LFE (0.35) + RR (-0.5 * 0.7071 ≈ -0.3535)
    }

    [Fact]
    public void DownmixToStereoSampleProvider_7Point1PreservesSideAndRearChannels()
    {
        // 7.1 layout: FL, FR, FC, LFE, RL, RR, SL, SR
        float[] input =
        {
            0f, 0f, 0f, 0f, 0.4f, 0f, 0.6f, 0f
        };

        var source = new StaticFloatSampleProvider(input, channels: 8, sampleRate: 48000);
        var downmixer = new DualAudioCaptureService.AudioPcmConverter.DownmixToStereoSampleProvider(source);

        float[] output = new float[2];
        int read = downmixer.Read(output, 0, output.Length);

        Assert.Equal(2, read);
        // (0.4 + 0.6) * 0.5 = 0.5 on Left, 0.0 on Right
        Assert.InRange(output[0], 0.49f, 0.51f);
        Assert.Equal(0f, output[1]);
    }

    [Fact]
    public void MixSamplesSoftClip_IsExactBelowKneeAndSmoothlyLimitsPeaks()
    {
        // Unity gain below ±28000 knee
        Assert.Equal((short)0, DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(0, 0));
        Assert.Equal((short)28000, DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(14000, 14000));
        Assert.Equal((short)-28000, DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(-14000, -14000));

        // Above knee: strictly increasing, never harsh clipping to 32767 at moderate overload
        short moderatePeak = DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(20000, 20000);
        short maxPeak = DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(short.MaxValue, short.MaxValue);
        Assert.InRange(moderatePeak, (short)28001, (short)32500);
        Assert.InRange(maxPeak, (short)(moderatePeak + 1), short.MaxValue);

        // Negative symmetry
        short negModeratePeak = DualAudioCaptureService.CombinedAudioMixPipeline.MixSamplesSoftClip(-20000, -20000);
        Assert.Equal((short)-moderatePeak, negModeratePeak);
    }

    [Fact]
    public void MfH264Encoder_ContainsAnnexBKeyframeNal_DetectsIdrWith3And4ByteStartCodes()
    {
        byte[] idr4Byte = { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84 };
        byte[] idr3Byte = { 0x00, 0x00, 0x01, 0x65, 0x88 };
        byte[] pFrameOnly = { 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x22 };
        byte[] spsPpsOnly = { 0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x00, 0x00, 0x01, 0x68, 0xEE };

        Assert.True(MfH264Encoder.ContainsAnnexBKeyframeNal(idr4Byte));
        Assert.True(MfH264Encoder.ContainsAnnexBKeyframeNal(idr3Byte));
        Assert.False(MfH264Encoder.ContainsAnnexBKeyframeNal(pFrameOnly));
        Assert.False(MfH264Encoder.ContainsAnnexBKeyframeNal(spsPpsOnly));
        Assert.False(MfH264Encoder.ContainsAnnexBKeyframeNal(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TryExtractAnnexBSpsPps_ExtractsSpsAndPpsFromKeyframeAndReturnsNullWhenIncomplete()
    {
        byte[] frameWithSpsPpsIdr =
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x28, // SPS (type 7)
            0x00, 0x00, 0x00, 0x01, 0x68, 0xEE, 0x3C, 0x80, // PPS (type 8)
            0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x00  // IDR (type 5)
        };

        byte[] expectedSpsPps = frameWithSpsPpsIdr[..16];

        byte[]? fromEncoder = MfH264Encoder.TryExtractAnnexBSpsPps(frameWithSpsPpsIdr);
        byte[]? fromMuxer = Mp4ReplayMuxer.TryExtractAnnexBSpsPps(frameWithSpsPpsIdr);

        Assert.NotNull(fromEncoder);
        Assert.NotNull(fromMuxer);
        Assert.Equal(expectedSpsPps, fromEncoder);
        Assert.Equal(expectedSpsPps, fromMuxer);

        byte[] spsOnly = { 0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x28 };
        byte[] pSliceOnly = { 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x22, 0x11 };
        Assert.Null(MfH264Encoder.TryExtractAnnexBSpsPps(spsOnly));
        Assert.Null(Mp4ReplayMuxer.TryExtractAnnexBSpsPps(spsOnly));
        Assert.Null(MfH264Encoder.TryExtractAnnexBSpsPps(pSliceOnly));
        Assert.Null(Mp4ReplayMuxer.TryExtractAnnexBSpsPps(pSliceOnly));
    }

    [Fact]
    public void AmfH264Encoder_AmfVariantMatchesX64AbiAndAdapterFilterRejectsNonAmd()
    {
        Assert.Equal(24, Marshal.SizeOf<AmfH264Encoder.AmfVariant>());

        var boolVar = AmfH264Encoder.AmfVariant.FromBool(true);
        Assert.Equal(AmfH264Encoder.AmfVariant.TypeBool, boolVar.Type);
        Assert.Equal((byte)1, boolVar.BoolValue);

        var intVar = AmfH264Encoder.AmfVariant.FromInt64(12_000_000);
        Assert.Equal(AmfH264Encoder.AmfVariant.TypeInt64, intVar.Type);
        Assert.Equal(12_000_000, intVar.Int64Value);

        var sizeVar = AmfH264Encoder.AmfVariant.FromSize(1920, 1080);
        Assert.Equal(AmfH264Encoder.AmfVariant.TypeSize, sizeVar.Type);
        Assert.Equal(1920, sizeVar.Width);
        Assert.Equal(1080, sizeVar.Height);

        var rateVar = AmfH264Encoder.AmfVariant.FromRate(60, 1);
        Assert.Equal(AmfH264Encoder.AmfVariant.TypeRate, rateVar.Type);
        Assert.Equal(60u, rateVar.Num);
        Assert.Equal(1u, rateVar.Den);

        Assert.False(AmfH264Encoder.ShouldTryForAdapter(null));
        Assert.False(AmfH264Encoder.ShouldTryForAdapter(""));
        Assert.False(AmfH264Encoder.ShouldTryForAdapter("NVIDIA GeForce RTX 4090"));
        Assert.False(AmfH264Encoder.ShouldTryForAdapter("Intel(R) Arc(TM) A770 Graphics"));
    }

    private sealed class StaticFloatSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public StaticFloatSampleProvider(float[] samples, int channels, int sampleRate)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
