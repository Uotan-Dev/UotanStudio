using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Routing;

/// <summary>
/// Validates network device addresses without resolving or contacting them. A port may be omitted when the
/// caller supplies the protocol default.
/// <para>校验网络设备地址，不解析 DNS，也不连接设备。调用方给出协议默认端口时，地址可省略端口。</para>
/// </summary>
public static class DebugBridgeNetworkAddress
{
    /// <summary>ADB <c>tcpip</c> and HDC <c>tconn</c> listen on this port by default.
    /// <para>ADB 的 <c>tcpip</c> 与 HDC 的 <c>tconn</c> 默认监听端口。</para></summary>
    /// <value>The constant <c>5555</c>. <para>常量 <c>5555</c>。</para></value>
    public const int DefaultDevicePort = 5555;

    /// <summary>
    /// Normalizes a hostname, IPv4 address or bracketed IPv6 address that must carry an explicit port.
    /// <para>规范化必须带显式端口的主机名、IPv4 地址或方括号包围的 IPv6 地址。</para>
    /// </summary>
    /// <param name="input">The device address, not the host server address.<para>设备地址，而非宿主 server 地址。</para></param>
    /// <param name="address">The validated address, or an empty string.<para>校验后的地址，或空字符串。</para></param>
    /// <returns>Whether the input identifies one network endpoint.<para>输入是否标识单个网络端点。</para></returns>
    public static bool TryNormalize(string? input, out string address)
        => TryNormalize(input, 0, out address);

    /// <summary>
    /// Normalizes a device address, applying <paramref name="defaultPort"/> when the input omits the port.
    /// <para>规范化设备地址；输入未带端口时补用 <paramref name="defaultPort"/>。</para>
    /// </summary>
    /// <param name="input">The device address, with or without a port.<para>设备地址，可带或不带端口。</para></param>
    /// <param name="defaultPort">Port applied to a bare host; <c>0</c> keeps the port mandatory.
    /// <para>裸主机地址补用的端口；为 <c>0</c> 时端口仍然必填。</para></param>
    /// <param name="address">The validated <c>host:port</c> address, or an empty string.
    /// <para>校验后的 <c>host:port</c> 地址，或空字符串。</para></param>
    /// <returns>Whether the input identifies one network endpoint.<para>输入是否标识单个网络端点。</para></returns>
    public static bool TryNormalize(string? input, int defaultPort, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 1024 || input.Any(char.IsControl))
        {
            return false;
        }

        var value = input.Trim();
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port))
        {
            return TryNormalizeHost(value[..separator], port, out address);
        }

        if (!value.StartsWith('[') && separator > 0)
        {
            return false;
        }

        if (defaultPort is < 1 or > 65535)
        {
            return false;
        }

        return TryNormalizeHost(value, defaultPort, out address);
    }

    /// <summary>
    /// Wraps an IPv6 literal in brackets so it can be embedded in the <c>host:port</c> grammar the ADB and
    /// HDC servers parse; a bare IPv6 address has interior colons, which the servers reject as an invalid
    /// port. Host names and IPv4 literals contain no colon and are returned unchanged. The rule is grammar
    /// driven rather than <see cref="IPAddress"/> based so a scoped link-local literal, which carries a
    /// zone suffix, is bracketed too.
    /// <para>将 IPv6 字面量包上方括号，使其能嵌入 ADB/HDC server 解析的 <c>host:port</c> 语法；裸 IPv6
    /// 含有内部冒号，会被 server 判为端口非法。主机名与 IPv4 字面量不含冒号，原样返回。该规则按语法判定
    /// 而非依赖 <see cref="IPAddress"/>，因此带 zone 后缀的链路本地字面量同样会被加括号。</para>
    /// </summary>
    /// <param name="host">Host name or IP literal, with or without existing brackets.
    /// <para>主机名或 IP 字面量，可已带方括号。</para></param>
    /// <returns>The address form safe for <c>host:port</c> composition.
    /// <para>可安全拼进 <c>host:port</c> 的形式。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.
    /// <para><paramref name="host"/> 为空。</para></exception>
    public static string FormatHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var value = host.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value;
        }

        return value.Contains(':') ? $"[{value}]" : value;
    }

    /// <summary>
    /// Builds the <c>host:port</c> identity used by server commands and by the device list, bracketing an
    /// IPv6 host.
    /// <para>构建 server 命令与设备列表使用的 <c>host:port</c> 标识；IPv6 主机加方括号。</para>
    /// </summary>
    /// <param name="host">Host name or IP literal. <para>主机名或 IP 字面量。</para></param>
    /// <param name="port">TCP port. <para>TCP 端口。</para></param>
    /// <returns>The bracketed-if-needed <c>host:port</c> text.
    /// <para>必要时加括号的 <c>host:port</c> 文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.
    /// <para><paramref name="host"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is out of range.
    /// <para><paramref name="port"/> 超出范围。</para></exception>
    public static string FormatEndpoint(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return $"{FormatHost(host)}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Splits a <c>host:port</c> identity into the bare host the sockets dial and its port, accepting both
    /// the bracketed IPv6 form produced by <see cref="FormatEndpoint"/> and a plain <c>host:port</c>. A
    /// bracket-less host with interior colons is rejected, because it cannot be told apart from an IPv6
    /// address that carries no port, matching the grammar <see cref="TryNormalize(string,int,out string)"/>
    /// accepts.
    /// <para>把 <c>host:port</c> 标识拆成套接字实际拨号的裸主机与端口；同时接受
    /// <see cref="FormatEndpoint"/> 产生的 IPv6 括号形式与普通 <c>host:port</c>。不带方括号却含内部冒号的
    /// 主机被拒绝，因为它无法与「不带端口的 IPv6 地址」区分，这与 <see cref="TryNormalize(string,int,out string)"/>
    /// 接受的语法一致。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint identity. <para>端点标识。</para></param>
    /// <param name="host">The bare host, without brackets. <para>不带方括号的裸主机。</para></param>
    /// <param name="port">The TCP port. <para>TCP 端口。</para></param>
    /// <returns>Whether the text carries a usable host and port.
    /// <para>文本是否带有可用主机与端口。</para></returns>
    public static bool TrySplitEndpoint(string? endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var value = endpoint.Trim();
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is < 1 or > 65535)
        {
            return false;
        }

        var candidate = value[..separator];
        if (candidate.StartsWith('[') && candidate.EndsWith(']'))
        {
            candidate = candidate[1..^1];
        }
        else if (candidate.Contains(':'))
        {
            return false;
        }

        if (candidate.Length == 0)
        {
            return false;
        }

        host = candidate;
        port = parsed;
        return true;
    }

    private static bool TryNormalizeHost(string host, int port, out string address)
    {
        address = string.Empty;
        if (port is < 1 or > 65535)
        {
            return false;
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            if (!IPAddress.TryParse(host[1..^1], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }
        else if (host.Contains(':') || host.Length > 253 ||
                 host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) ||
                 Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return false;
        }

        address = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }
}