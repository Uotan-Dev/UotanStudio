using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.VideoToolbox;

namespace UotanStudio.Media.Encoding;

/// <summary>An owned Annex-B access unit, including parameter sets, emitted by a video encoder.</summary>
/// <param name="Codec">Compressed codec.</param>
/// <param name="Data">Managed encoded data owned by the recipient.</param>
/// <param name="TimestampTicks">Presentation timestamp in TimeSpan ticks.</param>
public sealed record EncodedVideoPacket(VideoCodecId Codec, byte[] Data, long TimestampTicks);

/// <summary>
/// Hardware VideoToolbox H.264/H.265 encoder for 8-bit NV12 input; emits bounded Annex-B packets.
/// macOS 编码会话：显式要求硬件、关闭帧重排，输入像素逐平面复制到 CoreVideo。
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
    private long _lastTimestamp = -1;

    /// <summary>Creates a low-latency hardware encoder, failing if the codec or stream configuration is unavailable.</summary>
    public VideoToolboxVideoEncoder(VideoCodecId codec, int width, int height, Action<EncodedVideoPacket> packetCallback)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(packetCallback);
        _codec = codec; _width = width; _height = height; _callback = packetCallback;
        nint specification = VtNative.Dictionary();
        try
        {
            VtNative.SetBoolean(specification, "kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder", true);
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

    /// <summary>Encodes a complete NV12 frame with a nonnegative, strictly increasing timestamp, then delivers output.</summary>
    public void Encode(DecodedVideoFrame frame)
    {
        CheckReady();
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width || frame.Height != _height) throw new ArgumentException("Frame dimensions do not match the encoder.", nameof(frame));
        int required = VideoFrameLimits.Nv12Length(frame.Width, frame.Height, frame.Stride);
        if (frame.Span.Length < required) throw new ArgumentException("Truncated NV12 frame.", nameof(frame));
        if (frame.TimestampTicks < 0 || frame.TimestampTicks <= _lastTimestamp) throw new ArgumentException("Timestamps must strictly increase.", nameof(frame));
        VtNative.Check(VtNative.CVPixelBufferCreate(0, (nuint)_width, (nuint)_height, VtNative.Nv12, 0, out nint image), "create encoder pixels");
        try
        {
            VtNative.Check(VtNative.CVPixelBufferLockBaseAddress(image, 0), "lock encoder pixels");
            try
            {
                for (nuint plane = 0; plane < 2; plane++)
                {
                    int rows = plane == 0 ? _height : (_height + 1) / 2;
                    int rowBytes = plane == 0 ? _width : (_width + 1) & ~1;
                    nuint pitch = VtNative.CVPixelBufferGetBytesPerRowOfPlane(image, plane);
                    nint pointer = VtNative.CVPixelBufferGetBaseAddressOfPlane(image, plane);
                    if (pointer == 0 || pitch < (nuint)rowBytes || pitch > VideoFrameLimits.MaximumFrameBytes
                        || VtNative.CVPixelBufferGetHeightOfPlane(image, plane) < (nuint)rows) throw new InvalidDataException("Invalid encoder pixel plane.");
                    int start = plane == 0 ? 0 : frame.Stride * _height;
                    for (int row = 0; row < rows; row++)
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

    /// <summary>Completes pending native encoding work and delivers packets on the caller's thread.</summary>
    public void Flush()
    {
        CheckReady();
        VtNative.Check(VtNative.VTCompressionSessionCompleteFrames(_session, default), "complete frames"); // kCMTimeInvalid means all pending frames.
        Interlocked.Exchange(ref _error, null)?.Throw();
        while (_packets.TryDequeue(out var packet)) _callback(packet);
    }

    /// <inheritdoc />
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
        nint block = VtNative.CMSampleBufferGetDataBuffer(sample);
        nint format = VtNative.CMSampleBufferGetFormatDescription(sample);
        if (block == 0 || format == 0) throw new InvalidDataException("Missing encoded sample data or format.");
        nuint length = VtNative.CMBlockBufferGetDataLength(block);
        if (length == 0 || length > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Invalid encoded packet length.");
        using var output = new MemoryStream();
        int prefixBytes = 0;
        nuint parameterCount = 1;
        for (nuint i = 0; i < parameterCount; i++)
        {
            nint parameter;
            nuint size;
            int status = _codec == VideoCodecId.H264
                ? VtNative.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, out parameter, out size, out parameterCount, out prefixBytes)
                : VtNative.CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, i, out parameter, out size, out parameterCount, out prefixBytes);
            VtNative.Check(status, "encoded parameter set");
            if (parameter == 0 || parameterCount > 32 || size is 0 or > 1048576) throw new InvalidDataException("Invalid encoded parameter sets.");
            WriteNal(output, new ReadOnlySpan<byte>((void*)parameter, checked((int)size)));
        }
        if (prefixBytes is not (1 or 2 or 4)) throw new InvalidDataException("Invalid encoded NAL length size.");
        byte[] bytes = new byte[(int)length];
        fixed (byte* data = bytes) VtNative.Check(VtNative.CMBlockBufferCopyDataBytes(block, 0, length, data), "copy encoded packet");
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < prefixBytes) throw new InvalidDataException("Truncated encoded NAL length.");
            uint size = prefixBytes switch
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
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Encoder belongs to another thread.");
    }
}
