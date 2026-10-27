namespace UotanStudio.Mirroring.AirPlay;

/// <summary>
/// Describes one AirPlay receiving endpoint: the advertised instance name, the RTSP port to listen on, the
/// mDNS announcement switch and the optional FairPlay key material.
/// <para>描述一个 AirPlay 接收端：广播的实例名、监听的 RTSP 端口、mDNS 广播开关与可选的 FairPlay 密钥材料。</para>
/// </summary>
/// <remarks>
/// The receiver is passive: it opens a listening socket and waits for the device to connect, so nothing here
/// identifies a device. Mirrored audio and video are delivered as <see cref="ScrcpyVideoFrame"/> and
/// <see cref="ScrcpyAudioFrame"/> packets for the existing decode and presentation pipelines; the receiver
/// offers no reverse control channel.
/// <para>接收端是被动的：它打开监听 socket 并等待设备连接，因此这里的任何字段都不标识某台设备。镜像音频与视频以
/// <see cref="ScrcpyVideoFrame"/> 与 <see cref="ScrcpyAudioFrame"/> 包交给现有的解码与呈现管线；
/// 接收端不提供反向控制通道。</para>
/// </remarks>
public sealed record AirPlayOptions
{
    /// <summary>Instance name announced over mDNS and reported as the session device name.
    /// <para>经 mDNS 广播、并作为会话设备名报告的实例名。</para></summary>
    /// <value>One to 63 characters without control characters; defaults to <c>UotanStudio</c>.
    /// <para>1 到 63 个不含控制字符的字符；默认 <c>UotanStudio</c>。</para></value>
    public string Name { get; init; } = "UotanStudio";

    /// <summary>TCP port the RTSP server binds; <see cref="AirPlayConstants.DefaultRtspPort"/> is what iOS expects.
    /// <para>RTSP 服务器绑定的 TCP 端口；iOS 期望的是 <see cref="AirPlayConstants.DefaultRtspPort"/>。</para></summary>
    /// <value>Zero binds the next free port; the bound port is readable after <see cref="ManagedAirPlaySession.Connect"/>.
    /// <para>为零时绑定下一个空闲端口；绑定后的端口可在 <see cref="ManagedAirPlaySession.Connect"/> 之后读取。</para></value>
    public int RtspPort { get; init; } = AirPlayConstants.DefaultRtspPort;

    /// <summary>Whether the receiver answers mDNS browsing queries for its services.
    /// <para>接收端是否应答针对其服务的 mDNS 浏览查询。</para></summary>
    public bool Advertise { get; init; } = true;

    /// <summary>Device identifier announced as the mDNS <c>deviceid</c>; empty derives it from the host network adapter.
    /// <para>作为 mDNS <c>deviceid</c> 广播的设备标识；为空时从主机网卡推导。</para></summary>
    /// <value>Six colon-separated lowercase hexadecimal groups, or <see cref="string.Empty"/>.
    /// <para>六组以冒号分隔的小写十六进制，或 <see cref="string.Empty"/>。</para></value>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// Path to the FairPlay key material used to recover the AES key from the stream <c>ekey</c>. The receiver
    /// ships no key table, so encrypted mirroring stays unavailable until the operator supplies one.
    /// <para>用于从流 <c>ekey</c> 还原 AES 密钥的 FairPlay 密钥材料路径。接收端不随包提供密钥表，因此在操作者提供
    /// 之前，加密投屏不可用。</para>
    /// </summary>
    public string FairPlayKeyPath { get; init; } = string.Empty;

    /// <summary>Validates the option values and throws when one is unusable.
    /// <para>校验各选项取值，不可用时抛出异常。</para>
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="Name"/> is empty, longer than 63 characters or contains a
    /// control character, or <see cref="DeviceId"/> is not six colon-separated hexadecimal groups.
    /// <para><see cref="Name"/> 为空、超过 63 个字符或包含控制字符，或 <see cref="DeviceId"/> 不是六组以冒号
    /// 分隔的十六进制。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RtspPort"/> is outside 0..65535.
    /// <para><see cref="RtspPort"/> 不在 0..65535 内。</para></exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(RtspPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RtspPort, 65535);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 63)
        {
            throw new ArgumentException(
                "The AirPlay name must contain 1 to 63 characters. AirPlay 名称必须为 1 到 63 个字符。",
                nameof(Name));
        }

        foreach (var character in Name)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "The AirPlay name must not contain control characters. AirPlay 名称不得包含控制字符。",
                    nameof(Name));
            }
        }

        if (DeviceId.Length == 0)
        {
            return;
        }

        if (!IsDeviceId(DeviceId))
        {
            throw new ArgumentException(
                "The AirPlay device identifier must be six colon-separated hexadecimal groups. " +
                "AirPlay 设备标识必须是六组以冒号分隔的十六进制。",
                nameof(DeviceId));
        }

        if (FairPlayKeyPath.Length > 0 && FairPlayKeyPath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The FairPlay key path must not contain control characters. FairPlay 密钥路径不得包含控制字符。",
                nameof(FairPlayKeyPath));
        }
    }

    /// <summary>Formats a six-byte hardware address as the lowercase colon-separated device identifier used by mDNS.
    /// <para>把六字节硬件地址格式化为 mDNS 使用的小写冒号分隔设备标识。</para>
    /// </summary>
    /// <param name="address">Six raw address bytes; shorter or longer spans are rejected.
    /// <para>六个原始地址字节；更短或更长的跨度会被拒绝。</para></param>
    /// <returns>The formatted identifier, for example <c>aa:bb:cc:dd:ee:ff</c>.
    /// <para>格式化后的标识，例如 <c>aa:bb:cc:dd:ee:ff</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is not six bytes long.
    /// <para><paramref name="address"/> 不是六个字节。</para></exception>
    public static string FormatDeviceId(ReadOnlySpan<byte> address)
    {
        if (address.Length != 6)
        {
            throw new ArgumentException(
                "A device identifier needs six address bytes. 设备标识需要六个地址字节。",
                nameof(address));
        }

        return string.Create(17, address.ToArray(), static (span, bytes) =>
        {
            for (var index = 0; index < 6; index++)
            {
                if (index > 0)
                {
                    span[index * 3 - 1] = ':';
                }

                var value = bytes[index];
                span[index * 3] = ToHex(value >> 4);
                span[index * 3 + 1] = ToHex(value & 0x0F);
            }
        });
    }

    private static char ToHex(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));

    private static bool IsDeviceId(string value)
    {
        if (value.Length != 17)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index % 3 == 2)
            {
                if (character != ':')
                {
                    return false;
                }

                continue;
            }

            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}