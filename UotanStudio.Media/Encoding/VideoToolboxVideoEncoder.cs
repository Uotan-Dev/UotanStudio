using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.VideoToolbox;

namespace UotanStudio.Media.Encoding;

/// <summary>An owned Annex-B access unit, including parameter sets, emitted by a video encoder.
/// <para>视频编码器产出的、由接收方独占的 Annex-B 访问单元（含参数集）。</para></summary>
/// <param name="Codec">Compressed codec of the packet.<para>该数据包对应的压缩编码格式。</para></param>
/// <param name="Data">Managed encoded data owned by the recipient: NAL units separated by 00 00 00 01 start codes, with format-description parameter sets written first.
/// <para>接收方独占的托管编码数据：以 00 00 00 01 起始码分隔的 NAL 单元，格式描述中的参数集最先写入。</para></param>
/// <param name="TimestampTicks">Presentation timestamp in TimeSpan ticks.
/// <para>以 TimeSpan tick 计的表现时间戳。</para></param>
public sealed record EncodedVideoPacket(VideoCodecId Codec, byte[] Data, long TimestampTicks);

/// <summary>
/// Hardware VideoToolbox H.264/H.265 encoder for 8-bit NV12 input; emits bounded Annex-B packets.
/// <para>面向 8 位 NV12 输入的 VideoToolbox 硬件 H.264/H.265 编码器；产出有界的 Annex-B 数据包。
/// macOS 编码会话：显式要求硬件、关闭帧重排，输入像素逐平面复制到 CoreVideo。</para>
/// </summary>
/// <remarks>All calls and callbacks use the creating thread. Encoding is not required by a scrcpy receiver.</remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class VideoToolboxVideoEncoder : IDisposable
{
    private nint _session;
    private GCHandle _self;
    private readonly VideoCodecId _codec;
    private readonly int _width, _height, _thread = Environment.CurrentManagedThreadId;
    private readonly Action<EncodedVideoPacket> _callback;
    private readonly ConcurrentQueue<EncodedVideoPacket> _packets = new();
    private ExceptionDispatchInfo? _error;
    private bool _disposed;
    private bool _inCallback;
    private long _lastTimestamp = -1;

    /// <summary>Creates a low-latency hardware encoder, failing if the codec or stream configuration is unavailable.
    /// <para>创建低延迟硬件编码器；若编码格式或流配置不可用则立即失败。</para></summary>
    /// <param name="codec">Target compressed codec, H.264 or H.265.
    /// <para>目标压缩编码格式（H.264 或 H.265）。</para></param>
    /// <param name="width">Frame width in pixels; must be positive, at most 16384, and fit the shared frame byte cap together with <paramref name="height"/>.
    /// <para>帧宽度（像素）；必须为正且不超过 16384，并与 <paramref name="height"/> 共同满足共享的帧字节上限。</para></param>
    /// <param name="height">Frame height in pixels under the same bounds as <paramref name="width"/>.
    /// <para>帧高度（像素），边界与 <paramref name="width"/> 相同。</para></param>
    /// <param name="packetCallback">Consumer invoked with each encoded packet during <see cref="Flush"/> on the creating thread.
    /// <para>在 <see cref="Flush"/> 期间于创建者线程上接收每个编码数据包的消费者。</para></param>
    /// <exception cref="PlatformNotSupportedException">The current OS is not macOS.
    /// <para>当前操作系统不是 macOS。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="packetCallback"/> is null.
    /// <para><paramref name="packetCallback"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The dimensions are unsupported or excessive.
    /// <para>尺寸不受支持或超出上限。</para></exception>
    /// <exception cref="InvalidOperationException">The VideoToolbox session could not be created or prepared; the partially built encoder is disposed before the exception escapes.
    /// <para>无法创建或准备 VideoToolbox 会话；异常抛出前会先处置已部分构建的编码器。</para></exception>
    public VideoToolboxVideoEncoder(VideoCodecId codec, int width, int height, Action<EncodedVideoPacket> packetCallback)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(packetCallback);
        _codec = codec; _width = width; _height = height; _callback = packetCallback;
        var specification = VtNative.Dictionary();
        try
        {
            VtNative.SetBoolean(specification, VtNative.Vt, "kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder", true);
            _self = GCHandle.Alloc(this);
            VtNative.Check(VtNative.VTCompressionSessionCreate(0, width, height, VtNative.Codec(codec), specification, 0, 0,
                &OnPacket, GCHandle.ToIntPtr(_self), out _session), "create hardware encoder");
            SetBoolean("kVTCompressionPropertyKey_RealTime", true);
            SetBoolean("kVTCompressionPropertyKey_AllowFrameReordering", false);
            VtNative.Check(VtNative.VTCompressionSessionPrepareToEncodeFrames(_session), "prepare encoder");
        }
        catch { Dispose(); throw; }
        finally { VtNative.CFRelease(specification); }
    }

    /// <summary>Encodes a complete NV12 frame with a nonnegative, strictly increasing timestamp, then delivers output.
    /// <para>编码一帧完整的 NV12 图像（时间戳必须非负且严格递增），随后交付输出。</para></summary>
    /// <param name="frame">A CPU-readable NV12 frame whose dimensions match the encoder and whose span covers both planes at the frame stride.
    /// <para>可被 CPU 读取的 NV12 帧：尺寸必须与编码器一致，且跨度按帧行距完整覆盖两个平面。</para></param>
    /// <exception cref="ArgumentException">The frame is GPU-backed, its dimensions differ from the encoder, its NV12 span is truncated, or its timestamp is negative or does not strictly increase.
    /// <para>帧为 GPU 承载、尺寸与编码器不符、NV12 跨度被截断，或时间戳为负/未严格递增。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame stride is below the luma row bound or the computed NV12 length exceeds the shared frame byte cap.
    /// <para>帧行距小于亮度行最小值，或按 NV12 计算的长度超出共享的帧字节上限。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.
    /// <para><paramref name="frame"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">A CoreVideo pixel plane reports an unusable base address, pitch or height.
    /// <para>CoreVideo 像素平面报告了不可用的基地址、跨距或高度。</para></exception>
    /// <exception cref="InvalidOperationException">The call comes from an encoder callback or from a thread other than the creating thread, or a VideoToolbox or CoreVideo call fails.
    /// <para>调用来自编码器回调或创建者以外的线程，或 VideoToolbox/CoreVideo 调用失败。</para></exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.
    /// <para>编码器已被释放。</para></exception>
    public void Encode(DecodedVideoFrame frame)
    {
        if (frame.PixelFormat != VideoPixelFormat.Nv12) throw new ArgumentException("Encoder requires NV12 input, not a packed pixel layout.", nameof(frame));
        CheckReady();
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width || frame.Height != _height) throw new ArgumentException("Frame dimensions do not match the encoder.", nameof(frame));
        var required = VideoFrameLimits.Nv12Length(frame.Width, frame.Height, frame.Stride);
        if (frame.Span.Length < required) throw new ArgumentException("Truncated NV12 frame.", nameof(frame));
        if (frame.TimestampTicks < 0 || frame.TimestampTicks <= _lastTimestamp) throw new ArgumentException("Timestamps must strictly increase.", nameof(frame));
        VtNative.Check(VtNative.CVPixelBufferCreate(0, (nuint)_width, (nuint)_height, VtNative.Nv12, 0, out var image), "create encoder pixels");
        try
        {
            VtNative.Check(VtNative.CVPixelBufferLockBaseAddress(image, 0), "lock encoder pixels");
            try
            {
                for (nuint plane = 0; plane < 2; plane++)
                {
                    var rows = plane == 0 ? _height : (_height + 1) / 2;
                    var rowBytes = plane == 0 ? _width : (_width + 1) & ~1;
                    var pitch = VtNative.CVPixelBufferGetBytesPerRowOfPlane(image, plane);
                    var pointer = VtNative.CVPixelBufferGetBaseAddressOfPlane(image, plane);
                    if (pointer == 0 || pitch < (nuint)rowBytes || pitch > VideoFrameLimits.MaximumFrameBytes
                        || VtNative.CVPixelBufferGetHeightOfPlane(image, plane) < (nuint)rows) throw new InvalidDataException("Invalid encoder pixel plane.");
                    var start = plane == 0 ? 0 : frame.Stride * _height;
                    for (var row = 0; row < rows; row++)
                        frame.Span.Slice(start + row * frame.Stride, rowBytes).CopyTo(new Span<byte>((byte*)pointer + checked((nuint)row * pitch), rowBytes));
                }
            }
            finally { VtNative.Check(VtNative.CVPixelBufferUnlockBaseAddress(image, 0), "unlock encoder pixels"); }
            VtNative.Check(VtNative.VTCompressionSessionEncodeFrame(_session, image, CmTime.FromTicks(frame.TimestampTicks), default, 0, 0, out _), "encode frame");
            _lastTimestamp = frame.TimestampTicks;
            Flush();
        }
        finally { VtNative.CFRelease(image); }
    }

    /// <summary>Completes pending native encoding work and delivers packets on the caller's thread.
    /// <para>完成挂起的原生编码工作，并在调用者线程上交付数据包。</para></summary>
    /// <exception cref="InvalidDataException">A native encoder callback has captured an error; the stored exception is rethrown before any packet is delivered.
    /// <para>原生编码器回调已捕获错误；在交付任何数据包之前会重新抛出已保存的异常。</para></exception>
    /// <exception cref="InvalidOperationException">The call comes from an encoder callback or from a thread other than the creating thread, or a VideoToolbox call fails.
    /// <para>调用来自编码器回调或创建者以外的线程，或 VideoToolbox 调用失败。</para></exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.
    /// <para>编码器已被释放。</para></exception>
    public void Flush()
    {
        CheckReady();
        VtNative.Check(VtNative.VTCompressionSessionCompleteFrames(_session, default), "complete frames");
        Interlocked.Exchange(ref _error, null)?.Throw();
        while (_packets.TryDequeue(out var packet))
        {
            _inCallback = true;
            try { _callback(packet); } finally { _inCallback = false; }
        }
    }

    /// <summary>
    /// Completes, invalidates and releases the native session, frees the callback handle and drops undelivered packets; repeat calls are no-ops.
    /// <para>完成、作废并释放原生会话，回收回调句柄并丢弃未交付的数据包；重复调用无副作用。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The call comes from an encoder callback or from a thread other than the creating thread.
    /// <para>调用来自编码器回调或创建者以外的线程。</para>
    /// </exception>
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _disposed = true;
        if (_session != 0)
        {
            _ = VtNative.VTCompressionSessionCompleteFrames(_session, default);
            VtNative.VTCompressionSessionInvalidate(_session);
            VtNative.CFRelease(_session);
            _session = 0;
        }
        if (_self.IsAllocated) _self.Free();
        _packets.Clear();
    }

    [UnmanagedCallersOnly]
    private static void OnPacket(nint refCon, nint source, int status, uint flags, nint sample)
    {
        var encoder = (VideoToolboxVideoEncoder)GCHandle.FromIntPtr(refCon).Target!;
        try
        {
            VtNative.Check(status, "encoder callback");
            if (sample == 0) return;
            if (encoder._packets.Count >= 32) throw new InvalidDataException("Too many encoded packets.");
            encoder._packets.Enqueue(encoder.CopyPacket(sample));
        }
        catch (Exception exception) { Interlocked.CompareExchange(ref encoder._error, ExceptionDispatchInfo.Capture(exception), null); }
    }

    private EncodedVideoPacket CopyPacket(nint sample)
    {
        var block = VtNative.CMSampleBufferGetDataBuffer(sample);
        var format = VtNative.CMSampleBufferGetFormatDescription(sample);
        if (block == 0 || format == 0) throw new InvalidDataException("Missing encoded sample data or format.");
        var length = VtNative.CMBlockBufferGetDataLength(block);
        if (length == 0 || length > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Invalid encoded packet length.");
        using var output = new MemoryStream();
        var prefixBytes = 0;
        nuint parameterCount = 1;
        for (nuint i = 0; i < parameterCount; i++)
        {
            nint parameter;
            nuint size;
            var status = _codec == VideoCodecId.H264
                ? VtNative.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, out parameter, out size, out parameterCount, out prefixBytes)
                : VtNative.CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, i, out parameter, out size, out parameterCount, out prefixBytes);
            VtNative.Check(status, "encoded parameter set");
            if (parameter == 0 || parameterCount > 32 || size is 0 or > 1048576) throw new InvalidDataException("Invalid encoded parameter sets.");
            WriteNal(output, new ReadOnlySpan<byte>((void*)parameter, checked((int)size)));
        }
        if (prefixBytes is not (1 or 2 or 4)) throw new InvalidDataException("Invalid encoded NAL length size.");
        var bytes = new byte[(int)length];
        fixed (byte* data = bytes) VtNative.Check(VtNative.CMBlockBufferCopyDataBytes(block, 0, length, data), "copy encoded packet");
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < prefixBytes) throw new InvalidDataException("Truncated encoded NAL length.");
            var size = prefixBytes switch
            {
                1 => bytes[offset],
                2 => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset)),
                _ => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)),
            };
            offset += prefixBytes;
            if (size == 0 || size > bytes.Length - offset) throw new InvalidDataException("Truncated encoded NAL.");
            WriteNal(output, bytes.AsSpan(offset, (int)size));
            offset += (int)size;
        }
        return new(_codec, output.ToArray(), VtNative.CMSampleBufferGetPresentationTimeStamp(sample).Ticks);
    }
    private static void WriteNal(MemoryStream output, ReadOnlySpan<byte> nal)
    {
        if (output.Length + 4 + nal.Length > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Encoded access unit exceeds 32 MiB.");
        output.Write([0, 0, 0, 1]);
        output.Write(nal);
    }
    private void SetBoolean(string key, bool value) => VtNative.Check(VtNative.VTSessionSetProperty(_session,
        VtNative.Symbol(VtNative.Vt, key), VtNative.Symbol(VtNative.Cf, value ? "kCFBooleanTrue" : "kCFBooleanFalse")), key);
    private void CheckReady() { ObjectDisposedException.ThrowIf(_disposed, this); CheckThread(); }
    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Encoder callbacks cannot reenter the encoder.");
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Encoder belongs to another thread.");
    }
}