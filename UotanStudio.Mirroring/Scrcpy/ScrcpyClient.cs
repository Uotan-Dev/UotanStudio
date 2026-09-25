using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Services.Mirroring;

/// <summary>
/// Decoded video stream metadata announced by the server on the first socket.
/// <para>服务端在首个 socket 上宣告的视频流元数据。</para>
/// </summary>
/// <param name="CodecFourCc">Codec four-character code (e.g. 'H264'). <para>编码四字符码（如 'H264'）。</para></param>
/// <param name="Width">Video width in pixels. <para>视频宽度（像素）。</para></param>
/// <param name="Height">Video height in pixels. <para>视频高度（像素）。</para></param>
public sealed record ScrcpyVideoMetadata(uint CodecFourCc, int Width, int Height)
{
    /// <summary>
    /// Codec name decoded from the big-endian four-character code.
    /// <para>由大端序四字符码解码得到的编码名称。</para>
    /// </summary>
    /// <value>
    /// The ASCII text of the four-character code, for example <c>H264</c>.
    /// <para>四字符码的 ASCII 文本，例如 <c>H264</c>。</para>
    /// </value>
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
/// <para>显示时间戳；<see cref="ScrcpyConstants.NoPts"/> 表示编码器配置包。</para>
/// </param>
/// <param name="Data">The compressed access-unit bytes. <para>压缩访问单元字节。</para></param>
public sealed record ScrcpyVideoFrame(long Pts, byte[] Data)
{
    /// <summary>
    /// Whether the packet's PTS word carries the codec-config flag.
    /// <para>该包的 PTS 字是否携带编码器配置标志。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when this packet is a codec configuration packet (SPS/PPS).
    /// <para>当该包为编码器配置包（SPS/PPS）时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsCodecConfig => ((ulong)Pts & ScrcpyConstants.PacketFlagConfig) != 0;

    /// <summary>
    /// Whether the key-frame flag is set, independent of the timestamp bits.
    /// <para>关键帧标志是否置位，与时间戳位无关。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> for a non-config key frame.
    /// <para>对于非配置的关键帧返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsKeyFrame => !IsCodecConfig && ((ulong)Pts & ScrcpyConstants.PacketFlagKeyFrame) != 0;

    /// <summary>
    /// Media presentation timestamp with the config and key-frame flags masked out.
    /// <para>已屏蔽配置与关键帧标志的媒体显示时间戳。</para>
    /// </summary>
    /// <value>
    /// The timestamp in microseconds. <para>以微秒计的时间戳。</para>
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// The packet is a codec configuration packet, which has no timestamp.
    /// <para>该包为编码器配置包，没有时间戳。</para>
    /// </exception>
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
internal sealed record ScrcpyDeviceMessage(byte Type, string? Clipboard = null, ulong AckSequence = 0);

/// <summary>
/// Audio stream metadata announced by the server on the audio socket.
/// <para>服务端在音频 socket 上宣告的音频流元数据。</para>
/// </summary>
/// <param name="CodecFourCc">Codec four-character code ("opus", "aac", "flac"). <para>编码四字符码（"opus"、"aac"、"flac"）。</para></param>
public sealed record ScrcpyAudioMetadata(uint CodecFourCc)
{
    /// <summary>
    /// Uppercase codec name decoded from the big-endian four-character code.
    /// <para>由大端序四字符码解码得到的大写编码名称。</para>
    /// </summary>
    /// <value>
    /// The ASCII codec name with trailing NUL and space characters removed and upper-cased, for example <c>OPUS</c>.
    /// <para>去除末尾 NUL 与空格并转为大写的 ASCII 编码名称，例如 <c>OPUS</c>。</para>
    /// </value>
    public string CodecName
    {
        get
        {
            var bytes = BitConverter.GetBytes(CodecFourCc);
            Array.Reverse(bytes);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ').ToUpperInvariant();
        }
    }
}

/// <summary>
/// One demuxed audio packet from the audio socket.
/// <para>音频 socket 上解出的一个音频包。</para>
/// </summary>
/// <param name="Pts">
/// Presentation timestamp; the config flag marks the codec config packet.
/// <para>显示时间戳；配置标志表示编码器配置包。</para>
/// </param>
/// <param name="Data">The compressed audio packet bytes. <para>压缩音频包字节。</para></param>
public sealed record ScrcpyAudioFrame(long Pts, byte[] Data)
{
    /// <summary>
    /// Whether the packet's PTS word carries the codec-config flag.
    /// <para>该包的 PTS 字是否携带编码器配置标志。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when this packet is a codec configuration packet.
    /// <para>当该包为编码器配置包时返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsCodecConfig => ((ulong)Pts & ScrcpyConstants.PacketFlagConfig) != 0;

