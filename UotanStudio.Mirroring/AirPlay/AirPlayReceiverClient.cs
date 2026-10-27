using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Apple;
using UotanStudio.Mirroring.AirPlay.FairPlay;
using UotanStudio.Mirroring.AirPlay.Mdns;
using UotanStudio.Mirroring.AirPlay.Rtsp;

namespace UotanStudio.Mirroring.AirPlay;

/// <summary>
/// Assembles the AirPlay receiving path: RTSP handshake, mDNS announcement, the UDP (or interleaved) media
/// transports and the H.264 demuxer, and publishes the mirrored frame and audio queues for the session.
/// <para>组装 AirPlay 接收路径：RTSP 握手、mDNS 广播、UDP（或交织）媒体传输与 H.264 解复用，并向会话发布
/// 镜像帧与音频队列。</para>
/// </summary>
/// <remarks>
/// The device drives the session: it browses mDNS, connects to the RTSP port, announces its encryption material
/// and then pushes RTP packets. Nothing here identifies a device up front and no reverse control channel exists,
/// so the first RECORD is what makes the session usable.
/// <para>会话由设备驱动：设备浏览 mDNS、连接 RTSP 端口、宣告其加密材料，然后推送 RTP 包。此处不会事先标识任何设备，
/// 也没有反向控制通道，因此第一次 RECORD 才是会话可用的标志。</para>
/// </remarks>
internal sealed class AirPlayReceiverClient : IDisposable
{
    private const int MaximumQueuedFrames = 64;
    private const int MaximumQueuedAudioPackets = 128;
    private readonly AirPlayOptions _options;
    private readonly Action<string>? _logSink;
    private readonly AirPlayMirrorDemuxer _demuxer = new();
    private readonly Queue<ScrcpyVideoFrame> _frames = new();
    private readonly Queue<ScrcpyAudioFrame> _audio = new();
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly SemaphoreSlim _audioSignal = new(0);
    private readonly Lock _gate = new();
    private AirPlayRtspServer? _server;
    private AirPlayMdnsAdvertiser? _advertiser;
    private FairPlayPlayCipher? _fairPlay;
    private AesCtrCipher? _videoCipher;
    private AesCtrCipher? _audioCipher;
    private UdpClient? _videoSocket;
    private UdpClient? _audioSocket;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private bool _encryptionRequired;
    private int _started;
    private int _disposed;

