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
    private ID3D11VideoProcessorInputView? _cachedFallbackInputView;
    private nint _cachedDirectInputPtr0;
    private ID3D11VideoProcessorInputView? _cachedDirectInputView0;
    private nint _cachedDirectInputPtr1;
    private ID3D11VideoProcessorInputView? _cachedDirectInputView1;
    private bool _preferFallbackBgraInput;
    private ID3D11Texture2D? _cpuStagingNv12Texture;
    private ID3D11Texture2D? _cpuStagingBgraTexture;
    private int[]? _cpuSrcXMap;
    private int[]? _cpuSrcYMap;
    private int[]? _cpuUvSrcXMap;
    private int[]? _cpuUvSrcYMap;
    private int _cpuMapSrcWidth;
    private int _cpuMapSrcHeight;

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
        bool ownsDevice = false,
        int sourceWidth = 0,
        int sourceHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(immediateContext);

        (int rawWidth, int rawHeight) = GetTargetDimensions(resolutionPreset, sourceWidth, sourceHeight);
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
    /// Maps each supported <see cref="ResolutionPreset"/> to guaranteed even NV12/H.264 target dimensions
    /// using the default 16:9 aspect ratio.
    /// </summary>
    public static (int Width, int Height) GetTargetDimensions(ResolutionPreset preset)
        => GetTargetDimensions(preset, sourceWidth: 16, sourceHeight: 9);

    /// <summary>
    /// Maps <paramref name="preset"/> to guaranteed even NV12/H.264 target dimensions while preserving
    /// the capture monitor's native aspect ratio (including 21:9 ultrawide, 16:10, 4:3, and portrait displays)
    /// within H.264 encoder limits (4096×2304).
    /// </summary>
    public static (int Width, int Height) GetTargetDimensions(ResolutionPreset preset, int sourceWidth, int sourceHeight)
    {
        const int maxEncoderWidth = 4096;
        const int maxEncoderHeight = 2304;

        int presetShortEdge = preset switch
        {
            ResolutionPreset.P240 => 240,
            ResolutionPreset.P360 => 360,
            ResolutionPreset.P480 => 480,
            ResolutionPreset.P720 => 720,
            ResolutionPreset.P1080 => 1080,
            _ => 1080
        };

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            sourceWidth = 16;
            sourceHeight = 9;
        }

        double width;
        double height;

        if (sourceWidth >= sourceHeight)
        {
            height = presetShortEdge;
            width = height * ((double)sourceWidth / sourceHeight);
        }
        else
        {
            // Portrait monitor: anchor width to the preset short edge so 1080p portrait records 1080x1920.
            width = presetShortEdge;
            height = width * ((double)sourceHeight / sourceWidth);
        }

        if (width > maxEncoderWidth)
        {
            double scale = maxEncoderWidth / width;
            width = maxEncoderWidth;
            height *= scale;
        }

        if (height > maxEncoderHeight)
        {
            double scale = maxEncoderHeight / height;
            height = maxEncoderHeight;
            width *= scale;
        }

        int evenWidth = Math.Clamp((int)(Math.Round(width / 2.0) * 2.0), 2, maxEncoderWidth);
        int evenHeight = Math.Clamp((int)(Math.Round(height / 2.0) * 2.0), 2, maxEncoderHeight);
        return (evenWidth, evenHeight);
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
        ID3D11Texture2D? busyTexture2 = null,
        Func<ID3D11Texture2D, bool>? isTextureBusy = null,
        ID3D11Texture2D? busyTexture3 = null,
        bool cacheInputView = false)
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

        try
        {
            EnsureVideoProcessor(inputDesc.Width, inputDesc.Height);
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "GpuVideoProcessor",
                $"Runtime ID3D11VideoProcessor initialization failed ({inputDesc.Width}x{inputDesc.Height} -> {OutputWidth}x{OutputHeight}): {ex.Message}; falling back to CPU BGRA->NV12 conversion.",
                ex);
            ReleaseVideoProcessorResources();
            SupportsHardwareVideoProcessor = false;
            return inputBgraTexture;
        }

        int poolIndex = SelectAvailablePoolIndex(busyTexture1, busyTexture2, busyTexture3, isTextureBusy);

        ID3D11Texture2D outputTexture = _nv12OutputTextures[poolIndex];
        ID3D11VideoProcessorOutputView outputView = _nv12OutputViews[poolIndex]
            ?? throw new InvalidOperationException("Widok wyjściowy procesora wideo (VPOV) nie został zainicjalizowany.");

        ID3D11VideoProcessorInputView inputView = AcquireInputView(inputBgraTexture, inputDesc, cacheInputView, out bool ownsInputView);
        try
        {
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
        }
        finally
        {
            if (ownsInputView)
            {
                inputView.Dispose();
            }
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
        _immediateContext.Flush();

        MappedSubresource mapped = MapStagingReadNonStalling(staging);
        try
        {
            int srcW = (int)srcDesc.Width;
            int srcH = (int)srcDesc.Height;
            int dstW = OutputWidth;
            int dstH = OutputHeight;
            int requiredBytes = dstW * dstH * 3 / 2;

            EnsureCpuScaleMaps(srcW, srcH, dstW, dstH);
            int[] srcXMap = _cpuSrcXMap!;
            int[] srcYMap = _cpuSrcYMap!;
            int[] uvSrcXMap = _cpuUvSrcXMap!;
            int[] uvSrcYMap = _cpuUvSrcYMap!;

            destinationBuffer.Lock(out nint dstPtr, out int maxLength, out _);
            try
            {
                if (maxLength < requiredBytes)
                {
                    throw new InvalidOperationException(
                        $"Bufor wyjściowy NV12 ({maxLength} B) jest mniejszy niż wymagane {requiredBytes} B.");
                }

                nint srcBasePtr = mapped.DataPointer;
                int srcPitch = (int)mapped.RowPitch;
                nint yPlanePtr = dstPtr;
                nint uvPlanePtr = dstPtr + (dstW * dstH);
                int uvH = dstH / 2;
                int uvW = dstW / 2;
                bool isDirect1To1Bgra =
                    srcW == dstW &&
                    srcH == dstH &&
                    srcDesc.Format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.Unknown;
                bool isFp16 = srcDesc.Format == Format.R16G16B16A16_Float;
                bool isR10G10B10A2 = srcDesc.Format == Format.R10G10B10A2_UNorm;

                // Fixed-point BT.709 studio-range (16..235 / 16..240) conversion processing 2 Y rows + 1 UV row
                // per strip in a single Parallel.For dispatch for better cache locality and lower scheduling overhead.
                void ConvertStrip(int uy)
                {
                    int dy0 = uy * 2;
                    int dy1 = dy0 + 1;
                    byte* yRow0 = (byte*)yPlanePtr + ((long)dy0 * dstW);
                    byte* yRow1 = (byte*)yPlanePtr + ((long)dy1 * dstW);
                    byte* uvRow = (byte*)uvPlanePtr + ((long)uy * dstW);

                    if (isDirect1To1Bgra)
                    {
                        byte* srcRow0 = (byte*)srcBasePtr + ((long)dy0 * srcPitch);
                        byte* srcRow1 = (byte*)srcBasePtr + ((long)dy1 * srcPitch);

                        for (int ux = 0; ux < uvW; ux++)
                        {
                            int dx = ux * 2;
                            byte* p00 = srcRow0 + (dx * 4);
                            byte* p01 = p00 + 4;
                            byte* p10 = srcRow1 + (dx * 4);
                            byte* p11 = p10 + 4;

                            int b0 = p00[0], g0 = p00[1], r0 = p00[2];
                            int b1 = p01[0], g1 = p01[1], r1 = p01[2];
                            int b2 = p10[0], g2 = p10[1], r2 = p10[2];
                            int b3 = p11[0], g3 = p11[1], r3 = p11[2];

                            yRow0[dx] = (byte)(((47 * r0 + 157 * g0 + 16 * b0 + 128) >> 8) + 16);
                            yRow0[dx + 1] = (byte)(((47 * r1 + 157 * g1 + 16 * b1 + 128) >> 8) + 16);
                            yRow1[dx] = (byte)(((47 * r2 + 157 * g2 + 16 * b2 + 128) >> 8) + 16);
                            yRow1[dx + 1] = (byte)(((47 * r3 + 157 * g3 + 16 * b3 + 128) >> 8) + 16);

                            int avgR = (r0 + r1 + r2 + r3 + 2) >> 2;
                            int avgG = (g0 + g1 + g2 + g3 + 2) >> 2;
                            int avgB = (b0 + b1 + b2 + b3 + 2) >> 2;
                            int u = ((-26 * avgR - 86 * avgG + 112 * avgB + 128) >> 8) + 128;
                            int v = ((112 * avgR - 102 * avgG - 10 * avgB + 128) >> 8) + 128;
                            uvRow[dx] = (byte)Math.Clamp(u, 16, 240);
                            uvRow[dx + 1] = (byte)Math.Clamp(v, 16, 240);
                        }

                        return;
                    }

                    byte* srcRowY0 = (byte*)srcBasePtr + ((long)srcYMap[dy0] * srcPitch);
                    byte* srcRowY1 = (byte*)srcBasePtr + ((long)srcYMap[dy1] * srcPitch);
                    byte* srcRowUv = (byte*)srcBasePtr + ((long)uvSrcYMap[uy] * srcPitch);

                    if (!isFp16 && !isR10G10B10A2)
                    {
                        for (int dx = 0; dx < dstW; dx++)
                        {
                            int xOff = srcXMap[dx];
                            byte* px0 = srcRowY0 + xOff;
                            byte* px1 = srcRowY1 + xOff;
                            yRow0[dx] = (byte)(((47 * px0[2] + 157 * px0[1] + 16 * px0[0] + 128) >> 8) + 16);
                            yRow1[dx] = (byte)(((47 * px1[2] + 157 * px1[1] + 16 * px1[0] + 128) >> 8) + 16);
                        }

                        for (int ux = 0; ux < uvW; ux++)
                        {
                            byte* px = srcRowUv + uvSrcXMap[ux];
                            int b = px[0], g = px[1], r = px[2];
                            int u = ((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128;
                            int v = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                            uvRow[ux * 2] = (byte)Math.Clamp(u, 16, 240);
                            uvRow[ux * 2 + 1] = (byte)Math.Clamp(v, 16, 240);
                        }

                        return;
                    }

                    for (int dx = 0; dx < dstW; dx++)
                    {
                        int srcPxIdx = srcXMap[dx] >> 2;
                        ReadRgb8(srcRowY0, srcPxIdx, isFp16, isR10G10B10A2, out int r0, out int g0, out int b0);
                        ReadRgb8(srcRowY1, srcPxIdx, isFp16, isR10G10B10A2, out int r1, out int g1, out int b1);
                        yRow0[dx] = (byte)(((47 * r0 + 157 * g0 + 16 * b0 + 128) >> 8) + 16);
                        yRow1[dx] = (byte)(((47 * r1 + 157 * g1 + 16 * b1 + 128) >> 8) + 16);
                    }

                    for (int ux = 0; ux < uvW; ux++)
                    {
                        int srcPxIdx = uvSrcXMap[ux] >> 2;
                        ReadRgb8(srcRowUv, srcPxIdx, isFp16, isR10G10B10A2, out int r, out int g, out int b);
                        int u = ((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128;
                        int v = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                        uvRow[ux * 2] = (byte)Math.Clamp(u, 16, 240);
                        uvRow[ux * 2 + 1] = (byte)Math.Clamp(v, 16, 240);
                    }
                }

                if (dstH >= 360)
                {
                    Parallel.For(0, uvH, ConvertStrip);
                }
                else
                {
                    for (int uy = 0; uy < uvH; uy++) ConvertStrip(uy);
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

    private void EnsureCpuScaleMaps(int srcW, int srcH, int dstW, int dstH)
    {
        if (_cpuSrcXMap is not null &&
            _cpuMapSrcWidth == srcW &&
            _cpuMapSrcHeight == srcH)
        {
            return;
        }

        int[] srcX = new int[dstW];
        for (int dx = 0; dx < dstW; dx++)
        {
            srcX[dx] = (int)((long)dx * srcW / dstW) * 4;
        }

        int[] srcY = new int[dstH];
        for (int dy = 0; dy < dstH; dy++)
        {
            srcY[dy] = (int)((long)dy * srcH / dstH);
        }

        int uvW = dstW / 2;
        int uvH = dstH / 2;
        int[] uvX = new int[uvW];
        for (int ux = 0; ux < uvW; ux++)
        {
            uvX[ux] = (int)((long)(ux * 2) * srcW / dstW) * 4;
        }

        int[] uvY = new int[uvH];
        for (int uy = 0; uy < uvH; uy++)
        {
            uvY[uy] = (int)((long)(uy * 2) * srcH / dstH);
        }

        _cpuSrcXMap = srcX;
        _cpuSrcYMap = srcY;
        _cpuUvSrcXMap = uvX;
        _cpuUvSrcYMap = uvY;
        _cpuMapSrcWidth = srcW;
        _cpuMapSrcHeight = srcH;
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

    private static unsafe void ReadRgb8(byte* row, int pixelIndex, bool isFp16, bool isR10G10B10A2, out int r, out int g, out int b)
    {
        if (isFp16)
        {
            Half* h = (Half*)(row + ((long)pixelIndex * 8));
            float rf = Math.Max(0f, (float)h[0]) * 0.4f;
            float gf = Math.Max(0f, (float)h[1]) * 0.4f;
            float bf = Math.Max(0f, (float)h[2]) * 0.4f;
            r = (int)Math.Clamp(MathF.Pow(rf / (1f + rf), 1f / 2.2f) * 255f, 0f, 255f);
            g = (int)Math.Clamp(MathF.Pow(gf / (1f + gf), 1f / 2.2f) * 255f, 0f, 255f);
            b = (int)Math.Clamp(MathF.Pow(bf / (1f + bf), 1f / 2.2f) * 255f, 0f, 255f);
            return;
        }

        if (isR10G10B10A2)
        {
            uint packed = *((uint*)row + pixelIndex);
            r = (int)((packed & 0x3FFu) >> 2);
            g = (int)(((packed >> 10) & 0x3FFu) >> 2);
            b = (int)(((packed >> 20) & 0x3FFu) >> 2);
            return;
        }

        byte* px = row + ((long)pixelIndex * 4);
        b = px[0];
        g = px[1];
        r = px[2];
    }

    private int SelectAvailablePoolIndex(
        ID3D11Texture2D? busyTexture1,
        ID3D11Texture2D? busyTexture2,
        ID3D11Texture2D? busyTexture3,
        Func<ID3D11Texture2D, bool>? isTextureBusy)
    {
        for (int attempt = 0; attempt < OutputSurfacePoolSize; attempt++)
        {
            int candidateIndex = _nextPoolIndex;
            _nextPoolIndex = (_nextPoolIndex + 1) % OutputSurfacePoolSize;

            ID3D11Texture2D candidate = _nv12OutputTextures[candidateIndex];
            if (!ReferenceEquals(candidate, busyTexture1) &&
                !ReferenceEquals(candidate, busyTexture2) &&
                !ReferenceEquals(candidate, busyTexture3) &&
                (isTextureBusy is null || !isTextureBusy(candidate)))
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

    private ID3D11VideoProcessorInputView AcquireInputView(
        ID3D11Texture2D inputTexture,
        Texture2DDescription inputDesc,
        bool cacheInputView,
        out bool ownsInputView)
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

        if (!_preferFallbackBgraInput)
        {
            if (cacheInputView)
            {
                nint texPtr = inputTexture.NativePointer;
                if (texPtr != nint.Zero)
                {
                    if (texPtr == _cachedDirectInputPtr0 && _cachedDirectInputView0 is not null)
                    {
                        ownsInputView = false;
                        return _cachedDirectInputView0;
                    }

                    if (texPtr == _cachedDirectInputPtr1 && _cachedDirectInputView1 is not null)
                    {
                        ownsInputView = false;
                        return _cachedDirectInputView1;
                    }
                }
            }

            try
            {
                ID3D11VideoProcessorInputView created = _videoDevice!.CreateVideoProcessorInputView(inputTexture, _enumerator!, inputViewDesc);
                if (cacheInputView && inputTexture.NativePointer != nint.Zero)
                {
                    // Cache up to 2 persistent DXGI compositor textures (clean desktop + composited cursor target)
                    // so we avoid 60 COM view create/release cycles per second without retaining transient WGC frames.
                    if (_cachedDirectInputView0 is null)
                    {
                        _cachedDirectInputPtr0 = inputTexture.NativePointer;
                        _cachedDirectInputView0 = created;
                    }
                    else
                    {
                        _cachedDirectInputView1?.Dispose();
                        _cachedDirectInputPtr1 = inputTexture.NativePointer;
                        _cachedDirectInputView1 = created;
                    }

                    ownsInputView = false;
                    return created;
                }

                ownsInputView = true;
                return created;
            }
            catch (SharpGenException)
            {
                // Remember that this GPU driver rejects direct VPIV on shared capture textures so
                // we do not throw and catch 60 SharpGenExceptions per second on subsequent frames.
                _preferFallbackBgraInput = true;
            }
        }

        ID3D11Texture2D gpuCopy = EnsureFallbackBgraTexture(inputDesc.Width, inputDesc.Height, inputDesc.Format);
        _immediateContext.CopyResource(gpuCopy, inputTexture);

        _cachedFallbackInputView ??= _videoDevice!.CreateVideoProcessorInputView(gpuCopy, _enumerator!, inputViewDesc);
        ownsInputView = false;
        return _cachedFallbackInputView;
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

            _cachedFallbackInputView?.Dispose();
            _cachedFallbackInputView = null;
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
        _cachedDirectInputView0?.Dispose();
        _cachedDirectInputView0 = null;
        _cachedDirectInputPtr0 = nint.Zero;

        _cachedDirectInputView1?.Dispose();
        _cachedDirectInputView1 = null;
        _cachedDirectInputPtr1 = nint.Zero;

        _cachedFallbackInputView?.Dispose();
        _cachedFallbackInputView = null;

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
