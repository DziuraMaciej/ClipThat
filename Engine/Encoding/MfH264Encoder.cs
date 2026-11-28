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
public sealed class MfH264Encoder : IVideoEncoder
{
    private const uint ClsCtxInprocServer = 0x1u;
    private const uint H264ProfileHigh = 100u;
    private const uint H264ProfileMain = 77u;
    private const uint H264ProfileBase = 66u;
    private const uint RateControlModeCbr = 0u;
    private const uint RateControlModePeakConstrainedVbr = 1u;
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
    private static readonly Guid CodecApiAvEncCommonMaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");
    private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiAvEncMpvDefaultBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
    private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private readonly GpuVideoProcessor _gpuProcessor;
    private readonly Action<EncodedPacket> _onPacketEncoded;
    private readonly Action<ReadOnlyMemory<byte>>? _onSequenceHeader;
    private readonly Action<Exception>? _onEncoderError;
    private readonly object _mftLock = new();
    private readonly object _queueLock = new();
    private readonly Queue<QueuedInputItem> _pendingInputQueue = new(MaxPendingAsyncInputSamples + 1);
    private readonly nint[] _recentInFlightGpuTextures = new nint[4];
    private int _recentGpuTextureCursor;
    private readonly AutoResetEvent _inputWorkSignal = new(false);
    private readonly ManualResetEventSlim _drainCompleteEvent = new(false);

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly uint _targetBitrateBps;
    private readonly uint _peakBitrateBps;
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
    private IMFSample? _reusableOutputSample;
    private IMFMediaBuffer? _reusableOutputBuffer;
    private int _reusableOutputCapacity;
    private IMFSample? _reusableSyncCpuInputSample;
    private IMFMediaBuffer? _reusableSyncCpuInputBuffer;
    private long _lastOutputTimestamp100Ns = -1;
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
        double peakBitrateMbps = resourceEstimator.GetPeakVideoBitrateMbps(resolutionPreset, _fps);
        _targetBitrateBps = (uint)Math.Clamp((long)Math.Round(bitrateMbps * 1_000_000.0), 250_000L, 100_000_000L);
        _peakBitrateBps = (uint)Math.Clamp((long)Math.Round(peakBitrateMbps * 1_000_000.0), _targetBitrateBps, 100_000_000L);

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
    /// Returns <see langword="true"/> if <paramref name="texture"/> is currently queued for async input
    /// or was recently submitted to a zero-copy GPU hardware encoder, preventing pool overwrites.
    /// </summary>
    public bool IsTextureInUse(ID3D11Texture2D texture)
    {
        if (!_usesGpuInput || texture is null)
        {
            return false;
        }

        nint ptr = texture.NativePointer;
        if (ptr == nint.Zero)
        {
            return false;
        }

        lock (_queueLock)
        {
            foreach (QueuedInputItem item in _pendingInputQueue)
            {
                if (item.GpuTexturePtr == ptr)
                {
                    return true;
                }
            }

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

    /// <summary>
    /// Submits a single NV12 (or BGRA fallback) texture produced by <see cref="GpuVideoProcessor"/> to the encoder.
    /// For async MFTs, queues the sample without blocking the capture thread and wakes the dedicated
    /// input feeder, which submits via <c>ProcessInput</c> strictly as <c>METransformNeedInput</c> slots arrive.
    /// </summary>
    public bool EncodeFrame(ID3D11Texture2D frameTexture, long timestamp100Ns, long duration100Ns, bool forceKeyFrame = false)
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
        bool usingReusableSyncInput = false;

        try
        {
            if (_isAsync)
            {
                // Avoid allocating CPU NV12 staging buffers (~3 MB/frame) if Frame #0 is still awaiting
                // consumption and the async queue is already full.
                if (!_usesGpuInput && Volatile.Read(ref _submittedFrameCount) == 0)
                {
                    lock (_queueLock)
                    {
                        if (Volatile.Read(ref _disposed) != 0 ||
                            Volatile.Read(ref _stopping) != 0 ||
                            _pendingInputQueue.Count >= MaxPendingAsyncInputSamples)
                        {
                            return false;
                        }
                    }
                }

                inputSample = CreateInputSample(frameTexture, timestamp100Ns, effectiveDuration);
                nint gpuPtr = _usesGpuInput ? frameTexture.NativePointer : nint.Zero;

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

                        QueuedInputItem droppedOldest = _pendingInputQueue.Dequeue();
                        forceKeyFrame |= droppedOldest.ForceKeyFrame;
                        SafeDisposeSample(droppedOldest.Sample);
                    }

                    _pendingInputQueue.Enqueue(new QueuedInputItem(inputSample, gpuPtr, forceKeyFrame));
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

                inputSample = AcquireSyncInputSampleLocked(frameTexture, timestamp100Ns, effectiveDuration, out usingReusableSyncInput);

                try
                {
                    SubmitInputSampleLocked(inputSample, forceKeyFrame);
                }
                catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
                {
                    DrainSyncOutputsLocked(ref syncPackets);
                    SubmitInputSampleLocked(inputSample, forceKeyFrame);
                }

                if (!usingReusableSyncInput)
                {
                    // Release input IMFSample (and its underlying NV12 IMFMediaBuffer) immediately after ProcessInput.
                    SafeDisposeSample(inputSample);
                    inputSample = null;
                }

                DrainSyncOutputsLocked(ref syncPackets);

                // If the MFT retained a COM reference to our reusable sync input sample/buffer, detach it
                // so the next frame allocates a fresh buffer without overwriting in-use encoder input.
                if (usingReusableSyncInput && !IsReusableSyncInputExclusiveLocked())
                {
                    ReleaseReusableSyncInputSampleLocked();
                }
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
            if (!sampleOwnershipTransferred && !usingReusableSyncInput)
            {
                SafeDisposeSample(inputSample);
            }
        }
    }

    private void SubmitInputSampleLocked(IMFSample inputSample, bool forceKeyFrame = false)
    {
        if (_transform is null)
        {
            return;
        }

        // Force an immediate IDR keyframe on the very first submitted frame (or explicit keep-alive request)
        // so MediaRingBuffer receives decodable keyframes promptly across all hardware and software MFTs.
        if (forceKeyFrame || Volatile.Read(ref _submittedFrameCount) == 0)
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
                IMFTransform directTransform;
                try
                {
                    directTransform = new IMFTransform(transformPtr);
                }
                catch
                {
                    Marshal.Release(transformPtr);
                    throw;
                }

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
        }
        finally
        {
            MediaFoundationMftHelper.UnwrapActivatePointerArray(activatesPtr, count, results);
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
        }
        finally
        {
            MediaFoundationMftHelper.UnwrapActivatePointerArray(activatesPtr, count, results);
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
        bool d3dManagerAttached = false;

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
                    d3dManagerAttached = true;
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
            ConfigureCodecApi(transform, isSoftwareFallback: tier == EncoderNegotiationTier.SoftwareFallback);

            // Strict Media Foundation H.264 MFT contract: SetOutputType MUST be called BEFORE SetInputType.
            ConfigureOutputMediaType(transform, outputStreamId);

            // Negotiate MFVideoFormat_NV12 directly from GetInputAvailableType first so NVIDIA NVENC,
            // AMD AMF, Intel QuickSync, and Microsoft Software MFT keep driver-advertised attributes.
            ConfigureInputMediaType(transform, inputStreamId);

            // Re-apply CodecAPI hints after media types are negotiated for MFTs that validate post-SetOutputType.
            ConfigureCodecApi(transform, isSoftwareFallback: tier == EncoderNegotiationTier.SoftwareFallback);

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

            if (ReferenceEquals(_activeActivate, activate)) _activeActivate = null;
            if (ReferenceEquals(_transform, transform)) _transform = null;
            if (ReferenceEquals(_dxgiDeviceManager, dxgiManager)) _dxgiDeviceManager = null;
            if (ReferenceEquals(_eventGenerator, eventGenerator)) _eventGenerator = null;

            SafeDisposeComObject(eventGenerator);

            if (transform is not null)
            {
                if (d3dManagerAttached)
                {
                    try
                    {
                        transform.ProcessMessage(TMessageType.MessageSetD3DManager, nuint.Zero);
                    }
                    catch
                    {
                        // Best-effort detachment of IMFDXGIDeviceManager from failed hardware MFT.
                    }
                }

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

            SafeDisposeComObject(dxgiManager);
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
            for (int colorPass = 0; colorPass < 2; colorPass++)
            {
                using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _targetBitrateBps).CheckError();
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)_width, (uint)_height)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)_fps, 1u)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(_fps * 2));

                if (colorPass == 0)
                {
                    // Tag explicit BT.709 studio-range (16-235) colorimetry matching GpuVideoProcessor output
                    // so players do not misinterpret SD/720p H.264 streams as BT.601.
                    ApplyBt709StudioColorimetry(outputType);
                }

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
        }

        throw lastError ?? new InvalidOperationException("Nie udało się skonfigurować typu wyjściowego H.264.");
    }

    private static void ApplyBt709StudioColorimetry(IMFMediaType mediaType)
    {
        mediaType.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
        mediaType.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709);
        mediaType.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709);
        mediaType.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235);
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
                ApplyBt709StudioColorimetry(available);

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

    private void ConfigureCodecApi(IMFTransform transform, bool isSoftwareFallback)
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

        object? rawCodecApi = null;
        try
        {
            rawCodecApi = Marshal.GetUniqueObjectForIUnknown(codecApiPtr);
            if (rawCodecApi is not ICodecAPI codecApi)
            {
                return;
            }

            // Enable low-latency mode if supported so NVENC / QSV / AMF emit the initial IDR keyframe
            // without multi-frame lookahead buffering.
            TrySetCodecApiBool(codecApi, CodecApiAvLowLatencyMode, true);

            // Prefer Peak-Constrained VBR so calm desktop/menu scenes consume a fraction of RAM while
            // fast gameplay scenes burst cleanly up to PeakBitrate; fall back to Unconstrained VBR, then CBR.
            if (!TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModePeakConstrainedVbr) &&
                !TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeUnconstrainedVbr))
            {
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeCbr);
            }

            TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonMeanBitRate, _targetBitrateBps);
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonMaxBitRate, _peakBitrateBps);

            // 2-second GOP (2 * FPS): better quality per megabyte and fewer heavy IDR frames than 1s GOP.
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvGopSize, (uint)(_fps * 2));
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvDefaultBPictureCount, 0u);

            if (isSoftwareFallback)
            {
                // Favor faster real-time encoding on CPU fallback to keep background CPU impact low.
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonQualityVsSpeed, 33u);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ostrzeżenie konfiguracji ICodecAPI: {ex.Message}");
        }
        finally
        {
            if (rawCodecApi is not null)
            {
                Marshal.ReleaseComObject(rawCodecApi);
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

        object? rawCodecApi = null;
        try
        {
            rawCodecApi = Marshal.GetUniqueObjectForIUnknown(codecApiPtr);
            if (rawCodecApi is not ICodecAPI codecApi)
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
            // ForceKeyFrame is best-effort; GOP size is also configured to 2s.
        }
        finally
        {
            if (rawCodecApi is not null)
            {
                Marshal.ReleaseComObject(rawCodecApi);
            }

            Marshal.Release(codecApiPtr);
        }
    }

    private static bool TrySetCodecApiUInt32(ICodecAPI codecApi, Guid propertyGuid, uint value)
    {
        try
        {
            Guid api = propertyGuid;
            int supportedHr = codecApi.IsSupported(ref api);
            // E_NOTIMPL (0x80004001): some MFTs accept SetValue without implementing IsSupported.
            if (supportedHr != 0 && supportedHr != unchecked((int)0x80004001))
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
            int supportedHr = codecApi.IsSupported(ref api);
            if (supportedHr != 0 && supportedHr != unchecked((int)0x80004001))
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

    private IMFSample AcquireSyncInputSampleLocked(
        ID3D11Texture2D frameTexture,
        long timestamp100Ns,
        long duration100Ns,
        out bool usingReusableSample)
    {
        if (_usesGpuInput)
        {
            usingReusableSample = false;
            return CreateInputSample(frameTexture, timestamp100Ns, duration100Ns);
        }

        int nv12Size = _width * _height * 3 / 2;
        if (_reusableSyncCpuInputSample is null || _reusableSyncCpuInputBuffer is null)
        {
            IMFSample sample = MediaFactory.MFCreateSample();
            IMFMediaBuffer memBuffer = MediaFactory.MFCreateMemoryBuffer(nv12Size);
            try
            {
                sample.AddBuffer(memBuffer);
                _reusableSyncCpuInputSample = sample;
                _reusableSyncCpuInputBuffer = memBuffer;
            }
            catch
            {
                SafeDisposeComObject(memBuffer);
                SafeDisposeComObject(sample);
                throw;
            }
        }

        _reusableSyncCpuInputSample.SampleTime = timestamp100Ns;
        _reusableSyncCpuInputSample.SampleDuration = duration100Ns;
        _reusableSyncCpuInputBuffer.CurrentLength = 0;
        _gpuProcessor.CopyNv12ToMediaBuffer(frameTexture, _reusableSyncCpuInputBuffer);
        usingReusableSample = true;
        return _reusableSyncCpuInputSample;
    }

    private bool IsReusableSyncInputExclusiveLocked()
    {
        if (_reusableSyncCpuInputSample is null || _reusableSyncCpuInputBuffer is null)
        {
            return false;
        }

        nint samplePtr = _reusableSyncCpuInputSample.NativePointer;
        nint bufferPtr = _reusableSyncCpuInputBuffer.NativePointer;
        if (samplePtr == nint.Zero || bufferPtr == nint.Zero)
        {
            return false;
        }

        // Sample refcount == 1 (only our wrapper) and buffer refcount == 2 (our wrapper + sample).
        int sampleRefs = MediaFoundationMftHelper.QueryComRefCount(samplePtr);
        int bufferRefs = MediaFoundationMftHelper.QueryComRefCount(bufferPtr);
        return sampleRefs == 1 && bufferRefs == 2;
    }

    private void ReleaseReusableSyncInputSampleLocked()
    {
        SafeDisposeComObject(_reusableSyncCpuInputBuffer);
        _reusableSyncCpuInputBuffer = null;
        SafeDisposeComObject(_reusableSyncCpuInputSample);
        _reusableSyncCpuInputSample = null;
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
                SafeDisposeComObject(mediaEvent);
                continue;
            }

            MediaEventTypes eventType;
            Result errorStatus = Result.Ok;
            // Release the IMFMediaEvent COM reference immediately after reading its metadata,
            // before invoking ProcessOutput or dispatching packets.
            using (mediaEvent)
            {
                eventType = mediaEvent.EventType;
                if (eventType == MediaEventTypes.Error)
                {
                    errorStatus = mediaEvent.Status;
                }
            }

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
                    if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                    {
                        var eventEx = new SharpGenException(errorStatus);
                        EngineLogger.Error(
                            "MfH264Encoder",
                            $"MFT \"{EncoderName}\" ({ActiveTier}) emitted MEError (HR=0x{errorStatus.Code:X8}).",
                            eventEx);
                        _onEncoderError?.Invoke(eventEx);
                    }

                    return;
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
                QueuedInputItem? nextItem = TryDequeueSampleForAvailableSlot();
                if (!nextItem.HasValue)
                {
                    break;
                }

                IMFSample nextSample = nextItem.Value.Sample;
                try
                {
                    lock (_mftLock)
                    {
                        if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0 || _transform is null)
                        {
                            break;
                        }

                        SubmitInputSampleLocked(nextSample, nextItem.Value.ForceKeyFrame);
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
                _inputWorkSignal.WaitOne(1000);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private QueuedInputItem? TryDequeueSampleForAvailableSlot()
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

            QueuedInputItem item = _pendingInputQueue.Dequeue();
            if (item.GpuTexturePtr != nint.Zero)
            {
                _recentInFlightGpuTextures[_recentGpuTextureCursor] = item.GpuTexturePtr;
                _recentGpuTextureCursor = (_recentGpuTextureCursor + 1) % _recentInFlightGpuTextures.Length;
            }

            return item;
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
        int streamChangeRetries = 0;
        while (true)
        {
            ProcessOutputAttemptResult result = TryProcessSingleOutputLocked(out EncodedPacket? packet, out _);
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
        nint callerSamplePtr = nint.Zero;
        bool usingReusableOutputSample = false;

        try
        {
            if (!_mftProvidesOutputSamples)
            {
                int bufferCapacity = Math.Max(_outputStreamInfo.Size, DefaultOutputBufferBytes);
                callerAllocatedSample = EnsureReusableOutputSampleLocked(bufferCapacity);
                callerSamplePtr = callerAllocatedSample.NativePointer;
                usingReusableOutputSample = true;
                outputBuffer.Sample = callerAllocatedSample;
            }

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

            bool formatChanged = (statusFlags & OutputDataBufferFlags.OutputDataBufferFormatChange) != 0;
            if (formatChanged)
            {
                HandleOutputStreamChangeLocked();
            }

            IMFSample? producedSample = outputBuffer.Sample;
            if (producedSample is null || (statusFlags & OutputDataBufferFlags.OutputDataBufferNoSample) != 0)
            {
                return formatChanged
                    ? ProcessOutputAttemptResult.StreamChanged
                    : ProcessOutputAttemptResult.NoOutput;
            }

            packet = ExtractPacketFromSample(producedSample);
            return packet is not null
                ? ProcessOutputAttemptResult.ProducedPacket
                : ProcessOutputAttemptResult.NoOutput;
        }
        finally
        {
            SafeDisposeComObject(outputBuffer.Events);
            outputBuffer.Events = null;

            IMFSample? returnedSample = outputBuffer.Sample;
            outputBuffer.Sample = null;

            if (usingReusableOutputSample)
            {
                CleanupReusableOutputAlias(callerAllocatedSample, callerSamplePtr, returnedSample);
            }
            else
            {
                DisposeProcessOutputSamples(callerAllocatedSample, callerSamplePtr, returnedSample);
            }
        }
    }

    private IMFSample EnsureReusableOutputSampleLocked(int requiredCapacity)
    {
        if (_reusableOutputSample is not null &&
            _reusableOutputBuffer is not null &&
            _reusableOutputCapacity >= requiredCapacity)
        {
            // Clear stale sample attributes (especially MFSampleExtension_CleanPoint) so P-frames
            // never inherit CleanPoint = 1 from a preceding IDR frame on reusable output samples.
            _reusableOutputSample.DeleteAllItems();
            _reusableOutputBuffer.CurrentLength = 0;
            return _reusableOutputSample;
        }

        ReleaseReusableOutputSampleLocked();

        IMFSample sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(requiredCapacity);
        try
        {
            sample.AddBuffer(buffer);
            _reusableOutputSample = sample;
            _reusableOutputBuffer = buffer;
            _reusableOutputCapacity = requiredCapacity;
            return sample;
        }
        catch
        {
            SafeDisposeComObject(buffer);
            SafeDisposeComObject(sample);
            throw;
        }
    }

    private void ReleaseReusableOutputSampleLocked()
    {
        SafeDisposeComObject(_reusableOutputBuffer);
        _reusableOutputBuffer = null;
        SafeDisposeComObject(_reusableOutputSample);
        _reusableOutputSample = null;
        _reusableOutputCapacity = 0;
    }

    private static void CleanupReusableOutputAlias(
        IMFSample? reusableSample,
        nint reusableSamplePtr,
        IMFSample? returnedSample)
        => MediaFoundationMftHelper.CleanupReusableOutputAlias(reusableSample, reusableSamplePtr, returnedSample);

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
        long duration100Ns = TryGetSampleDuration(sample);
        long timestamp100Ns = TryGetSampleTime(sample, duration100Ns);
        bool isCleanPoint = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint cleanPoint).Success &&
                            cleanPoint != 0;

        EncodedPacket? packet = null;
        int currentLength = 0;
        bool hasAnnexBKeyframe = false;

        // Explicitly dispose the contiguous IMFMediaBuffer immediately after copying bytes out.
        using (IMFMediaBuffer contiguousBuffer = sample.ConvertToContiguousBuffer())
        {
            contiguousBuffer.Lock(out nint dataPtr, out _, out currentLength);
            try
            {
                if (currentLength <= 0 || dataPtr == nint.Zero)
                {
                    return null;
                }

                unsafe
                {
                    var nativeSpan = new ReadOnlySpan<byte>((void*)dataPtr, currentLength);
                    hasAnnexBKeyframe = ContainsAnnexBKeyframeNal(nativeSpan);
                }

                bool isKeyframe = isCleanPoint || hasAnnexBKeyframe;
                packet = EncodedPacket.CreateFromNativeMemory(
                    MediaTrackType.Video,
                    dataPtr,
                    currentLength,
                    timestamp100Ns,
                    duration100Ns,
                    isKeyframe);
            }
            finally
            {
                contiguousBuffer.Unlock();
            }
        }

        try
        {
            bool isKeyframe = packet.IsKeyframe;
            if (isKeyframe && _sequenceHeader is null)
            {
                byte[]? extractedHeader = TryExtractAnnexBSpsPps(packet.Data);
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

            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private long TryGetSampleTime(IMFSample sample, long fallbackDuration100Ns)
    {
        long candidate;
        try
        {
            candidate = sample.SampleTime;
        }
        catch (SharpGenException)
        {
            candidate = _lastOutputTimestamp100Ns >= 0
                ? _lastOutputTimestamp100Ns + fallbackDuration100Ns
                : 0L;
        }

        if (candidate < 0)
        {
            candidate = 0L;
        }

        if (_lastOutputTimestamp100Ns >= 0 && candidate <= _lastOutputTimestamp100Ns)
        {
            candidate = _lastOutputTimestamp100Ns + 1;
        }

        _lastOutputTimestamp100Ns = candidate;
        return candidate;
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

    private static void DisposeProcessOutputSamples(
        IMFSample? callerAllocatedSample,
        nint callerSamplePtr,
        IMFSample? returnedSample)
        => MediaFoundationMftHelper.DisposeProcessOutputSamples(callerAllocatedSample, callerSamplePtr, returnedSample);

    private static void SafeDisposeSample(IMFSample? sample)
        => EngineDisposeHelper.SafeDispose(sample);

    private static void SafeDisposeComObject(IDisposable? disposable)
        => EngineDisposeHelper.SafeDispose(disposable);

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
        => MediaFoundationMftHelper.DisposeCandidateList(candidates, excludeIndex);

    private static (int InputStreamId, int OutputStreamId) ResolveStreamIds(IMFTransform transform)
        => MediaFoundationMftHelper.ResolveStreamIds(transform);

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
    /// when a true H.264 IDR slice (<c>nalUnitType == 5</c>) is present.
    /// </summary>
    internal static bool ContainsAnnexBKeyframeNal(ReadOnlySpan<byte> data)
        => AnnexBHelper.ContainsKeyframeNal(data);

    internal static byte[]? TryExtractAnnexBSpsPps(ReadOnlySpan<byte> data)
        => AnnexBHelper.TryExtractSpsPps(data);

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
                SafeDisposeSample(_pendingInputQueue.Dequeue().Sample);
            }

            Array.Clear(_recentInFlightGpuTextures);
        }

        bool inputFeederExited = true;
        if (_inputFeederThread is not null && _inputFeederThread != Thread.CurrentThread)
        {
            inputFeederExited = _inputFeederThread.Join(2000);
        }

        if (inputFeederExited)
        {
            _inputFeederThread = null;
        }

        bool acquiredMftLock = false;
        if (_transform is not null)
        {
            // Avoid permanent deadlock if a wedged driver still holds _mftLock inside ProcessInput.
            int lockTimeoutMs = inputFeederExited ? 2000 : 500;
            acquiredMftLock = Monitor.TryEnter(_mftLock, lockTimeoutMs);
            if (acquiredMftLock)
            {
                try
                {
                    try
                    {
                        _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, nuint.Zero);
                        if (_isAsync)
                        {
                            _transform.ProcessMessage(TMessageType.MessageCommandDrain, nuint.Zero);
                        }
                    }
                    catch
                    {
                        // Ignore drain errors during shutdown.
                    }
                }
                finally
                {
                    Monitor.Exit(_mftLock);
                }

                if (_isAsync)
                {
                    _drainCompleteEvent.Wait(250);
                }

                acquiredMftLock = Monitor.TryEnter(_mftLock, lockTimeoutMs);
                if (acquiredMftLock)
                {
                    try
                    {
                        try
                        {
                            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, nuint.Zero);
                        }
                        catch
                        {
                            // Best-effort end streaming notification.
                        }

                        if (_usesGpuInput)
                        {
                            try
                            {
                                _transform.ProcessMessage(TMessageType.MessageSetD3DManager, nuint.Zero);
                            }
                            catch
                            {
                                // Best-effort detachment of IMFDXGIDeviceManager before MFT shutdown.
                            }
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
                    finally
                    {
                        Monitor.Exit(_mftLock);
                    }
                }
            }
            else
            {
                EngineLogger.Warn(
                    "MfH264Encoder",
                    $"Timed out waiting for _mftLock during Dispose on \"{EncoderName}\" (feederExited={inputFeederExited}); skipping unsafe MFT shutdown.");
            }
        }

        bool eventThreadExited = true;
        if (_eventThread is not null && _eventThread != Thread.CurrentThread)
        {
            eventThreadExited = _eventThread.Join(2000);
        }

        if (eventThreadExited)
        {
            _eventThread = null;
        }

        lock (_queueLock)
        {
            while (_pendingInputQueue.Count > 0)
            {
                SafeDisposeSample(_pendingInputQueue.Dequeue().Sample);
            }

            Array.Clear(_recentInFlightGpuTextures);
        }

        if (!inputFeederExited || !eventThreadExited || (_transform is not null && !acquiredMftLock))
        {
            EngineLogger.Warn(
                "MfH264Encoder",
                $"Async encoder threads did not exit cleanly (feederExited={inputFeederExited}, eventExited={eventThreadExited}, lockAcquired={acquiredMftLock}); skipping unsafe COM/MF teardown.");
            return;
        }

        lock (_mftLock)
        {
            ReleaseReusableSyncInputSampleLocked();
            ReleaseReusableOutputSampleLocked();

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

    private readonly record struct QueuedInputItem(IMFSample Sample, nint GpuTexturePtr, bool ForceKeyFrame);

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
