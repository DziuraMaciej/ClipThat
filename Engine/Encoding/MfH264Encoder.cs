using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Diagnostics;
using ClipThat.Engine.Interop;
using ClipThat.Engine.Video;
using ClipThat.Models;
using ClipThat.Services;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace ClipThat.Engine.Encoding;

/// <summary>
/// Identifies which negotiation tier succeeded when activating a Media Foundation H.264 encoder MFT.
/// </summary>
public enum EncoderNegotiationTier
{
    None = 0,
    HardwareGpuZeroCopy = 1,
    HardwareCpuBuffer = 2,
    SoftwareFallback = 3
}

/// <summary>
/// Universal 3-tier Media Foundation H.264 encoder (<see cref="IMFTransform"/>) supporting
/// NVIDIA NVENC, AMD AMF, Intel QuickSync, hybrid iGPU+dGPU systems, and Windows' built-in
/// synchronous <c>CLSID_CMSH264EncoderMFT</c> software encoder with guaranteed even frame dimensions,
/// continuous async <see cref="IMFMediaEventGenerator"/> draining, atomic input-slot tracking,
/// explicit initial keyframe forcing, and dual <c>MFSampleExtension_CleanPoint</c> + Annex-B IDR/SPS detection.
/// </summary>
public sealed class MfH264Encoder : IDisposable
{
    private const uint ClsCtxInprocServer = 0x1u;
    private const uint H264ProfileHigh = 100u;
    private const uint H264ProfileMain = 77u;
    private const uint H264ProfileBase = 66u;
    private const uint RateControlModeCbr = 0u;
    private const uint RateControlModeUnconstrainedVbr = 2u;
    private const int DefaultOutputBufferBytes = 512 * 1024;
    private const int MaxPendingAsyncInputSamples = 3;
    private const int MaxStreamChangeRetriesPerEvent = 4;
    private const int MaxIncompleteOutputDrainsPerEvent = 8;

    // Verified Windows SDK wmcodecdsp.h / mfapi.h / codecapi.h / icodecapi.h GUIDs
    private static readonly Guid ClsidMsH264EncoderMft = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IidImfTransform = typeof(IMFTransform).GUID;
    private static readonly Guid MftEnumAdapterLuidKey = new("1D39518C-E220-4DA8-A07F-BA172552D6B1");
    private static readonly Guid CodecApiIid = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");
    private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiAvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-BE7D96342F2D");
    private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private readonly GpuVideoProcessor _gpuProcessor;
    private readonly Action<EncodedPacket> _onPacketEncoded;
    private readonly Action<ReadOnlyMemory<byte>>? _onSequenceHeader;
    private readonly Action<Exception>? _onEncoderError;
    private readonly object _mftLock = new();
    private readonly object _queueLock = new();
    private readonly Queue<IMFSample> _pendingInputQueue = new(MaxPendingAsyncInputSamples + 1);
    private readonly AutoResetEvent _inputWorkSignal = new(false);
    private readonly ManualResetEventSlim _drainCompleteEvent = new(false);

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly uint _targetBitrateBps;
    private readonly long _defaultFrameDuration100Ns;
    private readonly EncoderNegotiationTier _minimumTier;

    private bool _mfStarted;
    private IMFActivate? _activeActivate;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _eventGenerator;
    private IMFDXGIDeviceManager? _dxgiDeviceManager;
    private Thread? _eventThread;
    private Thread? _inputFeederThread;

    private int _inputStreamId;
    private int _outputStreamId;
    private OutputStreamInfo _outputStreamInfo;
    private bool _mftProvidesOutputSamples;
    private bool _isAsync;
    private bool _usesGpuInput;
    private int _availableInputSlots;
    private int _needInputEventCount;
    private int _haveOutputEventCount;
    private int _submittedFrameCount;
    private int _encodedPacketCount;
    private int _encodedKeyframeCount;
    private byte[]? _sequenceHeader;
    private int _stopping;
    private int _disposed;

    public MfH264Encoder(
        GpuVideoProcessor gpuProcessor,
        ResolutionPreset resolutionPreset,
        int targetFps,
        ResourceEstimatorService resourceEstimator,
        Action<EncodedPacket> onPacketEncoded,
        Action<ReadOnlyMemory<byte>>? onSequenceHeader = null,
        Action<Exception>? onEncoderError = null,
        EncoderNegotiationTier minimumTier = EncoderNegotiationTier.HardwareGpuZeroCopy)
    {
        ArgumentNullException.ThrowIfNull(gpuProcessor);
        ArgumentNullException.ThrowIfNull(resourceEstimator);
        ArgumentNullException.ThrowIfNull(onPacketEncoded);

        _gpuProcessor = gpuProcessor;
        _onPacketEncoded = onPacketEncoded;
        _onSequenceHeader = onSequenceHeader;
        _onEncoderError = onEncoderError;
        _minimumTier = minimumTier == EncoderNegotiationTier.None
            ? EncoderNegotiationTier.HardwareGpuZeroCopy
            : minimumTier;

        // Guarantee strictly even H.264/NV12 dimensions on all GPUs and drivers.
        _width = Math.Max(2, gpuProcessor.OutputWidth & ~1);
        _height = Math.Max(2, gpuProcessor.OutputHeight & ~1);
        _fps = targetFps == 30 ? 30 : 60;
        _defaultFrameDuration100Ns = TimeSpan.TicksPerSecond / _fps;

        double bitrateMbps = resourceEstimator.GetVideoBitrateMbps(resolutionPreset, _fps);
        _targetBitrateBps = (uint)Math.Clamp((long)Math.Round(bitrateMbps * 1_000_000.0), 250_000L, 100_000_000L);

        try
        {
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            _mfStarted = true;

            InitializeEncoderMft();

            EngineLogger.Info(
                "MfH264Encoder",
                $"Active GPU adapter: \"{_gpuProcessor.AdapterName}\" | Active MFT encoder: \"{EncoderName}\" | " +
                $"Tier={ActiveTier} | Hardware={IsHardwareAccelerated} | ZeroCopyGpuInput={UsesGpuZeroCopyInput} | " +
                $"Async={IsAsyncMft} | Output={_width}x{_height}@{_fps}fps ({_targetBitrateBps / 1_000_000.0:0.0} Mbps)");
        }
        catch (Exception ex)
        {
            EngineLogger.Error(
                "MfH264Encoder",
                $"Failed to initialize any H.264 Media Foundation encoder on GPU \"{_gpuProcessor.AdapterName}\" ({_width}x{_height}@{_fps}fps, minTier={_minimumTier}).",
                ex);
            Dispose();
            throw;
        }
    }

    public string EncoderName { get; private set; } = "H.264";

    public EncoderNegotiationTier ActiveTier { get; private set; } = EncoderNegotiationTier.None;

    public bool IsHardwareAccelerated { get; private set; }

    public bool UsesGpuZeroCopyInput => _usesGpuInput;

    public bool IsAsyncMft => _isAsync;

    public int OutputWidth => _width;

    public int OutputHeight => _height;

    public int TargetFps => _fps;

    public uint TargetBitrateBps => _targetBitrateBps;