    /// <summary>Creates a receiver for one endpoint. <para>为一个端点创建接收端。</para></summary>
    /// <param name="options">Validated endpoint options. <para>已校验的端点选项。</para></param>
    /// <param name="logSink">Optional diagnostic sink; may be called from background threads.
    /// <para>可选的诊断接收端；可能由后台线程调用。</para></param>
    public AirPlayReceiverClient(AirPlayOptions options, Action<string>? logSink)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logSink = logSink;
        _demuxer.AttachLogSink(logSink);
    }

    /// <summary>Name reported to the editor for this receiving endpoint. <para>报告给编辑器的接收端点名称。</para></summary>
    public string DeviceName => _options.Name;

    /// <summary>Port the RTSP listener bound, or zero before <see cref="Start"/>.
    /// <para>RTSP 监听器绑定的端口；<see cref="Start"/> 之前为零。</para></summary>
    public int RtspPort => _server?.Port ?? 0;

    /// <summary>Video metadata parsed from the first SPS, or <see langword="null"/> before it arrives.
    /// <para>从首个 SPS 解析出的视频元数据；在到达之前为 <see langword="null"/>。</para>
    /// </summary>
    public ScrcpyVideoMetadata? VideoMetadata => _demuxer.Metadata;

    /// <summary>Fixed audio metadata of the mirrored AAC-ELD stream. <para>镜像 AAC-ELD 流的固定音频元数据。</para></summary>
    public ScrcpyAudioMetadata? AudioMetadata { get; } = new(0x00616163);

    /// <summary>Binds the RTSP port, starts the media pumps and announces the endpoint over mDNS.
    /// <para>绑定 RTSP 端口、启动媒体泵并持久广播该端点。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The receiver was already disposed.
    /// <para>接收端已被释放。</para></exception>
    /// <exception cref="SocketException">The RTSP port or the mDNS port cannot be bound.
    /// <para>RTSP 端口或 mDNS 端口无法绑定。</para></exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _fairPlay = FairPlayPlayCipher.TryCreate(_options.FairPlayKeyPath, _logSink);
        var server = new AirPlayRtspServer(_options.RtspPort, _logSink)
        {
            Handler = HandleRequest,
            MediaPacket = OnInterleavedPacket,
        };
        server.Start();
        _server = server;
        if (_options.Advertise)
        {
            var deviceId = _options.DeviceId.Length > 0
                ? _options.DeviceId
                : AirPlayMdnsAdvertiser.ResolveLocalEndpoint().DeviceId;
            var advertiser = new AirPlayMdnsAdvertiser(_options.Name, deviceId, server.Port, string.Empty, _logSink);
            advertiser.Start();
            _advertiser = advertiser;
        }

        _logSink?.Invoke(
            $"AirPlay receiver listening on port {server.Port} as '{_options.Name}' and waiting for a device.");
    }

    /// <summary>Dequeues one mirrored video frame. <para>取出一个镜像视频帧。</para></summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds. <para>最长等待时间（毫秒）。</para></param>
    /// <returns>The frame the caller must dispose, or <see langword="null"/> when nothing arrived in time.
    /// <para>由调用方释放的帧；超时未到达时为 <see langword="null"/>。</para></returns>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
    {
        if (!_frameSignal.Wait(Math.Max(0, timeoutMs)))
        {
            return null;
        }

        lock (_gate)
        {
            return _frames.Count > 0 ? _frames.Dequeue() : null;
        }
    }

    /// <summary>Dequeues one mirrored audio packet. <para>取出一个镜像音频包。</para></summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds. <para>最长等待时间（毫秒）。</para></param>
    /// <returns>The packet the caller must dispose, or <see langword="null"/> when nothing arrived in time.
    /// <para>由调用方释放的包；超时未到达时为 <see langword="null"/>。</para></returns>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs)
    {
        if (!_audioSignal.Wait(Math.Max(0, timeoutMs)))
        {
            return null;
        }

        lock (_gate)
        {
            return _audio.Count > 0 ? _audio.Dequeue() : null;
        }
    }

    private AirPlayRtspResponse HandleRequest(AirPlayRtspRequest request)
    {
        switch (request.Method)
        {
            case "OPTIONS":
                return AirPlayRtspResponse.Ok(null, ("Public", AirPlayConstants.PublicMethods));
            case "GET":
                return HandleGet();
            case "ANNOUNCE":
                return HandleAnnounce(request);
            case "SETUP":
                return HandleSetup(request);
            case "RECORD":
                return HandleRecord();
            case "FLUSH":
                DropQueued();
                return AirPlayRtspResponse.Ok();
            case "TEARDOWN":
                DropQueued();
                return AirPlayRtspResponse.Ok();
            case "GET_PARAMETER":
            case "SET_PARAMETER":
                return AirPlayRtspResponse.Ok();
            case "POST":
                return HandlePost(request);
            default:
                return AirPlayRtspResponse.Error(405, "Method Not Allowed");
        }
    }

    private AirPlayRtspResponse HandleGet()
    {
        var info = new Dictionary<string, object>(8)
        {
            ["features"] = (long)AirPlayConstants.Features,
            ["model"] = AirPlayConstants.Model,
            ["protovers"] = AirPlayConstants.ProtocolVersion,
            ["srcvers"] = AirPlayConstants.SourceVersion,
            ["vv"] = 2L,
            ["deviceid"] = _options.DeviceId,
        };
        return AirPlayRtspResponse.Ok(ApplePlist.ToBinary(info),
            ("Content-Type", "application/x-apple-binary-plist"));
    }

    private AirPlayRtspResponse HandlePost(AirPlayRtspRequest request)
    {
        _logSink?.Invoke(
            $"AirPlay pairing request '{request.Uri}' was rejected: HAP pair-setup/pair-verify is not implemented.");
        return AirPlayRtspResponse.Error(501, "Not Implemented");
    }

    private AirPlayRtspResponse HandleAnnounce(AirPlayRtspRequest request)
    {
        if (request.Body.Length == 0)
        {
            return AirPlayRtspResponse.Error(400, "Bad Request");
        }

        Dictionary<string, object> announcement;
        try
        {
            announcement = (Dictionary<string, object>)ApplePlist.Parse(
                request.Body, new ApplePlistLimits(AirPlayConstants.MaximumBodyBytes));
        }
        catch (Exception error) when (error is InvalidDataException or InvalidCastException)
        {
            _logSink?.Invoke("AirPlay ANNOUNCE payload was rejected: " + error.Message);
            return AirPlayRtspResponse.Error(400, "Bad Request");
        }

        var ekey = announcement.TryGetValue("ekey", out var keyValue) && keyValue is byte[] keyBytes
            ? keyBytes
            : [];
        var eiv = announcement.TryGetValue("eiv", out var ivValue) && ivValue is byte[] ivBytes ? ivBytes : [];
        if (ekey.Length == 0)
        {
            _logSink?.Invoke("AirPlay ANNOUNCE carried no encryption material; the stream is accepted as clear.");
            return AirPlayRtspResponse.Ok();
        }

        if (_fairPlay is null)
        {
            _encryptionRequired = true;
            _logSink?.Invoke(
                "AirPlay ANNOUNCE requires FairPlay unwrapping but no key material is configured; " +
                "set AirPlayOptions.FairPlayKeyPath to decode this stream.");
            return AirPlayRtspResponse.Error(403, "Forbidden");
        }

        if (!_fairPlay.TryDecryptEkey(ekey, eiv, out var aesKey, out var aesIv))
        {
            _logSink?.Invoke("AirPlay ANNOUNCE key material could not be unwrapped.");
            return AirPlayRtspResponse.Error(403, "Forbidden");
        }

        _videoCipher?.Dispose();
        _audioCipher?.Dispose();
        _videoCipher = new AesCtrCipher(aesKey, aesIv);
        _audioCipher = new AesCtrCipher(aesKey, aesIv);
        _encryptionRequired = false;
        return AirPlayRtspResponse.Ok();
    }

    private AirPlayRtspResponse HandleSetup(AirPlayRtspRequest request)
    {
        var response = new List<object>(2);
        if (request.Body.Length > 0)
        {
            Dictionary<string, object> setup;
            try
            {
                setup = (Dictionary<string, object>)ApplePlist.Parse(
                    request.Body, new ApplePlistLimits(AirPlayConstants.MaximumBodyBytes));
            }
            catch (Exception error) when (error is InvalidDataException or InvalidCastException)
            {
                _logSink?.Invoke("AirPlay SETUP payload was rejected: " + error.Message);
                return AirPlayRtspResponse.Error(400, "Bad Request");
            }

            if (setup.TryGetValue("streams", out var streams) && streams is List<object> entries)
            {
                foreach (var entry in entries)
                {
                    if (entry is not Dictionary<string, object> stream)
                    {
                        continue;
                    }

                    var type = stream.TryGetValue("type", out var typeValue) && typeValue is long typeNumber
                        ? (int)typeNumber
                        : 0;
                    var dataPort = type switch
                    {
                        AirPlayConstants.VideoPayloadType => EnsureVideoReceiver(),
                        AirPlayConstants.AudioPayloadType => EnsureAudioReceiver(),
                        _ => 0,
                    };
                    if (dataPort == 0)
                    {
                        continue;
                    }

                    response.Add(new Dictionary<string, object>(3)
                    {
                        ["type"] = (long)type,
                        ["dataPort"] = (long)dataPort,
                    });
                }
            }
        }

        var body = ApplePlist.ToBinary(new Dictionary<string, object>(1) { ["streams"] = response });
        return AirPlayRtspResponse.Ok(body,
            ("Content-Type", "application/x-apple-binary-plist"), ("Session", "1"));
    }

    private AirPlayRtspResponse HandleRecord()
    {
        if (_encryptionRequired)
        {
            _logSink?.Invoke("AirPlay RECORD was refused because the announced stream stays encrypted.");
            return AirPlayRtspResponse.Error(500, "Internal Server Error");
        }

        _logSink?.Invoke("AirPlay RECORD accepted; mirrored media is now expected from the device.");
        return AirPlayRtspResponse.Ok();
    }

    private int EnsureVideoReceiver()
    {
        if (_videoSocket is not null)
        {
            return LocalPort(_videoSocket);
        }

        var socket = CreateSocket();
        _videoSocket = socket;
        _videoThread = new Thread(() => Pump(socket, OnVideoPacket)) { IsBackground = true, Name = "airplay-video" };
        _videoThread.Start();
        return LocalPort(socket);
    }

    private int EnsureAudioReceiver()
    {
        if (_audioSocket is not null)
        {
            return LocalPort(_audioSocket);
        }

        var socket = CreateSocket();
        _audioSocket = socket;
        _audioThread = new Thread(() => Pump(socket, OnAudioPacket)) { IsBackground = true, Name = "airplay-audio" };
        _audioThread.Start();
        return LocalPort(socket);
    }

    private static UdpClient CreateSocket()
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        try
        {
            socket.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        }
        catch (SocketException)
        {
            socket.Client.ReceiveBufferSize = 256 * 1024;
        }

        return socket;
    }

    private static int LocalPort(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

    private void Pump(UdpClient socket, Action<byte[]> sink)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (Volatile.Read(ref _disposed) == 0)
        {
            byte[] packet;
            try
            {
                packet = socket.Receive(ref remote);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException
                or InvalidOperationException)
            {
                return;
            }

            if (packet.Length == 0 || packet.Length > AirPlayConstants.MaximumRtpPacketBytes)
            {
                continue;
            }

            try
            {
                sink(packet);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                _logSink?.Invoke("AirPlay media packet was rejected: " + error.Message);
            }
        }
    }

    private void OnInterleavedPacket(int channel, byte[] payload)
    {
        if (channel == AirPlayConstants.VideoPayloadType)
        {
            OnVideoPacket(payload);
        }
        else if (channel == AirPlayConstants.AudioPayloadType)
        {
            OnAudioPacket(payload);
        }
    }

    private void OnVideoPacket(byte[] packet)
    {
        var start = RtpPayloadOffset(packet);
        if (start < 0 || packet.Length - start < AirPlayConstants.MirrorHeaderLength)
        {
            return;
        }

        var payload = packet.AsSpan(start);
        if (_videoCipher is not null)
        {
            var declared = BinaryPrimitives.ReadUInt32BigEndian(payload);
            var available = payload.Length - AirPlayConstants.MirrorHeaderLength;
            var bodyLength = declared == 0
                ? available
                : (int)Math.Min(declared, (uint)available);
            if (bodyLength > 0)
            {
                _videoCipher.DecryptInPlace(payload.Slice(AirPlayConstants.MirrorHeaderLength, bodyLength));
            }
        }

        var frames = new List<ScrcpyVideoFrame>(2);
        _demuxer.Demux(payload, frames);
        if (frames.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }

                return;
            }

            foreach (var frame in frames)
            {
                while (_frames.Count >= MaximumQueuedFrames)
                {
                    _frames.Dequeue().Dispose();
                    _ = _frameSignal.Wait(0);
                }

                _frames.Enqueue(frame);
                _frameSignal.Release();
            }
        }
    }

    private void OnAudioPacket(byte[] packet)
    {
        var start = RtpPayloadOffset(packet);
        if (start < 0)
        {
            return;
        }

        var payload = packet.AsSpan(start);
        if (payload.Length < 8)
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        var data = payload[8..];
        if (length == 0 || length > (uint)data.Length)
        {
            return;
        }

        var bytes = data[..(int)length].ToArray();
        if (_audioCipher is not null)
        {
            _audioCipher.DecryptInPlace(bytes);
        }

        if (bytes.Length > AirPlayConstants.MaximumAudioPacketBytes)
        {
            return;
        }

        var frame = new ScrcpyAudioFrame((long)timestamp, bytes);
        lock (_gate)
        {
            while (_audio.Count >= MaximumQueuedAudioPackets)
            {
                _audio.Dequeue().Dispose();
                _ = _audioSignal.Wait(0);
            }

            _audio.Enqueue(frame);
            _audioSignal.Release();
        }
    }

    private static int RtpPayloadOffset(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || (packet[0] >> 6) != 2)
        {
            return 0;
        }

        var offset = 12 + (packet[0] & 0x0F) * 4;
        if ((packet[0] & 0x10) != 0)
        {
            if (offset + 4 > packet.Length)
            {
                return -1;
            }

            var extensionWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4 + extensionWords * 4;
        }

        return offset <= packet.Length ? offset : -1;
    }

    private void DropQueued()
    {
        lock (_gate)
        {
            while (_frames.Count > 0)
            {
                _frames.Dequeue().Dispose();
                _ = _frameSignal.Wait(0);
            }

            while (_audio.Count > 0)
            {
                _audio.Dequeue().Dispose();
                _ = _audioSignal.Wait(0);
            }
        }
    }

    /// <summary>Stops the listener, the media pumps and the announcement, then drains queued packets.
    /// <para>停止监听、媒体泵与广播，然后清空已排队的包。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _advertiser?.Dispose();
        }
        finally
        {
            _advertiser = null;
            try
            {
                _server?.Dispose();
            }
            finally
            {
                _server = null;
                _videoSocket?.Dispose();
                _audioSocket?.Dispose();
                _videoSocket = null;
                _audioSocket = null;
                _videoThread?.Join(TimeSpan.FromSeconds(2));
                _audioThread?.Join(TimeSpan.FromSeconds(2));
                _videoThread = null;
                _audioThread = null;
                _videoCipher?.Dispose();
                _audioCipher?.Dispose();
                _videoCipher = null;
                _audioCipher = null;
                DropQueued();
                _frameSignal.Release();
                _audioSignal.Release();
            }
        }
    }
}