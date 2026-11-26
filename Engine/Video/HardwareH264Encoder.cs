using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThat.Engine.Buffer;
using ClipThat.Engine.Interop;
using ClipThat.Models;
using ClipThat.Services;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace ClipThat.Engine.Video;

/// <summary>
/// Hardware-accelerated Media Foundation H.264 encoder (<see cref="IMFTransform"/>) with automatic
/// software MFT fallback, DXGI zero-copy GPU surface input, <c>ICodecAPI</c> GOP/rate-control
/// configuration, and unified synchronous/asynchronous (<see cref="IMFMediaEventGenerator"/>) execution.
/// </summary>
public sealed class HardwareH264Encoder : IDisposable
{
    private const uint H264ProfileHigh = 100u;
    private const uint H264ProfileMain = 77u;
    private const uint RateControlModeCbr = 0u;
    private const uint RateControlModeUnconstrainedVbr = 2u;
    private const int DefaultOutputBufferBytes = 512 * 1024;
    private const int AsyncNeedInputTimeoutMs = 100;

    // Verified Windows SDK codecapi.h / icodecapi.h GUIDs
    private static readonly Guid CodecApiIid = new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");
    private static readonly Guid CodecApiAvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiAvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiAvEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    private readonly GpuVideoProcessor _gpuProcessor;
    private readonly Action<EncodedPacket> _onPacketEncoded;
    private readonly Action<ReadOnlyMemory<byte>>? _onSequenceHeader;
    private readonly Action<Exception>? _onEncoderError;
    private readonly object _mftLock = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly SemaphoreSlim _needInputSemaphore = new(0);
    private readonly ManualResetEventSlim _drainCompleteEvent = new(false);

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly uint _targetBitrateBps;
    private readonly long _defaultFrameDuration100Ns;

    private bool _mfStarted;
    private IMFActivate? _activeActivate;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _eventGenerator;
    private IMFDXGIDeviceManager? _dxgiDeviceManager;
    private Thread? _eventThread;

    private int _inputStreamId;
    private int _outputStreamId;
    private OutputStreamInfo _outputStreamInfo;
    private bool _mftProvidesOutputSamples;
    private bool _isAsync;
    private bool _usesGpuInput;
    private byte[]? _sequenceHeader;
    private int _stopping;
    private int _disposed;

