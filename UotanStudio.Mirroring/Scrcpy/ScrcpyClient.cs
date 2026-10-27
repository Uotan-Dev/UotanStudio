using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Mirroring;

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
    /// The ASCII text of the codec identifier with NUL and space padding removed, for example <c>h264</c>.
    /// <para>去除 NUL 与空格填充后的编码标识 ASCII 文本，例如 <c>h264</c>。</para>
    /// </value>
    public string CodecName
    {
        get
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, CodecFourCc);
            return Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        }
    }
}

/// <summary>
/// One demuxed video frame (or codec config packet) from the video socket.
/// <para>视频 socket 上解出的一个视频帧（或编码器配置包）。</para>
/// </summary>
/// <param name="Pts">
/// Raw presentation-timestamp word of the packet header: the timestamp in microseconds with the
/// session and codec-config flags in bits 63 and 62 and the key-frame flag in bit 61.
/// <para>包头部的原始时间戳字：微秒时间戳，第 63 位为会话标志、第 62 位为编码配置标志、第 61 位为关键帧标志。</para>
/// </param>
/// <param name="Data">
/// The compressed access-unit bytes; a pooled array can be longer than <see cref="PayloadLength"/>.
/// <para>压缩访问单元字节；池化数组可能长于 <see cref="PayloadLength"/>。</para>
/// </param>
/// <remarks>
/// Media payloads are rented from <see cref="ArrayPool{T}.Shared"/> and must be returned by calling
/// <see cref="Dispose"/> once the decoder is done with them. Codec configuration packets are exact
/// allocations because consumers retain them across frames.
/// <para>媒体负载从 <see cref="ArrayPool{T}.Shared"/> 租用，解码器用完这些字节后必须调用 <see cref="Dispose"/> 归还。
/// 编码配置包使用精确分配，因为消费方会在帧之间保留它们。</para>
/// </remarks>
public sealed record ScrcpyVideoFrame(long Pts, byte[] Data) : IDisposable
{
    private readonly int _payloadLength;
    private readonly bool _pooled;
    private int _disposed;

    internal ScrcpyVideoFrame(long pts, byte[] data, int payloadLength)
        : this(pts, data)
    {
        _payloadLength = payloadLength;
        _pooled = true;
    }

    /// <summary>
    /// Gets the number of valid bytes at the start of <see cref="Data"/>.
    /// <para>获取 <see cref="Data"/> 起始处有效字节的数量。</para>
    /// </summary>
    /// <value>
    /// The packet length announced by the header; never greater than <see cref="Data"/> length.
    /// <para>头部宣告的包长度；不会大于 <see cref="Data"/> 的长度。</para>
    /// </value>
    public int PayloadLength => _pooled ? _payloadLength : Data.Length;

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

    /// <summary>
    /// Returns a pooled payload to <see cref="ArrayPool{T}.Shared"/> after the decoder has consumed it.
    /// <para>解码器消费完池化负载后将其归还给 <see cref="ArrayPool{T}.Shared"/>。</para>
    /// </summary>
    /// <remarks>
    /// Frames created from an exact array are unaffected, and repeat calls are no-ops. The frame must not
    /// be used after disposal.
    /// <para>由精确数组创建的帧不受影响，重复调用无效果。释放后不得再使用该帧。</para>
    /// </remarks>
    public void Dispose()
    {
        if (_pooled && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ArrayPool<byte>.Shared.Return(Data);
        }
    }
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
    /// The ASCII codec name with NUL and space padding removed and upper-cased, for example <c>OPUS</c>.
    /// <para>去除 NUL 与空格填充并转为大写的 ASCII 编码名称，例如 <c>OPUS</c>。</para>
    /// </value>
    /// <remarks>
    /// Short codec names are packed rather than padded: the server sends <c>aac</c> as <c>0x00616163</c>
    /// and <c>raw</c> as <c>0x00726177</c>, so the filler byte leads instead of trails.
    /// <para>短编码名按打包而非填充发送：服务端把 <c>aac</c> 发成 <c>0x00616163</c>、把 <c>raw</c> 发成
    /// <c>0x00726177</c>，因此补位字节出现在开头而不是结尾。</para>
    /// </remarks>
    public string CodecName
    {
        get
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, CodecFourCc);
            return Encoding.ASCII.GetString(bytes).Trim('\0', ' ').ToUpperInvariant();
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
/// <param name="Data">
/// The compressed audio packet bytes; a pooled array can be longer than <see cref="PayloadLength"/>.
/// <para>压缩音频包字节；池化数组可能长于 <see cref="PayloadLength"/>。</para>
/// </param>
/// <remarks>
/// Media payloads are rented from <see cref="ArrayPool{T}.Shared"/> and must be returned by calling
/// <see cref="Dispose"/> once the decoder is done with them. Codec configuration packets are exact
/// allocations and are never returned to the pool.
/// <para>媒体负载从 <see cref="ArrayPool{T}.Shared"/> 租用，解码器用完这些字节后必须调用 <see cref="Dispose"/> 归还。
/// 编码配置包使用精确分配，永远不会归还到池中。</para>
/// </remarks>
public sealed record ScrcpyAudioFrame(long Pts, byte[] Data) : IDisposable
{
    private readonly int _payloadLength;
    private readonly bool _pooled;
    private int _disposed;

