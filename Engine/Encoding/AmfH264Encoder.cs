using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Video;
using ClipThat.Models;
using ClipThat.Services;
using Vortice.Direct3D11;

namespace ClipThat.Engine.Encoding;

/// <summary>
/// Native zero-copy AMD Advanced Media Framework (<c>amfrt64.dll</c>) hardware H.264 encoder.
/// Feeds <see cref="ID3D11Texture2D"/> NV12 surfaces directly on the GPU via
/// <c>AMFContext::CreateSurfaceFromDX11Native</c>, bypassing broken or stalled AMD Media Foundation MFTs
/// (including RDNA4 / RX 9000 series) and delivering ~0.05–0.5% CPU overhead with Peak-Constrained VBR.
/// </summary>
public sealed unsafe class AmfH264Encoder : IVideoEncoder
{
    private const string AmfDllName = "amfrt64.dll";
    private const string AmfEncoderComponentId = "AMFVideoEncoderVCE_AVC";
    private const int AmfOk = 0;
    private const int AmfEof = 23;
    private const int AmfRepeat = 24;
    private const int AmfInputFull = 25;

    private const int AmfDx11_1 = 111;
    private const int AmfDx11_0 = 110;
    private const int AmfSurfaceNv12 = 1;

    private const long UsageLowLatency = 2;
    private const long QualityPresetBalanced = 0;
    private const long ProfileHigh = 100;
    private const long RateControlPeakConstrainedVbr = 2;
    private const long PictureTypeIdr = 2;
    private const long OutputDataTypeIdr = 0;
    private const long ColorProfileBt709Studio = 1;
    private const long ColorPrimariesBt709 = 1;
    private const long ColorTransferBt709 = 1;
    private const long ColorMatrixBt709 = 1;
    private const long QueryTimeoutMs = 50;
    private const long ForcedKeyframeInterval100Ns = 2L * TimeSpan.TicksPerSecond;

    private readonly GpuVideoProcessor _gpuProcessor;
    private readonly Action<EncodedPacket> _onPacketEncoded;
    private readonly Action<ReadOnlyMemory<byte>>? _onSequenceHeader;
    private readonly Action<Exception>? _onEncoderError;
    private readonly object _submitLock = new();
    private readonly object _inFlightLock = new();
    private readonly nint[] _recentInFlightGpuTextures = new nint[4];
    private int _recentGpuTextureCursor;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly uint _targetBitrateBps;
    private readonly uint _peakBitrateBps;
    private readonly long _defaultFrameDuration100Ns;

    private nint _amfLibrary;
    private nint _context;
    private nint _encoder;
    private Thread? _outputDrainThread;

    // Cached AMF vtable function pointers for zero-overhead hot path calls.
    private delegate* unmanaged[Stdcall]<nint, int> _ctxRelease;
    private delegate* unmanaged[Stdcall]<nint, int> _ctxTerminate;
    private delegate* unmanaged[Stdcall]<nint, nint, nint*, nint, int> _ctxCreateSurfaceFromDx11;

    private delegate* unmanaged[Stdcall]<nint, int> _encRelease;
    private delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int> _encSetProperty;
    private delegate* unmanaged[Stdcall]<nint, char*, AmfVariant*, int> _encGetProperty;
    private delegate* unmanaged[Stdcall]<nint, int> _encTerminate;
    private delegate* unmanaged[Stdcall]<nint, int> _encDrain;
    private delegate* unmanaged[Stdcall]<nint, nint, int> _encSubmitInput;
    private delegate* unmanaged[Stdcall]<nint, nint*, int> _encQueryOutput;

    private long _lastOutputTimestamp100Ns = -1;
    private long _lastKeyframeSubmitTimestamp100Ns = long.MinValue;
    private int _submittedFrameCount;
    private int _encodedPacketCount;
    private int _encodedKeyframeCount;
    private byte[]? _sequenceHeader;
    private int _disposed;

