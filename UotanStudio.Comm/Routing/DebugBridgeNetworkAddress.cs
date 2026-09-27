using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Routing;

/// <summary>
/// Validates explicit network device addresses without resolving or contacting them.
/// <para>校验显式网络设备地址，不解析 DNS，也不连接设备。</para>
/// </summary>
public static class DebugBridgeNetworkAddress
{
    /// <summary>
    /// Normalizes a hostname, IPv4 address or bracketed IPv6 address with an explicit port.
    /// <para>规范化带显式端口的主机名、IPv4 地址或方括号包围的 IPv6 地址。</para>
    /// </summary>
    /// <param name="input">The device address, not the host server address.<para>设备地址，而非宿主 server 地址。</para></param>
    /// <param name="address">The validated address, or an empty string.<para>校验后的地址，或空字符串。</para></param>
    /// <returns>Whether the input identifies one network endpoint.<para>输入是否标识单个网络端点。</para></returns>
    public static bool TryNormalize(string? input, out string address)
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
        if (separator <= 0 || !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        var host = value[..separator];
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