    internal ScrcpyAudioFrame(long pts, byte[] data, int payloadLength)
        : this(pts, data)
    {
        _payloadLength = payloadLength;
        _pooled = true;
    }

    /// <summary>
    /// Gets the number of valid bytes at the start of <see cref="Data"/>.
    /// <para>获取 <see cref="Data"/> 起始处有效字节的数量。</para>
    /// </summary>
    /// <value>
    /// The packet length announced by the header; never greater than <see cref="Data"/> length.
    /// <para>头部宣告的包长度；不会大于 <see cref="Data"/> 的长度。</para>
    /// </value>
    public int PayloadLength => _pooled ? _payloadLength : Data.Length;

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

    /// <summary>
    /// Returns a pooled payload to <see cref="ArrayPool{T}.Shared"/> after the decoder has consumed it.
    /// <para>解码器消费完池化负载后将其归还给 <see cref="ArrayPool{T}.Shared"/>。</para>
    /// </summary>
    /// <remarks>
    /// Frames created from an exact array are unaffected, and repeat calls are no-ops. The frame must not
    /// be used after disposal.
    /// <para>由精确数组创建的帧不受影响，重复调用无效果。释放后不得再使用该帧。</para>
    /// </remarks>
    public void Dispose()
    {
        if (_pooled && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ArrayPool<byte>.Shared.Return(Data);
        }
    }
}

/// <summary>
/// scrcpy client: connects the video / audio / control sockets over adbd
/// localabstract forwarding, demuxes framed video and exchanges control
/// messages.
/// <para>scrcpy 客户端：经 adbd localabstract 转发连接视频 / 音频 / 控制 socket，
/// 解出分帧视频并交换控制消息。</para>
/// </summary>
/// <remarks>
/// Sockets are opened in the server's expected order: video, (optional)
/// audio, control. The first socket carries the dummy byte, the 64-byte
/// device name, the 4-byte video codec id and the first 12-byte session
/// packet before the framed stream.
/// Every subsequent session packet on the video socket is consumed by
/// <see cref="ReadFrame"/> before the next media packet, so
/// <see cref="VideoMetadata"/> always describes the size the encoder is
/// actually producing; <see cref="SessionEpoch"/> counts those replacements
/// for consumers that must reconfigure a decoder.
/// All lengths read from the device are bounded: frame payloads are capped
/// at <see cref="MaxFrameLength"/>, clipboard text at 1 MiB; violations throw
/// rather than allocate unbounded buffers.
/// <para>socket 按服务端预期的顺序打开：视频、（可选）音频、控制。首个 socket 在分帧码流之前
/// 承载哑字节、64 字节设备名、4 字节视频编码标识与首个 12 字节会话包。
/// 视频 socket 上后续到达的会话包都由 <see cref="ReadFrame"/> 在下一个媒体包之前消费掉，
/// 因此 <see cref="VideoMetadata"/> 始终描述编码器实际产出的尺寸；<see cref="SessionEpoch"/>
/// 记录这些替换的次数，供需要重建解码器的使用方判断。
/// 从设备读取的所有长度均有界：帧负载上限 <see cref="MaxFrameLength"/>、剪贴板
/// 文本上限 1 MiB；越界即抛出异常，不进行无界分配。</para>
/// </remarks>
internal sealed class ScrcpyClient : IDisposable
{
    /// <summary>Upper bound for a single compressed video access unit, 32 MiB. <para>单个压缩视频访问单元的上限，32 MiB。</para></summary>
    internal const int MaxFrameLength = 32 * 1024 * 1024;

