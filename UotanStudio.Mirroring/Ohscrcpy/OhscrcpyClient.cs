using System.Buffers.Binary;

namespace UotanStudio.Mirroring;

/// <summary>Options for one ohscrcpy connection. <para>单条 ohscrcpy 连接的选项。</para></summary>
/// <param name="DeviceName">Name shown by the mirror UI; the protocol itself announces none.
/// <para>投屏界面显示的名称；协议本身不宣告名称。</para></param>
/// <param name="HeartbeatIntervalMs">Interval between client heartbeats; must stay below the server's
/// 30-second idle timeout. <para>客户端心跳间隔；必须低于服务端 30 秒空闲超时。</para></param>
public sealed record OhscrcpyClientOptions(string DeviceName = "", int HeartbeatIntervalMs = OhscrcpyConstants.HeartbeatIntervalMs);

/// <summary>
/// ohscrcpy client: consumes the single device TCP connection and demuxes the video configuration, H.264
/// access units, device status and heartbeats it carries, and writes control packets back.
/// <para>ohscrcpy 客户端：消费设备的单条 TCP 连接，解出其中的视频配置、H.264 访问单元、设备状态与心跳，
/// 并回写控制包。</para>
/// </summary>
/// <remarks>
/// <para>
/// The server multiplexes every stream over one connection, so exactly one thread may call
/// <see cref="ReadFrame"/>; control writes are serialized by an internal lock. Heartbeats are sent from
/// <see cref="ReadFrame"/> when the interval elapses, which keeps the connection alive as long as the
/// decode loop pumps frames.
/// </para>
/// <para>
/// 服务端把所有码流复用在一条连接上，因此只能有一个线程调用 <see cref="ReadFrame"/>；控制写入由内部锁串行化。
/// 心跳在 <see cref="ReadFrame"/> 中按间隔发送，只要解码循环持续取帧，连接就保持存活。
/// </para>
/// <para>
/// H.264 frames are delivered as <see cref="ScrcpyVideoFrame"/> instances so the existing GPU decode
/// pipeline consumes ohscrcpy access units without a second decoder stack: the codec-configuration packet
/// is flagged with <see cref="ScrcpyConstants.PacketFlagConfig"/> and key frames carry
/// <see cref="ScrcpyConstants.PacketFlagKeyFrame"/>, exactly like the scrcpy client.
/// </para>
/// <para>
/// H.264 帧以 <see cref="ScrcpyVideoFrame"/> 交付，使现有 GPU 解码管线无需第二套解码器即可消费 ohscrcpy
/// 访问单元：编码配置包带 <see cref="ScrcpyConstants.PacketFlagConfig"/> 标志，关键帧带
/// <see cref="ScrcpyConstants.PacketFlagKeyFrame"/> 标志，与 scrcpy 客户端一致。
/// </para>
/// </remarks>
internal sealed class OhscrcpyClient : IDisposable
{
    private readonly Func<CancellationToken, Stream> _open;
    private readonly OhscrcpyClientOptions _options;
    private readonly Queue<(uint Type, byte[] Payload)> _pendingPackets = new();
    private readonly Queue<OhscrcpyDeviceMessage> _deviceMessages = new();
    private readonly byte[] _header = new byte[OhscrcpyConstants.HeaderLength];
    private readonly Lock _writeLock = new();

    private Stream? _stream;
    private long _lastSentMs;
    private int _disposed;

