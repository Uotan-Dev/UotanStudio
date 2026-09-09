using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Comm.Protocols.Scrcpy;

/// <summary>
/// Decoded video stream metadata announced by the server on the first socket.
/// <para>服务端在首个 socket 上宣告的视频流元数据。</para>
/// </summary>
/// <param name="CodecFourCc">Codec four-character code (e.g. 'H264'). 编码四字符码（如 'H264'）。</param>
/// <param name="Width">Video width in pixels. 视频宽度（像素）。</param>
/// <param name="Height">Video height in pixels. 视频高度（像素）。</param>
public sealed record ScrcpyVideoMetadata(uint CodecFourCc, int Width, int Height)
{
    /// <summary>Gets the codec name derived from the four-character code.</summary>
    /// <summary>获取由四字符码推导的编码名称。</summary>
    public string CodecName
    {
        get
        {
            var bytes = BitConverter.GetBytes(CodecFourCc);
            Array.Reverse(bytes);
            return Encoding.ASCII.GetString(bytes);
        }
    }
}

/// <summary>
/// One demuxed video frame (or codec config packet) from the video socket.
/// <para>视频 socket 上解出的一个视频帧（或编码器配置包）。</para>
/// </summary>
/// <param name="Pts">
/// Presentation timestamp; <see cref="ScrcpyConstants.NoPts"/> marks the codec
/// config packet.
/// 显示时间戳；<see cref="ScrcpyConstants.NoPts"/> 表示编码器配置包。
/// </param>
/// <param name="Data">The compressed access-unit bytes. 压缩访问单元字节。</param>
public sealed record ScrcpyVideoFrame(long Pts, byte[] Data)
{
    /// <summary>Gets a value indicating whether this packet carries codec config (SPS/PPS).</summary>
    /// <summary>获取一个值，指示该包是否为编码器配置（SPS/PPS）。</summary>
    public bool IsCodecConfig => ((ulong)Pts & ScrcpyConstants.PacketFlagConfig) != 0;

    /// <summary>Gets the key-frame flag independently of the presentation timestamp.</summary>
    public bool IsKeyFrame => !IsCodecConfig && ((ulong)Pts & ScrcpyConstants.PacketFlagKeyFrame) != 0;

    /// <summary>Gets the media timestamp with config/key-frame flags removed.</summary>
    public long PresentationTimestampMicroseconds => !IsCodecConfig
        ? (long)((ulong)Pts & ScrcpyConstants.PacketTimestampMask)
        : throw new InvalidOperationException("Codec configuration has no presentation timestamp.");
}

/// <summary>
/// A parsed device → client control message.
/// <para>解析后的设备 → 客户端控制消息。</para>
/// </summary>
/// <param name="Type">Device message type. 设备消息类型。</param>
/// <param name="Clipboard">Clipboard text for <see cref="ScrcpyConstants.DeviceTypeClipboard"/>. 剪贴板消息的文本。</param>
/// <param name="AckSequence">Acknowledged sequence for ACK_CLIPBOARD. ACK_CLIPBOARD 确认的序号。</param>
public sealed record ScrcpyDeviceMessage(byte Type, string? Clipboard = null, ulong AckSequence = 0);

/// <summary>
/// scrcpy client: connects the video / audio / control sockets over adbd
/// localabstract forwarding, demuxes framed video and exchanges control
/// messages.
/// <para>scrcpy 客户端：经 adbd localabstract 转发连接视频 / 音频 / 控制 socket，
/// 解出分帧视频并交换控制消息。</para>
/// </summary>
/// <remarks>
/// <para>
/// Sockets are opened in the server's expected order: video, (optional)
/// audio, control. The first socket carries the dummy byte, the 64-byte
/// device name and the codec metadata before the framed stream.
/// </para>
/// <para>
/// socket 按服务端预期的顺序打开：视频、（可选）音频、控制。首个 socket 在
/// 分帧码流之前承载哑字节、64 字节设备名与编码器元数据。
/// </para>
/// <para>
/// All lengths read from the device are bounded: frame payloads are capped
/// at <see cref="MaxFrameLength"/>, clipboard text at 1 MiB; violations throw
/// rather than allocate unbounded buffers.
/// </para>
/// <para>
/// 从设备读取的所有长度均有界：帧负载上限 <see cref="MaxFrameLength"/>、剪贴板
/// 文本上限 1 MiB；越界即抛出异常，不进行无界分配。
/// </para>
/// </remarks>
public sealed class ScrcpyClient : IDisposable
{
    /// <summary>Upper bound for a single compressed access unit (32 MiB).</summary>
    /// <summary>单个压缩访问单元的上限（32 MiB）。</summary>
    public const int MaxFrameLength = 32 * 1024 * 1024;

