using ClipThat.Engine.Diagnostics;
using ClipThat.Models;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ClipThat.Engine.Video;

/// <summary>
/// Hardware <see cref="ID3D11VideoProcessor"/> pipeline bound to the monitor-matched <see cref="ID3D11Device"/>
/// that scales incoming monitor-sized BGRA textures and converts them directly on the GPU into reusable
/// even-dimensioned NV12 surfaces across NVIDIA, AMD, and Intel GPUs, with automatic CPU staging/conversion
/// fallback when software MFT encoding or a non-video driver is active.
/// </summary>
public sealed class GpuVideoProcessor : IDisposable
{
    private const int OutputSurfacePoolSize = 8;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediateContext;
    private readonly bool _ownsDevice;
    private readonly ID3D11VideoDevice? _videoDevice;
    private readonly ID3D11VideoContext? _videoContext;
    private readonly ID3D11Texture2D[] _nv12OutputTextures;
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView?[] _nv12OutputViews;
    private ID3D11Texture2D? _fallbackBgraInputTexture;
    private ID3D11Texture2D? _cpuStagingNv12Texture;
    private ID3D11Texture2D? _cpuStagingBgraTexture;

    private uint _configuredInputWidth;
    private uint _configuredInputHeight;
    private uint _outputFrameIndex;
    private int _nextPoolIndex;
    private int _disposed;

