using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// Synchronous Media Foundation decoder with CPU-readable NV12 output.
/// <para>同步 Media Foundation 解码器；不声明 D3D 硬件解码或 GPU 零拷贝支持。</para>
/// </summary>
/// <remarks>Initialize, Decode, Reset and Dispose must run on the same thread.</remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationVideoDecoder : IVideoDecoder
{
    private static readonly object LifetimeLock = new();
    private static int _leases;
    private readonly VideoDecoderOptions _options;
    private nint _transform;
    private Action<DecodedVideoFrame>? _callback;
    private int _width, _height, _stride;
    private int _ownerThread;
    private bool _comOwned, _mfOwned, _disposed;
    private long _sequence;
    private bool _inCallback;

    /// <summary>
    /// Creates a synchronous Media Foundation decoder that follows stream color metadata.
    /// <para>创建遵循码流色彩元数据的同步 Media Foundation 解码器。</para>
    /// </summary>
    public MediaFoundationVideoDecoder() : this(new VideoDecoderOptions()) { }

    /// <summary>
    /// Creates a synchronous Media Foundation decoder with per-session color overrides applied to the
    /// negotiated NV12 output type; overrides the MFT rejects fall back to stream metadata.
    /// <para>创建带会话色彩覆盖的同步 Media Foundation 解码器，覆盖会写入协商的 NV12 输出类型；
    /// 被解码器拒绝的覆盖回退为码流元数据。</para>
    /// </summary>
    /// <param name="options">
    /// Session choices; must not be <see langword="null"/>. Color overrides are forwarded to the output
    /// type; other fields are ignored by this backend.
    /// <para>会话选择；不得为 <see langword="null"/>。色彩覆盖写入输出类型；其他字段被此后端忽略。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/> 时抛出。</para></exception>
    public MediaFoundationVideoDecoder(VideoDecoderOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Whether a synchronous media transform is currently configured.
    /// <para>当前是否已配置同步媒体变换。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> after a successful <see cref="Initialize"/>; <see langword="false"/> before
    /// initialization, after a failed re-initialization, and after <see cref="Dispose"/>.
    /// <para>成功执行 <see cref="Initialize"/> 后为 <see langword="true"/>；初始化之前、重新初始化失败之后
    /// 以及 <see cref="Dispose"/> 之后为 <see langword="false"/>。</para>
    /// </value>
    public bool IsInitialized => _transform != 0;

    /// <summary>
    /// Initializes COM and Media Foundation on the calling thread, then activates the first synchronous
    /// transform that accepts the codec and negotiates NV12 output; the decoded geometry and color metadata
    /// come from the selected output type. Any previous transform is released first.
    /// <para>在调用线程上初始化 COM 与 Media Foundation，然后激活第一个接受该编码并协商出 NV12 输出的同步
    /// 变换；解码后的几何与色彩元数据取自所选输出类型。既有变换会先行释放。</para>
    /// </summary>
    /// <param name="codec">Compressed codec of the incoming access units. <para>输入访问单元的压缩编码格式。</para></param>
    /// <param name="width">
    /// Expected coded frame width in pixels, from 1 to 16 384; the negotiated output size may differ.
    /// <para>预期编码帧宽度（像素），1 到 16 384；协商出的输出尺寸可能不同。</para>
    /// </param>
    /// <param name="height">
    /// Expected coded frame height in pixels, from 1 to 16 384.
    /// <para>预期编码帧高度（像素），1 到 16 384。</para>
    /// </param>
    /// <param name="frameCallback">
    /// Receives every decoded NV12 frame on the calling thread; the frame is disposed once the callback returns,
    /// so consumers must copy or convert within it.
    /// <para>在调用线程上接收每个解码出的 NV12 帧；回调返回后帧即被释放，消费方须在其内部完成复制或转换。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frameCallback"/> is <see langword="null"/>.
    /// <para><paramref name="frameCallback"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The dimensions exceed the supported limits.
    /// <para>尺寸超出支持上限。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// Called from a callback or another thread, or a COM or Media Foundation call failed.
    /// <para>在回调内或其他线程上调用，或 COM、Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// No synchronous Media Foundation decoder accepts the codec with NV12 output.
    /// <para>没有以 NV12 输出接受该编码的同步 Media Foundation 解码器。</para>
    /// </exception>
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        Guid format = CodecGuid(codec);
        if (_ownerThread != 0) CheckThread();
        ReleaseTransform();
        if (!_mfOwned)
        {
            int hr = MfInterop.CoInitializeEx(0, 0);
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

        Check(MfInterop.MFCreateMediaType(out nint input), "MFCreateMediaType");
        try
        {
            Check(MfCom.SetGuid(input, MfInterop.MtMajorType, MfInterop.MediaTypeVideo), "major type");
            Check(MfCom.SetGuid(input, MfInterop.MtSubtype, format), "subtype");
            Check(MfCom.SetUInt64(input, MfInterop.MtFrameSize, ((ulong)(uint)width << 32) | (uint)height), "frame size");
            var registration = new MftRegisterTypeInfo(MfInterop.MediaTypeVideo, format);
            Check(MfInterop.MFTEnumEx(MfInterop.CategoryVideoDecoder, MfInterop.MftEnumFlags,
                registration, 0, out nint activates, out uint count), "MFTEnumEx");
            try
            {
                for (int i = 0; i < count; i++)
                {
                    nint activate = Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size));
                    nint transform = 0;
                    int hr = MfCom.Activate(activate, MfInterop.IidImfTransform, out transform);
                    if (hr < 0) { MfCom.Release(transform); continue; }
                    _transform = transform;
                    if (MfCom.SetType(transform, input, true) >= 0 && SelectOutputType())
                    {
                        ConfigureLowLatency(transform, codec);
                        break;
                    }
                    ReleaseTransform();
                }
            }
            finally
            {
                for (int i = 0; i < count; i++) MfCom.Release(Marshal.ReadIntPtr(activates, checked(i * IntPtr.Size)));
                Marshal.FreeCoTaskMem(activates);
            }
            if (_transform == 0) throw new NotSupportedException($"No synchronous Media Foundation NV12 decoder accepts {codec}.");
            _callback = frameCallback;
            _sequence = 0;
            Check(MfCom.Message(_transform, MfInterop.MftMessageBeginStreaming), "begin streaming");
            Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "start of stream");
        }
        catch { ReleaseTransform(); throw; }
        finally { MfCom.Release(input); }
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
    /// Feeds one access unit with an explicit timestamp and drains all available NV12 output frames into the
    /// callback on the calling thread before returning.
    /// <para>输入一个访问单元并附带显式时间戳，在返回前于调用线程上将全部可用的 NV12 输出帧排空并交付回调。</para>
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
    /// The decoder is not initialized, called from a callback or another thread, output made no bounded progress,
    /// or a COM or Media Foundation call failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、输出无法在限量内推进，或 COM、Media Foundation 调用失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The transform's renegotiated output type is not NV12.
    /// <para>变换重新协商后的输出类型不是 NV12 格式。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">The produced NV12 buffer is truncated.
    /// <para>产出的 NV12 缓冲区被截断。</para></exception>
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        nint sample = CreateSample(accessUnit, 0, 0);
        try
        {
            Check(MfCom.SetTime(sample, timestampTicks), "sample time");
            int hr = MfCom.Input(_transform, sample);
            if (hr == MfInterop.MfENotAccepting)
            {
                DrainOutput();
                hr = MfCom.Input(_transform, sample);
            }
            Check(hr, "ProcessInput");
            DrainOutput();
        }
        finally { MfCom.Release(sample); }
    }

    /// <summary>
    /// Flushes the transform, restarts the stream, and resets the timestamp sequence. Use after a stream
    /// discontinuity; the decoder stays initialized.
    /// <para>刷新变换、重启码流并重置时间戳序列；用于码流不连续之后，解码器保持已初始化状态。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, or a Media Foundation message failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用，或 Media Foundation 消息失败。</para>
    /// </exception>
    public void Reset()
    {
        CheckReady();
        Check(MfCom.Message(_transform, MfInterop.MftMessageFlush), "flush");
        Check(MfCom.Message(_transform, MfInterop.MftMessageStartOfStream), "restart stream");
        _sequence = 0;
    }

    /// <summary>
    /// Signals end of stream, issues the drain command, and emits all buffered frames into the callback.
    /// Call <see cref="Initialize"/> or <see cref="Reset"/> before feeding a new stream.
    /// <para>发出码流结束通知并下发 drain 命令，将全部缓存帧输出到回调。输入新码流前须调用
    /// <see cref="Initialize"/> 或 <see cref="Reset"/>。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The decoder was disposed. <para>解码器已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// The decoder is not initialized, called from a callback or another thread, output made no bounded progress,
    /// or a Media Foundation message failed.
    /// <para>解码器未初始化、在回调内或其他线程上调用、输出无法在限量内推进，或 Media Foundation 消息失败。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The transform's output type while draining is not NV12.
    /// <para>变换在排空期间的输出类型不是 NV12 格式。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">A drained NV12 buffer is truncated.
    /// <para>排空出的 NV12 缓冲区被截断。</para></exception>
    public void Drain()
    {
        CheckReady();
        Check(MfCom.Message(_transform, 0x10000002), "end of stream");
        Check(MfCom.Message(_transform, 1), "drain command");
        DrainOutput();
    }

    /// <summary>
    /// Flushes and releases the transform, drops the process-wide Media Foundation startup lease, and
    /// uninitializes COM on the owning thread. Repeat calls are no-ops.
    /// <para>刷新并释放变换对象、归还进程级 Media Foundation 启动租约，并在所属线程上反初始化 COM；
    /// 重复调用无效果。</para>
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
        ReleaseTransform();
        if (_mfOwned)
        {
            lock (LifetimeLock)
            {
                if (--_leases == 0) _ = MfInterop.MFShutdown();
                _mfOwned = false;
            }
        }
        if (_comOwned) { MfInterop.CoUninitialize(); _comOwned = false; }
    }

    internal static void ConfigureLowLatency(nint transform, VideoCodecId codec)
    {
        if (MfCom.GetAttributes(transform, out nint attributes) >= 0 && attributes != 0)
        {
            try { Check(MfCom.SetUInt32(attributes, new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), 1), "low latency attribute"); }
            finally { MfCom.Release(attributes); }
        }
        int hr = MfCom.QueryInterface(transform, new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), out nint codecApi);
        if (hr < 0) return;
        try
        {
            Check(MfCom.SetCodecLowLatency(codecApi, new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), codec == VideoCodecId.H264), "enable low latency");
        }
        finally { MfCom.Release(codecApi); }
    }

    private VideoColorMatrix _colorMatrix;
    private VideoColorRange _colorRange;

    /// <summary>Gets the color metadata negotiated on the output type; Auto while uninitialized.</summary>
    /// <summary>获取输出类型上协商出的色彩元数据；未初始化时为 Auto。</summary>
    internal VideoColorMatrix NegotiatedColorMatrix => _colorMatrix;
    /// <summary>Gets the nominal range negotiated on the output type; Auto while uninitialized.</summary>
    /// <summary>获取输出类型上协商出的数值范围；未初始化时为 Auto。</summary>
    internal VideoColorRange NegotiatedColorRange => _colorRange;

    private bool SelectOutputType() => SelectOutputType(applyOverrides: true) || SelectOutputType(applyOverrides: false);

    private bool SelectOutputType(bool applyOverrides)
    {
        for (uint index = 0; index < 256; index++)
        {
            int hr = MfCom.OutputType(_transform, index, out nint type);
            if (hr < 0) { MfCom.Release(type); return false; }
            try
            {
                if (MfCom.GetGuid(type, MfInterop.MtSubtype, out Guid subtype) < 0 || subtype != MfInterop.VideoFormatNv12) continue;
                if (applyOverrides && !ApplyColorOverrides(type, _options.ColorMatrix, _options.ColorRange)) continue;
                if (MfCom.SetType(_transform, type, false) < 0) continue;
                Check(MfCom.GetUInt64(type, MfInterop.MtFrameSize, out ulong size), "output dimensions");
                _ = MfCom.GetUInt32(type, MfInterop.MtVideoNominalRange, out uint range);
                _ = MfCom.GetUInt32(type, MfInterop.MtYuvMatrix, out uint matrix);
                _colorMatrix = matrix switch { 1 => VideoColorMatrix.Bt709, 2 => VideoColorMatrix.Bt601, _ => VideoColorMatrix.Auto };
                _colorRange = range switch { 1 => VideoColorRange.Full, 2 => VideoColorRange.Limited, _ => VideoColorRange.Auto };
                _width = checked((int)(size >> 32));
                _height = checked((int)(size & uint.MaxValue));
                VideoFrameLimits.ValidateDimensions(_width, _height);
                _stride = MfCom.GetUInt32(type, MfInterop.MtDefaultStride, out uint stride) >= 0
                    ? checked((int)stride) : checked((_width + 1) & ~1);
                _ = VideoFrameLimits.Nv12Length(_width, _height, _stride);
                return true;
            }
            finally { MfCom.Release(type); }
        }
        return false;
    }

    /// <summary>Writes the session color overrides onto a candidate output type; fails when the MFT rejects them.</summary>
    /// <summary>把会话色彩覆盖写入候选输出类型；MFT 拒绝时失败。</summary>
    internal static bool ApplyColorOverrides(nint type, VideoColorMatrix matrix, VideoColorRange range)
    {
        if (matrix != VideoColorMatrix.Auto
            && MfCom.SetUInt32(type, MfInterop.MtYuvMatrix, matrix == VideoColorMatrix.Bt709 ? 1u : 2u) < 0) return false;
        if (range != VideoColorRange.Auto
            && MfCom.SetUInt32(type, MfInterop.MtVideoNominalRange, range == VideoColorRange.Full ? 1u : 2u) < 0) return false;
        return true;
    }

    private void DrainOutput()
    {
        for (int attempts = 0; attempts < 256; attempts++)
        {
            Check(MfCom.OutputInfo(_transform, out var info), "GetOutputStreamInfo");
            nint supplied = (info.Flags & MfInterop.MftOutputStreamProvidesSamples) == 0
                ? CreateSample([], info.Size, info.Alignment) : 0;
            var output = new MftOutputDataBuffer { Sample = supplied };
            try
            {
                int hr = MfCom.Output(_transform, ref output);
                if (hr == MfInterop.MfETransformNeedMoreInput) return;
                if (hr == MfInterop.MfETransformStreamChange)
                {
                    if (!SelectOutputType()) throw new NotSupportedException("Decoder changed to a non-NV12 output format.");
                    continue;
                }
                Check(hr, "ProcessOutput");
                if (output.Sample == 0) return;
                EmitFrame(output.Sample);
            }
            finally
            {
                MfCom.Release(output.Events);
                if (output.Sample != supplied) MfCom.Release(output.Sample);
                MfCom.Release(supplied);
            }
        }
        throw new InvalidOperationException("Decoder failed to make bounded output progress.");
    }

    private void EmitFrame(nint sample)
    {
        Check(MfCom.ContiguousBuffer(sample, out nint buffer), "ConvertToContiguousBuffer");
        try
        {
            Check(MfCom.Lock(buffer, out nint source, out uint length), "buffer lock");
            try
            {
                int required = VideoFrameLimits.Nv12Length(_width, _height, _stride);
                if (length < required) throw new InvalidDataException("Truncated NV12 output.");
                byte[] pooled = ArrayPool<byte>.Shared.Rent(required);
                _ = MfCom.GetTime(sample, out long timestamp);
                using var frame = new DecodedVideoFrame(pooled, required, _width, _height, _stride, timestamp) { ColorMatrix = _colorMatrix, ColorRange = _colorRange };
                new ReadOnlySpan<byte>((void*)source, required).CopyTo(pooled);
                _inCallback = true;
                try { _callback!(frame); }
                finally { _inCallback = false; }
            }
            finally { Check(MfCom.Unlock(buffer), "buffer unlock"); }
        }
        finally { MfCom.Release(buffer); }
    }

    private static nint CreateSample(ReadOnlySpan<byte> data, uint capacity, uint alignment)
    {
        uint size = Math.Max((uint)data.Length, capacity);
        if (size > VideoFrameLimits.MaximumFrameBytes) throw new InvalidDataException("MFT buffer exceeds the frame limit.");
        if (alignment > 4096 || alignment != 0 && (alignment & (alignment - 1)) != 0)
            throw new InvalidDataException("Invalid MFT buffer alignment.");
        Check(MfInterop.MFCreateAlignedMemoryBuffer(Math.Max(size, 1), alignment == 0 ? 0 : alignment - 1, out nint buffer), "MFCreateAlignedMemoryBuffer");
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

    private void ReleaseTransform()
    {
        if (_transform != 0)
        {
            _ = MfCom.Message(_transform, MfInterop.MftMessageFlush);
            MfCom.Release(_transform);
            _transform = 0;
        }
        _callback = null;
    }
    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks must not reenter the decoder.");
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
        if (hr < 0) throw new InvalidOperationException($"Media Foundation {operation} failed: 0x{hr:X8}.");
    }
}