    public HardwareH264Encoder(
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

        _gpuProcessor = gpuProcessor;
        _onPacketEncoded = onPacketEncoded;
        _onSequenceHeader = onSequenceHeader;
        _onEncoderError = onEncoderError;

        _width = gpuProcessor.OutputWidth;
        _height = gpuProcessor.OutputHeight;
        _fps = targetFps == 30 ? 30 : 60;
        _defaultFrameDuration100Ns = TimeSpan.TicksPerSecond / _fps;

        double bitrateMbps = resourceEstimator.GetVideoBitrateMbps(resolutionPreset, _fps);
        _targetBitrateBps = (uint)Math.Clamp((long)Math.Round(bitrateMbps * 1_000_000.0), 250_000L, 100_000_000L);

        try
        {
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            _mfStarted = true;

            InitializeEncoderMft();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string EncoderName { get; private set; } = "H.264";

    public bool IsHardwareAccelerated { get; private set; }

    public bool UsesGpuZeroCopyInput => _usesGpuInput;

    public bool IsAsyncMft => _isAsync;

    public int OutputWidth => _width;

    public int OutputHeight => _height;

    public int TargetFps => _fps;

    public uint TargetBitrateBps => _targetBitrateBps;

    /// <summary>
    /// Encodes a single NV12 frame produced by <see cref="GpuVideoProcessor"/>.
    /// Returns <see langword="false"/> if the encoder is stopping or momentarily backlogged.
    /// </summary>
    public bool EncodeFrame(ID3D11Texture2D nv12Texture, long timestamp100Ns, long duration100Ns)
    {
        ArgumentNullException.ThrowIfNull(nv12Texture);

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0)
        {
            return false;
        }

        if (_isAsync)
        {
            try
            {
                if (!_needInputSemaphore.Wait(AsyncNeedInputTimeoutMs, _stopCts.Token))
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        long effectiveDuration = duration100Ns > 0 ? duration100Ns : _defaultFrameDuration100Ns;
        List<EncodedPacket>? syncPackets = null;

        try
        {
            using IMFSample inputSample = CreateInputSample(nv12Texture, timestamp100Ns, effectiveDuration);

            lock (_mftLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0 || _transform is null)
                {
                    return false;
                }

                if (_isAsync)
                {
                    _transform.ProcessInput(_inputStreamId, inputSample, 0);
                }
                else
                {
                    try
                    {
                        _transform.ProcessInput(_inputStreamId, inputSample, 0);
                    }
                    catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Notaccepting)
                    {
                        DrainSyncOutputsLocked(ref syncPackets);
                        _transform.ProcessInput(_inputStreamId, inputSample, 0);
                    }

                    DrainSyncOutputsLocked(ref syncPackets);
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
    }

    private void InitializeEncoderMft()
    {
        var outputTypeInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        // Pass 1: Prefer hardware H.264 encoders (NVENC, QuickSync, AMF).
        uint hardwareFlags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
        if (TryActivateAndConfigureFromFlags(hardwareFlags, outputTypeInfo, isHardwarePass: true))
        {
            return;
        }

        // Pass 2: Fall back to synchronous or asynchronous software H.264 encoders (e.g. Microsoft H264 Encoder MFT).
        uint softwareFlags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter);
        if (TryActivateAndConfigureFromFlags(softwareFlags, outputTypeInfo, isHardwarePass: false))
        {
            return;
        }

        throw new InvalidOperationException(
            "Nie znaleziono działającego kodera wideo H.264 (ani sprzętowego, ani programowego Media Foundation).");
    }

    private bool TryActivateAndConfigureFromFlags(uint enumFlags, RegisterTypeInfo outputTypeInfo, bool isHardwarePass)
    {
        using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            enumFlags,
            inputType: null,
            outputType: outputTypeInfo);

        var candidates = new List<IMFActivate>();
        foreach (IMFActivate activate in activates)
        {
            candidates.Add(activate);
        }

        int winningIndex = -1;
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                IMFActivate candidate = candidates[i];
                if (TryConfigureCandidate(candidate, isHardwarePass))
                {
                    winningIndex = i;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i != winningIndex)
                {
                    candidates[i].Dispose();
                }
            }
        }
    }

    private bool TryConfigureCandidate(IMFActivate activate, bool isHardwarePass)
    {
        IMFTransform? transform = null;
        IMFDXGIDeviceManager? dxgiManager = null;
        IMFMediaEventGenerator? eventGenerator = null;

        try
        {
            string friendlyName = TryGetFriendlyName(activate);
            transform = activate.ActivateObject<IMFTransform>();

            bool isAsync = false;
            bool d3d11Aware = false;

            using (IMFAttributes? attributes = TryGetTransformAttributes(transform))
            {
                if (attributes is not null)
                {
                    if (TryGetUInt32(attributes, TransformAttributeKeys.TransformAsync, out uint asyncFlag) &&
                        asyncFlag != 0)
                    {
                        isAsync = true;
                        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                    }

                    if (TryGetUInt32(attributes, TransformAttributeKeys.D3D11Aware, out uint d3d11Flag) &&
                        d3d11Flag != 0)
                    {
                        d3d11Aware = true;
                    }
                }
            }

            bool usesGpuInput = false;
            if (d3d11Aware)
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
                    Debug.WriteLine($"Koder '{friendlyName}' odrzucił IMFDXGIDeviceManager (fallback CPU): {ex.Message}");
                    dxgiManager?.Dispose();
                    dxgiManager = null;
                    usesGpuInput = false;
                }
            }

            (int inputStreamId, int outputStreamId) = ResolveStreamIds(transform);

            // Apply CodecAPI hints prior to SetOutputType (required by several hardware MFTs such as QSV/AMF).
            ConfigureCodecApi(transform);

            ConfigureOutputMediaType(transform, outputStreamId);
            ConfigureInputMediaType(transform, inputStreamId);

