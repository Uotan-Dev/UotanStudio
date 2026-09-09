using System.Buffers;
using UotanStudio.Media.GStreamer;

namespace UotanStudio.Media.Decoding.GStreamer;

/// <summary>Packetized GStreamer decoder using explicitly selected hardware or software plugins. GStreamer 分包视频解码器。</summary>
public sealed unsafe class GStreamerVideoDecoder(bool hardware) : IVideoDecoder
{
    private GstPipeline? _pipeline;
    private Action<DecodedVideoFrame>? _callback;
    private VideoCodecId _codec;
    private int _width, _height;
    private long _sequence;
    private bool _disposed;
    private bool _inCallback;
    /// <summary>Selected native decoder element. 实际原生解码器元素。</summary>
    public string DecoderElement { get; private set; } = string.Empty;
    /// <summary>Whether this instance requires a hardware plugin. 是否要求硬件插件。</summary>
    public bool UsesHardware => hardware;
    /// <inheritdoc />
    public bool IsInitialized => _pipeline is not null;
    internal static string[] Candidates(VideoCodecId codec, bool hardware)
    {
        string name = codec switch { VideoCodecId.H264 => "h264", VideoCodecId.H265 => "h265", VideoCodecId.Av1 => "av1", _ => throw new ArgumentOutOfRangeException(nameof(codec)) };
        if (!hardware) return codec switch { VideoCodecId.H264 => ["avdec_h264", "openh264dec"], VideoCodecId.H265 => ["avdec_h265"], _ => ["dav1ddec", "av1dec", "avdec_av1"] };
        if (OperatingSystem.IsWindows()) return [$"d3d11{name}dec", $"d3d12{name}dec", $"nv{name}dec"];
        if (OperatingSystem.IsMacOS()) return ["vtdec_hw"];
        return [$"va{name}dec", $"vaapi{name}dec", $"nv{name}dec", $"v4l2sl{name}dec"];
    }
    /// <summary>Checks installed plugin registration; stream and device initialization may still fail. 检查插件注册，不保证实际码流可解码。</summary>
    public static bool IsAvailable(VideoCodecId codec, bool hardware)
    {
        try
        {
            GstNative.Initialize();
            return Candidates(codec, hardware).Any(GstNative.HasElement);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or TypeInitializationException) { return false; }
    }
    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frameCallback);
        VideoFrameLimits.ValidateDimensions(width, height);
        _pipeline?.Dispose(); _pipeline = null;
        GstNative.Initialize();
        _codec = codec; _width = width; _height = height; _callback = frameCallback; _sequence = 0;
        string name = codec switch { VideoCodecId.H264 => "h264", VideoCodecId.H265 => "h265", VideoCodecId.Av1 => "av1", _ => throw new ArgumentOutOfRangeException(nameof(codec)) };
        string format = codec == VideoCodecId.Av1 ? "obu-stream,alignment=tu" : "byte-stream,alignment=au";
        var errors = new List<string>();
        foreach (string candidate in Candidates(codec, hardware))
        {
            if (!GstNative.HasElement(candidate)) continue;
            try
            {
                _pipeline = new GstPipeline($"appsrc name=input is-live=true format=time block=false max-bytes={GstPipeline.MaximumQueuedBytes} caps=video/x-{name},stream-format={format},width={width},height={height} ! {name}parse ! {candidate} ! video/x-raw ! videoconvert ! video/x-raw,format=NV12 ! appsink name=output sync=false max-buffers=8 drop=false enable-last-sample=false");
                DecoderElement = candidate;
                return;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { errors.Add($"{candidate}: {error.Message}"); }
        }
        throw new NotSupportedException($"No GStreamer {(hardware ? "hardware" : "software")} decoder accepts {codec}. {string.Join("; ", errors)}");
    }
    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit) => Decode(accessUnit, checked(_sequence++ * 166667));
    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        _pipeline!.Push(accessUnit, timestampTicks);
        ReadFrames(20_000_000); // Bound initial wait; output remains lossless in appsink.
    }
    /// <inheritdoc />
    public void Poll() { CheckReady(); ReadFrames(0); }
    private void ReadFrames(ulong timeout)
    {
        for (int i = 0; i < 4096; i++)
        {
            nint sample = _pipeline!.Pull(i == 0 ? timeout : 0);
            if (sample == 0) return;
            try { Deliver(sample); }
            finally { GstNative.gst_mini_object_unref(sample); }
        }
        throw new InvalidDataException("Excessive GStreamer output frames.");
    }
    private void Deliver(nint sample)
    {
        nint caps = GstNative.gst_sample_get_caps(sample);
        if (caps == 0) throw new InvalidDataException("Missing video output caps.");
        nint structure = GstNative.gst_caps_get_structure(caps, 0);
        if (GstNative.gst_structure_get_int(structure, "width", out int width) == 0 || GstNative.gst_structure_get_int(structure, "height", out int height) == 0)
            throw new InvalidDataException("Missing video dimensions.");
        VideoFrameLimits.ValidateDimensions(width, height);
        // GStreamer raw NV12 default layout: four-byte pitch, even luma plane height.
        int nativeStride = checked((width + 3) & ~3);
        int nativeYHeight = checked((height + 1) & ~1);
        int nativeSize = checked(nativeStride * (nativeYHeight + (height + 1) / 2));
        nint source = GstNative.gst_sample_get_buffer(sample);
        nint meta = source == 0 ? 0 : GstNative.gst_buffer_get_video_meta(source);
        if (source == 0 || (meta == 0 && GstNative.gst_buffer_get_size(source) != (nuint)nativeSize)) throw new InvalidDataException("Unexpected GStreamer NV12 memory layout.");
        int stride = (width + 1) & ~1;
        int size = VideoFrameLimits.Nv12Length(width, height, stride);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            fixed (byte* target = pixels)
            {
                if (meta != 0)
                {
                    CopyPlane(meta, 0, target, stride, width, height);
                    CopyPlane(meta, 1, target + height * stride, stride, stride, (height + 1) / 2);
                }
                else
                {
                    for (int y = 0; y < height; y++) CopyRow(source, y * nativeStride, target + y * stride, stride);
                    for (int y = 0; y < (height + 1) / 2; y++) CopyRow(source, (nativeYHeight + y) * nativeStride, target + (height + y) * stride, stride);
                }
            }
        }
        catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
        var color = new GstNative.Colorimetry();
        nint colorText = GstNative.gst_structure_get_string(structure, "colorimetry");
        if (colorText != 0) GstNative.gst_video_colorimetry_from_string(out color, colorText);
        using var frame = new DecodedVideoFrame(pixels, size, width, height, stride, GstNative.GetTimestamp(source))
        {
            ColorMatrix = color.Matrix switch { 3 => VideoColorMatrix.Bt709, 4 => VideoColorMatrix.Bt601, _ => VideoColorMatrix.Auto },
            ColorRange = color.Range switch { 1 => VideoColorRange.Full, 2 => VideoColorRange.Limited, _ => VideoColorRange.Auto },
        };
        _inCallback = true;
        try { _callback!(frame); } finally { _inCallback = false; }
    }
    private static void CopyPlane(nint meta, uint plane, byte* target, int targetStride, int rowBytes, int rows)
    {
        if (GstNative.gst_video_meta_map(meta, plane, out var map, out nint data, out int stride, 1) == 0)
            throw new InvalidDataException("Cannot map GStreamer video plane.");
        try
        {
            if (Math.Abs((long)stride) < rowBytes) throw new InvalidDataException("Invalid video plane stride.");
            for (int row = 0; row < rows; row++)
            {
                nint address = checked(data + checked(row * stride));
                long offset = checked((long)address - (long)map.Data);
                if (offset < 0 || (ulong)offset + (uint)rowBytes > (ulong)map.Size) throw new InvalidDataException("Video plane exceeds mapped memory.");
                new ReadOnlySpan<byte>((void*)address, rowBytes).CopyTo(new Span<byte>(target + row * targetStride, rowBytes));
                if (targetStride > rowBytes) new Span<byte>(target + row * targetStride + rowBytes, targetStride - rowBytes).Clear();
            }
        }
        finally { GstNative.gst_video_meta_unmap(meta, plane, ref map); }
    }
    private static void CopyRow(nint source, int offset, byte* target, int bytes)
    {
        if (GstNative.gst_buffer_extract(source, (nuint)offset, target, (nuint)bytes) != (nuint)bytes) throw new InvalidDataException("Truncated video plane.");
    }
    /// <inheritdoc />
    public void Drain()
    {
        CheckReady();
        _pipeline!.End();
        long deadline = Environment.TickCount64 + 5000;
        do
        {
            ReadFrames(10_000_000);
            if (_pipeline.IsEos) return;
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException("GStreamer drain exceeded five seconds.");
    }
    /// <inheritdoc />
    public void Reset() { CheckReady(); Initialize(_codec, _width, _height, _callback!); }
    private void CheckReady()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null) throw new InvalidOperationException("Decoder is not initialized.");
    }
    /// <inheritdoc />
    public void Dispose() { if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot dispose the decoder."); if (_disposed) return; _pipeline?.Dispose(); _pipeline = null; _callback = null; _disposed = true; }
}