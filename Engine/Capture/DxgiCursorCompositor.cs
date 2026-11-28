using System.Runtime.InteropServices;
using System.Text;
using ClipThat.Engine.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClipThat.Engine.Capture;

/// <summary>
/// Pure-GPU Direct3D 11 compositor that keeps a clean copy of the latest DXGI Desktop Duplication
/// surface and renders DXGI hardware pointer shapes (<see cref="PointerShapeType.Color"/>,
/// <see cref="PointerShapeType.MaskedColor"/>, and <see cref="PointerShapeType.Monochrome"/>)
/// strictly onto ClipThat's recorded frame copy without ever touching the live display's
/// hardware cursor plane.
/// </summary>
internal sealed class DxgiCursorCompositor : IDisposable
{
    private const uint ModeFullscreenCopy = 0u;

    private static readonly ID3D11RenderTargetView[] NullRenderTargets = { null! };
    private static readonly ID3D11ShaderResourceView[] NullShaderResources = { null!, null! };

    private const string HlslSource = @"
cbuffer CursorParams : register(b0)
{
    float4 CursorRectPx;
    float2 ScreenSizePx;
    uint   CursorType;
    uint   IsHdrLinear;
};

Texture2D<float4> DesktopTex : register(t0);
Texture2D<float4> CursorTex  : register(t1);
SamplerState PointSampler    : register(s0);

struct VSOut
{
    float4 Position  : SV_Position;
    float2 DesktopUv : TEXCOORD0;
    float2 CursorUv  : TEXCOORD1;
};

VSOut VSMain(uint id : SV_VertexID)
{
    float2 uv = float2((id & 1u) ? 1.0 : 0.0, (id & 2u) ? 1.0 : 0.0);
    VSOut o;
    if (CursorType == 0u)
    {
        o.DesktopUv = uv;
        o.CursorUv = uv;
        o.Position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    }
    else
    {
        float2 px = CursorRectPx.xy + uv * CursorRectPx.zw;
        float2 dUv = px / ScreenSizePx;
        o.DesktopUv = dUv;
        o.CursorUv = uv;
        o.Position = float4(dUv.x * 2.0 - 1.0, 1.0 - dUv.y * 2.0, 0.0, 1.0);
    }
    return o;
}

float3 SampleDesktopSdr(float2 uv)
{
    float4 rawBg = DesktopTex.Sample(PointSampler, uv);
    if (IsHdrLinear != 0u)
    {
        // Windows scRGB HDR (1.0 = 80 nits): soft Reinhard tone-map + linear-to-sRGB gamma.
        float3 hdr = max(rawBg.rgb, 0.0) * 0.4;
        float3 mapped = hdr / (1.0 + hdr);
        return pow(saturate(mapped), 1.0 / 2.2);
    }
    return saturate(rawBg.rgb);
}

float4 PSMain(VSOut input) : SV_Target
{
    float3 bgRgb = SampleDesktopSdr(input.DesktopUv);
    if (CursorType == 0u)
    {
        return float4(bgRgb, 1.0);
    }

    float4 cur = CursorTex.Sample(PointSampler, input.CursorUv);
    if (CursorType == 2u)
    {
        float3 blended = lerp(bgRgb, cur.rgb, cur.a);
        return float4(blended, 1.0);
    }

    if (CursorType == 4u)
    {
        if (cur.a > 0.5)
        {
            uint3 bgU = (uint3)round(bgRgb * 255.0);
            uint3 curU = (uint3)round(cur.rgb * 255.0);
            return float4((float3)(bgU ^ curU) / 255.0, 1.0);
        }
        return float4(cur.rgb, 1.0);
    }

    uint3 bgU = (uint3)round(bgRgb * 255.0);
    uint3 andMask = cur.r > 0.5 ? uint3(255u, 255u, 255u) : uint3(0u, 0u, 0u);
    uint3 xorMask = cur.g > 0.5 ? uint3(255u, 255u, 255u) : uint3(0u, 0u, 0u);
    return float4((float3)((bgU & andMask) ^ xorMask) / 255.0, 1.0);
}
";

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader? _vertexShader;
    private readonly ID3D11PixelShader? _pixelShader;
    private readonly ID3D11Buffer? _constantBuffer;
    private readonly ID3D11SamplerState? _pointSampler;
    private readonly ID3D11RasterizerState? _rasterizerState;
    private readonly ID3D11BlendState? _opaqueBlendState;
    private readonly ID3D11DepthStencilState? _depthDisabledState;
    private readonly bool _shaderReady;