    /// <summary>Upper bound for a single compressed audio packet, 4 MiB. <para>单个压缩音频包的上限，4 MiB。</para></summary>
    internal const int MaxAudioFrameLength = 4 * 1024 * 1024;

    /// <summary>Upper bound for device clipboard text, 1 MiB. <para>设备剪贴板文本上限，1 MiB。</para></summary>
    internal const int MaxClipboardLength = 1024 * 1024;

    private readonly Func<string, Stream> _open;
    private readonly ScrcpyServerOptions _options;
    private readonly byte[] _videoFrameHeader = new byte[12];
    private readonly byte[] _audioFrameHeader = new byte[12];

    private Stream? _videoStream;
    private Stream? _audioStream;
    private Stream? _controlStream;

    /// <summary>Delay between video socket attempts while the server process boots.
    /// <para>服务端进程启动期间，视频 socket 重试之间的等待。</para></summary>
    private const int SocketRetryDelayMs = 50;

    /// <summary>Initializes the client over an established device session; call <see cref="Connect"/> once the server is running. The session owns the streams returned by <c>OpenServiceStream</c>.
    /// <para>通过已建立的设备会话初始化客户端；服务端启动后调用 <see cref="Connect"/>。由 <c>OpenServiceStream</c> 返回的流归该会话所有。</para></summary>
    /// <param name="session">An established ADB session used to open service streams.
    /// <para>用于打开服务流的已建立 ADB 会话。</para></param>
    /// <param name="options">The options the server was started with; controls which sockets and metadata are read.
    /// <para>服务端启动时使用的选项；决定读取哪些 socket 与元数据。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    internal ScrcpyClient(Comm.Contracts.IAdbDeviceSession session, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        _open = session.OpenServiceStream;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Initializes the client from an existing ADB server, opening each service stream as an owned stream.
    /// <para>从现有 ADB 服务端初始化客户端，将每个服务流作为自有流打开。</para></summary>
    /// <param name="server">The ADB server services used to open localabstract streams; the client disposes the streams it opens.
    /// <para>用于打开 localabstract 流的 ADB 服务；客户端负责释放其打开的流。</para></param>
    /// <param name="options">The options the server was started with; controls which sockets and metadata are read.
    /// <para>服务端启动时使用的选项；决定读取哪些 socket 与元数据。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="server"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    internal ScrcpyClient(AdbServerServices server, ScrcpyServerOptions options)
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
    /// Video stream metadata read from the first socket: the codec id from the stream header and the
    /// size from the most recent session packet.
    /// <para>从首个 socket 读取的视频流元数据：流头部的编码标识，以及最近一个会话包给出的尺寸。</para>
    /// </summary>
    /// <value>
    /// The parsed codec and session size, or <see langword="null"/> before <see cref="Connect"/> or when the
    /// server does not send stream metadata.
    /// <para>已解析的编码与会话尺寸；在 <see cref="Connect"/> 之前或服务端未发送流元数据时返回 <see langword="null"/>。</para>
    /// </value>
    public ScrcpyVideoMetadata? VideoMetadata { get; private set; }

    /// <summary>
    /// Count of session packets consumed since this client connected.
    /// <para>本次连接以来已消费的会话包数量。</para>
    /// </summary>
    /// <value>
    /// Starts at 0 and increases once per session packet, including the one read during <see cref="Connect"/>;
    /// a consumer that compares it before and after a read detects a size change without owning state.
    /// <para>从 0 开始，每个会话包加一，包含 <see cref="Connect"/> 期间读到的那个；使用方只需在读取前后比较该值
    /// 即可检测尺寸变化，无需自己保存状态。</para>
    /// </value>
    public int SessionEpoch { get; private set; }

