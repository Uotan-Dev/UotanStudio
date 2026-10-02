using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// D3D11-aware decoding with pooled NV12 readback or GPU NV12-to-BGRA processing.
/// Supports synchronous system transforms and asynchronous hardware transforms sharing one D3D11 device.
/// <para>支持 D3D11 的解码，可选池化 NV12 回读或 GPU 上的 NV12 转 BGRA 处理。</para>
/// <para>同时支持同步系统变换与共享同一 D3D11 设备的异步硬件变换。</para>
/// </summary>
/// <remarks>Direct instances never fall back to CPU. The platform factory owns initialization fallback.</remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationGpuVideoDecoder : IVideoDecoder
{
    private readonly VideoDecoderOptions _options;
    /// <summary>
    /// Creates a D3D11 decoder using stream color metadata.
    /// <para>创建采用码流色彩信息的 D3D11 解码器。</para>
    /// </summary>
    public MediaFoundationGpuVideoDecoder() : this(new VideoDecoderOptions()) { }
    /// <summary>
    /// Creates a D3D11 decoder with per-session color overrides.
    /// <para>创建带会话色彩设置的 D3D11 解码器。</para>
    /// </summary>
    /// <param name="options">
    /// Color-space overrides and output-format preference; must not be <see langword="null"/>.
    /// <para>色彩空间覆盖与输出格式偏好；不得为 <see langword="null"/>。</para>
    /// </param>
    public MediaFoundationGpuVideoDecoder(VideoDecoderOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));
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
    private uint _stagingWidth, _stagingHeight;
    private nint _inputType;
    private Action<DecodedVideoFrame>? _callback;
    private bool _inCallback;
    private int _width, _height;
    private int _ownerThread;
    private bool _comOwned, _mfOwned, _disposed;
    private long _sequence;
    private uint _outputFrameCounter;
    private bool _isAsync;
    private int _inputRequests;
    private bool _drainComplete;
    private uint _inputColorSpace;
    private byte[]? _readbackBuffer;

    /// <summary>
    /// Whether a D3D11 media transform is currently configured.
    /// <para>当前是否已配置 D3D11 媒体变换。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after a successful <see cref="Initialize"/>; <see langword="false"/> before
    /// initialization, after a failed re-initialization, and after <see cref="Dispose"/>.
    /// <para>成功执行 <see cref="Initialize"/> 后为 <see langword="true"/>；初始化之前、重新初始化失败之后
    /// 以及 <see cref="Dispose"/> 之后为 <see langword="false"/>。</para>
    /// </value>
    public bool IsInitialized => _transform != 0;

    /// <summary>
    /// Creates a D3D11 device and Media Foundation device manager, activates the first asynchronous hardware or
    /// synchronous system transform that advertises D3D11 awareness and NV12 output for the codec, and registers
    /// the frame callback. Any previous native resources are released first. All calls must stay on the thread
    /// that owns the decoder; the first call after construction claims the calling thread.
    /// <para>创建 D3D11 设备与 Media Foundation 设备管理器，激活第一个声明支持 D3D11 且能按编码输出 NV12 的
    /// 异步硬件变换或同步系统变换，并登记帧回调；既有原生资源会先行释放。全部调用必须留在解码器所属线程上；
    /// 构造后的首次调用即绑定当前线程。</para>
    /// </summary>
    /// <param name="codec">Compressed codec of the incoming access units. <para>输入访问单元的压缩编码格式。</para></param>
    /// <param name="width">
    /// Expected coded frame width in pixels, from 1 to 16 384; the actual negotiated output size may differ.
    /// <para>预期编码帧宽度（像素），1 到 16 384；实际协商出的输出尺寸可能不同。</para>
    /// </param>
    /// <param name="height">
    /// Expected coded frame height in pixels, from 1 to 16 384.
    /// <para>预期编码帧高度（像素），1 到 16 384。</para>
    /// </param>
    /// <param name="frameCallback">
    /// Receives every decoded frame; the frame is disposed once the callback returns, so consumers must copy or
    /// convert within it. The callback runs on the calling thread.
    /// <para>接收每个解码帧；回调返回后帧即被释放，消费方须在其内部完成复制或转换。回调在调用线程上执行。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frameCallback"/> is <see langword="null"/>.
    /// <para><paramref name="frameCallback"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The dimensions exceed the supported limits.
    /// <para>尺寸超出支持上限。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// Called from a callback or another thread, or a COM, D3D11 or Media Foundation call failed.
    /// <para>在回调内或其他线程上调用，或 COM、D3D11、Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// No D3D11-aware decoder accepts the codec at the requested size.
    /// <para>没有支持 D3D11 的解码器接受该尺寸下的目标编码。</para>
    /// </exception>
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (_ownerThread != 0) CheckThread();
        ReleaseAll();
        if (!_mfOwned) AcquireLifetime();
        _callback = frameCallback;
        _sequence = 0;
        _outputFrameCounter = 0;
        try
        {
            CreateD3D11Device();
            CreateDeviceManager();
            CreateDecoder(codec, width, height);
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    /// <summary>
    /// Feeds one access unit with a synthesized 60 fps timestamp derived from an internal frame counter.
    /// <para>输入一个访问单元，时间戳为由内部帧计数器合成的 60 fps 值。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete compressed access unit, 1 byte to 32 MiB.
    /// <para>单个完整压缩访问单元，1 字节到 32 MiB。</para>
    /// </param>
    /// <exception cref="OverflowException">The internal timestamp sequence exceeded the tick range.
    /// <para>内部时间戳序列超出 tick 范围。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit)
        => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <summary>
    /// Feeds one access unit with an explicit timestamp. Synchronous transforms emit frames on the calling
    /// thread immediately; asynchronous hardware transforms first wait until the decoder accepts input and then
    /// pump its event queue, so output may arrive after this call returns and through <see cref="Poll"/>.
    /// <para>输入一个访问单元并附带显式时间戳。同步变换立即在调用线程上输出帧；异步硬件变换会先等待解码器
    /// 接受输入，再抽取其事件队列，因此输出可能在本调用返回之后以及 <see cref="Poll"/> 期间到达。</para>
    /// </summary>
    /// <param name="accessUnit">
    /// One complete compressed access unit, 1 byte to 32 MiB.
    /// <para>单个完整压缩访问单元，1 字节到 32 MiB。</para>
    /// </param>
    /// <param name="timestampTicks">
    /// Presentation time in 100-nanosecond ticks, stamped onto the input sample.
    /// <para>呈现时间，以 100 纳刻（tick）为单位，会写入输入样本。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The access unit is empty or exceeds 32 MiB.
    /// <para>访问单元为空或超过 32 MiB。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, the event queue exceeds the
    /// per-packet limit, or a COM or Media Foundation call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、事件队列超出单包上限，或 COM、Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A hardware decoder's renegotiated output type is not NV12.
    /// <para>硬件解码器重新协商后的输出类型不是 NV12 格式。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A decoded sample carried no buffers or an unexpected texture layout.
    /// <para>解码样本不含缓冲区或纹理布局非预期。</para>
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The asynchronous decoder did not signal an input event within five seconds.
    /// <para>异步解码器在五秒内未发出输入事件。</para>
    /// </exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        nint sample = CreateSample(accessUnit);
        try
        {
            Check(MfCom.SetTime(sample, timestampTicks), "sample time");
            if (_isAsync) PumpUntilInputAccepted(sample);
            else
            {
                int hr = MfCom.Input(_transform, sample);
                if (hr == MfInterop.MfENotAccepting)
                {
                    DrainSynchronousOutput();
                    hr = MfCom.Input(_transform, sample);
                }
                Check(hr, "ProcessInput");
                DrainSynchronousOutput();
            }
        }
        finally { MfCom.Release(sample); }
    }

    /// <summary>
    /// Flushes the transform, discards any pending asynchronous events, restarts the stream, and resets the
    /// timestamp and output counters. Use after a stream discontinuity; the decoder stays initialized.
    /// <para>刷新变换对象、丢弃待处理的异步事件、重启码流并重置时间戳与输出计数器；用于码流不连续之后，
    /// 解码器保持已初始化状态。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, the event queue did not empty
    /// after the flush, or a Media Foundation message failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、刷新后事件队列未清空，或 Media Foundation 消息失败。</para>
    /// </exception>
    public void Reset()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageFlush), "flush");
        if (_isAsync) DiscardPendingEvents();
        Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "restart stream");
        _sequence = 0;
        _outputFrameCounter = 0;
        _inputRequests = 0;
        _drainComplete = false;
    }

    /// <summary>
    /// Signals end of stream, issues the drain command, and emits all buffered frames: asynchronous transforms
    /// wait up to five seconds for the drain-complete event, synchronous transforms are drained inline. Call
    /// <see cref="Initialize"/> or <see cref="Reset"/> before feeding a new stream.
    /// <para>发出码流结束通知并下发 drain 命令，输出全部缓存帧：异步变换最多等待五秒的 drain-complete 事件，
    /// 同步变换则在调用内直接排空。输入新码流前须调用 <see cref="Initialize"/> 或 <see cref="Reset"/>。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, or a Media Foundation call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用，或 Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The asynchronous decoder did not report drain completion within five seconds.
    /// <para>异步解码器在五秒内未报告 drain 完成。</para>
    /// </exception>
    public void Drain()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageNotifyEndOfStream), "end of stream");
        _drainComplete = false;
        Check(MfCom.Message(_transform, MfInterop.MftMessageCommandDrain), "drain command");
        if (_isAsync) WaitForEventCondition(drain: true);
        else DrainSynchronousOutput();
    }

    /// <summary>
    /// Pumps the asynchronous decoder's event queue, emitting output frames without consuming another input
    /// packet; synchronous transforms return immediately. At most 4 096 events are processed per call.
    /// <para>抽取异步解码器的事件队列，在不输入新数据包的情况下输出帧；同步变换立即返回。单次调用至多处理
    /// 4 096 个事件。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, the event queue exceeded the
    /// per-call limit, or a Media Foundation call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、事件队列超出单次上限，或 Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The decoder's output type while producing events is not NV12.
    /// <para>解码器在产生事件期间的输出类型不是 NV12 格式。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A decoded sample carried no buffers or an unexpected texture layout.
    /// <para>解码样本不含缓冲区或纹理布局非预期。</para>
    /// </exception>
    public void Poll()
    {
        CheckReady();
        if (!_isAsync) return;
        for (int i = 0; i < 4096; i++)
            if (!PumpEvent()) return;
        throw new InvalidOperationException("Decoder event queue exceeded the polling limit.");
    }

    /// <summary>
    /// Releases every COM and D3D11 resource, drops the process-wide Media Foundation startup lease, and clears
    /// the frame callback. Repeat calls are no-ops; must run on the owning thread.
    /// <para>释放全部 COM 与 D3D11 资源、归还进程级 Media Foundation 启动租约并清除帧回调；重复调用无效果，
    /// 必须在所属线程上执行。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Called from inside a frame callback or from a thread other than the owning thread.
    /// <para>在帧回调内部或所属线程之外的其他线程上调用。</para>
    /// </exception>
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
            if (MfCom.QueryInterface(_context, D3D11Interop.IidMultithread, out nint multithread) >= 0)
            {
                try
                {
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

    private void CreateDecoder(VideoCodecId codec, int width, int height)
    {
        Guid format = CodecGuid(codec);
        Guid inputSubtype = format;
        var registration = new MftRegisterTypeInfo(MfInterop.MediaTypeVideo, inputSubtype);
        Check(MfInterop.MFTEnumEx(MfInterop.CategoryVideoDecoder, MfInterop.MftEnumFlagsAsyncHardware | MfInterop.MftEnumFlags,
            registration, 0, out nint activates, out uint count), "MFTEnumEx(D3D11 candidates)");
        var failures = new List<string>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint activate = Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size));
                nint transform = 0;
                int hr = MfCom.Activate(activate, MfInterop.IidImfTransform, out transform);
                if (hr < 0) { failures.Add($"activate: 0x{hr:X8}"); MfCom.Release(transform); continue; }
                _transform = transform;
                try
                {
                    if (TryConfigureDecoder(transform, codec, width, height)) break;
                    failures.Add("unsupported capabilities or media type");
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
                {
                    failures.Add(error.Message);
                }
                ReleaseTransformOnly();
            }
        }
        finally
        {
            for (int i = 0; i < count; i++) MfCom.Release(Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size)));
            Marshal.FreeCoTaskMem(activates);
        }
        if (_transform == 0)
            throw new NotSupportedException($"No D3D11-aware Media Foundation decoder accepts {codec} ({width}x{height}; candidates={count}; {string.Join("; ", failures)}).");
        StartStreaming();
    }

    private bool TryConfigureDecoder(nint transform, VideoCodecId codec, int width, int height)
    {
        if (MfCom.GetAttributes(transform, out nint attributes) < 0 || attributes == 0) return false;
        try
        {
            if (MfCom.GetUInt32(attributes, MfInterop.SaD3D11Aware, out uint aware) < 0 || aware == 0) return false;
            _isAsync = MfCom.GetUInt32(attributes, MfInterop.TransformAsync, out uint asyncFlag) >= 0 && asyncFlag != 0;
            if (_isAsync) Check(MfCom.SetUInt32(attributes, MfInterop.TransformAsyncUnlock, 1), "unlock async MFT");

        }
        finally { MfCom.Release(attributes); }

        Check(MfCom.Message(transform, 2, _deviceManager), "set D3D manager");
        if (_isAsync) Check(MfCom.QueryInterface(transform, MfInterop.IidImfMediaEventGenerator, out _eventGenerator), "event generator");
        MediaFoundationVideoDecoder.ConfigureLowLatency(transform, codec);

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
        Check(MfCom.SetType(_transform, input, true), "input type");
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
                _ = MfCom.GetUInt32(type, MfInterop.MtVideoNominalRange, out uint range);
                _ = MfCom.GetUInt32(type, MfInterop.MtYuvMatrix, out uint matrix);
                if (_options.ColorRange != VideoColorRange.Auto) range = _options.ColorRange == VideoColorRange.Full ? 1u : 2u;
                if (_options.ColorMatrix != VideoColorMatrix.Auto) matrix = _options.ColorMatrix == VideoColorMatrix.Bt709 ? 1u : 2u;
                _inputColorSpace = ((range == 1 ? 2u : 1u) << 4) | (matrix == 1 ? 4u : 0u);
                return true;
            }
            finally { MfCom.Release(type); }
        }
        return false;
    }

    private void BuildVideoProcessor()
    {
        ReleaseVideoProcessor();
        if (_options.PreferNv12Output) return;
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
        MfCom.D3DVideoProcessorSetStreamFrameFormat(_videoContext, _processor, 0, D3D11Interop.VideoFrameFormatProgressive);
        MfCom.D3DVideoProcessorSetStreamAutoProcessingMode(_videoContext, _processor, 0, 0);
        MfCom.D3DVideoProcessorSetStreamColorSpace(_videoContext, _processor, 0, D3D11VideoProcessorColorSpace.FromBits(_inputColorSpace));
        MfCom.D3DVideoProcessorSetOutputColorSpace(_videoContext, _processor, D3D11VideoProcessorColorSpace.FromBits(2u << 4));

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
        Check(MfCom.D3DCreateVideoProcessorOutputView(_videoDevice, _outputTexture, _processorEnumerator, outputViewDesc, out _outputView), "CreateVideoProcessorOutputView");

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

    private void PumpUntilInputAccepted(nint sample)
    {
        WaitForEventCondition(drain: false);
        Check(MfCom.Input(_transform, sample), "ProcessInput");
        _inputRequests--;
        for (int i = 0; i < 4096; i++)
            if (!PumpEvent()) return;
        throw new InvalidOperationException("Decoder event queue exceeded the per-packet limit.");
    }

    private void WaitForEventCondition(bool drain)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (drain ? !_drainComplete : _inputRequests == 0)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("Hardware decoder event wait exceeded five seconds.");
            if (!PumpEvent()) Thread.Sleep(1);
        }
    }
    private void DiscardPendingEvents()
    {
        for (int i = 0; i < 4096; i++)
        {
            int hr = MfCom.GetEvent(_eventGenerator, 1, out nint mediaEvent);
            MfCom.Release(mediaEvent);
            if (hr == D3D11Interop.MfENoEventsAvailable) return;
            Check(hr, "discard flushed event");
        }
        throw new InvalidOperationException("Decoder event queue did not empty after flush.");
    }

    private bool PumpEvent()
    {
        int hr = MfCom.GetEvent(_eventGenerator, 1, out nint mediaEvent);
        try
        {
            if (hr == D3D11Interop.MfENoEventsAvailable) return false;
            Check(hr, "GetEvent");
            Check(MfCom.EventType(mediaEvent, out uint type), "event type");
            int status;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)MfCom.PublicSlot(mediaEvent, 35))(mediaEvent, &status), "event status");
            Check(status, "asynchronous event");
            switch (type)
            {
                case D3D11Interop.MeTransformNeedInput: _inputRequests++; break;
                case D3D11Interop.MeTransformHaveOutput: ProduceOutput(); break;
                case D3D11Interop.MeTransformDrainComplete: _drainComplete = true; break;
            }
            return true;
        }
        finally { MfCom.Release(mediaEvent); }
    }

    private void DrainSynchronousOutput()
    {
        for (int i = 0; i < 4096; i++)
            if (!ProduceOutput()) return;
        throw new InvalidOperationException("Decoder output exceeded the per-packet frame limit.");
    }

    private bool ProduceOutput()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var output = new MftOutputDataBuffer();
            int hr = MfCom.Output(_transform, ref output);
            try
            {
                if (hr == MfInterop.MfETransformStreamChange)
                {
                    if (!SelectOutputType()) throw new NotSupportedException("Hardware decoder cannot renegotiate NV12 output.");
                    BuildVideoProcessor();
                    continue;
                }
                if (hr == MfInterop.MfETransformNeedMoreInput) return false;
                Check(hr, "ProcessOutput");
                if (output.Sample != 0) ConvertSample(output.Sample);
                return true;
            }
            finally
            {
                MfCom.Release(output.Events);
                MfCom.Release(output.Sample);
            }
        }
        throw new InvalidOperationException("Hardware decoder repeatedly changed output type.");
    }

    private void ConvertSample(nint sample)
    {
        Check(MfCom.GetTime(sample, out long timestamp), "sample time");
        Check(MfCom.BufferCount(sample, out uint buffers), "GetBufferCount");
        if (buffers == 0) throw new InvalidDataException("Decoder produced a sample without buffers.");
        Check(MfCom.GetBufferByIndex(sample, 0, out nint buffer), "GetBufferByIndex");
        try
        {
            Check(MfCom.QueryInterface(buffer, D3D11Interop.IidDxgiBuffer, out nint dxgiBuffer), "IMFDXGIBuffer");
            try
            {
                Check(MfCom.DxgiGetResource(dxgiBuffer, D3D11Interop.IidTexture2D, out nint sourceTexture), "GetResource(Texture2D)");
                try
                {
                    Check(MfCom.DxgiSubresourceIndex(dxgiBuffer, out uint subresource), "GetSubresourceIndex");
                    if (_options.PreferNv12Output) ReadBackNv12(sourceTexture, subresource, timestamp);
                    else
                    {
                        BlitToOutput(sourceTexture, subresource);
                        ReadBack(timestamp);
                    }
                }
                finally { MfCom.Release(sourceTexture); }
            }
            finally { MfCom.Release(dxgiBuffer); }
        }
        finally { MfCom.Release(buffer); }
    }

    private void ReadBackNv12(nint sourceTexture, uint subresource, long timestampTicks)
    {
        var desc = MfCom.D3DGetTexture2DDesc(sourceTexture);
        if (desc.Format != 103 || desc.Width < _width || desc.Height < _height
            || desc.SampleDescCount != 1 || desc.MipLevels != 1 || subresource >= desc.ArraySize)
            throw new InvalidDataException("Unexpected NV12 decoder texture layout.");
        VideoFrameLimits.ValidateDimensions(checked((int)desc.Width), checked((int)desc.Height));
        if (_stagingTexture == 0 || _stagingWidth != desc.Width || _stagingHeight != desc.Height)
        {
            ReleaseVideoProcessor();
            var staging = desc;
            staging.ArraySize = 1;
            staging.Usage = D3D11Interop.UsageStaging;
            staging.BindFlags = 0;
            staging.MiscFlags = 0;
            staging.CPUAccessFlags = D3D11Interop.CpuAccessRead;
            Check(MfCom.D3DCreateTexture2D(_device, staging, 0, out _stagingTexture), "CreateTexture2D(NV12 staging)");
            _stagingWidth = desc.Width;
            _stagingHeight = desc.Height;
        }
        MfCom.D3DCopySubresourceRegion(_context, _stagingTexture, 0, 0, 0, 0, sourceTexture, subresource, 0);
        Check(MfCom.D3DMap(_context, _stagingTexture, 0, 1, 0, out var mapped), "Map(NV12 staging)");
        try
        {
            int rowBytes = checked((_width + 1) & ~1);
            int sourceStride = checked((int)mapped.RowPitch);
            if (sourceStride < rowBytes) throw new InvalidDataException("NV12 staging row is truncated.");
            int length = VideoFrameLimits.Nv12Length(_width, _height, rowBytes);
            byte[] pixels = GetReadbackBuffer(length);
            using var frame = new DecodedVideoFrame(pixels, length, _width, _height, rowBytes, timestampTicks)
            {
                ReturnBufferToPool = false,
                ColorMatrix = (_inputColorSpace & 4) != 0 ? VideoColorMatrix.Bt709 : VideoColorMatrix.Bt601,
                ColorRange = (_inputColorSpace & 0x30) == 0x20 ? VideoColorRange.Full : VideoColorRange.Limited,
            };
            for (int row = 0; row < _height; row++)
                new ReadOnlySpan<byte>((byte*)mapped.pData + (nint)row * sourceStride, rowBytes)
                    .CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
            byte* uv = (byte*)mapped.pData + checked((nint)desc.Height * sourceStride);
            for (int row = 0; row < (_height + 1) / 2; row++)
                new ReadOnlySpan<byte>(uv + (nint)row * sourceStride, rowBytes)
                    .CopyTo(pixels.AsSpan((_height + row) * rowBytes, rowBytes));
            _inCallback = true;
            try { _callback!(frame); } finally { _inCallback = false; }
        }
        finally { MfCom.D3DUnmap(_context, _stagingTexture, 0); }
    }

    private void BlitToOutput(nint sourceTexture, uint subresource)
    {
        var inputViewDesc = new D3D11Interop.VideoProcessorInputViewDesc
        {
            ViewDimension = D3D11Interop.VpivDimensionTexture2D,
            Texture2DArraySlice = subresource,
        };
        Check(MfCom.D3DCreateVideoProcessorInputView(_videoDevice, sourceTexture, _processorEnumerator, inputViewDesc, out nint inputView), "CreateVideoProcessorInputView");
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
        MfCom.D3DCopySubresourceRegion(_context, _stagingTexture, 0, 0, 0, 0, _outputTexture, 0, 0);
        Check(MfCom.D3DMap(_context, _stagingTexture, 0, 1, 0, out var mapped), "Map(staging)");
        try
        {
            int stride = checked((int)mapped.RowPitch);
            int rowBytes = checked(_width * 4);
            int required = checked(rowBytes * _height);
            if (stride < rowBytes) throw new InvalidDataException("Staging readback is truncated.");
            // The staging map stays valid until Unmap, and the callback completes before that, so
            // the frame borrows the mapped memory instead of an extra full-frame copy.
            // staging 映射在 Unmap 之前一直有效，而回调在 Unmap 前完成，因此帧直接借用映射内存，
            // 省去一次整帧拷贝。
            using var frame = new PointerDecodedVideoFrame((byte*)mapped.pData, required, _width, _height, stride, timestampTicks)
            {
                ReturnBufferToPool = false,
                ColorMatrix = (_inputColorSpace & 4) != 0 ? VideoColorMatrix.Bt709 : VideoColorMatrix.Bt601,
                ColorRange = (_inputColorSpace & 0x30) == 0x20 ? VideoColorRange.Full : VideoColorRange.Limited,
            };
            _inCallback = true;
            try { _callback!(frame); } finally { _inCallback = false; }
        }
        finally { MfCom.D3DUnmap(_context, _stagingTexture, 0); }
    }

    private byte[] GetReadbackBuffer(int length)
    {
        if (_readbackBuffer is null || _readbackBuffer.Length != length)
            _readbackBuffer = GC.AllocateUninitializedArray<byte>(length);
        return _readbackBuffer;
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
        _readbackBuffer = null;
        ReleaseTransformOnly();
        ReleaseVideoProcessor();
        _callback = null;
        ReleaseD3D();
        if (_deviceManager != 0) { MfCom.Release(_deviceManager); _deviceManager = 0; }
        _inputRequests = 0;
        _drainComplete = false;
    }

    private void ReleaseVideoProcessor()
    {
        _stagingWidth = _stagingHeight = 0;
        if (_stagingTexture != 0) { MfCom.Release(_stagingTexture); _stagingTexture = 0; }
        if (_outputView != 0) { MfCom.Release(_outputView); _outputView = 0; }
        if (_outputTexture != 0) { MfCom.Release(_outputTexture); _outputTexture = 0; }
        if (_processor != 0) { MfCom.Release(_processor); _processor = 0; }
        if (_processorEnumerator != 0) { MfCom.Release(_processorEnumerator); _processorEnumerator = 0; }
    }

    private void ReleaseTransformOnly()
    {
        if (_eventGenerator != 0) { MfCom.Release(_eventGenerator); _eventGenerator = 0; }
        if (_inputType != 0) { MfCom.Release(_inputType); _inputType = 0; }
        if (_transform != 0)
        {
            _ = MfCom.Message(_transform, MfInterop.MftMessageFlush);
            MfCom.Release(_transform);
            _transform = 0;
        }
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
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
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
        VideoCodecId.Av1 => new Guid("31305641-0000-0010-8000-00aa00389b71"),
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
/// <para>由 <see cref="MediaFoundationGpuVideoDecoder"/> 输出的 BGRA 帧。它派生自
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
    /// <summary>
    /// Creates a frame taking ownership of a pooled BGRA buffer, skipping the base NV12 geometry validation.
    /// <para>创建一个接管池化 BGRA 缓冲区所有权的帧，并跳过基类的 NV12 几何校验。</para>
    /// </summary>
    /// <param name="buffer">
    /// Pooled BGRA pixel array held by the frame until disposal; must contain at least <paramref name="length"/> bytes.
    /// <para>帧在释放前持有的池化 BGRA 像素数组；长度不得小于 <paramref name="length"/> 字节。</para>
    /// </param>
    /// <param name="length">Valid pixel byte count inside <paramref name="buffer"/>.
    /// <para><paramref name="buffer"/> 内的有效像素字节数。</para></param>
    /// <param name="width">Frame width in pixels. <para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels. <para>帧高度（像素）。</para></param>
    /// <param name="stride">Bytes per BGRA row, at least width × 4. <para>每行 BGRA 字节数，不小于宽度 × 4。</para></param>
    /// <param name="timestampTicks">Presentation time in 100-nanosecond ticks.
    /// <para>呈现时间，以 100 纳刻（tick）为单位。</para></param>
    public GpuDecodedVideoFrame(byte[] buffer, int length, int width, int height, int stride, long timestampTicks)
        : base(buffer, length, width, height, stride, timestampTicks, skipNv12Validation: true)
    {
    }
}