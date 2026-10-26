using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UotanStudio.Mirroring.AirPlay.Mdns;

/// <summary>
/// Answers mDNS browsing queries for the advertised AirPlay and RAOP services without requiring Bonjour:
/// one multicast listener on port 5353, a periodic unsolicited announcement and a bounded reply whenever a
/// matching question arrives.
/// <para>在没有 Bonjour 的情况下应答针对所广播 AirPlay 与 RAOP 服务的 mDNS 浏览查询：在 5353 端口上的单播组播
/// 监听、周期性主动广播，以及匹配问题到达时的有界应答。</para>
/// </summary>
/// <remarks>
/// <para>
/// The receiver is the passive side of the connection, so this advertiser only describes local endpoints; it
/// never resolves peer addresses. Replies are multicast to the standard group because iOS clients accept them
/// either way, and duplicate announcements are suppressed so a burst of queries cannot be amplified.
/// </para>
/// <para>
/// 接收端是连接的被动方，因此本广播器只描述本机端点，从不解析对端地址。应答组播到标准组，因为 iOS 客户端两种
/// 形式都接受；重复广播会被抑制，避免查询突增被放大。
/// </para>
/// </remarks>
internal sealed class AirPlayMdnsAdvertiser : IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResponseInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _instanceName;
    private readonly string _deviceId;
    private readonly int _port;
    private readonly string _publicKey;
    private readonly Action<string>? _logSink;
    private readonly Lock _sendLock = new();
    private Socket? _socket;
    private Thread? _thread;
    private int _disposed;
    private long _lastResponseTicks;

    /// <summary>Creates an advertiser for one AirPlay endpoint. <para>为一个 AirPlay 端点创建广播器。</para></summary>
    /// <param name="instanceName">Advertised instance name. <para>广播的实例名。</para></param>
    /// <param name="deviceId">Colon-separated lowercase device identifier. <para>冒号分隔的小写设备标识。</para></param>
    /// <param name="port">RTSP port the client should connect to. <para>客户端应连接的 RTSP 端口。</para></param>
    /// <param name="publicKey">Base64 Ed25519 host public key announced as <c>pk</c>, or an empty string.
    /// <para>作为 <c>pk</c> 广播的 Base64 Ed25519 主机公钥，或空字符串。</para></param>
    /// <param name="logSink">Optional diagnostic sink; may be called from the listener thread.
    /// <para>可选的诊断接收端；可能由监听线程调用。</para></param>
    public AirPlayMdnsAdvertiser(string instanceName, string deviceId, int port, string publicKey,
        Action<string>? logSink)
    {
        _instanceName = instanceName;
        _deviceId = deviceId;
        _port = port;
        _publicKey = publicKey;
        _logSink = logSink;
        HostName = FormatHostName(deviceId);
    }

    /// <summary>Host name announced in SRV records. <para>在 SRV 记录中广播的主机名。</para></summary>
    public string HostName { get; }

    /// <summary>Starts the listener, joins the multicast group and sends the first announcement.
    /// <para>启动监听、加入组播组并发送首次广播。</para>
    /// </summary>
    /// <exception cref="SocketException">Port 5353 is already bound by another process without address reuse.
    /// <para>5353 端口已被其他进程占用且不允许地址复用。</para></exception>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(MulticastAddress));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.ReceiveTimeout = 1000;
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _thread = new Thread(Run) { IsBackground = true, Name = "airplay-mdns" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            Announce();
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            _logSink?.Invoke("AirPlay mDNS announcement failed: " + error.Message);
        }

        var lastAnnounce = Environment.TickCount64;
        var buffer = new byte[MdnsMessage.MaximumPacketBytes];
        while (Volatile.Read(ref _disposed) == 0)
        {
            var socket = _socket;
            if (socket is null)
            {
                return;
            }

            int count;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                count = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
            {
                count = 0;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return;
            }

            if (count > 0 && IsRelevantQuery(buffer.AsSpan(0, count)))
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastResponseTicks) >= (long)ResponseInterval.TotalMilliseconds)
                {
                    Interlocked.Exchange(ref _lastResponseTicks, now);
                    try
                    {
                        Announce();
                    }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            }

            var elapsed = Environment.TickCount64 - lastAnnounce;
            if (elapsed >= (long)AnnounceInterval.TotalMilliseconds)
            {
                lastAnnounce = Environment.TickCount64;
                try
                {
                    Announce();
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private bool IsRelevantQuery(ReadOnlySpan<byte> payload)
    {
        MdnsMessage message;
        try
        {
            message = MdnsMessage.Parse(payload);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if ((message.Flags & 0x8000) != 0)
        {
            return false;
        }

        var instance = InstanceName(AirPlayConstants.AirPlayServiceType);
        var raopInstance = InstanceName(AirPlayConstants.RaopServiceType);
        foreach (var question in message.Questions)
        {
            if (question.Type is not (MdnsMessage.TypePtr or MdnsMessage.TypeTxt or MdnsMessage.TypeSrv
                or MdnsMessage.TypeA or MdnsMessage.TypeAny))
            {
                continue;
            }

            var name = question.Name.TrimEnd('.');
            if (string.Equals(name, AirPlayConstants.AirPlayServiceType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, AirPlayConstants.RaopServiceType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, instance, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, raopInstance, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HostName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Announce()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var packet = MdnsMessage.BuildResponse(BuildRecords());
        lock (_sendLock)
        {
            socket.SendTo(packet, new IPEndPoint(MulticastAddress, MdnsPort));
        }
    }

    /// <summary>Builds the answer records describing the receiver. <para>构建描述接收端的应答记录。</para></summary>
    /// <returns>The announcement records in transmission order. <para>按发送顺序排列的广播记录。</para></returns>
    internal IReadOnlyList<MdnsRecord> BuildRecords()
    {
        var airPlayInstance = InstanceName(AirPlayConstants.AirPlayServiceType);
        var raopInstance = InstanceName(AirPlayConstants.RaopServiceType);
        var host = HostName;
        return
        [
            MdnsRecord.Pointer(AirPlayConstants.AirPlayServiceType, airPlayInstance),
            MdnsRecord.ServiceRecord(airPlayInstance, host, _port),
            MdnsRecord.TextRecord(airPlayInstance, BuildAirPlayText()),
            MdnsRecord.Pointer(AirPlayConstants.RaopServiceType, raopInstance),
            MdnsRecord.ServiceRecord(raopInstance, host, _port),
            MdnsRecord.TextRecord(raopInstance, BuildRaopText()),
        ];
    }

    private List<string> BuildAirPlayText()
    {
        var values = new List<string>(12)
        {
            "deviceid=" + _deviceId,
            "features=0x" + AirPlayConstants.Features.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            "flags=0x4",
            "model=" + AirPlayConstants.Model,
            "protovers=" + AirPlayConstants.ProtocolVersion,
            "srcvers=" + AirPlayConstants.SourceVersion,
            "vv=2",
        };

        if (_publicKey.Length > 0)
        {
            values.Add("pk=" + _publicKey);
        }

        return values;
    }

    private List<string> BuildRaopText()
    {
        var values = new List<string>(16)
        {
            "ch=2",
            "cn=0,1,2,3",
            "da=true",
            "et=0,3,5",
            "md=0,2",
            "am=" + AirPlayConstants.Model,
            "ft=0x" + AirPlayConstants.Features.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            "sf=0x4",
            "sr=44100",
            "ss=16",
            "sv=false",
            "tp=UDP",
            "txtvers=1",
            "vs=" + AirPlayConstants.SourceVersion,
            "vn=65537",
            "vv=2",
        };
        return values;
    }

    /// <summary>Builds the fully qualified instance name for one service type.
    /// <para>为单个服务类型构建完全限定实例名。</para>
    /// </summary>
    /// <param name="serviceType">Service type such as <see cref="AirPlayConstants.AirPlayServiceType"/>.
    /// <para>服务类型，例如 <see cref="AirPlayConstants.AirPlayServiceType"/>。</para></param>
    /// <returns>The instance name with a trailing dot. <para>带结尾点的实例名。</para></returns>
    internal string InstanceName(string serviceType)
    {
        var compact = _deviceId.Replace(":", string.Empty, StringComparison.Ordinal);
        return serviceType == AirPlayConstants.RaopServiceType
            ? compact + "@" + _instanceName + "." + serviceType + "."
            : _instanceName + "." + serviceType + ".";
    }

    /// <summary>Resolves the host IPv4 address and hardware identifier used in announcements.
    /// <para>解析广播中使用的主机 IPv4 地址与硬件标识。</para>
    /// </summary>
    /// <returns>The first operational non-loopbackIPv4 address and its six-byte address as a device identifier;
    /// a random identifier is used when no adapter reports a usable address.
    /// <para>第一个处于工作状态的非回环 IPv4 地址，以及作为设备标识的六字节硬件地址；没有网卡报告可用地址时
    /// 使用随机标识。</para></returns>
    public static (IPAddress Address, string DeviceId) ResolveLocalEndpoint()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var properties = adapter.GetIPProperties();
            var address = properties.UnicastAddresses
                .Select(entry => entry.Address)
                .FirstOrDefault(entry => entry.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                continue;
            }

            var bytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length != 6)
            {
                continue;
            }

            return (address, AirPlayOptions.FormatDeviceId(bytes));
        }

        Span<byte> random = stackalloc byte[6];
        Random.Shared.NextBytes(random);
        random[0] = (byte)((random[0] & 0xFE) | 0x02);
        return (IPAddress.Loopback, AirPlayOptions.FormatDeviceId(random));
    }

    /// <summary>Formats the host label announced in SRV records. <para>格式化 SRV 记录中广播的主机标签。</para></summary>
    /// <param name="deviceId">Colon-separated device identifier. <para>冒号分隔的设备标识。</para></param>
    /// <returns>A unique single-label name ending in <c>.local</c>. <para>以 <c>.local</c> 结尾的唯一单标签名称。</para></returns>
    public static string FormatHostName(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var compact = deviceId.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return "uotan-" + (compact.Length > 8 ? compact[^8..] : compact) + ".local";
    }

    /// <summary>Stops the listener and releases the multicast socket. <para>停止监听并释放组播 socket。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _socket?.Dispose();
        }
        finally
        {
            _socket = null;
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }
    }
}