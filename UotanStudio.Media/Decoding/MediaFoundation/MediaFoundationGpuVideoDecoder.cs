using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// Hardware-accelerated H.264/H.265 decoder: an asynchronous D3D11-aware Media
/// Foundation MFT decodes NV12 on the GPU, and the Microsoft video processor
/// converts NV12 → BGRA before a staging readback into pooled CPU buffers.
/// <para>硬件加速 H.264/H.265 解码器：异步 D3D11 感知的 Media Foundation MFT 在
/// GPU 上解码出 NV12，微软视频处理器把 NV12 转为 BGRA，再经 staging 回读进
/// 池化 CPU 缓冲区。</para>
/// </summary>
/// <remarks>
/// <para>
/// The async MFT is driven through its <c>IMFMediaEventGenerator</c>
/// (NeedInput / HaveOutput). A single D3D11 device is shared between the
/// decoder and the video processor via <c>IMFDXGIDeviceManager</c>; the final
/// BGRA pixels reach the callback with the same pooling contract as the CPU
/// decoder, so consumers do not change. Any initialization failure falls back
/// to <see cref="MediaFoundationVideoDecoder"/> at the factory.
/// </para>
/// <para>
/// 异步 MFT 经其 <c>IMFMediaEventGenerator</c>（NeedInput / HaveOutput）驱动。
/// 单一 D3D11 设备经 <c>IMFDXGIDeviceManager</c> 在解码器与视频处理器之间共享；
/// 最终 BGRA 像素以与 CPU 解码器相同的池化契约到达回调，消费方无需改动。
/// 初始化任何失败都会在工厂处回退到 <see cref="MediaFoundationVideoDecoder"/>。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationGpuVideoDecoder : IVideoDecoder
{
    private static readonly object LifetimeLock = new();
    private static int _leases;
    private nint _transform;
    private nint _eventGenerator;
    private nint _deviceManager;
    private nint _device;
    private nint _context;
    private nint _videoDevice;
    private nint _videoContext;
    private nint _processorEnumerator;
    private nint _processor;
    private nint _outputTexture;
    private nint _outputView;
    private nint _stagingTexture;
    private nint _inputType;
    private Action<DecodedVideoFrame>? _callback;
    private int _width, _height;
    private int _ownerThread;
    private bool _comOwned, _mfOwned, _disposed;
    private long _sequence;
    private uint _outputFrameCounter;
    private bool _waitingForInput;
    private bool _waitingForOutput;

    /// <inheritdoc />
    public bool IsInitialized => _transform != 0;

    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (_ownerThread != 0) CheckThread();
        ReleaseAll();
        AcquireLifetime();
        _callback = frameCallback;
        _sequence = 0;
        _outputFrameCounter = 0;
        try
        {
            CreateD3D11Device();
            CreateDeviceManager();
            CreateAsyncDecoder(codec, width, height);
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit)
        => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        nint sample = CreateSample(accessUnit);
        try
        {
            Check(MfCom.SetTime(sample, timestampTicks), "sample time");
            PumpUntilInputAccepted(sample, timestampTicks);
        }
        finally { MfCom.Release(sample); }
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageFlush), "flush");
        Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "restart stream");
        _sequence = 0;
        _waitingForInput = false;
        _waitingForOutput = false;
    }

    /// <inheritdoc />
    public void Drain()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageNotifyEndOfStream), "end of stream");
        Check(MfCom.Message(_transform, MfInterop.MftMessageCommandDrain), "drain command");
        PumpEvents(acceptOutput: true, blocking: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_ownerThread != 0) CheckThread();
        _disposed = true;
        ReleaseAll();
        ReleaseLifetime();
    }

    private void AcquireLifetime()
    {
        int hr = MfInterop.CoInitializeEx(0, MfInterop.CoInitMultithreaded);
        // RPC_E_CHANGED_MODE means the caller already owns a different apartment.
        if (hr != unchecked((int)0x80010106)) Check(hr, "CoInitializeEx");
        _comOwned = hr >= 0;
        _ownerThread = Environment.CurrentManagedThreadId;
        try
        {
            lock (LifetimeLock)
            {
                if (_leases == 0) Check(MfInterop.MFStartup(MfInterop.MfVersion, 0), "MFStartup");
                _leases++;
                _mfOwned = true;
            }
        }
        catch
        {
            if (_comOwned) MfInterop.CoUninitialize();
            _comOwned = false;
            _ownerThread = 0;
            throw;
        }
    }

    private void ReleaseLifetime()
    {
        if (_mfOwned)
        {
            lock (LifetimeLock)
            {
                if (--_leases == 0) _ = MfInterop.MFShutdown();
                _mfOwned = false;
            }
        }
        if (_comOwned) { MfInterop.CoUninitialize(); _comOwned = false; }
        _ownerThread = 0;
    }

    private void CreateD3D11Device()
    {
        Check(D3D11Interop.D3D11CreateDevice(
            0, D3D11Interop.DriverTypeHardware, 0,
            D3D11Interop.CreateDeviceVideoSupport | D3D11Interop.CreateDeviceBgraSupport,
            0, 0, D3D11Interop.SdkVersion,
            out _device, out _, out _context), "D3D11CreateDevice");
        try
        {
            Check(MfCom.QueryInterface(_device, D3D11Interop.IidVideoDevice, out _videoDevice), "ID3D11VideoDevice");
            Check(MfCom.QueryInterface(_context, D3D11Interop.IidVideoContext, out _videoContext), "ID3D11VideoContext");
            // The device manager hands the device to MF on another thread; enable
            // multithread protection so concurrent decoder/processor use is legal.
            if (MfCom.QueryInterface(_context, D3D11Interop.IidMultithread, out nint multithread) >= 0)
            {
                try
                {
                    // SetMultithreadProtected(TRUE) is ID3D11Multithread slot 5.
                    _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)MfCom.PublicSlot(multithread, 5))(multithread, 1);
                }
                finally { MfCom.Release(multithread); }
            }
        }
        catch
        {
            ReleaseD3D();
            throw;
        }
    }

    private void CreateDeviceManager()
    {
        Check(MfInterop.MFCreateDXGIDeviceManager(out uint token, out _deviceManager), "MFCreateDXGIDeviceManager");
        try
        {
            Check(MfCom.DxgiResetDevice(_deviceManager, _device, token), "ResetDevice");
        }
        catch
        {
            MfCom.Release(_deviceManager);
            _deviceManager = 0;
            throw;
        }
    }

    private void CreateAsyncDecoder(VideoCodecId codec, int width, int height)
    {
        Guid format = CodecGuid(codec);
        Guid inputSubtype = format;
        var registration = new MftRegisterTypeInfo(MfInterop.MediaTypeVideo, inputSubtype);
        Check(MfInterop.MFTEnumEx(MfInterop.CategoryVideoDecoder, MfInterop.MftEnumFlagsAsyncHardware,
            registration, 0, out nint activates, out uint count), "MFTEnumEx(async)");
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint activate = Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size));
                int hr = MfCom.Activate(activate, MfInterop.IidImfTransform, out nint transform);
                if (hr < 0) { MfCom.Release(transform); continue; }
                _transform = transform;
                if (TryConfigureDecoder(transform, codec, width, height)) break;
                ReleaseTransformOnly();
            }
        }
        finally
        {
            for (int i = 0; i < count; i++) MfCom.Release(Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size)));
            Marshal.FreeCoTaskMem(activates);
        }
        if (_transform == 0)
            throw new NotSupportedException($"No asynchronous hardware Media Foundation decoder accepts {codec}.");
        StartStreaming();
    }

    private bool TryConfigureDecoder(nint transform, VideoCodecId codec, int width, int height)
    {
        // Verify the MFT is D3D11-aware and async before investing in it.
        if (MfCom.GetAttributes(transform, out nint attributes) < 0 || attributes == 0) return false;
        try
        {
            if (MfCom.GetUInt32(attributes, MfInterop.SaD3D11Aware, out uint aware) < 0 || aware == 0) return false;
            if (MfCom.GetUInt32(attributes, MfInterop.TransformAsync, out uint asyncFlag) < 0 || asyncFlag == 0) return false;
            Check(MfCom.SetUInt32(attributes, MfInterop.TransformAsyncUnlock, 1), "unlock async MFT");

            // Hand the D3D11 device to the decoder through MF_DECODER_D3D_MANAGER.
            Check(MfCom.SetUnknown(attributes, MfInterop.DecoderD3DManager, _deviceManager), "set D3D manager");
        }
        finally { MfCom.Release(attributes); }

        Check(MfCom.QueryInterface(transform, MfInterop.IidImfMediaEventGenerator, out _eventGenerator), "event generator");

        if (!BuildInputType(codec, width, height)) return false;
        if (!SelectOutputType()) return false;
        BuildVideoProcessor();
        return true;
    }

    private bool BuildInputType(VideoCodecId codec, int width, int height)
    {
        Check(MfInterop.MFCreateMediaType(out nint input), "MFCreateMediaType");
        _inputType = input;
        Check(MfCom.SetGuid(input, MfInterop.MtMajorType, MfInterop.MediaTypeVideo), "major type");
        Check(MfCom.SetGuid(input, MfInterop.MtSubtype, CodecGuid(codec)), "subtype");
        Check(MfCom.SetUInt64(input, MfInterop.MtFrameSize, ((ulong)(uint)width << 32) | (uint)height), "frame size");
        if (MfCom.SetType(_transform, input, true) < 0) return false;
        _width = width;
        _height = height;
        return true;
    }

    private bool SelectOutputType()
    {
        for (uint index = 0; index < 64; index++)
        {
            int hr = MfCom.OutputType(_transform, index, out nint type);
            if (hr < 0) { MfCom.Release(type); return false; }
            try
            {
                if (MfCom.GetGuid(type, MfInterop.MtSubtype, out Guid subtype) < 0 || subtype != MfInterop.VideoFormatNv12) continue;
                if (MfCom.SetType(_transform, type, false) < 0) continue;
                Check(MfCom.GetUInt64(type, MfInterop.MtFrameSize, out ulong size), "output dimensions");
                _width = checked((int)(size >> 32));
                _height = checked((int)(size & uint.MaxValue));
                VideoFrameLimits.ValidateDimensions(_width, _height);
                return true;
            }
            finally { MfCom.Release(type); }
        }
        return false;
    }

    private void BuildVideoProcessor()
    {
        var content = new D3D11VideoProcessorContentDesc
        {
            InputFrameFormat = D3D11Interop.VideoFrameFormatProgressive,
            InputFrameRateNumerator = 60,
            InputFrameRateDenominator = 1,
            InputWidth = (uint)_width,
            InputHeight = (uint)_height,
            OutputFrameRateNumerator = 60,
            OutputFrameRateDenominator = 1,
            OutputWidth = (uint)_width,
            OutputHeight = (uint)_height,
            Usage = 0,
        };
        Check(MfCom.D3DCreateVideoProcessorEnumerator(_videoDevice, content, out _processorEnumerator), "CreateVideoProcessorEnumerator");
        Check(MfCom.D3DCreateVideoProcessor(_videoDevice, _processorEnumerator, 0, out _processor), "CreateVideoProcessor");

        var outputDesc = new D3D11Interop.Texture2DDesc
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Interop.DxgiFormatB8G8R8A8,
            SampleDescCount = 1,
            Usage = D3D11Interop.UsageDefault,
            BindFlags = D3D11Interop.BindRenderTarget,
        };
        Check(MfCom.D3DCreateTexture2D(_device, outputDesc, 0, out _outputTexture), "CreateTexture2D(BGRA output)");
        var outputViewDesc = new D3D11Interop.VideoProcessorOutputViewDesc
        {
            ViewDimension = D3D11Interop.VpovDimensionTexture2D,
        };
        Check(MfCom.D3DCreateVideoProcessorOutputView(_videoDevice, _outputTexture, outputViewDesc, out _outputView), "CreateVideoProcessorOutputView");

        var stagingDesc = outputDesc;
        stagingDesc.Usage = D3D11Interop.UsageStaging;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11Interop.CpuAccessRead;
        Check(MfCom.D3DCreateTexture2D(_device, stagingDesc, 0, out _stagingTexture), "CreateTexture2D(staging)");
    }

    private void StartStreaming()
    {
        Check(MfCom.Message(_transform, MfInterop.MftMessageBeginStreaming), "begin streaming");
        Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "start of stream");
    }

    /// <summary>
    /// Feeds one sample, pumping the async event loop until the MFT accepts it,
    /// then drains whatever output became available.
    /// </summary>
    private void PumpUntilInputAccepted(nint sample, long timestampTicks)
    {
        for (int round = 0; round < 256; round++)
        {
            if (!_waitingForInput && !_waitingForOutput)
                PumpEvents(acceptOutput: true, blocking: false);

            if (!_waitingForOutput)
            {
                int hr = MfCom.Input(_transform, sample);
                if (hr >= 0) { DrainAvailableOutput(); return; }
                if (hr == MfInterop.MfENotAccepting) { _waitingForInput = true; continue; }
                Check(hr, "ProcessInput");
            }

            PumpEvents(acceptOutput: true, blocking: true);
        }
        throw new InvalidOperationException("Async decoder failed to accept input within bounded rounds.");
    }

    private void DrainAvailableOutput()
    {
        while (_waitingForOutput)
        {
            bool handled = PumpEvents(acceptOutput: true, blocking: false);
            if (!handled && !_waitingForOutput) break;
        }
    }

    /// <summary>Pumps the event generator; returns true when any event was handled.</summary>
    private bool PumpEvents(bool acceptOutput, bool blocking)
    {
        bool handled = false;
        while (true)
        {
            int hr = MfCom.GetEvent(_eventGenerator, blocking ? 0u : 1u, out nint mediaEvent);
            if (hr < 0)
            {
                MfCom.Release(mediaEvent);
                if (hr == D3D11Interop.MfENoEventsAvailable) return handled;
                Check(hr, "GetEvent");
            }
            try
            {
                uint type;
                Check(MfCom.EventType(mediaEvent, out type), "event type");
                switch (type)
                {
                    case D3D11Interop.MeTransformNeedInput:
                        _waitingForInput = false;
                        handled = true;
                        break;
                    case D3D11Interop.MeTransformHaveOutput:
                        if (acceptOutput) { ProduceOutput(); handled = true; }
                        else _waitingForOutput = true;
                        break;
                    default:
                        handled = true;
                        break;
                }
            }
            finally { MfCom.Release(mediaEvent); }
            if (!blocking) return handled;
        }
    }

    private void ProduceOutput()
    {
        var output = new MftOutputDataBuffer();
        Check(MfCom.Output(_transform, ref output), "ProcessOutput(async)");
        try
        {
            if (output.Sample != 0) ConvertSample(output.Sample);
        }
        finally
        {
            MfCom.Release(output.Events);
            // Async MFTs provide their own samples (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES),
            // so output.Sample is always the MFT-owned instance here.
            if (output.Sample != 0) MfCom.Release(output.Sample);
        }
    }

    private void ConvertSample(nint sample)
    {
        Check(MfCom.GetTime(sample, out long timestamp), "sample time");
        // The async hardware MFT hands back one DXGI-backed buffer per sample.
        Check(MfCom.BufferCount(sample, out uint buffers), "GetBufferCount");
        if (buffers == 0) throw new InvalidDataException("Decoder produced a sample without buffers.");
        Check(MfCom.GetBufferByIndex(sample, 0, out nint buffer), "GetBufferByIndex");
        try
        {
            Check(MfCom.QueryInterface(buffer, D3D11Interop.IidDxgiBuffer, out nint dxgiBuffer), "IMFDXGIBuffer");
            try
            {
                Check(MfCom.DxgiGetResource(dxgiBuffer, D3D11Interop.IidTexture2D, out nint sourceTexture), "GetResource(Texture2D)");
                Check(MfCom.DxgiSubresourceIndex(dxgiBuffer, out uint subresource), "GetSubresourceIndex");

                BlitToOutput(sourceTexture, subresource);
                ReadBack(timestamp);
            }
            finally { MfCom.Release(dxgiBuffer); }
        }
        finally { MfCom.Release(buffer); }
    }

    private void BlitToOutput(nint sourceTexture, uint subresource)
    {
        var inputViewDesc = new D3D11Interop.VideoProcessorInputViewDesc
        {
            ViewDimension = D3D11Interop.VpivDimensionTexture2D,
            Texture2DArraySlice = subresource,
        };
        Check(MfCom.D3DCreateVideoProcessorInputView(_videoDevice, sourceTexture, inputViewDesc, out nint inputView), "CreateVideoProcessorInputView");
        try
        {
            var stream = new D3D11Interop.VideoProcessorStream
            {
                Enable = 1,
                OutputIndex = 0,
                InputFrameOrField = checked((int)_outputFrameCounter),
                PastFrames = 0,
                FutureFrames = 0,
                InputFramePtr = inputView,
            };
            Check(MfCom.D3DVideoProcessorBlt(_videoContext, _processor, _outputView, _outputFrameCounter, 1, stream), "VideoProcessorBlt");
            _outputFrameCounter++;
        }
        finally { MfCom.Release(inputView); }
    }

    private void ReadBack(long timestampTicks)
    {
        Check(MfCom.D3DMap(_context, _stagingTexture, 0, 1 /* D3D11_MAP_READ */, 0, out var mapped), "Map(staging)");
        try
        {
            int stride = checked((int)mapped.RowPitch);
            int rowBytes = checked(_width * 4);
            int required = checked(rowBytes * _height);
            if (stride * _height < required) throw new InvalidDataException("Staging readback is truncated.");
            byte[] pooled = ArrayPool<byte>.Shared.Rent(required);
            fixed (byte* target = pooled.AsSpan(0, required))
            {
                Span<byte> destination = new(target, required);
                for (int row = 0; row < _height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (nint)row * stride, rowBytes)
                        .CopyTo(destination.Slice(row * rowBytes, rowBytes));
                }
            }
            using var frame = new GpuDecodedVideoFrame(pooled, required, _width, _height, rowBytes, timestampTicks);
            _callback!(frame);
        }
        finally { MfCom.D3DUnmap(_context, _stagingTexture, 0); }
    }

    private static nint CreateSample(ReadOnlySpan<byte> data)
    {
        if (data.Length > VideoFrameLimits.MaximumFrameBytes) throw new InvalidDataException("MFT buffer exceeds the frame limit.");
        Check(MfInterop.MFCreateAlignedMemoryBuffer((uint)Math.Max(data.Length, 1), 0, out nint buffer), "MFCreateAlignedMemoryBuffer");
        try
        {
            if (!data.IsEmpty)
            {
                Check(MfCom.Lock(buffer, out nint target, out _), "buffer lock");
                try { data.CopyTo(new Span<byte>((void*)target, data.Length)); }
                finally { Check(MfCom.Unlock(buffer), "buffer unlock"); }
                Check(MfCom.SetLength(buffer, (uint)data.Length), "buffer length");
            }
            Check(MfInterop.MFCreateSample(out nint sample), "MFCreateSample");
            try { Check(MfCom.AddBuffer(sample, buffer), "AddBuffer"); return sample; }
            catch { MfCom.Release(sample); throw; }
        }
        finally { MfCom.Release(buffer); }
    }

    private void ReleaseAll()
    {
        ReleaseTransformOnly();
        if (_stagingTexture != 0) { MfCom.Release(_stagingTexture); _stagingTexture = 0; }
        if (_outputView != 0) { MfCom.Release(_outputView); _outputView = 0; }
        if (_outputTexture != 0) { MfCom.Release(_outputTexture); _outputTexture = 0; }
        if (_processor != 0) { MfCom.Release(_processor); _processor = 0; }
        if (_processorEnumerator != 0) { MfCom.Release(_processorEnumerator); _processorEnumerator = 0; }
        if (_inputType != 0) { MfCom.Release(_inputType); _inputType = 0; }
        if (_eventGenerator != 0) { _eventGenerator = 0; } // Released with the transform (same object).
        ReleaseD3D();
        if (_deviceManager != 0) { MfCom.Release(_deviceManager); _deviceManager = 0; }
        _waitingForInput = false;
        _waitingForOutput = false;
    }

    private void ReleaseTransformOnly()
    {
        if (_transform != 0)
        {
            _ = MfCom.Message(_transform, MfInterop.MftMessageFlush);
            MfCom.Release(_transform);
            _transform = 0;
        }
        _callback = null;
    }

    private void ReleaseD3D()
    {
        if (_videoContext != 0) { MfCom.Release(_videoContext); _videoContext = 0; }
        if (_videoDevice != 0) { MfCom.Release(_videoDevice); _videoDevice = 0; }
        if (_context != 0) { MfCom.Release(_context); _context = 0; }
        if (_device != 0) { MfCom.Release(_device); _device = 0; }
    }

    private void CheckThread()
    {
        if (_ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The decoder must remain on its owning thread.");
    }

    private void CheckReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) throw new InvalidOperationException("Decoder is not initialized.");
        CheckThread();
    }

    private static Guid CodecGuid(VideoCodecId codec) => codec switch
    {
        VideoCodecId.H264 => MfInterop.VideoFormatH264,
        VideoCodecId.H265 => MfInterop.VideoFormatHevc,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"Media Foundation GPU {operation} failed: 0x{hr:X8}.");
    }
}