            // Re-apply CodecAPI hints after media types are negotiated for MFTs that validate after SetOutputType.
            ConfigureCodecApi(transform);

            OutputStreamInfo outputStreamInfo = transform.GetOutputStreamInfo(outputStreamId);
            OutputStreamInfoFlags streamFlags = (OutputStreamInfoFlags)outputStreamInfo.Flags;
            bool mftProvidesSamples =
                (streamFlags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0 ||
                (streamFlags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0;

            if (isAsync)
            {
                eventGenerator = transform.QueryInterface<IMFMediaEventGenerator>();
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
            IsHardwareAccelerated = isHardwarePass;

            TryCaptureOutputSequenceHeaderLocked();

            if (_isAsync)
            {
                StartAsyncEventPump();
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Pominięto koder MFT: {ex.Message}");
            eventGenerator?.Dispose();
            dxgiManager?.Dispose();

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

                transform.Dispose();
            }

            try
            {
                activate.ShutdownObject();
            }
            catch
            {
                // Ignore cleanup errors for rejected MFT activate.
            }

            return false;
        }
    }

    private void ConfigureOutputMediaType(IMFTransform transform, int outputStreamId)
    {
        uint[] profileAttempts = { H264ProfileHigh, H264ProfileMain, 0u };
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
        // Primary path: explicit NV12 progressive media type matching GpuVideoProcessor dimensions and FPS.
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
            catch (SharpGenException)
            {
                // Fall through and negotiate using the MFT's advertised NV12 template.
            }
        }

        for (int index = 0; index < 32; index++)
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

            using (available)
            {
                if (available is null)
                {
                    break;
                }

                if (available.GetGUID(MediaTypeAttributeKeys.Subtype, out Guid subtype).Success &&
                    subtype == VideoFormatGuids.NV12)
                {
                    available.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
                    available.Set(MediaTypeAttributeKeys.FrameSize, PackUInt64((uint)_width, (uint)_height));
                    available.Set(MediaTypeAttributeKeys.FrameRate, PackUInt64((uint)_fps, 1u));
                    available.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackUInt64(1u, 1u));
                    transform.SetInputType(inputStreamId, available, 0);
                    return;
                }
            }
        }

        throw new InvalidOperationException("Koder H.264 nie zaakceptował wejściowego formatu NV12.");
    }

    private void ConfigureCodecApi(IMFTransform transform)
    {
        Guid codecApiIid = CodecApiIid;
        int hr = Marshal.QueryInterface(transform.NativePointer, ref codecApiIid, out nint codecApiPtr);
        if (hr < 0 || codecApiPtr == nint.Zero)
        {
            return;
        }

        ICodecAPI? codecApi = null;
        try
        {
            codecApi = (ICodecAPI)Marshal.GetUniqueObjectForIUnknown(codecApiPtr);

            // Prefer CBR for deterministic RAM ring-buffer sizing; fall back to Unconstrained VBR if unsupported.
            if (!TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeCbr))
            {
                TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonRateControlMode, RateControlModeUnconstrainedVbr);
            }

            TrySetCodecApiUInt32(codecApi, CodecApiAvEncCommonMeanBitRate, _targetBitrateBps);
            TrySetCodecApiUInt32(codecApi, CodecApiAvEncMpvGopSize, (uint)_fps);
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

    private static bool TrySetCodecApiUInt32(ICodecAPI codecApi, Guid propertyGuid, uint value)
    {
        Guid api = propertyGuid;
        if (codecApi.IsSupported(ref api) != 0)
        {
            return false;
        }

        object boxed = value;
        return codecApi.SetValue(ref api, ref boxed) >= 0;
    }

    private IMFSample CreateInputSample(ID3D11Texture2D nv12Texture, long timestamp100Ns, long duration100Ns)
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
                    nv12Texture,
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
                _gpuProcessor.CopyNv12ToMediaBuffer(nv12Texture, memBuffer);
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

    private void StartAsyncEventPump()
    {
        _eventThread = new Thread(AsyncEventLoop)
        {
            Name = "ClipThat.H264Encoder.Events",
            IsBackground = true
        };
        _eventThread.SetApartmentState(ApartmentState.MTA);
        _eventThread.Start();
    }

    private void AsyncEventLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            IMFMediaEventGenerator? generator = _eventGenerator;
            if (generator is null)
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
                    _onEncoderError?.Invoke(ex);
                }

                break;
            }

            using (mediaEvent)
            {
                MediaEventTypes eventType = mediaEvent.EventType;
                switch (eventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                        {
                            try
                            {
                                _needInputSemaphore.Release();
                            }
                            catch (ObjectDisposedException)
                            {
                                return;
                            }
                        }

                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        EncodedPacket? packet = null;
                        try
                        {
                            lock (_mftLock)
                            {
                                if (Volatile.Read(ref _disposed) != 0 || _transform is null)
                                {
                                    break;
                                }

                                // Async MFT contract: call ProcessOutput strictly once per METransformHaveOutput event.
                                // If MF_E_TRANSFORM_STREAM_CHANGE is returned, HandleOutputStreamChangeLocked updates
                                // the output type and the MFT will emit a subsequent METransformHaveOutput event.
                                TryProcessSingleOutputLocked(out packet);
                            }

                            if (packet is not null)
                            {
                                DeliverPacketSafe(packet);
                            }
                        }
                        catch (Exception ex)
                        {
                            packet?.Dispose();
                            if (Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _disposed) == 0)
                            {
                                _onEncoderError?.Invoke(ex);
                            }
                        }

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
                            _onEncoderError?.Invoke(new SharpGenException(status));
                        }

                        return;
                }
            }
        }
    }

    private void DrainSyncOutputsLocked(ref List<EncodedPacket>? packets)
    {
        while (true)
        {
            ProcessOutputAttemptResult result = TryProcessSingleOutputLocked(out EncodedPacket? packet);
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

    private ProcessOutputAttemptResult TryProcessSingleOutputLocked(out EncodedPacket? packet)
    {
        packet = null;
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

        using IMFMediaType updatedType = _transform.GetOutputAvailableType(_outputStreamId, 0);
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
            using IMFMediaType currentOutputType = _transform.GetOutputCurrentType(_outputStreamId);
            if (currentOutputType.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out uint blobSize).Success &&
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

            bool isCleanPoint = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint cleanPoint).Success &&
                                cleanPoint != 0;
            bool isKeyframe = isCleanPoint || ContainsAnnexBIdrNal(payload);

            if (isKeyframe && _sequenceHeader is null)
            {
                byte[]? extractedHeader = TryExtractAnnexBSpsPps(payload);
                if (extractedHeader is not null)
                {
                    _sequenceHeader = extractedHeader;
                    _onSequenceHeader?.Invoke(extractedHeader);
                }
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

    private void DeliverPacketSafe(EncodedPacket packet)
    {
        try
        {
            _onPacketEncoded(packet);
        }
        catch
        {
            packet.Dispose();
            throw;
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
            string? name = activate.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return string.IsNullOrWhiteSpace(name) ? "Media Foundation H.264" : name.Trim();
        }
        catch
        {
            return "Media Foundation H.264";
        }
    }

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    private static bool ContainsAnnexBIdrNal(ReadOnlySpan<byte> data)
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
                    if (nalType == 5) // IDR slice
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
        using var stream = new System.IO.MemoryStream();
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

        try
        {
            _stopCts.Cancel();
        }
        catch
        {
            // Ignore cancellation errors during teardown.
        }

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

                    try
                    {
                        _activeActivate?.ShutdownObject();
                    }
                    catch
                    {
                        // Best-effort activate shutdown.
                    }
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

        lock (_mftLock)
        {
            _eventGenerator?.Dispose();
            _eventGenerator = null;

            _transform?.Dispose();
            _transform = null;

            _activeActivate?.Dispose();
            _activeActivate = null;

            _dxgiDeviceManager?.Dispose();
            _dxgiDeviceManager = null;
        }

        _stopCts.Dispose();
        _needInputSemaphore.Dispose();
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