    public int SubmittedFrameCount => Volatile.Read(ref _submittedFrameCount);

    public int EncodedPacketCount => Volatile.Read(ref _encodedPacketCount);

    public int EncodedKeyframeCount => Volatile.Read(ref _encodedKeyframeCount);

    public int NeedInputEventCount => Volatile.Read(ref _needInputEventCount);

    public int HaveOutputEventCount => Volatile.Read(ref _haveOutputEventCount);

    public bool HasEmittedKeyframe => Volatile.Read(ref _encodedKeyframeCount) > 0;

    /// <summary>
    /// Submits a single NV12 (or BGRA fallback) texture produced by <see cref="GpuVideoProcessor"/> to the encoder.
    /// For async MFTs, queues the sample without blocking the WGC callback thread and wakes the dedicated
    /// input feeder, which submits via <c>ProcessInput</c> strictly as <c>METransformNeedInput</c> slots arrive.
    /// </summary>
    public bool EncodeFrame(ID3D11Texture2D frameTexture, long timestamp100Ns, long duration100Ns)
    {
        ArgumentNullException.ThrowIfNull(frameTexture);

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0)
        {
            return false;
        }

        long effectiveDuration = duration100Ns > 0 ? duration100Ns : _defaultFrameDuration100Ns;
        List<EncodedPacket>? syncPackets = null;
        IMFSample? inputSample = null;
        bool sampleOwnershipTransferred = false;

        try
        {
            inputSample = CreateInputSample(frameTexture, timestamp100Ns, effectiveDuration);

            if (_isAsync)
            {
                lock (_queueLock)
                {
                    if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0)
                    {
                        return false;
                    }

                    if (_pendingInputQueue.Count >= MaxPendingAsyncInputSamples)
                    {
                        if (Volatile.Read(ref _submittedFrameCount) == 0)
                        {
                            // Preserve queued initial frame(s) until Frame #0 is consumed by the async MFT.
                            return false;
                        }

                        IMFSample droppedOldest = _pendingInputQueue.Dequeue();
                        SafeDisposeSample(droppedOldest);
                    }

                    _pendingInputQueue.Enqueue(inputSample);
                    sampleOwnershipTransferred = true;
                }

                SignalInputWorkSafe();
                return true;
            }

            lock (_mftLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0 || _transform is null)
                {
                    return false;
                }

                try
                {
                    SubmitInputSampleLocked(inputSample);
                }
                catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
                {
                    DrainSyncOutputsLocked(ref syncPackets);
                    SubmitInputSampleLocked(inputSample);
                }

