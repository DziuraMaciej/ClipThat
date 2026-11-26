using ClipThat.Models;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ClipThat.Engine.Video;

/// <summary>
/// Owns the Direct3D11 device and hardware <see cref="ID3D11VideoProcessor"/> pipeline that scales
/// incoming monitor-sized BGRA textures and converts them directly on the GPU into reusable NV12 surfaces.
/// </summary>
public sealed class GpuVideoProcessor : IDisposable
{
    private const int OutputSurfacePoolSize = 4;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _immediateContext;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11Texture2D[] _nv12OutputTextures;
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView?[] _nv12OutputViews;
    private ID3D11Texture2D? _fallbackBgraInputTexture;
    private ID3D11Texture2D? _cpuStagingNv12Texture;

    private uint _configuredInputWidth;
    private uint _configuredInputHeight;
    private uint _outputFrameIndex;
    private int _nextPoolIndex;
    private int _disposed;

    public GpuVideoProcessor(ResolutionPreset resolutionPreset, int targetFps)
    {
        (int width, int height) = GetTargetDimensions(resolutionPreset);
        OutputWidth = width;
        OutputHeight = height;
        TargetFps = targetFps == 30 ? 30 : 60;

        (_device, _immediateContext) = CreateD3D11Device();

        try
        {
            using ID3D11Multithread? multithread = _device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);

            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _immediateContext.QueryInterface<ID3D11VideoContext>();

            _nv12OutputTextures = new ID3D11Texture2D[OutputSurfacePoolSize];
            _nv12OutputViews = new ID3D11VideoProcessorOutputView?[OutputSurfacePoolSize];

            Texture2DDescription nv12Desc = new()
            {
                Width = (uint)OutputWidth,
                Height = (uint)OutputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            for (int i = 0; i < OutputSurfacePoolSize; i++)
            {
                _nv12OutputTextures[i] = _device.CreateTexture2D(nv12Desc);
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

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    public int TargetFps { get; }

    /// <summary>
    /// Maps each supported <see cref="ResolutionPreset"/> to even NV12-compatible target dimensions.
    /// </summary>
    public static (int Width, int Height) GetTargetDimensions(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.P240 => (426, 240),
        ResolutionPreset.P360 => (640, 360),
        ResolutionPreset.P480 => (854, 480),
        ResolutionPreset.P720 => (1280, 720),
        ResolutionPreset.P1080 => (1920, 1080),
        _ => (1920, 1080)
    };

    /// <summary>
    /// Scales and color-converts an incoming <see cref="Format.B8G8R8A8_UNorm"/> GPU texture
    /// directly on the GPU into a reusable pooled <see cref="Format.NV12"/> <see cref="ID3D11Texture2D"/>.
    /// </summary>
    public ID3D11Texture2D ProcessFrame(ID3D11Texture2D inputBgraTexture)
    {
        ArgumentNullException.ThrowIfNull(inputBgraTexture);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Texture2DDescription inputDesc = inputBgraTexture.Description;
        if (inputDesc.Width == 0 || inputDesc.Height == 0)
        {
            throw new InvalidOperationException("Przechwycona tekstura GPU ma zerowe wymiary.");
        }

        EnsureVideoProcessor(inputDesc.Width, inputDesc.Height);

        int poolIndex = _nextPoolIndex;
        _nextPoolIndex = (_nextPoolIndex + 1) % OutputSurfacePoolSize;

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
        }
        finally
        {
            // Clear the COM reference from the reusable stream struct before inputView is disposed.
            _streams[0].InputSurface = null;
        }

        return outputTexture;
    }

    /// <summary>
    /// Copies a GPU NV12 texture into a CPU <see cref="IMFMediaBuffer"/> using a lazily allocated staging texture.
    /// Only invoked when encoding falls back to a software MFT that cannot consume D3D11 DXGI surfaces directly.
    /// </summary>
    public unsafe void CopyNv12ToMediaBuffer(ID3D11Texture2D nv12Texture, IMFMediaBuffer destinationBuffer)
    {
        ArgumentNullException.ThrowIfNull(nv12Texture);
        ArgumentNullException.ThrowIfNull(destinationBuffer);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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

        _immediateContext.CopyResource(_cpuStagingNv12Texture, nv12Texture);

        MappedSubresource mapped = _immediateContext.Map(_cpuStagingNv12Texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
            return _videoDevice.CreateVideoProcessorInputView(inputTexture, _enumerator!, inputViewDesc);
        }
        catch (SharpGenException)
        {
            // Some GPU drivers disallow creating a VideoProcessorInputView directly on a WGC shared texture.
            // Fall back to a GPU-only copy into a cached D3D11 texture on the same adapter (still zero CPU copy).
            ID3D11Texture2D gpuCopy = EnsureFallbackBgraTexture(inputDesc.Width, inputDesc.Height, inputDesc.Format);
            _immediateContext.CopyResource(gpuCopy, inputTexture);
            return _videoDevice.CreateVideoProcessorInputView(gpuCopy, _enumerator!, inputViewDesc);
        }
    }

    private ID3D11Texture2D EnsureFallbackBgraTexture(uint width, uint height, Format format)
    {
        if (_fallbackBgraInputTexture is not null)
        {
            Texture2DDescription existing = _fallbackBgraInputTexture.Description;
            if (existing.Width == width && existing.Height == height && existing.Format == format)
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
            Format = format == Format.Unknown ? Format.B8G8R8A8_UNorm : format,
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

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        var sourceRect = new RawRect(0, 0, (int)inputWidth, (int)inputHeight);
        var destRect = new RawRect(0, 0, OutputWidth, OutputHeight);

        _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
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

    private static (ID3D11Device Device, ID3D11DeviceContext Context) CreateD3D11Device()
    {
        FeatureLevel[] featureLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        Result hr = D3D11.D3D11CreateDevice(
            nint.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            featureLevels,
            out ID3D11Device? device,
            out FeatureLevel _,
            out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();

            hr = D3D11.D3D11CreateDevice(
                nint.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out device,
                out FeatureLevel _,
                out context);

            hr.CheckError();
        }

        if (device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();
            throw new InvalidOperationException("Nie udało się utworzyć sprzętowego urządzenia Direct3D11.");
        }

        return (device, context);
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

        if (_nv12OutputTextures is not null)
        {
            for (int i = 0; i < _nv12OutputTextures.Length; i++)
            {
                _nv12OutputTextures[i]?.Dispose();
            }
        }

        _videoContext?.Dispose();
        _videoDevice?.Dispose();
        _immediateContext?.Dispose();
        _device?.Dispose();
    }
}