    /// <summary>Creates a client over a caller-supplied tunnel opener.
    /// <para>基于调用方提供的通道打开器创建客户端。</para>
    /// </summary>
    /// <param name="open">Opens the device connection; called once per <see cref="Connect"/>.
    /// <para>打开设备连接；每次 <see cref="Connect"/> 调用一次。</para></param>
    /// <param name="options">Connection options. <para>连接选项。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> or <paramref name="options"/> is
    /// <see langword="null"/>. <para><paramref name="open"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    internal OhscrcpyClient(Func<CancellationToken, Stream> open, OhscrcpyClientOptions options)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Creates a client over an HDC tunnel opener, without exposing a socket to this layer.
    /// <para>基于 HDC 通道打开器创建客户端，本层不暴露套接字。</para>
    /// </summary>
    /// <param name="tunnel">Tunnel that yields the forwarded device connection.
    /// <para>产出已转发设备连接的通道。</para></param>
    /// <param name="options">Connection options. <para>连接选项。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="tunnel"/> or <paramref name="options"/> is
    /// <see langword="null"/>. <para><paramref name="tunnel"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    internal OhscrcpyClient(IOhscrcpyTunnel tunnel, OhscrcpyClientOptions options)
        : this(OpenTunnel(tunnel), options)
    {
    }