    /// <summary>
    /// Whether the latest session packet reported a client-requested resize.
    /// <para>最近一个会话包是否由客户端请求的调整尺寸所致。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the server echoed the <c>isClientResize</c> flag on the latest session
    /// packet; the value is only meaningful once <see cref="SessionEpoch"/> is non-zero.
    /// <para>服务端在最近一个会话包上回显了 <c>isClientResize</c> 标志时为 <see langword="true"/>；仅在
    /// <see cref="SessionEpoch"/> 非零时有意义。</para>
    /// </value>
    public bool LastSessionChangeWasClientRequested { get; private set; }

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
    /// Connects the sockets in server order and reads the stream metadata. The video socket is retried
    /// until <paramref name="timeoutMs"/> elapses because the server creates its sockets only after the
    /// device runtime has booted.
    /// <para>按服务端要求的顺序连接 socket 并读取流元数据。视频 socket 会在 <paramref name="timeoutMs"/>
    /// 内持续重试，因为服务端要等设备运行时完成启动后才创建 socket。</para>
    /// </summary>
    /// <param name="timeoutMs">Budget for the initial video socket wait, and per-read timeout in
    /// milliseconds. <para>初始视频 socket 等待的总预算，同时作为单次读取超时（毫秒）。</para></param>
    /// <exception cref="InvalidDataException">Thrown when the handshake fails. <para>握手失败时抛出。</para></exception>
    public void Connect(int timeoutMs = 10000)
    {
        Dispose();
        DeviceName = string.Empty;
        VideoMetadata = null;
        AudioMetadata = null;
        AudioDisabledByServer = false;
        SessionEpoch = 0;
        LastSessionChangeWasClientRequested = false;
        try { ConnectCore(timeoutMs); }
        catch { Dispose(); throw; }
    }

    private void ConnectCore(int timeoutMs)
    {
        _videoStream = OpenVideoStream(timeoutMs);

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
            DeviceName = Encoding.UTF8.GetString(name.AsSpan().TrimEnd((byte)0));
        }

        if (_options.SendStreamMeta)
        {
            var codec = ReadStreamCodecId(_videoStream, timeoutMs, "video");
            var session = ReadExact(_videoStream, ScrcpyConstants.HeaderLength, timeoutMs);
            ApplySessionPacket(session, codec);
        }

        if (_audioStream is not null && _options.SendStreamMeta)
        {
            var codecId = ReadStreamCodecId(_audioStream, timeoutMs, "audio");
            if (codecId == ScrcpyConstants.StreamDisabledByServer)
            {
                AudioDisabledByServer = true;
                _audioStream.Dispose();
                _audioStream = null;
            }
            else AudioMetadata = new ScrcpyAudioMetadata(codecId);
        }
    }

    /// <summary>
    /// Reads the 4-byte codec identifier that opens a stream and rejects the two disable sentinels.
    /// <para>读取每条流开头的 4 字节编码标识，并拒绝两个禁用哨兵值。</para>
    /// </summary>
    /// <param name="stream">The socket carrying the stream header. <para>承载流头部的 socket。</para></param>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <param name="streamName">Stream name used in the failure text. <para>失败文本中使用的流名称。</para></param>
    /// <returns>The codec four-character code announced by the server.
    /// <para>服务端宣告的编码四字符码。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The server disabled this stream (identifier 0) or reported a device-side configuration error
    /// (identifier 1), and the stream cannot be consumed.
    /// <para>服务端禁用了该流（标识 0）或上报了设备侧配置错误（标识 1），该流无法消费。</para>
    /// </exception>
    private uint ReadStreamCodecId(Stream stream, int timeoutMs, string streamName)
    {
        var header = ReadExact(stream, ScrcpyConstants.StreamMetaLength, timeoutMs);
        var codecId = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (codecId == ScrcpyConstants.StreamDisabledByServer)
        {
            throw new InvalidDataException(
                $"The device disabled the {streamName} stream. 设备已禁用 {streamName} 流。");
        }

        if (codecId == ScrcpyConstants.StreamDisabledByError)
        {
            throw new InvalidDataException(
                $"The device reported a {streamName} stream configuration error. 设备上报了 {streamName} 流的配置错误。");
        }

        return codecId;
    }