    /// <summary>
    /// Media presentation timestamp with the flag bits masked out.
    /// <para>已屏蔽标志位的媒体显示时间戳。</para>
    /// </summary>
    /// <value>
    /// The timestamp in microseconds. <para>以微秒计的时间戳。</para>
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// The packet is a codec configuration packet, which has no timestamp.
    /// <para>该包为编码器配置包，没有时间戳。</para>
    /// </exception>
    public long PresentationTimestampMicroseconds => !IsCodecConfig
        ? (long)((ulong)Pts & ScrcpyConstants.PacketTimestampMask)
        : throw new InvalidOperationException("Codec configuration has no presentation timestamp.");
}

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
    /// <summary>Upper bound for a single compressed video access unit, 32 MiB. <para>单个压缩视频访问单元的上限，32 MiB。</para></summary>
    public const int MaxFrameLength = 32 * 1024 * 1024;

    /// <summary>Upper bound for a single compressed audio packet, 4 MiB. <para>单个压缩音频包的上限，4 MiB。</para></summary>
    public const int MaxAudioFrameLength = 4 * 1024 * 1024;

    /// <summary>Upper bound for device clipboard text, 1 MiB. <para>设备剪贴板文本上限，1 MiB。</para></summary>
    public const int MaxClipboardLength = 1024 * 1024;

    private readonly Func<string, Stream> _open;
    private readonly ScrcpyServerOptions _options;

    private Stream? _videoStream;
    private Stream? _audioStream;
    private Stream? _controlStream;

    /// <summary>
    /// Initializes the client over an established device session; call <see cref="Connect"/> once the server is running. The session owns the streams returned by <c>OpenServiceStream</c>.
    /// <para>通过已建立的设备会话初始化客户端；服务端启动后调用 <see cref="Connect"/>。由 <c>OpenServiceStream</c> 返回的流归该会话所有。</para>
    /// </summary>
    /// <param name="session">
    /// An established ADB session used to open service streams.
    /// <para>用于打开服务流的已建立 ADB 会话。</para>
    /// </param>
    /// <param name="options">
    /// The options the server was started with; controls which sockets and metadata are read.
    /// <para>服务端启动时使用的选项；决定读取哪些 socket 与元数据。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public ScrcpyClient(UotanStudio.Comm.Contracts.IAdbDeviceSession session, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        _open = service => session.OpenServiceStream(service);
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Initializes the client from an existing ADB server, opening each service stream as an owned stream.
    /// <para>从现有 ADB 服务端初始化客户端，将每个服务流作为自有流打开。</para>
    /// </summary>
    /// <param name="server">
    /// The ADB server services used to open localabstract streams; the client disposes the streams it opens.
    /// <para>用于打开 localabstract 流的 ADB 服务；客户端负责释放其打开的流。</para>
    /// </param>
    /// <param name="options">
    /// The options the server was started with; controls which sockets and metadata are read.
    /// <para>服务端启动时使用的选项；决定读取哪些 socket 与元数据。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="server"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="server"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
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

    /// <summary>
    /// Device name read from the first socket during <see cref="Connect"/>, with trailing NUL characters trimmed.
    /// <para>在 <see cref="Connect"/> 期间从首个 socket 读取的设备名，已去除尾部 NUL 字符。</para>
    /// </summary>
    /// <value>
    /// The announced device name, or <see cref="string.Empty"/> before a successful connect or when the device name is not sent.
    /// <para>宣告的设备名；在成功连接之前或未发送设备名时返回 <see cref="string.Empty"/>。</para>
    /// </value>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>
    /// Video stream metadata read from the first socket during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间从首个 socket 读取的视频流元数据。</para>
    /// </summary>
    /// <value>
    /// The parsed codec/width/height header, or <see langword="null"/> before <see cref="Connect"/> or when the server does not send codec metadata.
    /// <para>已解析的编码/宽/高头部；在 <see cref="Connect"/> 之前或服务端未发送编码元数据时返回 <see langword="null"/>。</para>
    /// </value>
    public ScrcpyVideoMetadata? VideoMetadata { get; private set; }

    /// <summary>
    /// Audio codec metadata read from the audio socket during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间从音频 socket 读取的音频编码元数据。</para>
    /// </summary>
    /// <value>
    /// The four-character codec header, or <see langword="null"/> when audio is not requested, metadata is not sent, or the server disabled audio.
    /// <para>四字符编码头部；在未请求音频、未发送元数据或服务端已禁用音频时返回 <see langword="null"/>。</para>
    /// </value>
    public ScrcpyAudioMetadata? AudioMetadata { get; private set; }

