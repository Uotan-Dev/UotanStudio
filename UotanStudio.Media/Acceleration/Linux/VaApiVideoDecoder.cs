using System.Buffers;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>
/// Linux VA-API hardware decoder using the versioned uotan_media_va bridge and libavcodec.
/// Supports 8-bit 4:2:0 H.264/HEVC/AV1 when the selected driver exposes the corresponding profile.
/// </summary>
/// <remarks>
/// Requires libuotan_media_va.so, libavcodec/libavutil and a VA driver; does not require GStreamer.
/// Software decoding is forbidden. Complete access units are decoded in display order with their
/// original PTS. NV12 is downloaded through the driver and copied into callback-scoped pooled frames.
/// Calls must use one owning thread. Stream failures require Reset or reinitialization.
/// </remarks>
public sealed class VaApiVideoDecoder : IVideoDecoder
{
    private readonly VideoDecoderOptions _options;
    private readonly Func<VideoCodecId, string?, IVaCodecSession> _open;
    private IVaCodecSession? _session;
    private Action<DecodedVideoFrame>? _callback;
    private VideoCodecId _codec;
    private int _thread;
    private bool _disposed, _inCallback, _faulted, _ended;
    private byte[]? _configuration;
    private long _sequence;

    /// <summary>Creates an uninitialized hardware-only VA-API decoder.</summary>
    public VaApiVideoDecoder() : this(new VideoDecoderOptions()) { }

    /// <summary>Creates a decoder with an optional DRM render node and color overrides.</summary>
    public VaApiVideoDecoder(VideoDecoderOptions options) : this(options, VaCodecNative.Open) { }