    /// <summary>
    /// Applies one 12-byte session packet header (flags, width, height) to <see cref="VideoMetadata"/>.
    /// <para>把一个 12 字节会话包头（flags、宽、高）应用到 <see cref="VideoMetadata"/>。</para>
    /// </summary>
    /// <param name="header">The 12 header bytes just read from the video socket.
    /// <para>刚从视频 socket 读到的 12 个头部字节。</para></param>
    /// <param name="codec">Codec identifier to keep for the announced size.
    /// <para>与新尺寸一并保留的编码标识符。</para></param>
    /// <exception cref="InvalidDataException">
    /// The header does not carry the session flag, or the announced size is out of the supported range.
    /// <para>该头部未携带会话标志，或宣告的尺寸超出支持范围。</para>
    /// </exception>
    /// <remarks>
    /// The server sets only bit 31 of the flags integer (the session marker itself) and bit 0
    /// (<c>isClientResize</c>). Any other bit in that integer means the stream is being read out of step,
    /// so the packet is refused instead of silently re-annotating the video size.
    /// <para>服务端只会置上 flags 整数的第 31 位（会话标志本身）与第 0 位（<c>isClientResize</c>）。
    /// 该整数出现其他位就说明码流已经读错位，因此宁可拒绝该包，也不悄悄改写视频尺寸。</para>
    /// </remarks>
    private void ApplySessionPacket(ReadOnlySpan<byte> header, uint codec)
    {
        var flagsAndWidth = BinaryPrimitives.ReadUInt64BigEndian(header);
        if ((flagsAndWidth & ScrcpyConstants.PacketFlagSession) == 0)
        {
            throw new InvalidDataException(
                "Expected a session metadata packet before the first video frame. " +
                "首帧之前应收到会话元数据包。");
        }

        const ulong knownFlags = ScrcpyConstants.PacketFlagSession | ScrcpyConstants.SessionFlagClientResize;
        if ((flagsAndWidth & 0xFFFF_FFFF_0000_0000UL & ~knownFlags) != 0)
        {
            throw new InvalidDataException(
                "The session packet carries unexpected flag bits. 会话包携带了未预期的标志位。");
        }

        var width = unchecked((int)(flagsAndWidth & 0xFFFF_FFFF));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(8, 4));
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
        {
            throw new InvalidDataException(
                $"Server announced invalid video size {width}x{height}. 服务端宣告了无效的视频尺寸 {width}x{height}。");
        }