    /// <summary>
    /// Whether the server explicitly disabled audio by sending a codec identifier of <c>0</c> or <c>1</c>.
    /// <para>服务端是否通过发送值为 <c>0</c> 或 <c>1</c> 的编码标识显式禁用音频。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when audio was disabled by the server; the audio stream is then closed and not read.
    /// <para>服务端禁用音频时为 <see langword="true"/>；此时音频流已关闭且不会被读取。</para>
    /// </value>
    public bool AudioDisabledByServer { get; private set; }

    /// <summary>
    /// Whether the control socket has been opened and not yet disposed.
    /// <para>控制 socket 已打开且尚未释放。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when a control stream is held; otherwise <see langword="false"/>.
    /// <para>持有控制流时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para>
    /// </value>
    public bool IsControlConnected => _controlStream is not null;

    /// <summary>
    /// Connects the sockets in server order and reads the stream metadata.
    /// <para>按服务端要求的顺序连接 socket 并读取流元数据。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <exception cref="InvalidDataException">Thrown when the handshake fails. <para>握手失败时抛出。</para></exception>
    public void Connect(int timeoutMs = 10000)
    {
        Dispose();
        DeviceName = string.Empty;
        VideoMetadata = null;
        AudioMetadata = null;
        AudioDisabledByServer = false;
        try { ConnectCore(timeoutMs); }
        catch { Dispose(); throw; }
    }

    private void ConnectCore(int timeoutMs)
    {
        _videoStream = _open("localabstract:" + _options.SocketName);

        if (_options.SendDummyByte)
        {
            var dummy = ReadExact(_videoStream, 1, timeoutMs);
            if (dummy.Length != 1 || dummy[0] != 0)
            {
                throw new InvalidDataException("Expected 0x00 dummy byte on the video socket. 视频 socket 上应收到 0x00 哑字节。");
            }
        }

        if (_options.Audio)
        {
            _audioStream = _open("localabstract:" + _options.SocketName);

        }

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

        if (_audioStream is not null && _options.SendCodecMeta)
        {
            var audioCodec = ReadExact(_audioStream, 4, timeoutMs);
            uint codecId = BinaryPrimitives.ReadUInt32BigEndian(audioCodec);
            if (codecId <= 1)
            {
                AudioDisabledByServer = true;
                _audioStream.Dispose();
                _audioStream = null;
            }
            else AudioMetadata = new ScrcpyAudioMetadata(codecId);
        }
    }

    /// <summary>
    /// Reads one framed video packet from the video socket.
    /// <para>从视频 socket 读取一个分帧视频包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
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
            return null;
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
    /// Reads one framed audio packet from the audio socket.
    /// <para>从音频 socket 读取一个分帧音频包。</para>
    /// </summary>
    /// <remarks>
    /// Audio packets use the same 12-byte header as video (PTS + length).
    /// The payload bound is <see cref="MaxAudioFrameLength"/>; violations throw.
    /// <para>音频包与视频共用 12 字节头（PTS + 长度）。负载上限 <see cref="MaxAudioFrameLength"/>，越界即抛出。</para>
    /// </remarks>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame, or <see langword="null"/> when no header arrived before timeout.</returns>
    /// <exception cref="InvalidOperationException">Thrown when audio is disabled or not connected. <para>音频未启用或未连接时抛出。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the audio stream.</exception>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs)
    {
        var stream = _audioStream;
        if (stream is null)
        {
            throw new InvalidOperationException(
                "Audio is not enabled or the client is not connected. 音频未启用或客户端尚未连接。");
        }

        var header = TryReadExact(stream, 12, timeoutMs);
        if (header is null || header.Length != 12)
        {
            return null;
        }

        var pts = BinaryPrimitives.ReadInt64BigEndian(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (length is < 0 or > MaxAudioFrameLength)
        {
            throw new InvalidDataException(
                $"Audio frame length {length} exceeds the {MaxAudioFrameLength}-byte bound. 音频帧长度 {length} 超出 {MaxAudioFrameLength} 字节上限。");
        }

        var payload = ReadExact(stream, length, timeoutMs);
        return new ScrcpyAudioFrame(pts, payload);
    }

    /// <summary>
    /// Sends one control message to the device.
    /// <para>向设备发送一条控制消息。</para>
    /// </summary>
    /// <param name="message">The encoded message from <see cref="ScrcpyControlMessageWriter"/>. <para><see cref="ScrcpyControlMessageWriter"/> 编码的消息。</para></param>
    /// <exception cref="InvalidOperationException">Thrown when control is disabled. <para>未启用控制时抛出。</para></exception>
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
    internal ScrcpyDeviceMessage? TryReadDeviceMessage(int timeoutMs = 0)
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

    /// <summary>
    /// Closes any video, audio or control stream the client currently holds; safe to call more than once.
    /// <para>关闭客户端当前持有的视频、音频或控制流；可安全地多次调用。</para>
    /// </summary>
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