    private static Func<CancellationToken, Stream> OpenTunnel(IOhscrcpyTunnel tunnel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);
        return tunnel.Open;
    }

    /// <summary>Name shown by the mirror UI. <para>投屏界面显示的名称。</para></summary>
    /// <value>The configured device name. <para>配置的设备名。</para></value>
    public string DeviceName => _options.DeviceName;

    /// <summary>Whether a connection is currently held. <para>当前是否持有连接。</para></summary>
    /// <value><see langword="true"/> after a successful <see cref="Connect"/> and before disposal.
    /// <para>在成功的 <see cref="Connect"/> 之后、释放之前为 <see langword="true"/>。</para></value>
    public bool IsConnected => _stream is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>Whether control packets can be written. <para>是否可以写入控制包。</para></summary>
    /// <value><see langword="true"/> while the connection is held. <para>持有连接时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => IsConnected;

    /// <summary>Video metadata from the most recent configuration packet.
    /// <para>最近一个配置包给出的视频元数据。</para></summary>
    /// <value>The H.264 codec, width and height, or <see langword="null"/> before the first configuration.
    /// <para>H.264 编码、宽与高；首个配置之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata { get; private set; }

    /// <summary>Parsed video configuration from the most recent configuration packet.
    /// <para>最近一个配置包解析出的视频配置。</para></summary>
    /// <value>The configuration, or <see langword="null"/> before the first configuration.
    /// <para>视频配置；首个配置之前为 <see langword="null"/>。</para></value>
    public OhscrcpyVideoConfig? VideoConfig { get; private set; }

    /// <summary>Number of configuration packets consumed since connecting; a consumer that compares it
    /// before and after a read detects a reconfiguration.
    /// <para>本次连接以来已消费的配置包数量；使用方在读取前后比较该值即可检测重配置。</para></summary>
    /// <value>Starts at zero and increases once per configuration packet.
    /// <para>从零开始，每个配置包加一。</para></value>
    public int ConfigEpoch { get; private set; }

    /// <summary>
    /// Connects the tunnel and completes the heartbeat challenge the server echoes verbatim.
    /// <para>连接通道并完成服务端原样回显的心跳挑战。</para>
    /// </summary>
    /// <param name="timeoutMs">Budget for the tunnel connect and the heartbeat echo.
    /// <para>通道连接与心跳回显的总预算。</para></param>
    /// <exception cref="InvalidDataException">The server did not echo the challenge, or the handshake
    /// packet stream is malformed. <para>服务端未回显挑战，或握手包流格式不符。</para></exception>
    /// <exception cref="EndOfStreamException">The connection closed during the handshake.
    /// <para>握手期间连接关闭。</para></exception>
    public void Connect(int timeoutMs = 10_000)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        DisposeConnection();
        var stream = _open(CancellationToken.None)
            ?? throw new InvalidOperationException("The ohscrcpy tunnel returned no stream.");
        _stream = stream;
        try
        {
            Handshake(stream, timeoutMs);
        }
        catch
        {
            DisposeConnection();
            throw;
        }
    }

    private void Handshake(Stream stream, int timeoutMs)
    {
        Span<byte> challenge = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(challenge, DateTime.UtcNow.Ticks);
        WritePacket(stream, OhscrcpyConstants.PacketTypeHeartbeat, challenge);
        var challengeValue = challenge.ToArray();
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 1);
        while (true)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0)
            {
                throw new InvalidDataException(
                    $"The ohscrcpy service did not echo the heartbeat within {timeoutMs} ms. "
                    + "ohscrcpy 服务未在时限内回显心跳。");
            }

            var packet = ReadPacket(stream, remaining);
            if (packet.Type == OhscrcpyConstants.PacketTypeHeartbeat
                && packet.Payload.AsSpan().SequenceEqual(challengeValue))
            {
                return;
            }

            if (_pendingPackets.Count >= 32)
            {
                throw new InvalidDataException(
                    "The ohscrcpy server sent too many packets before answering the heartbeat probe.");
            }

            _pendingPackets.Enqueue(packet);
        }
    }

    /// <summary>
    /// Reads the next video frame or codec-configuration packet, consuming heartbeat and device-status
    /// packets that arrive first.
    /// <para>读取下一个视频帧或编码配置包，并先消费其间到达的心跳与设备状态包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-packet timeout in milliseconds. <para>单包读取超时（毫秒）。</para></param>
    /// <returns>The frame owned by the caller, which must dispose it after decoding, or
    /// <see langword="null"/> when no frame arrived before the timeout.
    /// <para>由调用方持有并在解码后释放的帧；超时未收到帧时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    /// <exception cref="InvalidDataException">A packet is malformed or announces an over-long payload.
    /// <para>数据包格式不符或宣告了过长负载。</para></exception>
    /// <exception cref="NotSupportedException">The server switched to a JPEG or raw-RGBA stream, which the
    /// H.264 GPU pipeline cannot decode. <para>服务端切换到了 H.264 GPU 管线无法解码的 JPEG 或原始 RGBA 码流。</para></exception>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
    {
        var stream = _stream ?? throw new InvalidOperationException("Client is not connected. 客户端尚未连接。");
        MaybeSendHeartbeat(stream);
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 1);
        while (true)
        {
            if (_pendingPackets.Count > 0)
            {
                var queued = _pendingPackets.Dequeue();
                if (Process(queued) is { } buffered) return buffered;
                if (queued.Type == OhscrcpyConstants.PacketTypeDeviceStatus) return null;
                continue;
            }

            var remaining = (int)Math.Max(deadline - Environment.TickCount64, 1);
            (uint Type, byte[] Payload) packet;
            try
            {
                packet = ReadPacket(stream, remaining);
            }
            catch (TimeoutException)
            {
                return null;
            }

            if (Process(packet) is { } frame) return frame;
            if (packet.Type == OhscrcpyConstants.PacketTypeDeviceStatus) return null;
            if (Environment.TickCount64 >= deadline) return null;
        }
    }

    private ScrcpyVideoFrame? Process((uint Type, byte[] Payload) packet)
    {
        switch (packet.Type)
        {
            case OhscrcpyConstants.PacketTypeHeartbeat:
                return null;

            case OhscrcpyConstants.PacketTypeVideoConfig:
                var config = OhscrcpyPayload.ParseVideoConfig(packet.Payload);
                if (!config.IsH264)
                {
                    throw new NotSupportedException(
                        $"The ohscrcpy server switched to {config.CodecName} video, which the H.264 decode "
                        + "pipeline does not support. 服务端切换到 "
                        + $"{config.CodecName} 视频，H.264 解码管线不支持该编码。");
                }

                VideoConfig = config;
                VideoMetadata = new ScrcpyVideoMetadata(FourCcH264, config.Width, config.Height);
                ConfigEpoch++;
                return new ScrcpyVideoFrame(unchecked((long)ScrcpyConstants.PacketFlagConfig),
                    config.BuildAnnexBConfiguration());

            case OhscrcpyConstants.PacketTypeVideoFrame:
                var (keyFrame, timestamp) = OhscrcpyPayload.ParseVideoFrameHeader(packet.Payload);
                if (timestamp < 0 || ((ulong)timestamp & ScrcpyConstants.PacketFlagKeyFrame) != 0)
                {
                    throw new InvalidDataException("The ohscrcpy frame timestamp carries flag bits.");
                }

                var accessUnit = packet.Payload.AsSpan(9).ToArray();
                var pts = (ulong)timestamp |
                    (keyFrame ? ScrcpyConstants.PacketFlagKeyFrame : 0UL);
                return new ScrcpyVideoFrame(unchecked((long)pts), accessUnit);

            case OhscrcpyConstants.PacketTypeDeviceStatus:
                if (packet.Payload.Length < 1)
                {
                    throw new InvalidDataException("The ohscrcpy device-status packet carries no sub-type.");
                }

                if (_deviceMessages.Count >= 64) _deviceMessages.Dequeue();
                _deviceMessages.Enqueue(new OhscrcpyDeviceMessage(packet.Payload[0], packet.Payload[1..]));
                return null;

            case OhscrcpyConstants.PacketTypeControl:
                return null;

            default:
                throw new InvalidDataException($"Unknown ohscrcpy packet type 0x{packet.Type:X8}.");
        }
    }

    /// <summary>Removes one device-status message received by <see cref="ReadFrame"/>.
    /// <para>取出一条由 <see cref="ReadFrame"/> 收到的设备状态消息。</para>
    /// </summary>
    /// <param name="message">The oldest queued message, or <see langword="null"/> when none is pending.
    /// <para>队列中最旧的消息；没有待处理消息时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a message was dequeued.
    /// <para>取出了消息时为 <see langword="true"/>。</para></returns>
    public bool TryDequeueDeviceMessage(out OhscrcpyDeviceMessage? message)
    {
        if (_deviceMessages.Count == 0)
        {
            message = null;
            return false;
        }

        message = _deviceMessages.Dequeue();
        return true;
    }

    /// <summary>Sends one complete ohscrcpy packet. <para>发送一个完整的 ohscrcpy 数据包。</para></summary>
    /// <param name="packet">Framed packet from <see cref="OhscrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="OhscrcpyControlMessageWriter"/> 生成的分帧数据包。</para></param>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    public void SendPacket(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var stream = _stream ?? throw new InvalidOperationException("Control is not connected. 控制通道尚未连接。");
        lock (_writeLock)
        {
            stream.Write(packet, 0, packet.Length);
            stream.Flush();
            _lastSentMs = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Translates and sends one scrcpy control message, so the existing mirror UI input path works against
    /// an HDC target. <para>翻译并发送一条 scrcpy 控制消息，使现有投屏界面输入路径可用于 HDC 目标。</para>
    /// </summary>
    /// <param name="message">A message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 产生的消息。</para></param>
    /// <returns><see langword="true"/> when the message has an ohscrcpy equivalent and was sent.
    /// <para>消息存在 ohscrcpy 对应物并已发送时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    public bool SendScrcpyControl(ReadOnlySpan<byte> message)
    {
        if (!ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet)) return false;
        SendPacket(packet);
        return true;
    }

    /// <summary>Sends one heartbeat carrying the current tick count.
    /// <para>发送一条携带当前计时值的心跳。</para></summary>
    /// <exception cref="InvalidOperationException">The client is not connected.
    /// <para>客户端尚未连接。</para></exception>
    public void SendHeartbeat()
    {
        var stream = _stream ?? throw new InvalidOperationException("The client is not connected. 客户端尚未连接。");
        WriteHeartbeat(stream);
    }

    private void MaybeSendHeartbeat(Stream stream)
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastSentMs) < _options.HeartbeatIntervalMs) return;
        WriteHeartbeat(stream);
    }

    private void WriteHeartbeat(Stream stream)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, DateTime.UtcNow.Ticks);
        lock (_writeLock)
        {
            WritePacket(stream, OhscrcpyConstants.PacketTypeHeartbeat, payload);
            _lastSentMs = Environment.TickCount64;
        }
    }

    private static void WritePacket(Stream stream, uint type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[OhscrcpyConstants.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, type);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)payload.Length);
        stream.Write(header);
        if (!payload.IsEmpty) stream.Write(payload);
        stream.Flush();
    }

    private (uint Type, byte[] Payload) ReadPacket(Stream stream, int timeoutMs)
    {
        if (!TryReadExact(stream, _header, timeoutMs))
        {
            throw new TimeoutException("No ohscrcpy packet header arrived before the timeout.");
        }

        var type = BinaryPrimitives.ReadUInt32BigEndian(_header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
        if (length > OhscrcpyConstants.MaxPacketLength)
        {
            throw new InvalidDataException(
                $"ohscrcpy packet 0x{type:X8} announces {length} payload bytes, above the "
                + $"{OhscrcpyConstants.MaxPacketLength}-byte bound.");
        }

        var payload = new byte[length];
        if (!TryReadExact(stream, payload, timeoutMs))
        {
            throw new InvalidDataException(
                $"The ohscrcpy packet 0x{type:X8} stream ended before its {length}-byte payload.");
        }

        return (type, payload);
    }

    private static bool TryReadExact(Stream stream, byte[] buffer, int timeoutMs)
    {
        var offset = 0;
        if (stream.CanTimeout) stream.ReadTimeout = timeoutMs <= 0 ? 1 : timeoutMs;
        while (offset < buffer.Length)
        {
            int count;
            try
            {
                count = stream.Read(buffer, offset, buffer.Length - offset);
            }
            catch (TimeoutException) when (offset == 0)
            {
                return false;
            }
            catch (IOException ex) when (offset == 0 && ex.InnerException
                is System.Net.Sockets.SocketException
            {
                SocketErrorCode: System.Net.Sockets.SocketError.TimedOut,
            })
            {
                return false;
            }

            if (count == 0)
            {
                throw new EndOfStreamException("The ohscrcpy connection closed during a packet read.");
            }

            offset += count;
        }

        return true;
    }

    private void DisposeConnection()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        _pendingPackets.Clear();
        _deviceMessages.Clear();
        VideoMetadata = null;
        VideoConfig = null;
        ConfigEpoch = 0;
    }

    /// <summary>Closes the tunnel connection and clears per-session state; safe to call repeatedly.
    /// <para>关闭通道连接并清除会话状态；可安全地重复调用。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisposeConnection();
    }

    /// <summary>Big-endian four-character code <c>H264</c>. <para>大端四字符码 <c>H264</c>。</para></summary>
    private const uint FourCcH264 = 0x48323634;
}