    private ID3D11Texture2D? _cleanDesktopTexture;
    private ID3D11ShaderResourceView? _cleanDesktopSrv;
    private ID3D11Texture2D? _compositedTexture;
    private ID3D11RenderTargetView? _compositedRtv;
    private uint _desktopWidth;
    private uint _desktopHeight;
    private Format _desktopFormat;

    private ID3D11Texture2D? _cursorTexture;
    private ID3D11ShaderResourceView? _cursorSrv;
    private uint _cursorWidth;
    private uint _cursorHeight;
    private uint _cursorShapeType;
    private byte[]? _monochromeStagingBytes;
    private bool _hasCursorShape;
    private int _disposed;

    public DxgiCursorCompositor(ID3D11Device device, ID3D11DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);

        _device = device;
        _context = context;

        try
        {
            byte[] vsBytecode = CompileHlsl(HlslSource, "VSMain", "vs_5_0");
            byte[] psBytecode = CompileHlsl(HlslSource, "PSMain", "ps_5_0");

            _vertexShader = _device.CreateVertexShader(vsBytecode);
            _pixelShader = _device.CreatePixelShader(psBytecode);

            _constantBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<CursorConstantBuffer>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            _pointSampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue
            });

            _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = false,
                ScissorEnable = false
            });

            _opaqueBlendState = _device.CreateBlendState(BlendDescription.Opaque);

            _depthDisabledState = _device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                StencilEnable = false
            });

            _shaderReady = true;
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "DxgiCursorCompositor",
                $"GPU cursor shader initialization failed ({ex.Message}); DXGI frames will be recorded without cursor overlay.",
                ex);
            _shaderReady = false;
            DisposeShaderResources();
        }
    }

    public bool HasDesktopFrame => _cleanDesktopTexture is not null;

    /// <summary>
    /// Copies the acquired DXGI desktop texture onto our persistent GPU surface so
    /// <see cref="IDXGIOutputDuplication.ReleaseFrame"/> can be called immediately.
    /// </summary>
    public void UpdateDesktopTexture(ID3D11Texture2D acquiredTexture)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Texture2DDescription desc = acquiredTexture.Description;
        EnsureDesktopSurfaces(desc.Width, desc.Height, desc.Format);

        _context.CopyResource(_cleanDesktopTexture!, acquiredTexture);
    }

    /// <summary>
    /// Uploads a new DXGI pointer bitmap onto our tiny GPU cursor texture. Called only when
    /// <c>OutduplFrameInfo.PointerShapeBufferSize &gt; 0</c> (when the cursor graphic changes).
    /// </summary>
    public unsafe void UpdateCursorShape(byte[] shapeData, int shapeDataLength, OutduplPointerShapeInfo shapeInfo)
    {
        if (!_shaderReady || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        uint shapeType = shapeInfo.Type;
        uint width = shapeInfo.Width;
        uint height = shapeInfo.Height;
        uint pitch = shapeInfo.Pitch;

        if (width == 0 || height == 0 || pitch == 0)
        {
            _hasCursorShape = false;
            return;
        }

        if (shapeType == (uint)PointerShapeType.Monochrome)
        {
            // DXGI monochrome cursor packs 1-bpp AND mask (top half) over 1-bpp XOR mask (bottom half).
            uint cursorHeight = height / 2;
            if (cursorHeight == 0)
            {
                _hasCursorShape = false;
                return;
            }

            uint minPitch = (width + 7u) / 8u;
            long requiredBytes = (long)pitch * height;
            if (pitch < minPitch || shapeDataLength < requiredBytes)
            {
                return;
            }

            int unpackedBytes = checked((int)(width * cursorHeight * 4));
            if (_monochromeStagingBytes is null || _monochromeStagingBytes.Length < unpackedBytes)
            {
                _monochromeStagingBytes = new byte[unpackedBytes];
            }

            EnsureCursorTexture(width, cursorHeight);

            uint xorBaseOffset = cursorHeight * pitch;
            for (uint y = 0; y < cursorHeight; y++)
            {
                uint andRowOffset = y * pitch;
                uint xorRowOffset = xorBaseOffset + (y * pitch);
                int dstRowOffset = (int)(y * width * 4);

                for (uint x = 0; x < width; x++)
                {
                    byte bitMask = (byte)(0x80 >> (int)(x & 7u));
                    int byteCol = (int)(x >> 3);
                    bool andBit = (shapeData[andRowOffset + byteCol] & bitMask) != 0;
                    bool xorBit = (shapeData[xorRowOffset + byteCol] & bitMask) != 0;

                    int dstIdx = dstRowOffset + (int)(x * 4);
                    // BGRA layout: [0]=B, [1]=G (XOR mask), [2]=R (AND mask), [3]=A.
                    _monochromeStagingBytes[dstIdx] = 0;
                    _monochromeStagingBytes[dstIdx + 1] = xorBit ? (byte)255 : (byte)0;
                    _monochromeStagingBytes[dstIdx + 2] = andBit ? (byte)255 : (byte)0;
                    _monochromeStagingBytes[dstIdx + 3] = 255;
                }
            }

            UploadCursorPixels(_monochromeStagingBytes, width, cursorHeight, width * 4);
            _cursorShapeType = shapeType;
            _hasCursorShape = true;
            return;
        }

        if (shapeType == (uint)PointerShapeType.Color || shapeType == (uint)PointerShapeType.MaskedColor)
        {
            long requiredBytes = (long)pitch * height;
            if (shapeDataLength < requiredBytes || pitch < width * 4)
            {
                return;
            }

            EnsureCursorTexture(width, height);
            UploadCursorPixels(shapeData, width, height, pitch);
            _cursorShapeType = shapeType;
            _hasCursorShape = true;
            return;
        }

        _hasCursorShape = false;
    }

    /// <summary>
    /// Produces the BGRA GPU texture ready for <see cref="Video.GpuVideoProcessor"/>, compositing
    /// the current hardware pointer onto the copy when visible.
    /// </summary>
    public ID3D11Texture2D ComposeFrame(bool cursorVisible, int cursorX, int cursorY)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_cleanDesktopTexture is null)
        {
            throw new InvalidOperationException("Brak zbuforowanej klatki pulpitu DXGI.");
        }

        bool isNativeBgra = _desktopFormat is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb;
        bool shouldDrawCursor =
            _shaderReady &&
            cursorVisible &&
            _hasCursorShape &&
            _cursorSrv is not null &&
            _cursorWidth > 0 &&
            _cursorHeight > 0 &&
            cursorX < (int)_desktopWidth &&
            cursorY < (int)_desktopHeight &&
            cursorX + (int)_cursorWidth > 0 &&
            cursorY + (int)_cursorHeight > 0;

        if (!_shaderReady || (isNativeBgra && !shouldDrawCursor))
        {
            return _cleanDesktopTexture;
        }

        EnsureCompositedTarget();

        uint isHdrLinear = _desktopFormat == Format.R16G16B16A16_Float ? 1u : 0u;

        if (isNativeBgra)
        {
            // Fast GPU-to-GPU blit of the clean desktop, followed by a tiny cursor-only quad draw.
            _context.CopyResource(_compositedTexture!, _cleanDesktopTexture);
        }
        else
        {
            // Normalize non-BGRA desktop formats (e.g., 10-bit / HDR) into BGRA via fullscreen pass.
            DrawPass(new CursorConstantBuffer
            {
                CursorRectLeft = 0f,
                CursorRectTop = 0f,
                CursorRectWidth = _desktopWidth,
                CursorRectHeight = _desktopHeight,
                ScreenWidth = _desktopWidth,
                ScreenHeight = _desktopHeight,
                CursorType = ModeFullscreenCopy,
                IsHdrLinear = isHdrLinear
            }, bindCursorSrv: false);
        }

        if (shouldDrawCursor)
        {
            DrawPass(new CursorConstantBuffer
            {
                CursorRectLeft = cursorX,
                CursorRectTop = cursorY,
                CursorRectWidth = _cursorWidth,
                CursorRectHeight = _cursorHeight,
                ScreenWidth = _desktopWidth,
                ScreenHeight = _desktopHeight,
                CursorType = _cursorShapeType,
                IsHdrLinear = isHdrLinear
            }, bindCursorSrv: true);
        }

        return _compositedTexture!;
    }

    private unsafe void DrawPass(CursorConstantBuffer constants, bool bindCursorSrv)
    {
        MappedSubresource mapped = _context.Map(_constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(CursorConstantBuffer*)mapped.DataPointer = constants;
        }
        finally
        {
            _context.Unmap(_constantBuffer!, 0);
        }

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _pointSampler);
        _context.PSSetShaderResource(0, _cleanDesktopSrv!);
        if (bindCursorSrv && _cursorSrv is not null)
        {
            _context.PSSetShaderResource(1, _cursorSrv);
        }

        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(new Viewport(0f, 0f, _desktopWidth, _desktopHeight, 0f, 1f));
        _context.OMSetBlendState(_opaqueBlendState);
        _context.OMSetDepthStencilState(_depthDisabledState);
        _context.OMSetRenderTargets(_compositedRtv!, null);

        try
        {
            _context.Draw(4, 0);
        }
        finally
        {
            // Immediately unbind RTV and SRVs so subsequent VideoProcessorBlt / CopyResource calls
            // never encounter D3D11 simultaneous read/write resource hazards.
            _context.OMSetRenderTargets(0, NullRenderTargets, null);
            _context.PSSetShaderResources(0, 2, NullShaderResources);
        }
    }

    private void EnsureDesktopSurfaces(uint width, uint height, Format format)
    {
        // Map B8G8R8A8_UNorm_SRgb to B8G8R8A8_UNorm so CopyResource copies raw sRGB bytes without
        // causing D3D11 SRV sRGB->linear darkening under the cursor bounding box.
        Format effectiveFormat = format is Format.Unknown or Format.B8G8R8A8_UNorm_SRgb
            ? Format.B8G8R8A8_UNorm
            : format;
        if (_cleanDesktopTexture is not null &&
            _desktopWidth == width &&
            _desktopHeight == height &&
            _desktopFormat == effectiveFormat)
        {
            return;
        }

        ReleaseDesktopSurfaces();

        _cleanDesktopTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = effectiveFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        if (_shaderReady)
        {
            _cleanDesktopSrv = _device.CreateShaderResourceView(_cleanDesktopTexture);
        }

        _desktopWidth = width;
        _desktopHeight = height;
        _desktopFormat = effectiveFormat;
    }

    private void EnsureCompositedTarget()
    {
        if (_compositedTexture is not null &&
            _compositedRtv is not null &&
            _compositedTexture.Description.Width == _desktopWidth &&
            _compositedTexture.Description.Height == _desktopHeight)
        {
            return;
        }

        _compositedRtv?.Dispose();
        _compositedRtv = null;
        _compositedTexture?.Dispose();
        _compositedTexture = null;

        _compositedTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = _desktopWidth,
            Height = _desktopHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        _compositedRtv = _device.CreateRenderTargetView(_compositedTexture);
    }

    private void EnsureCursorTexture(uint width, uint height)
    {
        if (_cursorTexture is not null &&
            _cursorSrv is not null &&
            _cursorWidth == width &&
            _cursorHeight == height)
        {
            return;
        }

        _cursorSrv?.Dispose();
        _cursorSrv = null;
        _cursorTexture?.Dispose();
        _cursorTexture = null;

        _cursorTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        });

        _cursorSrv = _device.CreateShaderResourceView(_cursorTexture);
        _cursorWidth = width;
        _cursorHeight = height;
    }

    private unsafe void UploadCursorPixels(byte[] srcBytes, uint width, uint height, uint srcPitch)
    {
        MappedSubresource mapped = _context.Map(_cursorTexture!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int copyRowBytes = checked((int)(width * 4));
            int dstPitch = (int)mapped.RowPitch;
            byte* dstBase = (byte*)mapped.DataPointer;

            fixed (byte* srcBase = srcBytes)
            {
                for (uint y = 0; y < height; y++)
                {
                    System.Buffer.MemoryCopy(
                        srcBase + ((long)y * srcPitch),
                        dstBase + ((long)y * dstPitch),
                        dstPitch,
                        copyRowBytes);
                }
            }
        }
        finally
        {
            _context.Unmap(_cursorTexture!, 0);
        }
    }

    private static unsafe byte[] CompileHlsl(string source, string entryPoint, string targetProfile)
    {
        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
        nint codeBlob = nint.Zero;
        nint errorBlob = nint.Zero;

        try
        {
            fixed (byte* srcPtr = sourceBytes)
            {
                int hr = D3DCompile(
                    (nint)srcPtr,
                    (nuint)sourceBytes.Length,
                    pSourceName: "DxgiCursorCompositor.hlsl",
                    pDefines: nint.Zero,
                    pInclude: nint.Zero,
                    pEntrypoint: entryPoint,
                    pTarget: targetProfile,
                    flags1: 0u,
                    flags2: 0u,
                    out codeBlob,
                    out errorBlob);

                if (hr < 0 || codeBlob == nint.Zero)
                {
                    string errorMessage = ReadBlobString(errorBlob);
                    throw new InvalidOperationException(
                        $"D3DCompile({entryPoint}, {targetProfile}) failed (HR=0x{hr:X8}): {errorMessage}");
                }

                nint dataPtr = GetBlobBufferPointer(codeBlob);
                nuint dataSize = GetBlobBufferSize(codeBlob);
                byte[] bytecode = new byte[(int)dataSize];
                Marshal.Copy(dataPtr, bytecode, 0, bytecode.Length);
                return bytecode;
            }
        }
        finally
        {
            if (errorBlob != nint.Zero)
            {
                Marshal.Release(errorBlob);
            }

            if (codeBlob != nint.Zero)
            {
                Marshal.Release(codeBlob);
            }
        }
    }

    private static unsafe nint GetBlobBufferPointer(nint blob)
    {
        // ID3DBlob::GetBufferPointer is vtable slot 3 (after IUnknown slots 0..2).
        void** vtable = *(void***)blob;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint>)vtable[3];
        return fn(blob);
    }

    private static unsafe nuint GetBlobBufferSize(nint blob)
    {
        // ID3DBlob::GetBufferSize is vtable slot 4.
        void** vtable = *(void***)blob;
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4];
        return fn(blob);
    }

    private static string ReadBlobString(nint blob)
    {
        if (blob == nint.Zero)
        {
            return string.Empty;
        }

        nint ptr = GetBlobBufferPointer(blob);
        nuint size = GetBlobBufferSize(blob);
        if (ptr == nint.Zero || size == 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringAnsi(ptr, (int)Math.Min(size, 2048u)) ?? string.Empty;
    }

    private void ReleaseDesktopSurfaces()
    {
        _compositedRtv?.Dispose();
        _compositedRtv = null;
        _compositedTexture?.Dispose();
        _compositedTexture = null;

        _cleanDesktopSrv?.Dispose();
        _cleanDesktopSrv = null;
        _cleanDesktopTexture?.Dispose();
        _cleanDesktopTexture = null;

        _desktopWidth = 0;
        _desktopHeight = 0;
        _desktopFormat = Format.Unknown;
    }

    private void DisposeShaderResources()
    {
        _cursorSrv?.Dispose();
        _cursorSrv = null;
        _cursorTexture?.Dispose();
        _cursorTexture = null;

        _depthDisabledState?.Dispose();
        _opaqueBlendState?.Dispose();
        _rasterizerState?.Dispose();
        _pointSampler?.Dispose();
        _constantBuffer?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseDesktopSurfaces();
        DisposeShaderResources();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorConstantBuffer
    {
        public float CursorRectLeft;
        public float CursorRectTop;
        public float CursorRectWidth;
        public float CursorRectHeight;
        public float ScreenWidth;
        public float ScreenHeight;
        public uint CursorType;
        public uint IsHdrLinear;
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        nint pSrcData,
        nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string pSourceName,
        nint pDefines,
        nint pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1,
        uint flags2,
        out nint ppCode,
        out nint ppErrorMsgs);
}