        VideoMetadata = new ScrcpyVideoMetadata(codec, width, height);
        SessionEpoch++;
        LastSessionChangeWasClientRequested = (flagsAndWidth & ScrcpyConstants.SessionFlagClientResize) != 0;
    }

    /// <summary>
    /// Opens the video socket and consumes the readiness byte, retrying until the launch budget runs
    /// out. The server publishes its abstract sockets only after the device runtime has started, so an
    /// early attempt is answered by the daemon with an accepted-then-closed stream rather than a refusal.
    /// <para>打开视频 socket 并消费就绪字节，在启动预算耗尽前持续重试。服务端要等设备运行时启动后才发布
    /// 其 abstract socket，因此过早的尝试会被守护进程以“先接受、再关闭”的流回应，而不是直接拒绝。</para>
    /// </summary>
    /// <param name="timeoutMs">Total wait budget in milliseconds. <para>总等待预算（毫秒）。</para></param>
    /// <returns>The ready video socket with the dummy byte already consumed.
    /// <para>已消费哑字节、可供读取的视频 socket。</para></returns>
    private Stream OpenVideoStream(int timeoutMs)
    {
        var endpoint = "localabstract:" + _options.SocketName;
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 1);
        while (true)
        {
            var remaining = (int)Math.Max(deadline - Environment.TickCount64, 1);
            Stream? stream = null;
            byte dummy = 0;
            try
            {
                stream = _open(endpoint);
                if (_options.SendDummyByte)
                {
                    dummy = ReadExact(stream, 1, remaining)[0];
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       && Environment.TickCount64 < deadline)
            {
                stream?.Dispose();
                Thread.Sleep(SocketRetryDelayMs);
                continue;
            }

            if (dummy != 0)
            {
                stream.Dispose();
                throw new InvalidDataException("Expected 0x00 dummy byte on the video socket. 视频 socket 上应收到 0x00 哑字节。");
            }

            return stream;
        }
    }

    /// <summary>
    /// Reads one framed video packet from the video socket, consuming any session packet that arrives
    /// first.
    /// <para>从视频 socket 读取一个分帧视频包，并先消费其间到达的会话包。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds. <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame owned by the caller, which must dispose it after decoding, or
    /// <see langword="null"/> when no header arrived before timeout.
    /// <para>由调用方持有并在解码后释放的帧；超时未收到头部时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The client is not connected. <para>客户端尚未连接。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the video stream.</exception>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs)
    {
        var stream = _videoStream;
        if (stream is null)
        {
            throw new InvalidOperationException("Client is not connected. 客户端尚未连接。");
        }

        while (true)
        {
            if (!TryReadExactInto(stream, _videoFrameHeader, timeoutMs))
            {
                return null;
            }

            var header = _videoFrameHeader;
            var flagsAndValue = BinaryPrimitives.ReadUInt64BigEndian(header);
            if ((flagsAndValue & ScrcpyConstants.PacketFlagSession) != 0)
            {
                ApplySessionPacket(header, VideoMetadata?.CodecFourCc ?? 0);
                continue;
            }

            var pts = unchecked((long)flagsAndValue);
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
            if (length is < 0 or > MaxFrameLength)
            {
                throw new InvalidDataException(
                    $"Frame length {length} exceeds the {MaxFrameLength}-byte bound. 帧长度 {length} 超出 {MaxFrameLength} 字节上限。");
            }

            var pooled = ((ulong)pts & ScrcpyConstants.PacketFlagConfig) == 0;
            var payload = pooled
                ? ArrayPool<byte>.Shared.Rent(length)
                : GC.AllocateUninitializedArray<byte>(length);
            try
            {
                if (!TryReadExactInto(stream, payload, length, timeoutMs))
                {
                    throw new InvalidDataException(
                        $"Stream ended after 0/{length} expected bytes. 流在读取 0/{length} 字节后提前结束。");
                }
            }
            catch
            {
                if (pooled)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
                throw;
            }

            return pooled
                ? new ScrcpyVideoFrame(pts, payload, length)
                : new ScrcpyVideoFrame(pts, payload);
        }
    }

    /// <summary>Reads one framed audio packet from the audio socket.
    /// <para>从音频 socket 读取一个分帧音频包。</para></summary>
    /// <remarks>Audio packets use the same 12-byte header as video (PTS + length). The payload bound is <see cref="MaxAudioFrameLength"/>; violations throw.
    /// <para>音频包与视频共用 12 字节头（PTS + 长度）。负载上限 <see cref="MaxAudioFrameLength"/>，越界即抛出。</para></remarks>
    /// <param name="timeoutMs">Per-read timeout in milliseconds.<para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame owned by the caller, which must dispose non-config packets after decoding, or
    /// <see langword="null"/> when no header arrived before timeout.
    /// <para>由调用方持有、非配置包须在解码后释放的帧；超时未收到头部时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">Thrown when audio is disabled or not connected.
    /// <para>音频未启用或未连接时抛出。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the audio stream.</exception>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs)
    {
        var stream = _audioStream;
        if (stream is null)
        {
            throw new InvalidOperationException(
                "Audio is not enabled or the client is not connected. 音频未启用或客户端尚未连接。");
        }

        if (!TryReadExactInto(stream, _audioFrameHeader, timeoutMs))
        {
            return null;
        }

        var header = _audioFrameHeader;
        var flagsAndValue = BinaryPrimitives.ReadUInt64BigEndian(header);
        if ((flagsAndValue & ScrcpyConstants.PacketFlagSession) != 0)
        {
            throw new InvalidDataException(
                "A session packet arrived on the audio socket. 音频 socket 上出现了会话包。");
        }

        var pts = unchecked((long)flagsAndValue);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (length is < 0 or > MaxAudioFrameLength)
        {
            throw new InvalidDataException(
                $"Audio frame length {length} exceeds the {MaxAudioFrameLength}-byte bound. 音频帧长度 {length} 超出 {MaxAudioFrameLength} 字节上限。");
        }

        var pooled = ((ulong)pts & ScrcpyConstants.PacketFlagConfig) == 0;
        var payload = pooled
            ? ArrayPool<byte>.Shared.Rent(length)
            : GC.AllocateUninitializedArray<byte>(length);
        try
        {
            if (!TryReadExactInto(stream, payload, length, timeoutMs))
            {
                throw new InvalidDataException(
                    $"Stream ended after 0/{length} expected bytes. 流在读取 0/{length} 字节后提前结束。");
            }
        }
        catch
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
            throw;
        }

        return pooled
            ? new ScrcpyAudioFrame(pts, payload, length)
            : new ScrcpyAudioFrame(pts, payload);
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
    /// <exception cref="InvalidDataException">The announced clipboard length exceeds the bound, or the type byte
    /// is not a known device message type.
    /// <para>宣告的剪贴板长度超出上限，或类型字节不属于已知的设备消息类型。</para></exception>
    /// <remarks>
    /// 5.x adds <c>TYPE_UHID_OUTPUT</c> (<see cref="ScrcpyConstants.DeviceTypeUhidOutput"/>), which the
    /// server only emits for a UHID device created by a prior <c>TYPE_UHID_CREATE</c>. This client never creates
    /// one, so that type is intentionally left unhandled and arriving at it is reported rather than skipped
    /// without knowing the payload length.
    /// <para>5.x 新增了 <c>TYPE_UHID_OUTPUT</c>（<see cref="ScrcpyConstants.DeviceTypeUhidOutput"/>），服务端只会
    /// 为先前由 <c>TYPE_UHID_CREATE</c> 创建的 UHID 设备发出它。本客户端从不创建 UHID 设备，因此该类型被有意保留为
    /// 不消费：真的收到时按异常上报，而不是在不知道负载长度的情况下跳过。</para>
    /// </remarks>
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

    private byte[] ReadExact(Stream stream, int length, int timeoutMs)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        if (!TryReadExactInto(stream, buffer, timeoutMs))
        {
            throw new InvalidDataException(
                $"Stream ended after 0/{length} expected bytes. 流在读取 0/{length} 字节后提前结束。");
        }

        return buffer;
    }

    private byte[]? TryReadExact(Stream stream, int length, int timeoutMs)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        return TryReadExactInto(stream, buffer, timeoutMs) ? buffer : null;
    }

    /// <summary>
    /// Fills the supplied buffer exactly once per call, so per-frame reads reuse the caller's header
    /// scratch instead of allocating.
    /// <para>每次调用精确填满给定缓冲区，使逐帧读取复用调用方的头部暂存而不是分配。</para>
    /// </summary>
    private bool TryReadExactInto(Stream stream, byte[] buffer, int timeoutMs)
        => TryReadExactInto(stream, buffer, buffer.Length, timeoutMs);

    /// <summary>
    /// Fills the first <paramref name="length"/> bytes of the supplied buffer exactly once per call, so a
    /// pooled payload is read only up to its announced length.
    /// <para>每次调用精确填满给定缓冲区的前 <paramref name="length"/> 字节，使池化负载只按宣告长度读取。</para>
    /// </summary>
    private bool TryReadExactInto(Stream stream, byte[] buffer, int length, int timeoutMs)
    {
        if (length == 0)
        {
            return true;
        }

        var offset = 0;
        if (stream.CanTimeout) stream.ReadTimeout = timeoutMs == 0 ? 1 : timeoutMs;
        while (offset < length)
        {
            int count;
            try { count = stream.Read(buffer, offset, length - offset); }
            catch (TimeoutException) when (offset == 0) { return false; }
            catch (IOException ex) when (offset == 0 && ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut })
            { return false; }
            if (count == 0) throw new EndOfStreamException("scrcpy service closed during a packet read.");
            offset += count;
        }

        return true;
    }
}