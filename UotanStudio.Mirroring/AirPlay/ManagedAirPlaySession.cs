namespace UotanStudio.Mirroring.AirPlay;

/// <summary>
/// Owns one AirPlay receiving endpoint while presentation consumes the mirrored packets. The surface mirrors
/// <see cref="ManagedMirrorSession"/> so the host keeps one session shape, but the direction is reversed: the
/// device connects to this endpoint, and there is no reverse control channel.
/// <para>持有 AirPlay 接收端点直至呈现层消费镜像包。其表面与 <see cref="ManagedMirrorSession"/> 对齐，使宿主只需
/// 一种会话形状；但方向相反：设备连接到本端点，且没有反向控制通道。</para>
/// </summary>
public sealed class ManagedAirPlaySession : IDisposable
{
    private readonly AirPlayReceiverClient _client;
    private int _disposed;

    /// <summary>Wraps a configured receiver. <para>包装已配置的接收端。</para></summary>
    /// <param name="client">The receiver this session drives. <para>本会话驱动的接收端。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.
    /// <para><paramref name="client"/> 为 <see langword="null"/>。</para></exception>
    internal ManagedAirPlaySession(AirPlayReceiverClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Name of the receiving endpoint. <para>接收端点的名称。</para></summary>
    /// <value>The configured instance name; the peer device name is not known before it connects.
    /// <para>配置的实例名；对端设备名在连接之前不可知。</para></value>
    public string DeviceName => _client.DeviceName;

    /// <summary>RTSP port the endpoint bound, or zero before <see cref="Connect"/>.
    /// <para>端点绑定的 RTSP 端口；<see cref="Connect"/> 之前为零。</para></summary>
    public int RtspPort => _client.RtspPort;

    /// <summary>Video metadata parsed from the mirrored SPS. <para>从镜像 SPS 解析的视频元数据。</para></summary>
    /// <value>The H.264 geometry, or <see langword="null"/> before the first stream configuration.
    /// <para>H.264 几何；首个流配置之前为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => _client.VideoMetadata;

    /// <summary>Audio metadata of the mirrored AAC-ELD stream. <para>镜像 AAC-ELD 流的音频元数据。</para></summary>
    /// <value>Always the AAC-ELD description at 44100 Hz, two channels, 480 samples per frame.
    /// <para>始终为 44100 Hz、双声道、每帧 480 采样的 AAC-ELD 描述。</para></value>
    public ScrcpyAudioMetadata? AudioMetadata => _client.AudioMetadata;

    /// <summary>Whether control messages can be written; always <see langword="false"/> because AirPlay
    /// mirroring is read-only here. <para>是否可写入控制消息；此处 AirPlay 镜像只读，因此恒为 <see langword="false"/>。</para></summary>
    /// <value><see langword="false"/> in every state. <para>任何状态下都为 <see langword="false"/>。</para></value>
    public bool IsControlConnected => false;

    /// <summary>Binds the endpoint and starts waiting for a device. <para>绑定端点并开始等待设备。</para></summary>
    /// <exception cref="ObjectDisposedException">The session was already disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="System.Net.Sockets.SocketException">The RTSP or mDNS port cannot be bound.
    /// <para>RTSP 或 mDNS 端口无法绑定。</para></exception>
    public void Connect() => _client.Start();

    /// <summary>Dequeues one mirrored video frame. <para>取出一个镜像视频帧。</para></summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds. <para>最长等待时间（毫秒）。</para></param>
    /// <returns>The frame the caller must dispose, or <see langword="null"/> on timeout.
    /// <para>由调用方释放的帧；超时时为 <see langword="null"/>。</para></returns>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => _client.ReadFrame(timeoutMs);

    /// <summary>Dequeues one mirrored audio packet. <para>取出一个镜像音频包。</para></summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds. <para>最长等待时间（毫秒）。</para></param>
    /// <returns>The packet the caller must dispose, or <see langword="null"/> on timeout.
    /// <para>由调用方释放的包；超时时为 <see langword="null"/>。</para></returns>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs) => _client.ReadAudioFrame(timeoutMs);

    /// <summary>Stops the endpoint and releases its sockets. <para>停止端点并释放其 socket。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.Dispose();
    }
}