    public AmfH264Encoder(
        GpuVideoProcessor gpuProcessor,
        ResolutionPreset resolutionPreset,
        int targetFps,
        ResourceEstimatorService resourceEstimator,
        Action<EncodedPacket> onPacketEncoded,
        Action<ReadOnlyMemory<byte>>? onSequenceHeader = null,
        Action<Exception>? onEncoderError = null)
    {
        ArgumentNullException.ThrowIfNull(gpuProcessor);
        ArgumentNullException.ThrowIfNull(resourceEstimator);
        ArgumentNullException.ThrowIfNull(onPacketEncoded);

        if (!gpuProcessor.SupportsHardwareVideoProcessor)
        {
            throw new InvalidOperationException("Natywny koder AMD AMF wymaga sprzętowego potoku D3D11 NV12.");
        }

        _gpuProcessor = gpuProcessor;
        _onPacketEncoded = onPacketEncoded;
        _onSequenceHeader = onSequenceHeader;
        _onEncoderError = onEncoderError;

        _width = Math.Max(2, gpuProcessor.OutputWidth & ~1);
        _height = Math.Max(2, gpuProcessor.OutputHeight & ~1);
        _fps = targetFps == 30 ? 30 : 60;
        _defaultFrameDuration100Ns = TimeSpan.TicksPerSecond / _fps;

        double targetMbps = resourceEstimator.GetVideoBitrateMbps(resolutionPreset, _fps);
        double peakMbps = resourceEstimator.GetPeakVideoBitrateMbps(resolutionPreset, _fps);
        _targetBitrateBps = (uint)Math.Clamp((long)Math.Round(targetMbps * 1_000_000.0), 250_000L, 100_000_000L);
        _peakBitrateBps = (uint)Math.Clamp((long)Math.Round(peakMbps * 1_000_000.0), _targetBitrateBps, 100_000_000L);

        try
        {
            InitializeAmfPipeline();

            EngineLogger.Info(
                "AmfH264Encoder",
                $"Active GPU adapter: \"{_gpuProcessor.AdapterName}\" | Active encoder: \"{EncoderName}\" | " +
                $"Tier={ActiveTier} | ZeroCopyGpuInput=True | RateControl=PeakConstrainedVBR | " +
                $"Output={_width}x{_height}@{_fps}fps (target {_targetBitrateBps / 1_000_000.0:0.0} Mbps / peak {_peakBitrateBps / 1_000_000.0:0.0} Mbps, GOP={_fps * 2})");
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "AmfH264Encoder",
                $"Native AMD AMF initialization failed on \"{_gpuProcessor.AdapterName}\" ({ex.Message}); will fall back to Media Foundation.",
                ex);
            Dispose();
            throw;
        }
    }

    public string EncoderName => "AMD AMF H.264";

    public EncoderNegotiationTier ActiveTier => EncoderNegotiationTier.HardwareGpuZeroCopy;

    public bool IsHardwareAccelerated => true;

    public int OutputWidth => _width;

    public int OutputHeight => _height;

    public int TargetFps => _fps;

    public uint TargetBitrateBps => _targetBitrateBps;

    public int SubmittedFrameCount => Volatile.Read(ref _submittedFrameCount);

    public int EncodedPacketCount => Volatile.Read(ref _encodedPacketCount);

    public int EncodedKeyframeCount => Volatile.Read(ref _encodedKeyframeCount);

    public int NeedInputEventCount => SubmittedFrameCount;

    public int HaveOutputEventCount => EncodedPacketCount;

    public bool HasEmittedKeyframe => Volatile.Read(ref _encodedKeyframeCount) > 0;

    /// <summary>
    /// Returns <see langword="true"/> when the capture GPU appears to be an AMD Radeon adapter
    /// and the system AMD AMF runtime (<c>amfrt64.dll</c>) can be loaded.
    /// </summary>
    public static bool ShouldTryForAdapter(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return false;
        }

        bool isAmd = adapterName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                     adapterName.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        if (!isAmd)
        {
            return false;
        }

        if (NativeLibrary.TryLoad(AmfDllName, out nint handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }

    public bool IsTextureInUse(ID3D11Texture2D texture)
    {
        if (texture is null)
        {
            return false;
        }

        nint ptr = texture.NativePointer;
        if (ptr == nint.Zero)
        {
            return false;
        }

        lock (_inFlightLock)
        {
            for (int i = 0; i < _recentInFlightGpuTextures.Length; i++)
            {
                if (_recentInFlightGpuTextures[i] == ptr)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool EncodeFrame(ID3D11Texture2D frameTexture, long timestamp100Ns, long duration100Ns, bool forceKeyFrame = false)
    {
        ArgumentNullException.ThrowIfNull(frameTexture);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        long effectiveDuration = duration100Ns > 0 ? duration100Ns : _defaultFrameDuration100Ns;
        nint texturePtr = frameTexture.NativePointer;
        if (texturePtr == nint.Zero)
        {
            return false;
        }

        lock (_submitLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _context == 0 || _encoder == 0)
            {
                return false;
            }

            nint surface = 0;
            int createRes = _ctxCreateSurfaceFromDx11(_context, texturePtr, &surface, 0);
            if (createRes != AmfOk || surface == 0)
            {
                throw new InvalidOperationException($"AMF CreateSurfaceFromDX11Native zwróciło błąd ({createRes}).");
            }

            nint* surfVtbl = *(nint**)surface;
            var surfRelease = (delegate* unmanaged[Stdcall]<nint, int>)surfVtbl[1];
            var surfSetProp = (delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int>)surfVtbl[3];
            var surfSetPts = (delegate* unmanaged[Stdcall]<nint, long, void>)surfVtbl[19];
            var surfSetDuration = (delegate* unmanaged[Stdcall]<nint, long, void>)surfVtbl[21];

            try
            {
                surfSetPts(surface, timestamp100Ns);
                surfSetDuration(surface, effectiveDuration);

                bool requestIdr = forceKeyFrame ||
                                  Volatile.Read(ref _submittedFrameCount) == 0 ||
                                  _lastKeyframeSubmitTimestamp100Ns == long.MinValue ||
                                  (timestamp100Ns - _lastKeyframeSubmitTimestamp100Ns) >= ForcedKeyframeInterval100Ns;

                if (requestIdr)
                {
                    SetInt64Property(surface, surfSetProp, "ForcePictureType", PictureTypeIdr);
                    SetBoolProperty(surface, surfSetProp, "InsertSPS", true);
                    SetBoolProperty(surface, surfSetProp, "InsertPPS", true);
                }

                lock (_inFlightLock)
                {
                    _recentInFlightGpuTextures[_recentGpuTextureCursor] = texturePtr;
                    _recentGpuTextureCursor = (_recentGpuTextureCursor + 1) % _recentInFlightGpuTextures.Length;
                }

                int submitRes = _encSubmitInput(_encoder, surface);
                if (submitRes == AmfInputFull)
                {
                    // Drop frame cleanly instead of blocking capture if GPU queue is momentarily saturated.
                    return false;
                }

                if (submitRes != AmfOk)
                {
                    throw new InvalidOperationException($"AMF SubmitInput zwróciło błąd ({submitRes}).");
                }

                if (requestIdr)
                {
                    _lastKeyframeSubmitTimestamp100Ns = timestamp100Ns;
                }

                Interlocked.Increment(ref _submittedFrameCount);
                return true;
            }
            finally
            {
                surfRelease(surface);
            }
        }
    }

    private void InitializeAmfPipeline()
    {
        _amfLibrary = NativeLibrary.Load(AmfDllName);

        var queryVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)NativeLibrary.GetExport(_amfLibrary, "AMFQueryVersion");
        var amfInit = (delegate* unmanaged[Cdecl]<ulong, nint*, int>)NativeLibrary.GetExport(_amfLibrary, "AMFInit");

        ulong runtimeVersion = 0;
        int verRes = queryVersion(&runtimeVersion);
        if (verRes != AmfOk || runtimeVersion == 0)
        {
            throw new InvalidOperationException($"AMFQueryVersion nie powiodło się (kod {verRes}).");
        }

        // Request AMF 1.4.24 (broadly supported across Adrenalin drivers) or clamp to installed runtime version.
        ulong requestedVersion = (1UL << 48) | (4UL << 32) | (24UL << 16);
        if (runtimeVersion < requestedVersion)
        {
            requestedVersion = runtimeVersion;
        }

        nint factory = 0;
        int initRes = amfInit(requestedVersion, &factory);
        if (initRes != AmfOk || factory == 0)
        {
            throw new InvalidOperationException($"AMFInit nie powiodło się (kod {initRes}).");
        }

        nint* facVtbl = *(nint**)factory;
        var createContext = (delegate* unmanaged[Stdcall]<nint, nint*, int>)facVtbl[0];
        var createComponent = (delegate* unmanaged[Stdcall]<nint, nint, char*, nint*, int>)facVtbl[1];

        nint context = 0;
        int ctxRes = createContext(factory, &context);
        if (ctxRes != AmfOk || context == 0)
        {
            throw new InvalidOperationException($"AMF CreateContext nie powiodło się (kod {ctxRes}).");
        }

        _context = context;
        nint* ctxVtbl = *(nint**)context;
        _ctxRelease = (delegate* unmanaged[Stdcall]<nint, int>)ctxVtbl[1];
        _ctxTerminate = (delegate* unmanaged[Stdcall]<nint, int>)ctxVtbl[13];
        var ctxInitDx11 = (delegate* unmanaged[Stdcall]<nint, nint, int, int>)ctxVtbl[18];
        _ctxCreateSurfaceFromDx11 = (delegate* unmanaged[Stdcall]<nint, nint, nint*, nint, int>)ctxVtbl[49];

        nint d3d11DevicePtr = _gpuProcessor.Device.NativePointer;
        int dx11Res = ctxInitDx11(context, d3d11DevicePtr, AmfDx11_1);
        if (dx11Res != AmfOk)
        {
            dx11Res = ctxInitDx11(context, d3d11DevicePtr, AmfDx11_0);
        }

        if (dx11Res != AmfOk)
        {
            throw new InvalidOperationException($"AMF InitDX11 nie powiodło się (kod {dx11Res}).");
        }

        nint encoder = 0;
        fixed (char* componentId = AmfEncoderComponentId)
        {
            int compRes = createComponent(factory, context, componentId, &encoder);
            if (compRes != AmfOk || encoder == 0)
            {
                throw new InvalidOperationException($"AMF CreateComponent({AmfEncoderComponentId}) nie powiodło się (kod {compRes}).");
            }
        }

        _encoder = encoder;
        nint* encVtbl = *(nint**)encoder;
        _encRelease = (delegate* unmanaged[Stdcall]<nint, int>)encVtbl[1];
        _encSetProperty = (delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int>)encVtbl[3];
        _encGetProperty = (delegate* unmanaged[Stdcall]<nint, char*, AmfVariant*, int>)encVtbl[4];
        var encInit = (delegate* unmanaged[Stdcall]<nint, int, int, int, int>)encVtbl[17];
        _encTerminate = (delegate* unmanaged[Stdcall]<nint, int>)encVtbl[19];
        _encDrain = (delegate* unmanaged[Stdcall]<nint, int>)encVtbl[20];
        _encSubmitInput = (delegate* unmanaged[Stdcall]<nint, nint, int>)encVtbl[22];
        _encQueryOutput = (delegate* unmanaged[Stdcall]<nint, nint*, int>)encVtbl[23];

        int gopFrames = _fps * 2;
        SetInt64Property(encoder, _encSetProperty, "Usage", UsageLowLatency);
        SetInt64Property(encoder, _encSetProperty, "QualityPreset", QualityPresetBalanced);
        SetInt64Property(encoder, _encSetProperty, "Profile", ProfileHigh);
        SetSizeProperty(encoder, _encSetProperty, "FrameSize", _width, _height);
        SetRateProperty(encoder, _encSetProperty, "FrameRate", (uint)_fps, 1u);
        SetInt64Property(encoder, _encSetProperty, "RateControlMethod", RateControlPeakConstrainedVbr);
        SetInt64Property(encoder, _encSetProperty, "TargetBitrate", _targetBitrateBps);
        SetInt64Property(encoder, _encSetProperty, "PeakBitrate", _peakBitrateBps);
        SetInt64Property(encoder, _encSetProperty, "IDRPeriod", gopFrames);
        SetInt64Property(encoder, _encSetProperty, "HeaderInsertionSpacing", gopFrames);
        SetInt64Property(encoder, _encSetProperty, "BPicturesPattern", 0);
        SetBoolProperty(encoder, _encSetProperty, "FillerDataEnable", false);
        SetInt64Property(encoder, _encSetProperty, "QueryTimeout", QueryTimeoutMs);

        // Explicit BT.709 studio-range (16-235) colorimetry matching GpuVideoProcessor NV12 output.
        SetBoolProperty(encoder, _encSetProperty, "InputFullRangeColor", false);
        SetBoolProperty(encoder, _encSetProperty, "FullRangeColor", false);
        SetInt64Property(encoder, _encSetProperty, "InColorProfile", ColorProfileBt709Studio);
        SetInt64Property(encoder, _encSetProperty, "OutColorProfile", ColorProfileBt709Studio);
        SetInt64Property(encoder, _encSetProperty, "InColorPrimaries", ColorPrimariesBt709);
        SetInt64Property(encoder, _encSetProperty, "OutColorPrimaries", ColorPrimariesBt709);
        SetInt64Property(encoder, _encSetProperty, "InColorTransferChar", ColorTransferBt709);
        SetInt64Property(encoder, _encSetProperty, "OutColorTransferChar", ColorTransferBt709);
        SetInt64Property(encoder, _encSetProperty, "InMatrixCoeff", ColorMatrixBt709);
        SetInt64Property(encoder, _encSetProperty, "OutMatrixCoeff", ColorMatrixBt709);

        int encInitRes = encInit(encoder, AmfSurfaceNv12, _width, _height);
        if (encInitRes != AmfOk)
        {
            throw new InvalidOperationException($"AMF Encoder.Init(NV12, {_width}x{_height}) nie powiodło się (kod {encInitRes}).");
        }

        TryCaptureSequenceHeader();

        _outputDrainThread = new Thread(OutputDrainLoop)
        {
            Name = "ClipThat.AmfH264Encoder.Output",
            IsBackground = true
        };
        _outputDrainThread.SetApartmentState(ApartmentState.MTA);
        _outputDrainThread.Start();
    }

    private void TryCaptureSequenceHeader()
    {
        if (_encoder == 0)
        {
            return;
        }

        AmfVariant extraVar = default;
        fixed (char* propName = "ExtraData")
        {
            int res = _encGetProperty(_encoder, propName, &extraVar);
            if (res != AmfOk || extraVar.Type != AmfVariant.TypeInterface || extraVar.Int64Value == 0)
            {
                return;
            }
        }

        nint extraBuf = (nint)extraVar.Int64Value;
        nint* bufVtbl = *(nint**)extraBuf;
        var bufRelease = (delegate* unmanaged[Stdcall]<nint, int>)bufVtbl[1];
        var bufGetSize = (delegate* unmanaged[Stdcall]<nint, nuint>)bufVtbl[24];
        var bufGetNative = (delegate* unmanaged[Stdcall]<nint, nint>)bufVtbl[25];

        try
        {
            nuint size = bufGetSize(extraBuf);
            nint ptr = bufGetNative(extraBuf);
            if (size > 0 && size <= 16384 && ptr != 0)
            {
                byte[] header = new byte[(int)size];
                Marshal.Copy(ptr, header, 0, header.Length);
                _sequenceHeader = header;
                _onSequenceHeader?.Invoke(header);
            }
        }
        finally
        {
            bufRelease(extraBuf);
        }
    }

    private void OutputDrainLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            nint encoder = _encoder;
            if (encoder == 0)
            {
                break;
            }

            long beforeQuery = Stopwatch.GetTimestamp();
            nint outData = 0;
            int qRes = _encQueryOutput(encoder, &outData);

            if (qRes == AmfOk && outData != 0)
            {
                try
                {
                    EmitEncodedBuffer(outData);
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        EngineLogger.Error("AmfH264Encoder", $"Failed processing AMF output buffer: {ex.Message}", ex);
                        _onEncoderError?.Invoke(ex);
                    }

                    break;
                }

                continue;
            }

            if (qRes == AmfEof)
            {
                break;
            }

            if (qRes == AmfRepeat)
            {
                // Older drivers that ignore QueryTimeout return AMF_REPEAT immediately; avoid CPU spin.
                if (Stopwatch.GetElapsedTime(beforeQuery).TotalMilliseconds < 2.0)
                {
                    Thread.Sleep(2);
                }

                continue;
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                var ex = new InvalidOperationException($"AMF QueryOutput zakończyło się błędem ({qRes}).");
                EngineLogger.Error("AmfH264Encoder", ex.Message, ex);
                _onEncoderError?.Invoke(ex);
            }

            break;
        }
    }

    private void EmitEncodedBuffer(nint outData)
    {
        nint* bufVtbl = *(nint**)outData;
        var bufRelease = (delegate* unmanaged[Stdcall]<nint, int>)bufVtbl[1];
        var bufGetProp = (delegate* unmanaged[Stdcall]<nint, char*, AmfVariant*, int>)bufVtbl[4];
        var bufGetPts = (delegate* unmanaged[Stdcall]<nint, long>)bufVtbl[20];
        var bufGetDuration = (delegate* unmanaged[Stdcall]<nint, long>)bufVtbl[22];
        var bufGetSize = (delegate* unmanaged[Stdcall]<nint, nuint>)bufVtbl[24];
        var bufGetNative = (delegate* unmanaged[Stdcall]<nint, nint>)bufVtbl[25];

        EncodedPacket? packet = null;
        try
        {
            nuint rawSize = bufGetSize(outData);
            nint nativePtr = bufGetNative(outData);
            if (rawSize == 0 || rawSize > int.MaxValue || nativePtr == 0)
            {
                return;
            }

            int length = (int)rawSize;
            long duration100Ns = bufGetDuration(outData);
            if (duration100Ns <= 0)
            {
                duration100Ns = _defaultFrameDuration100Ns;
            }

            long pts100Ns = NormalizeTimestamp(bufGetPts(outData), duration100Ns);

            bool isIdrProperty = false;
            AmfVariant dtVar = default;
            fixed (char* propName = "OutputDataType")
            {
                if (bufGetProp(outData, propName, &dtVar) == AmfOk &&
                    dtVar.Type == AmfVariant.TypeInt64 &&
                    dtVar.Int64Value == OutputDataTypeIdr)
                {
                    isIdrProperty = true;
                }
            }

            var nativeSpan = new ReadOnlySpan<byte>((void*)nativePtr, length);
            bool hasAnnexBIdr = AnnexBHelper.ContainsKeyframeNal(nativeSpan);
            bool isKeyframe = isIdrProperty || hasAnnexBIdr;

            packet = EncodedPacket.CreateFromNativeMemory(
                MediaTrackType.Video,
                nativePtr,
                length,
                pts100Ns,
                duration100Ns,
                isKeyframe);
        }
        finally
        {
            bufRelease(outData);
        }

        if (packet is null)
        {
            return;
        }

        try
        {
            if (packet.IsKeyframe && _sequenceHeader is null)
            {
                byte[]? extracted = AnnexBHelper.TryExtractSpsPps(packet.Data);
                if (extracted is not null)
                {
                    _sequenceHeader = extracted;
                    _onSequenceHeader?.Invoke(extracted);
                }
            }

            int packetNumber = Interlocked.Increment(ref _encodedPacketCount);
            if (packet.IsKeyframe && Interlocked.Increment(ref _encodedKeyframeCount) == 1)
            {
                EngineLogger.Info(
                    "AmfH264Encoder",
                    $"First encoded H.264 keyframe: Bytes={packet.Length}, IsKeyFrame=True, " +
                    $"Tier={ActiveTier}, Encoder=\"{EncoderName}\"");
            }
            else if (packetNumber == 1)
            {
                EngineLogger.Info(
                    "AmfH264Encoder",
                    $"First encoded H.264 packet (non-keyframe): Bytes={packet.Length}, Encoder=\"{EncoderName}\"");
            }

            _onPacketEncoded(packet);
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private long NormalizeTimestamp(long candidate, long fallbackDuration100Ns)
    {
        if (candidate < 0)
        {
            candidate = _lastOutputTimestamp100Ns >= 0
                ? _lastOutputTimestamp100Ns + fallbackDuration100Ns
                : 0L;
        }

        if (_lastOutputTimestamp100Ns >= 0 && candidate <= _lastOutputTimestamp100Ns)
        {
            candidate = _lastOutputTimestamp100Ns + 1;
        }

        _lastOutputTimestamp100Ns = candidate;
        return candidate;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool lockAcquired = Monitor.TryEnter(_submitLock, 1500);
        try
        {
            if (_encoder != 0 && _encDrain != null)
            {
                try
                {
                    _encDrain(_encoder);
                }
                catch
                {
                    // Best-effort drain to unblock QueryOutput with AMF_EOF.
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                Monitor.Exit(_submitLock);
            }
        }

        Thread? drainThread = Interlocked.Exchange(ref _outputDrainThread, null);
        bool drainExited = true;
        if (drainThread is not null && drainThread != Thread.CurrentThread)
        {
            drainExited = drainThread.Join(2000);
        }

        lock (_inFlightLock)
        {
            Array.Clear(_recentInFlightGpuTextures);
        }

        if (!drainExited || !lockAcquired)
        {
            EngineLogger.Warn(
                "AmfH264Encoder",
                $"AMF output thread did not exit cleanly (drainExited={drainExited}, lockAcquired={lockAcquired}); skipping unsafe native teardown.");
            return;
        }

        if (_encoder != 0)
        {
            try { _encTerminate(_encoder); } catch { /* ignore */ }
            try { _encRelease(_encoder); } catch { /* ignore */ }
            _encoder = 0;
        }

        if (_context != 0)
        {
            try { _ctxTerminate(_context); } catch { /* ignore */ }
            try { _ctxRelease(_context); } catch { /* ignore */ }
            _context = 0;
        }

        if (_amfLibrary != 0)
        {
            try { NativeLibrary.Free(_amfLibrary); } catch { /* ignore */ }
            _amfLibrary = 0;
        }
    }

    private static void SetInt64Property(nint obj, delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int> setProp, string name, long value)
    {
        fixed (char* pName = name)
        {
            _ = setProp(obj, pName, AmfVariant.FromInt64(value));
        }
    }

    private static void SetBoolProperty(nint obj, delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int> setProp, string name, bool value)
    {
        fixed (char* pName = name)
        {
            _ = setProp(obj, pName, AmfVariant.FromBool(value));
        }
    }

    private static void SetSizeProperty(nint obj, delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int> setProp, string name, int width, int height)
    {
        fixed (char* pName = name)
        {
            _ = setProp(obj, pName, AmfVariant.FromSize(width, height));
        }
    }

    private static void SetRateProperty(nint obj, delegate* unmanaged[Stdcall]<nint, char*, AmfVariant, int> setProp, string name, uint num, uint den)
    {
        fixed (char* pName = name)
        {
            _ = setProp(obj, pName, AmfVariant.FromRate(num, den));
        }
    }

    /// <summary>
    /// Exact x64 MSVC ABI layout of AMD AMF's 24-byte <c>AMFVariantStruct</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct AmfVariant
    {
        public const int TypeBool = 1;
        public const int TypeInt64 = 2;
        public const int TypeSize = 5;
        public const int TypeRate = 7;
        public const int TypeInterface = 12;

        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public byte BoolValue;
        [FieldOffset(8)] public long Int64Value;
        [FieldOffset(8)] public int Width;
        [FieldOffset(12)] public int Height;
        [FieldOffset(8)] public uint Num;
        [FieldOffset(12)] public uint Den;

        public static AmfVariant FromBool(bool value) => new() { Type = TypeBool, BoolValue = value ? (byte)1 : (byte)0 };
        public static AmfVariant FromInt64(long value) => new() { Type = TypeInt64, Int64Value = value };
        public static AmfVariant FromSize(int width, int height) => new() { Type = TypeSize, Width = width, Height = height };
        public static AmfVariant FromRate(uint num, uint den) => new() { Type = TypeRate, Num = num, Den = den };
    }
}