/// <summary>
/// Opens the already-forwarded device connection for an ohscrcpy session.
/// <para>为 ohscrcpy 会话打开已完成转发的设备连接。</para>
/// </summary>
/// <remarks>
/// <para>
/// Implementations live where ports are owned: the host registers the HDC <c>fport</c> mapping through
/// Comm and hands back the resulting stream. This interface keeps sockets out of
/// <c>UotanStudio.Mirroring</c> while letting the client be tested over an in-memory stream.
/// </para>
/// <para>
/// 实现放在持有端口的一侧：宿主经 Comm 登记 HDC <c>fport</c> 映射并交回所得流。该接口让套接字不进入
/// <c>UotanStudio.Mirroring</c>，同时允许客户端用内存流测试。
/// </para>
/// </remarks>
public interface IOhscrcpyTunnel : IDisposable
{
    /// <summary>Opens the forwarded connection to the device service port.
    /// <para>打开到设备服务端口的已转发连接。</para></summary>
    /// <param name="cancellationToken">Cancels the connect and the port-forward setup.
    /// <para>取消连接与端口转发建立。</para></param>
    /// <returns>A stream whose bytes are the ohscrcpy protocol. <para>字节流即 ohscrcpy 协议的流。</para></returns>
    Stream Open(CancellationToken cancellationToken = default);
}