                DrainSyncOutputsLocked(ref syncPackets);
            }

            if (syncPackets is not null)
            {
                DispatchPackets(syncPackets);
            }

            return true;
        }
        catch (Exception ex) when (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            Debug.WriteLine($"Ignorowanie błędu kodera podczas zamykania: {ex.Message}");
            DisposePacketList(syncPackets);
            return false;
        }
        catch
        {
            DisposePacketList(syncPackets);
            throw;
        }
        finally
        {
            if (!sampleOwnershipTransferred)
            {
                SafeDisposeSample(inputSample);
            }
        }
    }

    private void SubmitInputSampleLocked(IMFSample inputSample)
    {
        if (_transform is null)
        {
            return;
        }

        // Force an immediate IDR keyframe on the very first submitted frame so MediaRingBuffer
        // receives a decodable keyframe right away across NVENC, AMF, QuickSync, and Software MFT.
        if (Volatile.Read(ref _submittedFrameCount) == 0)
        {
            TryForceKeyFrameLocked(_transform);
        }

        _transform.ProcessInput(_inputStreamId, inputSample, 0);
        Interlocked.Increment(ref _submittedFrameCount);
    }

    private void InitializeEncoderMft()
    {
        var outputTypeInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        uint hardwareFlags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
        uint softwareFlags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter);

        // Tier 1: Hardware MFT (MFT_ENUM_FLAG_HARDWARE) with Zero-Copy D3D11 GPU Surface Input.
        if (_minimumTier <= EncoderNegotiationTier.HardwareGpuZeroCopy &&
            _gpuProcessor.SupportsHardwareVideoProcessor)
        {
            if (HasValidLuid(_gpuProcessor.AdapterLuid) &&
                TryActivateHardwareByAdapterLuid(hardwareFlags, outputTypeInfo, _gpuProcessor.AdapterLuid))
            {
                return;
            }

            if (TryActivateFromEnumEx(
                    hardwareFlags,
                    outputTypeInfo,
                    tier: EncoderNegotiationTier.HardwareGpuZeroCopy,
                    allowD3d11Manager: true,
                    requireD3d11Manager: true))
            {
                return;
            }
        }

        // Tier 2: Hardware MFT (MFT_ENUM_FLAG_HARDWARE) with CPU-Memory NV12 Input.
        if (_minimumTier <= EncoderNegotiationTier.HardwareCpuBuffer)
        {
            if (TryActivateFromEnumEx(
                    hardwareFlags,
                    outputTypeInfo,
                    tier: EncoderNegotiationTier.HardwareCpuBuffer,
                    allowD3d11Manager: false,
                    requireD3d11Manager: false))
            {
                return;
            }
        }

        // Tier 3: Windows' built-in synchronous H.264 encoder instantiated directly via
        // CoCreateInstance / MFCreateTransformActivate with CLSID_CMSH264EncoderMFT
        // (6ca50344-051a-4ded-9779-a43305165e35), falling back to software MFTEnumEx if needed.
        if (TryActivateBuiltinSoftwareMftDirect())
        {
            return;
        }

        if (TryActivateFromEnumEx(
                softwareFlags,
                outputTypeInfo,
                tier: EncoderNegotiationTier.SoftwareFallback,
                allowD3d11Manager: false,
                requireD3d11Manager: false))
        {
            return;
        }

        throw new InvalidOperationException(
            "Nie znaleziono działającego kodera wideo H.264 (ani sprzętowego NVENC/AMF/QuickSync, ani programowego Media Foundation).");
    }

    /// <summary>
    /// Instantiates Windows' built-in synchronous H.264 encoder (<c>CLSID_CMSH264EncoderMFT</c>) directly
    /// via <c>CoCreateInstance</c> (and <c>MFCreateTransformActivate</c> fallback) as <see cref="IMFTransform"/>,
    /// without calling <c>MF_TRANSFORM_ASYNC_UNLOCK</c>, configuring <c>SetOutputType(H264)</c> before
    /// <c>SetInputType(NV12)</c>.
    /// </summary>
    private bool TryActivateBuiltinSoftwareMftDirect()
    {
        const string builtinName = "Microsoft H264 Encoder MFT (CLSID_CMSH264EncoderMFT)";

        // Path 3A: Direct CoCreateInstance(CLSID_CMSH264EncoderMFT, IID_IMFTransform).
        try
        {
            Guid clsid = ClsidMsH264EncoderMft;
            Guid iid = IidImfTransform;
            int hr = CoCreateInstance(ref clsid, nint.Zero, ClsCtxInprocServer, ref iid, out nint transformPtr);
            if (hr >= 0 && transformPtr != nint.Zero)
            {
                var directTransform = new IMFTransform(transformPtr);
                if (TryConfigureTransform(
                        directTransform,
                        activate: null,
                        friendlyName: builtinName,
                        tier: EncoderNegotiationTier.SoftwareFallback,
                        allowD3d11Manager: false,
                        requireD3d11Manager: false,
                        allowAsyncUnlock: false))
                {
                    return true;
                }
            }
            else
            {
                EngineLogger.Warn(
                    "MfH264Encoder",
                    $"Tier SoftwareFallback: CoCreateInstance(CLSID_CMSH264EncoderMFT) returned HR=0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Tier SoftwareFallback: CoCreateInstance(CLSID_CMSH264EncoderMFT) threw:\n{ex}");
        }

        // Path 3B: MFCreateTransformActivate + MFT_TRANSFORM_CLSID_Attribute = CLSID_CMSH264EncoderMFT.
        IMFActivate? transformActivate = null;
        try
        {
            Result hr = MediaFactory.MFCreateTransformActivate(out transformActivate);
            if (hr.Success && transformActivate is not null && transformActivate.NativePointer != nint.Zero)
            {
                transformActivate.Set(TransformAttributeKeys.MftTransformClsidAttribute, ClsidMsH264EncoderMft).CheckError();
                if (TryConfigureCandidate(
                        transformActivate,
                        EncoderNegotiationTier.SoftwareFallback,
                        allowD3d11Manager: false,
                        requireD3d11Manager: false,
                        allowAsyncUnlock: false))
                {
                    transformActivate = null; // Ownership transferred to _activeActivate.
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Tier SoftwareFallback: MFCreateTransformActivate(CLSID_CMSH264EncoderMFT) threw:\n{ex}");
        }
        finally
        {
            SafeDisposeComObject(transformActivate);
        }

        return false;
    }

    private bool TryActivateHardwareByAdapterLuid(
        uint enumFlags,
        RegisterTypeInfo outputTypeInfo,
        Luid adapterLuid)
    {
        List<IMFActivate> candidates;
        try
        {
            candidates = EnumerateMftsByAdapterLuid(enumFlags, outputTypeInfo, adapterLuid);
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Tier 1 MFTEnum2 adapter-LUID enumeration failed ({adapterLuid.HighPart:X8}:{adapterLuid.LowPart:X8}):\n{ex}");
            return false;
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        return TryActivateCandidateList(
            candidates,
            EncoderNegotiationTier.HardwareGpuZeroCopy,
            allowD3d11Manager: true,
            requireD3d11Manager: true,
            allowAsyncUnlock: true);
    }

    private bool TryActivateFromEnumEx(
        uint enumFlags,
        RegisterTypeInfo outputTypeInfo,
        EncoderNegotiationTier tier,
        bool allowD3d11Manager,
        bool requireD3d11Manager)
    {
        List<IMFActivate> candidates;
        try
        {
            // Use the raw MFTEnumEx overload (not IMFActivateCollection) so individual IMFActivate COM
            // pointers remain owned by this list and are never prematurely disposed before activation.
            candidates = EnumerateMftsRaw(enumFlags, outputTypeInfo);
        }
        catch (Exception ex)
        {
            EngineLogger.Warn("MfH264Encoder", $"Tier {tier} MFTEnumEx failed:\n{ex}");
            return false;
        }

        if (candidates.Count == 0)
        {
            EngineLogger.Info("MfH264Encoder", $"Tier {tier}: MFTEnumEx returned 0 H.264 MFT candidates.");
            return false;
        }

        return TryActivateCandidateList(
            candidates,
            tier,
            allowD3d11Manager,
            requireD3d11Manager,
            allowAsyncUnlock: true);
    }

    private static List<IMFActivate> EnumerateMftsRaw(uint enumFlags, RegisterTypeInfo outputTypeInfo)
    {
        var results = new List<IMFActivate>();
        nint activatesPtr = nint.Zero;
        uint count = 0;

        try
        {
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                enumFlags,
                inputType: null,
                outputType: outputTypeInfo,
                out activatesPtr,
                out count);

            if (activatesPtr != nint.Zero && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    nint itemPtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                    if (itemPtr != nint.Zero)
                    {
                        results.Add(new IMFActivate(itemPtr));
                    }
                }
            }
        }
        finally
        {
            if (activatesPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(activatesPtr);
            }
        }

        return results;
    }

    private bool TryActivateCandidateList(
        List<IMFActivate> candidates,
        EncoderNegotiationTier tier,
        bool allowD3d11Manager,
        bool requireD3d11Manager,
        bool allowAsyncUnlock)
    {
        int winningIndex = -1;
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                IMFActivate? candidate = candidates[i];
                if (candidate is null || candidate.NativePointer == nint.Zero)
                {
                    EngineLogger.Warn("MfH264Encoder", $"Tier {tier}: Encountered null/disposed IMFActivate at index {i}; skipping.");
                    continue;
                }

                if (TryConfigureCandidate(candidate, tier, allowD3d11Manager, requireD3d11Manager, allowAsyncUnlock))
                {
                    winningIndex = i;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            DisposeCandidateList(candidates, excludeIndex: winningIndex);
        }
    }

    private static List<IMFActivate> EnumerateMftsByAdapterLuid(
        uint enumFlags,
        RegisterTypeInfo outputTypeInfo,
        Luid adapterLuid)
    {
        var results = new List<IMFActivate>();
        ulong packedLuid = PackLuid(adapterLuid);

        using IMFAttributes enumAttributes = MediaFactory.MFCreateAttributes(1);
        enumAttributes.Set(MftEnumAdapterLuidKey, packedLuid).CheckError();

        nint activatesPtr = nint.Zero;
        uint count = 0;
        try
        {
            MediaFactory.MFTEnum2(
                TransformCategoryGuids.VideoEncoder,
                enumFlags,
                inputType: null,
                outputType: outputTypeInfo,
                enumAttributes,
                out activatesPtr,
                out count);

            if (activatesPtr != nint.Zero && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    nint itemPtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                    if (itemPtr != nint.Zero)
                    {
                        results.Add(new IMFActivate(itemPtr));
                    }
                }
            }
        }
        finally
        {
            if (activatesPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(activatesPtr);
            }
        }

        return results;
    }

    private bool TryConfigureCandidate(
        IMFActivate activate,
        EncoderNegotiationTier tier,
        bool allowD3d11Manager,
        bool requireD3d11Manager,
        bool allowAsyncUnlock)
    {
        if (activate is null || activate.NativePointer == nint.Zero)
        {
            EngineLogger.Warn("MfH264Encoder", $"Tier {tier}: Skipped null/disposed IMFActivate candidate.");
            return false;
        }

        string friendlyName = TryGetFriendlyName(activate);
        IMFTransform? transform;

        try
        {
            transform = activate.ActivateObject<IMFTransform>();
            if (transform is null || transform.NativePointer == nint.Zero)
            {
                throw new InvalidOperationException($"ActivateObject<IMFTransform>() zwróciło pusty obiekt dla \"{friendlyName}\".");
            }
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Tier {tier}: Failed to activate MFT \"{friendlyName}\":\n{ex}");
            TryShutdownActivate(activate);
            return false;
        }

        return TryConfigureTransform(
            transform,
            activate,
            friendlyName,
            tier,
            allowD3d11Manager,
            requireD3d11Manager,
            allowAsyncUnlock);
    }

    private bool TryConfigureTransform(
        IMFTransform transform,
        IMFActivate? activate,
        string friendlyName,
        EncoderNegotiationTier tier,
        bool allowD3d11Manager,
        bool requireD3d11Manager,
        bool allowAsyncUnlock)
    {
        IMFDXGIDeviceManager? dxgiManager = null;
        IMFMediaEventGenerator? eventGenerator = null;

        try
        {
            if (transform is null || transform.NativePointer == nint.Zero)
            {
                throw new InvalidOperationException($"Przekazano pusty obiekt IMFTransform dla \"{friendlyName}\".");
            }

            bool isAsync = false;
            bool d3d11Aware = false;

            using (IMFAttributes? attributes = TryGetTransformAttributes(transform))
            {
                if (attributes is not null && attributes.NativePointer != nint.Zero)
                {
                    if (TryGetUInt32(attributes, TransformAttributeKeys.TransformAsync, out uint asyncFlag) &&
                        asyncFlag != 0)
                    {
                        isAsync = true;
                        if (allowAsyncUnlock)
                        {
                            // Must unlock async MFT (MF_TRANSFORM_ASYNC_UNLOCK = 1) BEFORE any other MFT calls.
                            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                        }
                    }

                    if (TryGetUInt32(attributes, TransformAttributeKeys.D3D11Aware, out uint d3d11Flag) &&
                        d3d11Flag != 0)
                    {
                        d3d11Aware = true;
                    }
                }
            }

            bool usesGpuInput = false;
            if (allowD3d11Manager && d3d11Aware && _gpuProcessor?.Device is not null)
            {
                try
                {
                    dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
                    dxgiManager.ResetDevice(_gpuProcessor.Device).CheckError();
                    transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)dxgiManager.NativePointer);
                    usesGpuInput = true;
                }
                catch (Exception ex)
                {
                    EngineLogger.Warn(
                        "MfH264Encoder",
                        $"Tier {tier}: MFT \"{friendlyName}\" rejected IMFDXGIDeviceManager: {ex.Message}");
                    SafeDisposeComObject(dxgiManager);
                    dxgiManager = null;
                    usesGpuInput = false;
                }
            }

            if (requireD3d11Manager && !usesGpuInput)
            {
                throw new InvalidOperationException(
                    $"Koder \"{friendlyName}\" nie zaakceptował trybu zero-copy D3D11 (przejście do kolejnego poziomu).");
            }

            (int inputStreamId, int outputStreamId) = ResolveStreamIds(transform);

            // Apply CodecAPI rate-control/GOP/low-latency hints prior to SetOutputType (required by QSV/AMF).
            ConfigureCodecApi(transform);

            // Strict Media Foundation H.264 MFT contract: SetOutputType MUST be called BEFORE SetInputType.
            ConfigureOutputMediaType(transform, outputStreamId);

            // Negotiate MFVideoFormat_NV12 directly from GetInputAvailableType first so NVIDIA NVENC,
            // AMD AMF, Intel QuickSync, and Microsoft Software MFT keep driver-advertised attributes.
            ConfigureInputMediaType(transform, inputStreamId);

            // Re-apply CodecAPI hints after media types are negotiated for MFTs that validate post-SetOutputType.
            ConfigureCodecApi(transform);

            OutputStreamInfo outputStreamInfo = transform.GetOutputStreamInfo(outputStreamId);
            OutputStreamInfoFlags streamFlags = (OutputStreamInfoFlags)outputStreamInfo.Flags;
            bool mftProvidesSamples =
                (streamFlags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0 ||
                (streamFlags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

            if (isAsync)
            {
                eventGenerator = transform.QueryInterface<IMFMediaEventGenerator>();
                if (eventGenerator is null || eventGenerator.NativePointer == nint.Zero)
                {
                    throw new InvalidOperationException($"Asynchroniczny MFT \"{friendlyName}\" nie implementuje IMFMediaEventGenerator.");
                }
            }

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, nuint.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, nuint.Zero);

            _activeActivate = activate;
            _transform = transform;
            _dxgiDeviceManager = dxgiManager;
            _eventGenerator = eventGenerator;
            _inputStreamId = inputStreamId;
            _outputStreamId = outputStreamId;
            _outputStreamInfo = outputStreamInfo;
            _mftProvidesOutputSamples = mftProvidesSamples;
            _isAsync = isAsync;
            _usesGpuInput = usesGpuInput;
            EncoderName = friendlyName;
            ActiveTier = tier;
            IsHardwareAccelerated = tier != EncoderNegotiationTier.SoftwareFallback;

            TryCaptureOutputSequenceHeaderLocked();

            if (_isAsync)
            {
                StartAsyncPumps();
            }

            return true;
        }
        catch (Exception ex)
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Tier {tier}: Skipped MFT \"{friendlyName}\":\n{ex}");

            SafeDisposeComObject(eventGenerator);
            SafeDisposeComObject(dxgiManager);

            if (transform is not null)
            {
                try
                {
                    MediaFactory.MFShutdownObject(transform);
                }
                catch
                {
                    // Best-effort cleanup of failed MFT candidate.
                }

                SafeDisposeComObject(transform);
            }

            TryShutdownActivate(activate);
            return false;
        }
    }

    private void ConfigureOutputMediaType(IMFTransform transform, int outputStreamId)
    {
        uint[] profileAttempts = { H264ProfileHigh, H264ProfileMain, H264ProfileBase, 0u };
        Exception? lastError = null;

        for (int i = 0; i < profileAttempts.Length; i++)
        {
            uint profile = profileAttempts[i];
            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _targetBitrateBps).CheckError();
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            outputType.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)_width, (uint)_height)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)_fps, 1u)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u)).CheckError();
            outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)_fps);

            if (profile != 0u)
            {
                outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile);
            }

            try
            {
                transform.SetOutputType(outputStreamId, outputType, 0);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Nie udało się skonfigurować typu wyjściowego H.264.");
    }

    private void ConfigureInputMediaType(IMFTransform transform, int inputStreamId)
    {
        Exception? lastError = null;

        // Primary universal path: negotiate MFVideoFormat_NV12 directly from GetInputAvailableType
        // so NVIDIA NVENC, AMD AMF, Intel QuickSync, and Software MFT keep driver-advertised attributes.
        for (int index = 0; index < 64; index++)
        {
            IMFMediaType? available;
            try
            {
                available = transform.GetInputAvailableType(inputStreamId, index);
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.NoMoreTypes)
            {
                break;
            }
            catch (SharpGenException)
            {
                break;
            }

            if (available is null || available.NativePointer == nint.Zero)
            {
                break;
            }

            using (available)
            {
                if (!available.GetGUID(MediaTypeAttributeKeys.Subtype, out Guid subtype).Success ||
                    subtype != VideoFormatGuids.NV12)
                {
                    continue;
                }

                available.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                available.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                available.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                available.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)_width, (uint)_height));
                available.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)_fps, 1u));
                available.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u));

                // Ensure any driver-advertised stride/sample-size matches the configured even NV12 frame size.
                if (available.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint advertisedStride).Success &&
                    advertisedStride != (uint)_width)
                {
                    available.Set(MediaTypeAttributeKeys.DefaultStride, (uint)_width);
                }

                uint expectedNv12SampleSize = (uint)(_width * _height * 3 / 2);
                if (available.GetUInt32(MediaTypeAttributeKeys.SampleSize, out uint advertisedSampleSize).Success &&
                    advertisedSampleSize != expectedNv12SampleSize)
                {
                    available.Set(MediaTypeAttributeKeys.SampleSize, expectedNv12SampleSize);
                }

                try
                {
                    transform.SetInputType(inputStreamId, available, 0);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }

        // Secondary fallback: explicit freshly-built NV12 progressive media type if GetInputAvailableType
        // was unimplemented or rejected attribute overrides.
        using (IMFMediaType inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            inputType.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)_width, (uint)_height)).CheckError();
            inputType.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)_fps, 1u)).CheckError();
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u)).CheckError();

            try
            {
                transform.SetInputType(inputStreamId, inputType, 0);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Koder H.264 nie zaakceptował wejściowego formatu NV12.");
    }

    private void ConfigureCodecApi(IMFTransform transform)
    {
        if (transform is null || transform.NativePointer == nint.Zero)
        {
            return;
        }

        Guid codecApiIid = CodecApiIid;
        int hr = Marshal.QueryInterface(transform.NativePointer, ref codecApiIid, out nint codecApiPtr);
        if (hr < 0 || codecApiPtr == nint.Zero)
        {
            return;
        }

        ICodecAPI? codecApi = null;
        try
        {
            codecApi = Marshal.GetUniqueObjectForIUnknown(codecApiPtr) as ICodecAPI;
            if (codecApi is null)
            {
                return;
            }

            // Enable low-latency mode if supported so AMD AMF / NVENC / QSV emit the initial IDR keyframe
            // without multi-frame lookahead buffering.
            TrySetCodecApiBool(codecApi, CodecApiAvLowLatencyMode, true);

            // Prefer CBR for deterministic RAM ring-buffer sizing; fall back to Unconstrained VBR if unsupported.
            if (!TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeCbr))
            {
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeUnconstrainedVbr);
            }

            TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonMeanBitRate, _targetBitrateBps);
            // 1 keyframe per second (GOP size == FPS).
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvGopSize, (uint)_fps);
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvDefaultBPictureCount, 0u);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ostrzeżenie konfiguracji ICodecAPI: {ex.Message}");
        }
        finally
        {
            if (codecApi is not null)
            {
                Marshal.ReleaseComObject(codecApi);
            }

            Marshal.Release(codecApiPtr);
        }
    }

    private static void TryForceKeyFrameLocked(IMFTransform transform)
    {
        if (transform is null || transform.NativePointer == nint.Zero)
        {
            return;
        }

        Guid codecApiIid = CodecApiIid;
        int hr = Marshal.QueryInterface(transform.NativePointer, ref codecApiIid, out nint codecApiPtr);
        if (hr < 0 || codecApiPtr == nint.Zero)
        {
            return;
        }

        ICodecAPI? codecApi = null;
        try
        {
            codecApi = Marshal.GetUniqueObjectForIUnknown(codecApiPtr) as ICodecAPI;
            if (codecApi is null)
            {
                return;
            }

            Guid forceKeyGuid = CodecApiAvEncVideoForceKeyFrame;
            int supportedHr = codecApi.IsSupported(ref forceKeyGuid);
            // E_NOTIMPL (0x80004001): some MFTs accept SetValue without implementing IsSupported.
            if (supportedHr == 0 || supportedHr == unchecked((int)0x80004001))
            {
                object boxed = 1u;
                codecApi.SetValue(ref forceKeyGuid, ref boxed);
            }
        }
        catch
        {
            // ForceKeyFrame is best-effort; GOP size is also configured to 1s.
        }
        finally
        {
            if (codecApi is not null)
            {
                Marshal.ReleaseComObject(codecApi);
            }

            Marshal.Release(codecApiPtr);
        }
    }

    private static bool TrySetCodecApiUInt32(ICodecAPI codecApi, Guid propertyGuid, uint value)
    {
        try
        {
            Guid api = propertyGuid;
            if (codecApi.IsSupported(ref api) != 0)
            {
                return false;
            }

            object boxed = value;
            return codecApi.SetValue(ref api, ref boxed) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetCodecApiBool(ICodecAPI codecApi, Guid propertyGuid, bool value)
    {
        try
        {
            Guid api = propertyGuid;
            if (codecApi.IsSupported(ref api) != 0)
            {
                return false;
            }

            object boxed = value;
            return codecApi.SetValue(ref api, ref boxed) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private IMFSample CreateInputSample(ID3D11Texture2D frameTexture, long timestamp100Ns, long duration100Ns)
    {
        IMFSample sample = MediaFactory.MFCreateSample();
        try
        {
            sample.SampleTime = timestamp100Ns;
            sample.SampleDuration = duration100Ns;

            if (_usesGpuInput)
            {
                using IMFMediaBuffer dxgiBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                    CaptureInterop.Direct3D11Texture2DIid,
                    frameTexture,
                    subresourceIndex: 0,
                    bottomUpWhenLinear: false);

                using IMF2DBuffer? buffer2D = dxgiBuffer.QueryInterfaceOrNull<IMF2DBuffer>();
                if (buffer2D is not null)
                {
                    dxgiBuffer.CurrentLength = buffer2D.ContiguousLength;
                }

                sample.AddBuffer(dxgiBuffer);
            }
            else
            {
                int nv12Size = _width * _height * 3 / 2;
                using IMFMediaBuffer memBuffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);
                _gpuProcessor.CopyNv12ToMediaBuffer(frameTexture, memBuffer);
                sample.AddBuffer(memBuffer);
            }

            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private void StartAsyncPumps()
    {
        _inputFeederThread = new Thread(AsyncInputFeederLoop)
        {
            Name = "ClipThat.MfH264Encoder.InputFeeder",
            IsBackground = true
        };
        _inputFeederThread.SetApartmentState(ApartmentState.MTA);
        _inputFeederThread.Start();

        _eventThread = new Thread(AsyncEventLoop)
        {
            Name = "ClipThat.MfH264Encoder.Events",
            IsBackground = true
        };
        _eventThread.SetApartmentState(ApartmentState.MTA);
        _eventThread.Start();
    }

    /// <summary>
    /// Dedicated background loop that continuously drains <see cref="IMFMediaEventGenerator.GetEvent"/>:
    /// atomically increments available input slots on <c>METransformNeedInput</c> (waking the feeder
    /// thread without blocking event draining) and immediately drains <c>ProcessOutput</c> on every
    /// <c>METransformHaveOutput</c>.
    /// </summary>
    private void AsyncEventLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            IMFMediaEventGenerator? generator = _eventGenerator;
            if (generator is null || generator.NativePointer == nint.Zero)
            {
                break;
            }

            IMFMediaEvent mediaEvent;
            try
            {
                mediaEvent = generator.GetEvent(0);
            }
            catch (SharpGenException ex) when (
                ex.ResultCode == ResultCode.Shutdown ||
                Volatile.Read(ref _stopping) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                break;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                {
                    EngineLogger.Error("MfH264Encoder", $"Async event pump failed on \"{EncoderName}\".", ex);
                    _onEncoderError?.Invoke(ex);
                }

                break;
            }

            if (mediaEvent is null || mediaEvent.NativePointer == nint.Zero)
            {
                continue;
            }

            using (mediaEvent)
            {
                MediaEventTypes eventType = mediaEvent.EventType;
                switch (eventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                        {
                            Interlocked.Increment(ref _needInputEventCount);
                            Interlocked.Increment(ref _availableInputSlots);
                            SignalInputWorkSafe();
                        }

                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        Interlocked.Increment(ref _haveOutputEventCount);
                        HandleAsyncHaveOutput();
                        break;

                    case MediaEventTypes.TransformDrainComplete:
                        _drainCompleteEvent.Set();
                        if (Volatile.Read(ref _stopping) != 0)
                        {
                            return;
                        }

                        break;

                    case MediaEventTypes.Error:
                        Result status = mediaEvent.Status;
                        if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                        {
                            var eventEx = new SharpGenException(status);
                            EngineLogger.Error(
                                "MfH264Encoder",
                                $"MFT \"{EncoderName}\" ({ActiveTier}) emitted MEError (HR=0x{status.Code:X8}).",
                                eventEx);
                            _onEncoderError?.Invoke(eventEx);
                        }

                        return;
                }
            }
        }
    }

    /// <summary>
    /// Dedicated input-feeding thread pairing <c>METransformNeedInput</c> slots with queued NV12
    /// samples so <c>GetEvent(0)</c> is never blocked by <c>ProcessInput</c> and WGC never stalls.
    /// </summary>
    private void AsyncInputFeederLoop()
    {
        while (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
        {
            while (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                IMFSample? nextSample = TryDequeueSampleForAvailableSlot();
                if (nextSample is null)
                {
                    break;
                }

                try
                {
                    lock (_mftLock)
                    {
                        if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0 || _transform is null)
                        {
                            break;
                        }

                        SubmitInputSampleLocked(nextSample);
                    }
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                    {
                        EngineLogger.Error(
                            "MfH264Encoder",
                            $"Async ProcessInput failed on \"{EncoderName}\" ({ActiveTier}): {ex.Message}",
                            ex);
                        _onEncoderError?.Invoke(ex);
                    }

                    return;
                }
                finally
                {
                    SafeDisposeSample(nextSample);
                }
            }

            if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0)
            {
                break;
            }

            try
            {
                _inputWorkSignal.WaitOne(100);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private IMFSample? TryDequeueSampleForAvailableSlot()
    {
        if (Volatile.Read(ref _availableInputSlots) <= 0)
        {
            return null;
        }

        lock (_queueLock)
        {
            if (_pendingInputQueue.Count == 0 || !TryConsumeInputSlot())
            {
                return null;
            }

            return _pendingInputQueue.Dequeue();
        }
    }

    private bool TryConsumeInputSlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref _availableInputSlots);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _availableInputSlots, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private void SignalInputWorkSafe()
    {
        try
        {
            _inputWorkSignal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Encoder is shutting down.
        }
    }

    private void HandleAsyncHaveOutput()
    {
        List<EncodedPacket>? producedPackets = null;
        try
        {
            lock (_mftLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || _transform is null)
                {
                    return;
                }

                // Drain the output sample signaled by METransformHaveOutput.
                // If the MFT returns MF_E_TRANSFORM_STREAM_CHANGE (common on AMDh264Encoder when emitting
                // its initial SPS/PPS sequence header), renegotiate SetOutputType and immediately call
                // ProcessOutput again so the pending keyframe sample is never stranded.
                int streamChangeRetries = 0;
                int incompleteDrains = 0;

                while (Volatile.Read(ref _disposed) == 0)
                {
                    ProcessOutputAttemptResult result = TryProcessSingleOutputLocked(
                        out EncodedPacket? packet,
                        out bool hasMoreIncompleteOutput);

                    if (result == ProcessOutputAttemptResult.StreamChanged)
                    {
                        if (++streamChangeRetries >= MaxStreamChangeRetriesPerEvent)
                        {
                            break;
                        }

                        continue;
                    }

                    if (result == ProcessOutputAttemptResult.ProducedPacket && packet is not null)
                    {
                        producedPackets ??= new List<EncodedPacket>(2);
                        producedPackets.Add(packet);

                        if (hasMoreIncompleteOutput && ++incompleteDrains < MaxIncompleteOutputDrainsPerEvent)
                        {
                            continue;
                        }
                    }

                    break;
                }
            }

            if (producedPackets is not null)
            {
                DispatchPackets(producedPackets);
            }
        }
        catch (Exception ex)
        {
            DisposePacketList(producedPackets);
            if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                EngineLogger.Error(
                    "MfH264Encoder",
                    $"ProcessOutput failed on \"{EncoderName}\" ({ActiveTier}): {ex.Message}",
                    ex);
                _onEncoderError?.Invoke(ex);
            }
        }
    }

    private void DrainSyncOutputsLocked(ref List<EncodedPacket>? packets)
    {
        while (true)
        {
            ProcessOutputAttemptResult result = TryProcessSingleOutputLocked(out EncodedPacket? packet, out _);
            if (result == ProcessOutputAttemptResult.StreamChanged)
            {
                continue;
            }

            if (result == ProcessOutputAttemptResult.ProducedPacket && packet is not null)
            {
                packets ??= new List<EncodedPacket>(2);
                packets.Add(packet);
                continue;
            }

            break;
        }
    }

    private ProcessOutputAttemptResult TryProcessSingleOutputLocked(
        out EncodedPacket? packet,
        out bool hasMoreIncompleteOutput)
    {
        packet = null;
        hasMoreIncompleteOutput = false;

        if (_transform is null)
        {
            return ProcessOutputAttemptResult.NoOutput;
        }

        OutputDataBuffer outputBuffer = new()
        {
            StreamID = _outputStreamId
        };

        IMFSample? callerAllocatedSample = null;
        if (!_mftProvidesOutputSamples)
        {
            callerAllocatedSample = MediaFactory.MFCreateSample();
            int bufferCapacity = Math.Max(_outputStreamInfo.Size, DefaultOutputBufferBytes);
            using IMFMediaBuffer memoryBuffer = MediaFactory.MFCreateMemoryBuffer(bufferCapacity);
            callerAllocatedSample.AddBuffer(memoryBuffer);
            outputBuffer.Sample = callerAllocatedSample;
        }

        try
        {
            Result hr = _transform.ProcessOutput(
                ProcessOutputFlags.None,
                cOutputBufferCount: 1,
                ref outputBuffer,
                out ProcessOutputStatus _);

            if (hr == ResultCode.TransformNeedMoreInput)
            {
                return ProcessOutputAttemptResult.NoOutput;
            }

            if (hr == ResultCode.TransformStreamChange)
            {
                HandleOutputStreamChangeLocked();
                return ProcessOutputAttemptResult.StreamChanged;
            }

            if (hr.Failure)
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    hr.CheckError();
                }

                return ProcessOutputAttemptResult.NoOutput;
            }

            OutputDataBufferFlags statusFlags = (OutputDataBufferFlags)outputBuffer.Status;
            hasMoreIncompleteOutput = (statusFlags & OutputDataBufferFlags.OutputDataBufferIncomplete) != 0;

            if ((statusFlags & OutputDataBufferFlags.OutputDataBufferFormatChange) != 0)
            {
                HandleOutputStreamChangeLocked();
            }

            IMFSample? producedSample = outputBuffer.Sample;
            if (producedSample is null || (statusFlags & OutputDataBufferFlags.OutputDataBufferNoSample) != 0)
            {
                return ProcessOutputAttemptResult.NoOutput;
            }

            packet = ExtractPacketFromSample(producedSample);
            return packet is not null
                ? ProcessOutputAttemptResult.ProducedPacket
                : ProcessOutputAttemptResult.NoOutput;
        }
        finally
        {
            outputBuffer.Events?.Dispose();

            if (outputBuffer.Sample is not null)
            {
                outputBuffer.Sample.Dispose();
            }
            else
            {
                callerAllocatedSample?.Dispose();
            }
        }
    }

    private void HandleOutputStreamChangeLocked()
    {
        if (_transform is null)
        {
            return;
        }

        using IMFMediaType? updatedType = _transform.GetOutputAvailableType(_outputStreamId, 0);
        if (updatedType is null || updatedType.NativePointer == nint.Zero)
        {
            return;
        }

        _transform.SetOutputType(_outputStreamId, updatedType, 0);

        _outputStreamInfo = _transform.GetOutputStreamInfo(_outputStreamId);
        OutputStreamInfoFlags flags = (OutputStreamInfoFlags)_outputStreamInfo.Flags;
        _mftProvidesOutputSamples =
            (flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0 ||
            (flags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

        TryCaptureOutputSequenceHeaderLocked();
    }

    private void TryCaptureOutputSequenceHeaderLocked()
    {
        if (_transform is null)
        {
            return;
        }

        try
        {
            using IMFMediaType? currentOutputType = _transform.GetOutputCurrentType(_outputStreamId);
            if (currentOutputType is not null &&
                currentOutputType.NativePointer != nint.Zero &&
                currentOutputType.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out uint blobSize).Success &&
                blobSize > 0)
            {
                byte[] header = new byte[blobSize];
                if (currentOutputType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, header).Success)
                {
                    _sequenceHeader = header;
                    _onSequenceHeader?.Invoke(header);
                }
            }
        }
        catch
        {
            // Sequence header blob is optional; SPS/PPS can also be extracted inline from Annex-B keyframes.
        }
    }

    private EncodedPacket? ExtractPacketFromSample(IMFSample sample)
    {
        using IMFMediaBuffer contiguousBuffer = sample.ConvertToContiguousBuffer();

        byte[]? rented = null;
        int currentLength;
        contiguousBuffer.Lock(out nint dataPtr, out _, out currentLength);
        try
        {
            if (currentLength <= 0 || dataPtr == nint.Zero)
            {
                return null;
            }

            rented = ArrayPool<byte>.Shared.Rent(currentLength);
            Marshal.Copy(dataPtr, rented, 0, currentLength);
        }
        catch
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            throw;
        }
        finally
        {
            contiguousBuffer.Unlock();
        }

        try
        {
            long timestamp100Ns = TryGetSampleTime(sample);
            long duration100Ns = TryGetSampleDuration(sample);
            ReadOnlySpan<byte> payload = rented.AsSpan(0, currentLength);

            // Detect keyframes using BOTH MFSampleExtension_CleanPoint != 0 AND Annex-B NAL unit inspection
            // (nalUnitType == 5 IDR or nalUnitType == 7 SPS), since AMDh264Encoder frequently emits Annex-B
            // SPS/PPS/IDR packets without setting MFSampleExtension_CleanPoint.
            bool isCleanPoint = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint cleanPoint).Success &&
                                cleanPoint != 0;
            bool hasAnnexBKeyframe = ContainsAnnexBKeyframeNal(payload);
            bool isKeyframe = isCleanPoint || hasAnnexBKeyframe;

            if (isKeyframe && _sequenceHeader is null)
            {
                byte[]? extractedHeader = TryExtractAnnexBSpsPps(payload);
                if (extractedHeader is not null)
                {
                    _sequenceHeader = extractedHeader;
                    _onSequenceHeader?.Invoke(extractedHeader);
                }
            }

            int packetNumber = Interlocked.Increment(ref _encodedPacketCount);
            if (isKeyframe)
            {
                if (Interlocked.Increment(ref _encodedKeyframeCount) == 1)
                {
                    EngineLogger.Info(
                        "MfH264Encoder",
                        $"First encoded H.264 keyframe: Bytes={currentLength}, IsKeyFrame=True, " +
                        $"Tier={ActiveTier}, Encoder=\"{EncoderName}\", CleanPoint={isCleanPoint}, AnnexBKey={hasAnnexBKeyframe}");
                }
            }
            else if (packetNumber == 1)
            {
                EngineLogger.Info(
                    "MfH264Encoder",
                    $"First encoded H.264 packet (non-keyframe): Bytes={currentLength}, IsKeyFrame=False, " +
                    $"Tier={ActiveTier}, Encoder=\"{EncoderName}\"");
            }

            return new EncodedPacket(
                MediaTrackType.Video,
                rented,
                currentLength,
                timestamp100Ns,
                duration100Ns,
                isKeyframe);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private long TryGetSampleTime(IMFSample sample)
    {
        try
        {
            return sample.SampleTime;
        }
        catch (SharpGenException)
        {
            return 0L;
        }
    }

    private long TryGetSampleDuration(IMFSample sample)
    {
        try
        {
            long duration = sample.SampleDuration;
            return duration > 0 ? duration : _defaultFrameDuration100Ns;
        }
        catch (SharpGenException)
        {
            return _defaultFrameDuration100Ns;
        }
    }

    private void DispatchPackets(List<EncodedPacket> packets)
    {
        for (int i = 0; i < packets.Count; i++)
        {
            EncodedPacket packet = packets[i];
            try
            {
                _onPacketEncoded(packet);
            }
            catch
            {
                for (int j = i; j < packets.Count; j++)
                {
                    packets[j].Dispose();
                }

                throw;
            }
        }
    }

    private static void DisposePacketList(List<EncodedPacket>? packets)
    {
        if (packets is null)
        {
            return;
        }

        for (int i = 0; i < packets.Count; i++)
        {
            packets[i].Dispose();
        }
    }

    private static void SafeDisposeSample(IMFSample? sample)
    {
        if (sample is null)
        {
            return;
        }

        try
        {
            sample.Dispose();
        }
        catch
        {
            // Ignore sample disposal errors during teardown.
        }
    }

    private static void SafeDisposeComObject(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Ignore COM disposal errors on rejected/closing candidates.
        }
    }

    private static void TryShutdownActivate(IMFActivate? activate)
    {
        if (activate is null || activate.NativePointer == nint.Zero)
        {
            return;
        }

        try
        {
            activate.ShutdownObject();
        }
        catch
        {
            // Ignore activate shutdown errors.
        }
    }

    private static void DisposeCandidateList(List<IMFActivate> candidates, int excludeIndex)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i != excludeIndex)
            {
                SafeDisposeComObject(candidates[i]);
            }
        }
    }

    private static (int InputStreamId, int OutputStreamId) ResolveStreamIds(IMFTransform transform)
    {
        int[] inputIds = new int[1];
        int[] outputIds = new int[1];

        try
        {
            transform.GetStreamIDs(1, inputIds, 1, outputIds);
            return (inputIds[0], outputIds[0]);
        }
        catch (SharpGenException)
        {
            // E_NOTIMPL is standard for fixed-stream MFTs, meaning stream IDs default to 0.
            return (0, 0);
        }
    }

    private static IMFAttributes? TryGetTransformAttributes(IMFTransform transform)
    {
        try
        {
            return transform.Attributes;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static bool TryGetUInt32(IMFAttributes attributes, Guid key, out uint value)
    {
        Result hr = attributes.GetUInt32(key, out value);
        return hr.Success;
    }

    private static string TryGetFriendlyName(IMFActivate activate)
    {
        try
        {
            if (activate is null || activate.NativePointer == nint.Zero)
            {
                return "Media Foundation H.264";
            }

            string? name = activate.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return string.IsNullOrWhiteSpace(name) ? "Media Foundation H.264" : name.Trim();
        }
        catch
        {
            return "Media Foundation H.264";
        }
    }

    private static bool HasValidLuid(Luid luid) => luid.LowPart != 0u || luid.HighPart != 0;

    private static ulong PackLuid(Luid luid) => ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>
    /// Inspects Annex-B start codes (<c>00 00 01</c> or <c>00 00 00 01</c>) and returns <see langword="true"/>
    /// when an H.264 IDR slice (<c>nalUnitType == 5</c>) or SPS (<c>nalUnitType == 7</c>) is present.
    /// </summary>
    private static bool ContainsAnnexBKeyframeNal(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i + 3 < data.Length)
        {
            int startCodeLength = 0;
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLength = 3;
                }
                else if (i + 4 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                }
            }

            if (startCodeLength > 0)
            {
                int nalOffset = i + startCodeLength;
                if (nalOffset < data.Length)
                {
                    int nalType = data[nalOffset] & 0x1F;
                    if (nalType is 5 or 7) // IDR slice (5) or SPS (7)
                    {
                        return true;
                    }
                }

                i = nalOffset + 1;
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    private static byte[]? TryExtractAnnexBSpsPps(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream();
        int pos = FindNextStartCode(data, 0, out int startCodeLen);

        while (pos >= 0 && pos + startCodeLen < data.Length)
        {
            int nalStart = pos + startCodeLen;
            int nextPos = FindNextStartCode(data, nalStart, out int nextStartCodeLen);
            int nalEnd = nextPos >= 0 ? nextPos : data.Length;

            int nalType = data[nalStart] & 0x1F;
            if (nalType is 7 or 8) // SPS (7) or PPS (8)
            {
                stream.Write(data.Slice(pos, nalEnd - pos));
            }

            pos = nextPos;
            startCodeLen = nextStartCodeLen;
        }

        return stream.Length > 0 ? stream.ToArray() : null;
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int startIndex, out int startCodeLength)
    {
        for (int i = startIndex; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    startCodeLength = 3;
                    return i;
                }

                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                    return i;
                }
            }
        }

        startCodeLength = 0;
        return -1;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _stopping, 1);
        Interlocked.Exchange(ref _availableInputSlots, 0);
        SignalInputWorkSafe();

        lock (_queueLock)
        {
            while (_pendingInputQueue.Count > 0)
            {
                SafeDisposeSample(_pendingInputQueue.Dequeue());
            }
        }

        if (_inputFeederThread is not null && _inputFeederThread != Thread.CurrentThread)
        {
            _inputFeederThread.Join(1000);
        }

        _inputFeederThread = null;

        List<EncodedPacket>? remainingSyncPackets = null;
        try
        {
            if (_transform is not null)
            {
                lock (_mftLock)
                {
                    try
                    {
                        _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, nuint.Zero);
                        _transform.ProcessMessage(TMessageType.MessageCommandDrain, nuint.Zero);
                        if (!_isAsync)
                        {
                            DrainSyncOutputsLocked(ref remainingSyncPackets);
                        }
                    }
                    catch
                    {
                        // Ignore drain errors during shutdown.
                    }
                }

                if (_isAsync)
                {
                    _drainCompleteEvent.Wait(250);
                }

                lock (_mftLock)
                {
                    try
                    {
                        _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, nuint.Zero);
                    }
                    catch
                    {
                        // Best-effort end streaming notification.
                    }

                    try
                    {
                        MediaFactory.MFShutdownObject(_transform);
                    }
                    catch
                    {
                        // Best-effort IMFShutdown call to unblock GetEvent(0).
                    }

                    TryShutdownActivate(_activeActivate);
                }
            }
        }
        finally
        {
            DisposePacketList(remainingSyncPackets);
        }

        if (_eventThread is not null && _eventThread != Thread.CurrentThread)
        {
            _eventThread.Join(1000);
        }

        _eventThread = null;

        lock (_queueLock)
        {
            while (_pendingInputQueue.Count > 0)
            {
                SafeDisposeSample(_pendingInputQueue.Dequeue());
            }
        }

        lock (_mftLock)
        {
            SafeDisposeComObject(_eventGenerator);
            _eventGenerator = null;

            SafeDisposeComObject(_transform);
            _transform = null;

            SafeDisposeComObject(_activeActivate);
            _activeActivate = null;

            SafeDisposeComObject(_dxgiDeviceManager);
            _dxgiDeviceManager = null;
        }

        _inputWorkSignal.Dispose();
        _drainCompleteEvent.Dispose();

        if (_mfStarted)
        {
            _mfStarted = false;
            try
            {
                MediaFactory.MFShutdown();
            }
            catch
            {
                // Ignore MFShutdown errors on process teardown.
            }
        }
    }

    private enum ProcessOutputAttemptResult
    {
        NoOutput,
        ProducedPacket,
        StreamChanged
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        out nint ppv);

    [ComImport]
    [Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig]
        int IsSupported([In] ref Guid api);

        [PreserveSig]
        int IsModifiable([In] ref Guid api);

        [PreserveSig]
        int GetParameterRange(
            [In] ref Guid api,
            out object valueMin,
            out object valueMax,
            out object steppingDelta);

        [PreserveSig]
        int GetParameterValues(
            [In] ref Guid api,
            out nint values,
            out int valuesCount);

        [PreserveSig]
        int GetDefaultValue(
            [In] ref Guid api,
            out object value);

        [PreserveSig]
        int GetValue(
            [In] ref Guid api,
            out object value);

        [PreserveSig]
        int SetValue(
            [In] ref Guid api,
            [In] ref object value);
    }
}
