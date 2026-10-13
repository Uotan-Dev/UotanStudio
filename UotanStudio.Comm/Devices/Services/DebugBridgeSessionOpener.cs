using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-ownership decisions and routed session openers for the debug bridges. A present server
/// keeps serving its targets even when they are missing or unauthorized.
/// <para>调试桥的 server 归属判定与路由开启器。只要 server 存在，其目标即使缺失或未授权也继续由它服务。</para>
/// </summary>
public static class DebugBridgeSessionOpener
{
    /// <summary>
    /// Checks ADB ownership by exact serial; unknown identities stay protected when targets exist.
    /// Server presence comes from the global <see cref="DebugBridgeRoute.AdbServerAvailable"/> parameter.
    /// <para>按精确序列号检查 ADB 归属；存在目标时未知身份仍受保护。server 存在性取全局参数
    /// <see cref="DebugBridgeRoute.AdbServerAvailable"/>。</para>
    /// </summary>
    /// <param name="serial">
    /// Target serial to check; when null or blank, any nonempty target list counts as ownership.
    /// <para>待检查的目标序列号；为 null 或空白时，只要目标列表非空即视为归属。</para>
    /// </param>
    /// <param name="host">
    /// adb server host to decide for. <para>待判定的 adb server 主机。</para>
    /// </param>
    /// <param name="port">
    /// adb server port to decide for. <para>待判定的 adb server 端口。</para>
    /// </param>
    /// <returns><see langword="true"/> when the server is marked available and owns the serial; a present
    /// server never falls back to raw USB.
    /// <para>当 server 被标记为存在并且拥有该序列号时为 <see langword="true"/>；server 存在时绝不回退裸 USB。</para></returns>
    public static bool AdbServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
    {
        if (!DebugBridgeRoute.UseAdbServer(host, port)) return false;
        var targets = new AdbServerServices(string.Empty, host, port).ListTargets();
        return string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target.Serial, serial, StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks HDC ownership by exact connect key: a live server owns only the targets its list
    /// actually contains, so an unregistered device falls back to direct USB.
    /// <para>按连接键检查 HDC 归属：活着的 server 只拥有其列表中实际存在的目标，未注册的设备回退直连 USB。</para>
    /// </summary>
    /// <param name="serial">
    /// Connect key to check; when null or blank, any nonempty target list counts as ownership.
    /// <para>待检查的连接键；为 null 或空白时，只要目标列表非空即视为归属。</para>
    /// </param>
    /// <param name="host">
    /// HDC server host to probe. <para>待探测的 HDC server 主机。</para>
    /// </param>
    /// <param name="port">
    /// HDC server port; null uses the standard default port. <para>HDC server 端口；null 使用标准默认端口。</para>
    /// </param>
    /// <returns><see langword="true"/> when a live server lists this connect key.
    /// <para>当存活的 server 列出该连接键时为 <see langword="true"/>。</para></returns>
    public static bool HdcServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        if (!DebugBridgeRoute.UseHdcServer(host, port)) return false;
        using var server = new HdcServerClient(host, port);
        var targets = server.ListTargets();
        return string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target, serial, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rejects raw debug USB access when the selected interface belongs to a server target.
    /// <para>当所选接口属于某个 server 目标时，拒绝裸 USB 调试访问。</para>
    /// Only an interface class of <c>0xff</c> with protocol <c>1</c> is examined; the subclass
    /// selects the bridge (<c>0x42</c> ADB, <c>0x50</c> HDC).
    /// <para>仅检查接口类为 <c>0xff</c> 且协议为 <c>1</c> 的接口；子类决定调试桥（<c>0x42</c> ADB、<c>0x50</c> HDC）。</para>
    /// </summary>
    /// <param name="interfaceClass">
    /// USB interface class byte of the interface being claimed. <para>待申请接口的 USB 接口类字节。</para>
    /// </param>
    /// <param name="subclass">
    /// USB interface subclass selecting the bridge. <para>用于选择调试桥的 USB 接口子类。</para>
    /// </param>
    /// <param name="protocol">
    /// USB interface protocol byte. <para>USB 接口协议字节。</para>
    /// </param>
    /// <param name="serial">
    /// Optional serial used for exact ownership matching. <para>用于精确归属匹配的可选序列号。</para>
    /// </param>
    /// <exception cref="IOException">
    /// A debug bridge server is running and owns this interface.
    /// <para>调试桥 server 正在运行并且拥有该接口。</para>
    /// </exception>
    public static void RequireDirectUsb(byte? interfaceClass, byte? subclass, byte? protocol,
        string? serial = null)
    {
        if (interfaceClass != 0xff || protocol != 1) return;
        bool present = subclass switch
        {
            0x42 => AdbServerOwns(serial),
            0x50 => HdcServerOwns(serial),
            _ => false
        };
        if (present) throw new IOException("A debug bridge server is running; use its target service instead of opening USB.");
    }

    /// <summary>
    /// Routes exact server targets through smart sockets and other USB identities through the direct factory.
    /// <para>把精确匹配 server 的目标路由到智能套接字，其余 USB 身份交给直连工厂。</para>
    /// </summary>
    /// <param name="serial">
    /// Target serial to route. <para>待路由的目标序列号。</para>
    /// </param>
    /// <param name="openDirect">
    /// Factory invoked when no live server owns the serial. <para>无存活 server 拥有该序列号时调用的工厂。</para>
    /// </param>
    /// <param name="host">
    /// adb server host to probe. <para>待探测的 adb server 主机。</para>
    /// </param>
    /// <param name="port">
    /// adb server port to probe. <para>待探测的 adb server 端口。</para>
    /// </param>
    /// <returns>The server-backed session, or the direct factory result (possibly <see langword="null"/>) when unclaimed.
    /// <para>server 会话；若无人接管，则返回直连工厂结果（可能为 <see langword="null"/>）。</para></returns>
    public static IAdbDeviceSession? OpenAdb(string serial, Func<IAdbDeviceSession?> openDirect,
        string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
        => AdbServerOwns(serial, host, port) ? AdbSession.ForServer(serial, host, port) : openDirect();

    /// <summary>
    /// Creates a routed HDC session: the server serves only targets it actually lists; a serial
    /// missing from the live server's list falls back to the direct factory.
    /// <para>创建路由 HDC 会话：server 只服务其列表中实际存在的目标；活着的 server 列表里
    /// 没有的 serial 回退到直连工厂。</para>
    /// </summary>
    /// <param name="serial">
    /// Target connect key to route. <para>待路由的目标连接键。</para>
    /// </param>
    /// <param name="openDirect">
    /// Factory invoked when the server is absent or does not list this key. <para>server 不存在或未列出该连接键时调用的工厂。</para>
    /// </param>
    /// <param name="host">
    /// HDC server host to probe. <para>待探测的 HDC server 主机。</para>
    /// </param>
    /// <param name="port">
    /// HDC server port; null uses the standard default port. <para>HDC server 端口；null 使用标准默认端口。</para>
    /// </param>
    /// <returns>The server-backed session, or the direct factory result when the key is unlisted.
    /// <para>server 会话；连接键未被列出时返回直连工厂结果。</para></returns>
    public static HdcSession OpenHdc(string serial, Func<HdcSession> openDirect,
        string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        if (!DebugBridgeRoute.UseHdcServer(host, port)) return openDirect();
        using var server = new HdcServerClient(host, port);
        var targets = server.ListTargets();
        if (!string.IsNullOrWhiteSpace(serial) && !targets.Any(target => string.Equals(target, serial, StringComparison.Ordinal)))
        {
            return openDirect();
        }

        return server.CreateSession(serial);
    }
}