/// <summary>
/// BGRA frame delivered by <see cref="MediaFoundationGpuVideoDecoder"/>. It
/// derives from <see cref="DecodedVideoFrame"/> so the GPU path flows through
/// the same callback; consumers detect it to skip the NV12→BGRA conversion.
/// <para><see cref="MediaFoundationGpuVideoDecoder"/> 输出的 BGRA 帧。它派生自
/// <see cref="DecodedVideoFrame"/>，因此 GPU 路径走同一回调；消费方据此跳过
/// NV12→BGRA 转换。</para>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DecodedVideoFrame.Buffer"/> holds premultiplied-opaque BGRA pixels
/// (4 bytes per pixel, row-major, <see cref="DecodedVideoFrame.Stride"/> ≥ width × 4)
/// instead of NV12 planes; the class-name "Nv12" comments of the base type do not apply.
/// </para>
/// <para>
/// <see cref="DecodedVideoFrame.Buffer"/> 持有 BGRA 像素（每像素 4 字节，行主序，
/// <see cref="DecodedVideoFrame.Stride"/> ≥ 宽度 × 4），而非 NV12 平面；基类型中
/// "Nv12" 相关注释不适用。
/// </para>
/// </remarks>
public sealed class GpuDecodedVideoFrame : DecodedVideoFrame
{
    /// <summary>Creates a frame taking ownership of a pooled BGRA buffer.</summary>
    /// <summary>创建一个接管池化 BGRA 缓冲区所有权的帧。</summary>
    public GpuDecodedVideoFrame(byte[] buffer, int length, int width, int height, int stride, long timestampTicks)
        : base(buffer, length, width, height, stride, timestampTicks, skipNv12Validation: true)
    {
    }
}