    internal VaApiVideoDecoder(VideoDecoderOptions options, Func<VideoCodecId, string?, IVaCodecSession> open)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _open = open;
    }

    /// <inheritdoc />
    public bool IsInitialized => _session is not null;

    /// <summary>Checks native codec and driver VLD capability; stream-specific profile support still needs decoding.</summary>
    public static bool IsSupported(VideoCodecId codec, string? renderNode = null)
    {
        if (!OperatingSystem.IsLinux() || codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) return false;
        try { using var session = VaCodecNative.Open(codec, renderNode); return true; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or IOException or InvalidOperationException)
        { return false; }
    }

    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) throw new ArgumentOutOfRangeException(nameof(codec));
        if (_options.VaApiRenderNode is { } node && (string.IsNullOrWhiteSpace(node) || !Path.IsPathFullyQualified(node)))
            throw new ArgumentException("VA-API render node must be an absolute device path.", nameof(_options));
        _session?.Dispose();
        _session = null;
        _callback = null;
        _configuration = null;
        _thread = Environment.CurrentManagedThreadId;
        _codec = codec;
        _sequence = 0;
        _faulted = _ended = false;
        _session = _open(codec, _options.VaApiRenderNode);
        _callback = frameCallback;
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit) => Decode(accessUnit, checked(_sequence++ * 166667));

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
    {
        CheckReady();
        if (_ended) throw new InvalidOperationException("Reset the decoder after draining before sending new input.");
        VideoFrameLimits.ValidateAccessUnit(accessUnit);
        try
        {
            // Standalone VPS/SPS/PPS/SEI packets are retained until the next picture.
            // libavcodec expects complete access units, as supplied by scrcpy.
            bool hasPicture = _codec == VideoCodecId.Av1 && Av1Obu.ContainsFrame(accessUnit);
            if (_codec != VideoCodecId.Av1)
            {
                foreach (Range range in AnnexB.Split(accessUnit))
                {
                    ReadOnlySpan<byte> nal = accessUnit[range];
                    if (_codec == VideoCodecId.H265 && nal.Length < 2) throw new InvalidDataException("Truncated HEVC NAL header.");
                    int type = _codec == VideoCodecId.H264 ? nal[0] & 31 : (nal[0] >> 1) & 63;
                    hasPicture |= _codec == VideoCodecId.H264 ? type is >= 1 and <= 5 : type <= 31;
                }
            }
            byte[]? combined = null;
            if (_configuration is not null)
            {
                int length = checked(_configuration.Length + accessUnit.Length);
                if (length > VideoFrameLimits.MaximumAccessUnitBytes) throw new InvalidDataException("Pending access unit exceeds 32 MiB.");
                combined = new byte[length];
                _configuration.CopyTo(combined, 0);
                accessUnit.CopyTo(combined.AsSpan(_configuration.Length));
                accessUnit = combined;
            }
            if (!hasPicture) { _configuration = combined ?? accessUnit.ToArray(); return; }
            _configuration = null;
            int status = _session!.Send(accessUnit, timestampTicks);
            if (status == 2)
            {
                ReadFrames(out int frames);
                if (frames == 0) throw new InvalidDataException("VA-API decoder made no progress before retry.");
                status = _session.Send(accessUnit, timestampTicks);
            }
            if (status != 0) throw new InvalidDataException("VA-API decoder did not accept the access unit.");
            ReadFrames(out _);
        }
        catch { _faulted = true; throw; }
    }

    /// <inheritdoc />
    public void Poll()
    {
        CheckReady();
        try { ReadFrames(out _); }
        catch { _faulted = true; throw; }
    }

    /// <inheritdoc />
    public void Drain()
    {
        CheckReady();
        if (_ended) return;
        try
        {
            int status = _session!.Send([], 0);
            if (status == 2) { ReadFrames(out _); status = _session.Send([], 0); }
            if (status is not (0 or 3)) throw new InvalidDataException("VA-API decoder rejected EOS.");
            if (ReadFrames(out _) != 3) throw new InvalidDataException("VA-API decoder did not finish draining.");
            _configuration = null;
            _ended = true;
        }
        catch { _faulted = true; throw; }
    }

    private int ReadFrames(out int count)
    {
        for (count = 0; count < 4096; count++)
        {
            int status = _session!.Receive(out VaFrameInfo info);
            if (status is 0 or 3) return status;
            if (status != 1) throw new InvalidDataException("Unexpected VA-API receive status.");
            int length = VideoFrameLimits.Nv12Length(info.Width, info.Height, info.Stride);
            if (info.Length != length || info.Matrix is < 0 or > 2 || info.Range is < 0 or > 2)
                throw new InvalidDataException("Invalid VA-API output layout or color metadata.");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try { _session.Copy(buffer.AsSpan(0, length)); }
            catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
            using var frame = new DecodedVideoFrame(buffer, length, info.Width, info.Height, info.Stride, info.Timestamp)
            {
                ColorMatrix = _options.ColorMatrix != VideoColorMatrix.Auto ? _options.ColorMatrix : (VideoColorMatrix)info.Matrix,
                ColorRange = _options.ColorRange != VideoColorRange.Auto ? _options.ColorRange : (VideoColorRange)info.Range,
            };
            _inCallback = true;
            try { _callback!(frame); } finally { _inCallback = false; }
        }
        throw new InvalidDataException("Excessive VA-API output frames.");
    }

    /// <inheritdoc />
    public void Reset()
    {
        CheckReady(allowFault: true);
        _session!.Reset();
        _faulted = _ended = false;
        _configuration = null;
        _sequence = 0;
    }

    private void CheckReady(bool allowFault = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        if (_session is null) throw new InvalidOperationException("Decoder is not initialized.");
        if (_faulted && !allowFault) throw new InvalidOperationException("Reset or reinitialize the decoder after a stream failure.");
    }

    private void CheckThread()
    {
        if (_inCallback) throw new InvalidOperationException("Decoder callbacks cannot reenter the decoder.");
        if (_thread != 0 && _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Decoder belongs to another thread.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _session?.Dispose();
        _session = null;
        _callback = null;
        _configuration = null;
        _disposed = true;
    }
}