    public GpuVideoProcessor(
        ID3D11Device device,
        ID3D11DeviceContext immediateContext,
        ResolutionPreset resolutionPreset,
        int targetFps,
        string? adapterName = null,
        Luid adapterLuid = default,
        bool ownsDevice = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(immediateContext);

        (int rawWidth, int rawHeight) = GetTargetDimensions(resolutionPreset);
        OutputWidth = Math.Max(2, rawWidth & ~1);
        OutputHeight = Math.Max(2, rawHeight & ~1);
        TargetFps = targetFps == 30 ? 30 : 60;
        AdapterName = string.IsNullOrWhiteSpace(adapterName) ? "Unknown GPU" : adapterName.Trim();
        AdapterLuid = adapterLuid;

        _device = device;
        _immediateContext = immediateContext;
        _ownsDevice = ownsDevice;
        _nv12OutputTextures = new ID3D11Texture2D[OutputSurfacePoolSize];
        _nv12OutputViews = new ID3D11VideoProcessorOutputView?[OutputSurfacePoolSize];

        try
        {
            using ID3D11Multithread? multithread = _device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);

            _videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            _videoContext = _immediateContext.QueryInterfaceOrNull<ID3D11VideoContext>();

            if (_videoDevice is not null && _videoContext is not null)
            {
                Texture2DDescription nv12Desc = new()
                {
                    Width = (uint)OutputWidth,
                    Height = (uint)OutputHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };

                try
                {
                    try
                    {
                        for (int i = 0; i < OutputSurfacePoolSize; i++)
                        {
                            _nv12OutputTextures[i] = _device.CreateTexture2D(nv12Desc);
                        }
                    }
                    catch (SharpGenException)
                    {
                        ReleaseNv12Textures();
                        nv12Desc.BindFlags = BindFlags.RenderTarget;
                        for (int i = 0; i < OutputSurfacePoolSize; i++)
                        {
                            _nv12OutputTextures[i] = _device.CreateTexture2D(nv12Desc);
                        }
                    }

                    SupportsHardwareVideoProcessor = true;
                }
                catch (Exception ex)
                {
                    EngineLogger.Warn(
                        "GpuVideoProcessor",
                        $"GPU \"{AdapterName}\" failed to allocate NV12 VideoProcessor surfaces ({OutputWidth}x{OutputHeight}); falling back to CPU BGRA->NV12 conversion.",
                        ex);
                    ReleaseNv12Textures();
                    SupportsHardwareVideoProcessor = false;
                }
            }
            else
            {
                EngineLogger.Warn(
                    "GpuVideoProcessor",
                    $"GPU \"{AdapterName}\" does not expose ID3D11VideoDevice/ID3D11VideoContext; falling back to CPU BGRA->NV12 conversion.");
                SupportsHardwareVideoProcessor = false;
            }
        }
        catch
        {
            DisposeInternal();
            throw;
        }
    }

    public ID3D11Device Device => _device;

    public ID3D11DeviceContext ImmediateContext => _immediateContext;

    public string AdapterName { get; }

    public Luid AdapterLuid { get; }

    public bool SupportsHardwareVideoProcessor { get; private set; }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public int TargetFps { get; }

    /// <summary>
    /// Maps each supported <see cref="ResolutionPreset"/> to guaranteed even NV12/H.264 target dimensions.
    /// </summary>
    public static (int Width, int Height) GetTargetDimensions(ResolutionPreset preset)
    {
        (int w, int h) = preset switch
        {
            ResolutionPreset.P240 => (426, 240),
            ResolutionPreset.P360 => (640, 360),
            ResolutionPreset.P480 => (854, 480),
            ResolutionPreset.P720 => (1280, 720),
            ResolutionPreset.P1080 => (1920, 1080),
            _ => (1920, 1080)
        };

        return (w & ~1, h & ~1);
    }

    /// <summary>
    /// Scales and color-converts an incoming <see cref="Format.B8G8R8A8_UNorm"/> GPU texture
    /// directly on the GPU into a reusable pooled <see cref="Format.NV12"/> <see cref="ID3D11Texture2D"/>.
    /// When the active adapter does not support <see cref="ID3D11VideoProcessor"/>, returns the
    /// BGRA texture unchanged for CPU-side NV12 conversion inside <see cref="CopyNv12ToMediaBuffer"/>.
    /// </summary>
    public ID3D11Texture2D ProcessFrame(
        ID3D11Texture2D inputBgraTexture,
        ID3D11Texture2D? busyTexture1 = null,
        ID3D11Texture2D? busyTexture2 = null)
    {
        ArgumentNullException.ThrowIfNull(inputBgraTexture);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Texture2DDescription inputDesc = inputBgraTexture.Description;
        if (inputDesc.Width == 0 || inputDesc.Height == 0)
        {
            throw new InvalidOperationException("Przechwycona tekstura GPU ma zerowe wymiary.");
        }

        if (!SupportsHardwareVideoProcessor || _videoDevice is null || _videoContext is null)
        {
            return inputBgraTexture;
        }

        EnsureVideoProcessor(inputDesc.Width, inputDesc.Height);

        int poolIndex = SelectAvailablePoolIndex(busyTexture1, busyTexture2);

        ID3D11Texture2D outputTexture = _nv12OutputTextures[poolIndex];
        ID3D11VideoProcessorOutputView outputView = _nv12OutputViews[poolIndex]
            ?? throw new InvalidOperationException("Widok wyjściowy procesora wideo (VPOV) nie został zainicjalizowany.");

        using ID3D11VideoProcessorInputView inputView = CreateInputView(inputBgraTexture, inputDesc);

        _streams[0] = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView
        };

        try
        {
            _videoContext.VideoProcessorBlt(_videoProcessor!, outputView, _outputFrameIndex++, 1, _streams).CheckError();
            // Flush queued D3D11 video blit commands so async hardware encoders (AMD AMF / NVENC / QSV)
            // immediately observe the completed NV12 surface on their video encode engine queue.
            _immediateContext.Flush();
        }
        finally
        {
            // Clear the COM reference from the reusable stream struct before inputView is disposed.
            _streams[0].InputSurface = null;
        }

        return outputTexture;
    }

    /// <summary>
    /// Copies a GPU NV12 texture (or converts a BGRA texture when hardware video processing is unavailable)
    /// into a CPU <see cref="IMFMediaBuffer"/> using a lazily allocated staging texture.
    /// Used for Tier 2 (cross-adapter hardware MFT) and Tier 3 (software MFT) encoding.
    /// </summary>
    public unsafe void CopyNv12ToMediaBuffer(ID3D11Texture2D sourceTexture, IMFMediaBuffer destinationBuffer)
    {
        ArgumentNullException.ThrowIfNull(sourceTexture);
        ArgumentNullException.ThrowIfNull(destinationBuffer);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Texture2DDescription sourceDesc = sourceTexture.Description;
        if (sourceDesc.Format != Format.NV12)
        {
            CopyBgraToNv12MediaBuffer(sourceTexture, sourceDesc, destinationBuffer);
            return;
        }

        _cpuStagingNv12Texture ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)OutputWidth,
            Height = (uint)OutputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        _immediateContext.CopyResource(_cpuStagingNv12Texture, sourceTexture);
        _immediateContext.Flush();

        MappedSubresource mapped = MapStagingReadNonStalling(_cpuStagingNv12Texture);
        try
        {
            int width = OutputWidth;
            int height = OutputHeight;
            int uvHeight = height / 2;
            int totalRows = height + uvHeight;
            int requiredBytes = width * totalRows;

            destinationBuffer.Lock(out nint dstPtr, out int maxLength, out _);
            try
            {
                if (maxLength < requiredBytes)
                {
                    throw new InvalidOperationException(
                        $"Bufor wyjściowy NV12 ({maxLength} B) jest mniejszy niż wymagane {requiredBytes} B.");
                }

                byte* srcBase = (byte*)mapped.DataPointer;
                byte* dstBase = (byte*)dstPtr;
                int srcPitch = (int)mapped.RowPitch;

                if (srcPitch == width)
                {
                    System.Buffer.MemoryCopy(srcBase, dstBase, maxLength, requiredBytes);
                }
                else
                {
                    for (int row = 0; row < totalRows; row++)
                    {
                        System.Buffer.MemoryCopy(
                            srcBase + ((long)row * srcPitch),
                            dstBase + ((long)row * width),
                            width,
                            width);
                    }
                }
            }
            finally
            {
                destinationBuffer.Unlock();
            }

            destinationBuffer.CurrentLength = requiredBytes;
        }
        finally
        {
            _immediateContext.Unmap(_cpuStagingNv12Texture, 0);
        }
    }

    private unsafe void CopyBgraToNv12MediaBuffer(
        ID3D11Texture2D bgraTexture,
        Texture2DDescription srcDesc,
        IMFMediaBuffer destinationBuffer)
    {
        ID3D11Texture2D staging = EnsureCpuStagingBgraTexture(srcDesc.Width, srcDesc.Height, srcDesc.Format);
        _immediateContext.CopyResource(staging, bgraTexture);

        MappedSubresource mapped = _immediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int srcW = (int)srcDesc.Width;
            int srcH = (int)srcDesc.Height;
            int dstW = OutputWidth;
            int dstH = OutputHeight;
            int requiredBytes = dstW * dstH * 3 / 2;

            destinationBuffer.Lock(out nint dstPtr, out int maxLength, out _);
            try
            {
                if (maxLength < requiredBytes)
                {
                    throw new InvalidOperationException(
                        $"Bufor wyjściowy NV12 ({maxLength} B) jest mniejszy niż wymagane {requiredBytes} B.");
                }

                byte* srcBase = (byte*)mapped.DataPointer;
                int srcPitch = (int)mapped.RowPitch;
                byte* yPlane = (byte*)dstPtr;
                byte* uvPlane = yPlane + (dstW * dstH);

                // Fixed-point BT.709 studio-range (16..235 / 16..240) BGRA -> NV12 conversion with nearest scaling.
                for (int dy = 0; dy < dstH; dy++)
                {
                    int sy = (int)((long)dy * srcH / dstH);
                    byte* srcRow = srcBase + ((long)sy * srcPitch);
                    byte* yRow = yPlane + ((long)dy * dstW);

                    for (int dx = 0; dx < dstW; dx++)
                    {
                        int sx = (int)((long)dx * srcW / dstW);
                        byte* px = srcRow + (sx * 4);
                        int b = px[0];
                        int g = px[1];
                        int r = px[2];
                        yRow[dx] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
                    }
                }

                int uvH = dstH / 2;
                int uvW = dstW / 2;
                for (int uy = 0; uy < uvH; uy++)
                {
                    int sy = (int)((long)(uy * 2) * srcH / dstH);
                    byte* srcRow = srcBase + ((long)sy * srcPitch);
                    byte* uvRow = uvPlane + ((long)uy * dstW);

                    for (int ux = 0; ux < uvW; ux++)
                    {
                        int sx = (int)((long)(ux * 2) * srcW / dstW);
                        byte* px = srcRow + (sx * 4);
                        int b = px[0];
                        int g = px[1];
                        int r = px[2];
                        int u = ((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128;
                        int v = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                        uvRow[ux * 2] = (byte)Math.Clamp(u, 16, 240);
                        uvRow[ux * 2 + 1] = (byte)Math.Clamp(v, 16, 240);
                    }
                }
            }
            finally
            {
                destinationBuffer.Unlock();
            }

            destinationBuffer.CurrentLength = requiredBytes;
        }
        finally
        {
            _immediateContext.Unmap(staging, 0);
        }
    }

    private ID3D11Texture2D EnsureCpuStagingBgraTexture(uint width, uint height, Format format)
    {
        Format effectiveFormat = format == Format.Unknown ? Format.B8G8R8A8_UNorm : format;
        if (_cpuStagingBgraTexture is not null)
        {
            Texture2DDescription existing = _cpuStagingBgraTexture.Description;
            if (existing.Width == width && existing.Height == height && existing.Format == effectiveFormat)
            {
                return _cpuStagingBgraTexture;
            }

            _cpuStagingBgraTexture.Dispose();
            _cpuStagingBgraTexture = null;
        }

        _cpuStagingBgraTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = effectiveFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        return _cpuStagingBgraTexture;
    }

    private int SelectAvailablePoolIndex(ID3D11Texture2D? busyTexture1, ID3D11Texture2D? busyTexture2)
    {
        for (int attempt = 0; attempt < OutputSurfacePoolSize; attempt++)
        {
            int candidateIndex = _nextPoolIndex;
            _nextPoolIndex = (_nextPoolIndex + 1) % OutputSurfacePoolSize;

            ID3D11Texture2D candidate = _nv12OutputTextures[candidateIndex];
            if (!ReferenceEquals(candidate, busyTexture1) && !ReferenceEquals(candidate, busyTexture2))
            {
                return candidateIndex;
            }
        }

        int fallback = _nextPoolIndex;
        _nextPoolIndex = (_nextPoolIndex + 1) % OutputSurfacePoolSize;
        return fallback;
    }

    /// <summary>
    /// Polls <see cref="Vortice.Direct3D11.MapFlags.DoNotWait"/> briefly outside the D3D11 multithread
    /// critical section so CPU staging readback does not stall the capture thread while the GPU finishes
    /// copying the NV12 frame.
    /// </summary>
    private MappedSubresource MapStagingReadNonStalling(ID3D11Texture2D stagingTexture)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            Result hr = _immediateContext.Map(
                stagingTexture,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.DoNotWait,
                out MappedSubresource mapped);

            if (hr.Success)
            {
                return mapped;
            }

            if (hr != Vortice.DXGI.ResultCode.WasStillDrawing)
            {
                break;
            }

            Thread.Sleep(1);
        }

        return _immediateContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
    }

    private ID3D11VideoProcessorInputView CreateInputView(ID3D11Texture2D inputTexture, Texture2DDescription inputDesc)
    {
        var inputViewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView
            {
                MipSlice = 0,
                ArraySlice = 0
            }
        };

        try
        {
            return _videoDevice!.CreateVideoProcessorInputView(inputTexture, _enumerator!, inputViewDesc);
        }
        catch (SharpGenException)
        {
            // Some GPU drivers disallow creating a VideoProcessorInputView directly on a WGC shared texture.
            // Fall back to a GPU-only copy into a cached D3D11 texture on the same adapter (still zero CPU copy).
            ID3D11Texture2D gpuCopy = EnsureFallbackBgraTexture(inputDesc.Width, inputDesc.Height, inputDesc.Format);
            _immediateContext.CopyResource(gpuCopy, inputTexture);
            return _videoDevice!.CreateVideoProcessorInputView(gpuCopy, _enumerator!, inputViewDesc);
        }
    }

    private ID3D11Texture2D EnsureFallbackBgraTexture(uint width, uint height, Format format)
    {
        Format effectiveFormat = format == Format.Unknown ? Format.B8G8R8A8_UNorm : format;
        if (_fallbackBgraInputTexture is not null)
        {
            Texture2DDescription existing = _fallbackBgraInputTexture.Description;
            if (existing.Width == width && existing.Height == height && existing.Format == effectiveFormat)
            {
                return _fallbackBgraInputTexture;
            }

            _fallbackBgraInputTexture.Dispose();
            _fallbackBgraInputTexture = null;
        }

        _fallbackBgraInputTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = effectiveFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        return _fallbackBgraInputTexture;
    }

    private void EnsureVideoProcessor(uint inputWidth, uint inputHeight)
    {
        if (_videoProcessor is not null &&
            _enumerator is not null &&
            _configuredInputWidth == inputWidth &&
            _configuredInputHeight == inputHeight)
        {
            return;
        }

        ReleaseVideoProcessorResources();

        try
        {
            var contentDesc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)TargetFps, 1),
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                OutputFrameRate = new Rational((uint)TargetFps, 1),
                OutputWidth = (uint)OutputWidth,
                OutputHeight = (uint)OutputHeight,
                Usage = VideoUsage.PlaybackNormal
            };

            _enumerator = _videoDevice!.CreateVideoProcessorEnumerator(contentDesc);
            _videoProcessor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

            var sourceRect = new RawRect(0, 0, (int)inputWidth, (int)inputHeight);
            var destRect = new RawRect(0, 0, OutputWidth, OutputHeight);

            _videoContext!.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, false);
            _videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, 0, true, sourceRect);
            _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, 0, true, destRect);
            _videoContext.VideoProcessorSetOutputTargetRect(_videoProcessor, true, destRect);

            // Configure explicit sRGB full-range -> BT.709 studio-range YCbCr conversion for crisp desktop colors.
            _videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace
            {
                Usage = 0,         // Playback
                RGB_Range = 0,     // Full range (0-255)
                YCbCr_Matrix = 1,  // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 2  // 0-255
            });

            _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace
            {
                Usage = 0,         // Playback
                RGB_Range = 0,
                YCbCr_Matrix = 1,  // BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 1  // Studio range (16-235) standard for H.264 NV12
            });

            var outputViewDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView
                {
                    MipSlice = 0
                }
            };

            for (int i = 0; i < OutputSurfacePoolSize; i++)
            {
                _nv12OutputViews[i] = _videoDevice.CreateVideoProcessorOutputView(
                    _nv12OutputTextures[i],
                    _enumerator,
                    outputViewDesc);
            }

            _configuredInputWidth = inputWidth;
            _configuredInputHeight = inputHeight;
        }
        catch
        {
            ReleaseVideoProcessorResources();
            throw;
        }
    }

    private void ReleaseVideoProcessorResources()
    {
        if (_nv12OutputViews is not null)
        {
            for (int i = 0; i < _nv12OutputViews.Length; i++)
            {
                _nv12OutputViews[i]?.Dispose();
                _nv12OutputViews[i] = null;
            }
        }

        _videoProcessor?.Dispose();
        _videoProcessor = null;

        _enumerator?.Dispose();
        _enumerator = null;

        _configuredInputWidth = 0;
        _configuredInputHeight = 0;
    }

    private void ReleaseNv12Textures()
    {
        if (_nv12OutputTextures is null)
        {
            return;
        }

        for (int i = 0; i < _nv12OutputTextures.Length; i++)
        {
            _nv12OutputTextures[i]?.Dispose();
            _nv12OutputTextures[i] = null!;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeInternal();
    }

    private void DisposeInternal()
    {
        ReleaseVideoProcessorResources();

        _fallbackBgraInputTexture?.Dispose();
        _fallbackBgraInputTexture = null;

        _cpuStagingNv12Texture?.Dispose();
        _cpuStagingNv12Texture = null;

        _cpuStagingBgraTexture?.Dispose();
        _cpuStagingBgraTexture = null;

        ReleaseNv12Textures();

        _videoContext?.Dispose();
        _videoDevice?.Dispose();

        if (_ownsDevice)
        {
            _immediateContext?.Dispose();
            _device?.Dispose();
        }
    }
}
