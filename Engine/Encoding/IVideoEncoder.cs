using Vortice.Direct3D11;

namespace ClipThat.Engine.Encoding;

/// <summary>
/// Common contract for GPU-accelerated and software H.264 replay encoders
/// (native AMD AMF zero-copy, Media Foundation NVENC/QSV/AMF MFT, and Microsoft Software H.264 MFT).
/// </summary>
public interface IVideoEncoder : IDisposable
{
    string EncoderName { get; }

    EncoderNegotiationTier ActiveTier { get; }

    bool IsHardwareAccelerated { get; }

    bool UsesGpuZeroCopyInput { get; }

    bool IsAsyncMft { get; }

    int OutputWidth { get; }

    int OutputHeight { get; }

    int TargetFps { get; }

    uint TargetBitrateBps { get; }

    int SubmittedFrameCount { get; }

    int EncodedPacketCount { get; }

    int EncodedKeyframeCount { get; }

    int NeedInputEventCount { get; }

    int HaveOutputEventCount { get; }

    bool HasEmittedKeyframe { get; }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="texture"/> is still referenced in flight
    /// by the encoder and must not be overwritten by the GPU surface pool.
    /// </summary>
    bool IsTextureInUse(ID3D11Texture2D texture);

    /// <summary>
    /// Submits a single NV12 (or BGRA fallback) texture to the encoder.
    /// </summary>
    bool EncodeFrame(ID3D11Texture2D frameTexture, long timestamp100Ns, long duration100Ns, bool forceKeyFrame = false);
}