    /// <summary>Upper bound for device clipboard text (1 MiB).</summary>
    /// <summary>设备剪贴板文本上限（1 MiB）。</summary>
    public const int MaxClipboardLength = 1024 * 1024;

    private readonly Func<string, Stream> _open;
    private readonly ScrcpyServerOptions _options;

    private Stream? _videoStream;
    private Stream? _audioStream;
    private Stream? _controlStream;

    /// <summary>Initializes the client; call <see cref="Connect"/> after the server is running.</summary>
    /// <summary>初始化客户端；服务端启动后调用 <see cref="Connect"/>。</summary>
    /// <param name="session">An established ADB session. 已建立的 ADB 会话。</param>
    /// <param name="options">The options the server was started with. 服务端启动时使用的选项。</param>
    public ScrcpyClient(AdbSession session, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        _open = service => new AdbServiceStream(session.Connection.OpenStream(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Creates a client using owned service streams from an existing ADB server.</summary>
    public ScrcpyClient(AdbServerServices server, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        _open = server.Open;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal ScrcpyClient(Func<string, Stream> open, ScrcpyServerOptions options)
    {
        _open = open;
        _options = options;
    }

    /// <summary>Gets the device name announced on the first socket.</summary>
    /// <summary>获取首个 socket 宣告的设备名。</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Gets the video metadata announced on the first socket.</summary>
    /// <summary>获取首个 socket 宣告的视频元数据。</summary>
    public ScrcpyVideoMetadata? VideoMetadata { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the control socket is connected.
    /// </summary>
    /// <summary>
    /// 获取一个值，指示控制 socket 是否已连接。
    /// </summary>
    public bool IsControlConnected => _controlStream is not null;

    /// <summary>
    /// Connects the sockets in server order and reads the stream metadata.
    /// <para>按服务端要求的顺序连接 socket 并读取流元数据。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. 单次读取超时（毫秒）。</param>
    /// <exception cref="InvalidDataException">Thrown when the handshake fails. 握手失败时抛出。</exception>
    public void Connect(int timeoutMs = 10000)
    {
        Dispose();
        DeviceName = string.Empty;
        VideoMetadata = null;
        try { ConnectCore(timeoutMs); }
        catch { Dispose(); throw; }
    }

    private void ConnectCore(int timeoutMs)
    {
        // Socket 1: video (dummy byte + device name + codec meta + framed stream).
        // socket 1：视频（哑字节 + 设备名 + 编码器元数据 + 分帧码流）。
        _videoStream = _open("localabstract:" + _options.SocketName);

        if (_options.SendDummyByte)
        {
            var dummy = ReadExact(_videoStream, 1, timeoutMs);
            if (dummy.Length != 1 || dummy[0] != 0)
            {
                throw new InvalidDataException("Expected 0x00 dummy byte on the video socket. 视频 socket 上应收到 0x00 哑字节。");
            }
        }

        // Socket 2: audio (codec id only, no dummy/meta in current version).
        // socket 2：音频（当前版本仅含编码器 ID，无哑字节/设备元数据）。
        if (_options.Audio)
        {
            _audioStream = _open("localabstract:" + _options.SocketName);

        }

        // Socket 3: control.
        // socket 3：控制。
        if (_options.Control)
        {
            _controlStream = _open("localabstract:" + _options.SocketName);
        }
        if (_options.SendDeviceMeta)
        {
            var name = ReadExact(_videoStream, ScrcpyConstants.DeviceNameLength, timeoutMs);
            DeviceName = Encoding.UTF8.GetString(name).TrimEnd('\0');
        }

        if (_options.SendCodecMeta)
        {
            var meta = ReadExact(_videoStream, 12, timeoutMs);
            var codec = BinaryPrimitives.ReadUInt32BigEndian(meta);
            var width = BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(4));
            var height = BinaryPrimitives.ReadInt32BigEndian(meta.AsSpan(8));
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException(
                    $"Server announced invalid video size {width}x{height}. 服务端宣告了无效的视频尺寸 {width}x{height}。");
            }

            VideoMetadata = new ScrcpyVideoMetadata(codec, width, height);
        }

        if (_audioStream is not null && _options.SendCodecMeta) _ = ReadExact(_audioStream, 4, timeoutMs);

    }

    /// <summary>
    /// Reads one framed video packet from the video socket.
    /// <para>从视频 socket 读取一个分帧视频包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. 单次读取超时（毫秒）。</param>
    /// <returns>The frame, or <see langword="null"/> when no header arrived before timeout.</returns>
    /// <exception cref="EndOfStreamException">The server closed the video stream.</exception>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
    {
        var stream = _videoStream;
        if (stream is null)
        {
            throw new InvalidOperationException("Client is not connected. 客户端尚未连接。");
        }

        var header = TryReadExact(stream, 12, timeoutMs);
        if (header is null || header.Length != 12)
        {
            return null; // No header arrived before the read timeout.
        }

        var pts = BinaryPrimitives.ReadInt64BigEndian(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (length is < 0 or > MaxFrameLength)
        {
            throw new InvalidDataException(
                $"Frame length {length} exceeds the {MaxFrameLength}-byte bound. 帧长度 {length} 超出 {MaxFrameLength} 字节上限。");
        }

        var payload = ReadExact(stream, length, timeoutMs);
        return new ScrcpyVideoFrame(pts, payload);
    }

    /// <summary>
    /// Sends one control message to the device.
    /// <para>向设备发送一条控制消息。</para>
    /// </summary>
    /// <param name="message">The encoded message from <see cref="ScrcpyControlMessageWriter"/>. <see cref="ScrcpyControlMessageWriter"/> 编码的消息。</param>
    /// <exception cref="InvalidOperationException">Thrown when control is disabled. 未启用控制时抛出。</exception>
    public void SendControl(byte[] message)
    {
        var stream = _controlStream;
        if (stream is null)
        {
            throw new InvalidOperationException(
                "Control is not enabled or the client is not connected. 控制未启用或客户端尚未连接。");
        }

        stream.Write(message);
    }

    /// <summary>
    /// Polls one device → client message (clipboard, ack) from the control socket.
    /// <para>从控制 socket 轮询一条设备 → 客户端消息（剪贴板、确认）。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. 单次读取超时（毫秒）。</param>
    /// <returns>The message, or <see langword="null"/> when none arrived. 消息；未到达时为 <see langword="null"/>。</returns>
    public ScrcpyDeviceMessage? TryReadDeviceMessage(int timeoutMs = 0)
    {
        var stream = _controlStream;
        if (stream is null)
        {
            return null;
        }

        var type = TryReadExact(stream, 1, timeoutMs);
        if (type is null || type.Length != 1)
        {
            return null;
        }

        switch (type[0])
        {
            case ScrcpyConstants.DeviceTypeClipboard:
            {
                var lengthBytes = ReadExact(stream, 4, timeoutMs);
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
                if (length is < 0 or > MaxClipboardLength)
                {
                    throw new InvalidDataException(
                        $"Clipboard length {length} exceeds the bound. 剪贴板长度 {length} 超出上限。");
                }

                var text = ReadExact(stream, length, timeoutMs);
                return new ScrcpyDeviceMessage(type[0], Encoding.UTF8.GetString(text));
            }

            case ScrcpyConstants.DeviceTypeAckClipboard:
            {
                var seq = ReadExact(stream, 8, timeoutMs);
                return new ScrcpyDeviceMessage(type[0], AckSequence: BinaryPrimitives.ReadUInt64BigEndian(seq));
            }

            default:
                throw new InvalidDataException($"Unknown device message type {type[0]}. 未知的设备消息类型 {type[0]}。");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _videoStream, null)?.Dispose();
        Interlocked.Exchange(ref _audioStream, null)?.Dispose();
        Interlocked.Exchange(ref _controlStream, null)?.Dispose();
    }

    private static byte[] ReadExact(Stream stream, int length, int timeoutMs)
    {
        if (!TryReadExact(stream, length, timeoutMs, out var result) || result!.Length != length)
        {
            throw new InvalidDataException(
                $"Stream ended after {result?.Length ?? 0}/{length} expected bytes. 流在读取 {result?.Length ?? 0}/{length} 字节后提前结束。");
        }

        return result!;
    }

    private static byte[]? TryReadExact(Stream stream, int length, int timeoutMs)
        => TryReadExact(stream, length, timeoutMs, out var result) ? result : null;

    private static bool TryReadExact(Stream stream, int length, int timeoutMs, out byte[]? result)
    {
        if (length == 0)
        {
            result = [];
            return true;
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            if (stream.CanTimeout) stream.ReadTimeout = timeoutMs == 0 ? 1 : timeoutMs;
            int count;
            try { count = stream.Read(buffer, offset, length - offset); }
            catch (TimeoutException) when (offset == 0) { result = null; return false; }
            catch (IOException ex) when (offset == 0 && ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut })
            { result = null; return false; }
            if (count == 0) throw new EndOfStreamException("scrcpy service closed during a packet read.");
            offset += count;
        }

        result = buffer;
        return true;
    }
}
