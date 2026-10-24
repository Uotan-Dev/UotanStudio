using System.Net.Sockets;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-ownership decisions and routed session openers for the debug bridges, all reading the one
/// fixed route mode and the one immutable snapshot. A present server keeps serving its targets even
/// when they are missing or unauthorized; direct access never silently takes over.
/// <para>调试桥的 server 归属判定与路由开启器，全部读取同一个固定路由模式和同一份不可变快照。
/// 只要 server 存在，其目标即使缺失或未授权也继续由它服务；直连访问绝不静默接管。</para>
/// </summary>
public static class DebugBridgeSessionOpener
{
    /// <summary>Checks ADB ownership by exact serial; unknown identities stay protected when targets exist. The route mode and the snapshot decide whether the server is consulted at all.
    /// <para>按精确序列号检查 ADB 归属；存在目标时未知身份仍受保护。是否咨询 server 由路由模式与快照共同决定。</para></summary>
    /// <param name="serial">Target serial to check; when null or blank, any nonempty target list counts as ownership.
    /// <para>待检查的目标序列号；为 null 或空白时，只要目标列表非空即视为归属。</para></param>
    /// <param name="host">adb server host to decide for.<para>待判定的 adb server 主机。</para></param>
    /// <param name="port">adb server port to decide for.<para>待判定的 adb server 端口。</para></param>
    /// <returns><see langword="true"/> when the server is selected by the mode, marked available, and owns the serial.
    /// <para>模式选择 server、快照标记可用且拥有该序列号时为 <see langword="true"/>。</para></returns>
    public static bool AdbServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
    {
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.DirectOnly) return false;
        if (!DebugBridgeRoute.UseAdbServer(host, port) && !AnyServerListening("adb"))
        {
            return false;
        }

        var targets = new AdbServerServices(string.Empty, host, port).ListTargets();
        return string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target.Serial, serial, StringComparison.Ordinal));
    }

    /// <summary>Checks HDC ownership by exact connect key against the verbose list, so an offline or unauthorized target remains server-owned instead of falling back to direct USB. The route mode decides whether the server is consulted at all.
    /// <para>按详细目标列表中的精确连接键检查 HDC 归属，使离线或未授权的目标仍归 server 所有，而不是回退直连 USB。是否咨询 server 由路由模式决定。</para></summary>
    /// <param name="serial">Connect key to check; when null or blank, any nonempty target list counts as ownership.
    /// <para>待检查的连接键；为 null 或空白时，只要目标列表非空即视为归属。</para></param>
    /// <param name="host">HDC server host to probe.<para>待探测的 HDC server 主机。</para></param>
    /// <param name="port">HDC server port; null uses the standard default port.
    /// <para>HDC server 端口；null 使用标准默认端口。</para></param>
    /// <returns><see langword="true"/> when the mode selects the server and its verbose list contains this connect key.
    /// <para>模式选择 server 且其详细列表包含该连接键时为 <see langword="true"/>。</para></returns>
    public static bool HdcServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.DirectOnly) return false;
        if (!DebugBridgeRoute.UseHdcServer(host, port)) return false;
        using var server = new HdcServerClient(host, port);
        return HdcTargetListOwns(server.ListTargetDetails(), serial);
    }

    /// <summary>
    /// Decides ownership from a verbose HDC target snapshot; every listed state (connected, offline or unauthorized)
    /// counts as server-owned.
    /// <para>依据 HDC 详细目标快照判定归属；只要出现在列表中（已连接、离线或未授权）都视为 server 所有。</para>
    /// </summary>
    /// <param name="targets">Verbose target rows returned by <c>list targets -v</c>.<para><c>list targets -v</c> 返回的目标行。</para></param>
    /// <param name="serial">Connect key to match, or null/blank for "any target".<para>待匹配的连接键；null/空白表示任意目标。</para></param>
    /// <returns><see langword="true"/> when the list contains the requested key or any key for a blank request.
    /// <para>列表包含指定键，或在请求为空时包含任意键，返回 <see langword="true"/>。</para></returns>
    internal static bool HdcTargetListOwns(IReadOnlyList<HdcTargetInfo> targets, string? serial)
        => string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target.ConnectKey, serial, StringComparison.Ordinal));

    /// <summary>Rejects raw debug USB access according to the fixed route mode: server-only always refuses, direct-only refuses while a server is listening instead of switching, startup-frozen refuses when a server is present at startup or appears later, and automatic mode refuses only when the selected server owns the target.
    /// <para>按固定路由模式拒绝裸 USB 调试访问：仅 server 模式一律拒绝；仅直连模式在 server 正在监听时拒绝而不是切换；
    /// 启动冻结模式在启动时有 server 或之后出现 server 时拒绝；自动模式仅在所选 server 拥有该目标时拒绝。</para>Only an interface class of <c>0xff</c> with protocol <c>1</c> is examined; the subclass selects the bridge (<c>0x42</c> ADB, <c>0x50</c> HDC).<para>仅检查接口类为 <c>0xff</c> 且协议为 <c>1</c> 的接口；子类决定调试桥（<c>0x42</c> ADB、<c>0x50</c> HDC）。</para></summary>
    /// <param name="interfaceClass">USB interface class byte of the interface being claimed.
    /// <para>待申请接口的 USB 接口类字节。</para></param>
    /// <param name="subclass">USB interface subclass selecting the bridge.
    /// <para>用于选择调试桥的 USB 接口子类。</para></param>
    /// <param name="protocol">USB interface protocol byte.<para>USB 接口协议字节。</para></param>
    /// <param name="serial">Optional serial used for exact ownership matching.
    /// <para>用于精确归属匹配的可选序列号。</para></param>
    /// <exception cref="IOException">The route mode or a running server prohibits direct access to this interface.
    /// <para>路由模式或正在运行的 server 禁止直连该接口。</para></exception>
    public static void RequireDirectUsb(byte? interfaceClass, byte? subclass, byte? protocol,
        string? serial = null)
    {
        if (interfaceClass != 0xff || protocol != 1) return;
        var bridge = subclass switch
        {
            0x42 => "adb",
            0x50 => "hdc",
            _ => null
        };
        if (bridge is null)
        {
            return;
        }

        switch (DebugBridgeRoute.Mode)
        {
            case DebugBridgeRouteMode.ServerOnly:
                throw new IOException(
                    $"The {bridge} route mode is server-only; direct USB debug access is disabled.");
            case DebugBridgeRouteMode.DirectOnly:
                if (AnyServerListening(bridge))
                {
                    throw new IOException(
                        $"The {bridge} route mode is direct-only; a server is listening, so direct USB access is refused instead of switching routes.");
                }

                return;
            case DebugBridgeRouteMode.StartupFrozen:
                if (FrozenServerPresent(bridge) || AnyServerListening(bridge))
                {
                    throw new IOException(
                        $"The {bridge} route was frozen at startup and a server is present; restart the application to change the route.");
                }

                return;
            default:
                var present = bridge == "adb" ? AdbServerOwns(serial) : HdcServerOwns(serial);
                if (present)
                {
                    throw new IOException("A debug bridge server is running; use its target service instead of opening USB.");
                }

                return;
        }
    }

    /// <summary>Routes exact server targets through smart sockets and other USB identities through the direct factory, honoring the fixed mode: server-only requires the server, direct-only never uses it.
    /// <para>把精确匹配 server 的目标路由到智能套接字，其余 USB 身份交给直连工厂，并遵循固定模式：
    /// 仅 server 模式要求 server，仅直连模式永不使用 server。</para></summary>
    /// <param name="serial">Target serial to route.<para>待路由的目标序列号。</para></param>
    /// <param name="openDirect">Factory invoked when no live server owns the serial.
    /// <para>无存活 server 拥有该序列号时调用的工厂。</para></param>
    /// <param name="host">adb server host to probe.<para>待探测的 adb server 主机。</para></param>
    /// <param name="port">adb server port to probe.<para>待探测的 adb server 端口。</para></param>
    /// <returns>The server-backed session, or the direct factory result (possibly <see langword="null"/>) when unclaimed.
    /// <para>server 会话；若无人接管，则返回直连工厂结果（可能为 <see langword="null"/>）。</para></returns>
    /// <exception cref="IOException">The server-only mode is selected and the server is unavailable.
    /// <para>选择了仅 server 模式但 server 不可用。</para></exception>
    public static IAdbDeviceSession? OpenAdb(string serial, Func<IAdbDeviceSession?> openDirect,
        string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
    {
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.ServerOnly)
        {
            if (!DebugBridgeRoute.UseAdbServer(host, port))
            {
                throw new IOException("The ADB server is required by the ServerOnly route mode but is not listening.");
            }

            return AdbSession.ForServer(serial, host, port);
        }

        return AdbServerOwns(serial, host, port) ? AdbSession.ForServer(serial, host, port) : openDirect();
    }

    /// <summary>Creates a routed HDC session: the server serves only targets it actually lists; a serial missing from the live server's list falls back to the direct factory, except in server-only mode where it is an error.
    /// <para>创建路由 HDC 会话：server 只服务其列表中实际存在的目标；活着的 server 列表里没有的 serial 回退到直连工厂，
    /// 仅 server 模式下则视为错误。</para></summary>
    /// <param name="serial">Target connect key to route.<para>待路由的目标连接键。</para></param>
    /// <param name="openDirect">Factory invoked when the server is absent or does not list this key.
    /// <para>server 不存在或未列出该连接键时调用的工厂。</para></param>
    /// <param name="host">HDC server host to probe.<para>待探测的 HDC server 主机。</para></param>
    /// <param name="port">HDC server port; null uses the standard default port.
    /// <para>HDC server 端口；null 使用标准默认端口。</para></param>
    /// <returns>The server-backed session, or the direct factory result when the key is unlisted.
    /// <para>server 会话；连接键未被列出时返回直连工厂结果。</para></returns>
    /// <exception cref="IOException">The server-only mode is selected and the server is unavailable or does not list the key.
    /// <para>选择了仅 server 模式但 server 不可用或未列出该连接键。</para></exception>
    public static HdcSession OpenHdc(string serial, Func<HdcSession> openDirect,
        string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        var serverOnly = DebugBridgeRoute.Mode == DebugBridgeRouteMode.ServerOnly;
        if (!DebugBridgeRoute.UseHdcServer(host, port))
        {
            if (serverOnly)
            {
                throw new IOException("The HDC server is required by the ServerOnly route mode but is not listening.");
            }

            return openDirect();
        }

        using var server = new HdcServerClient(host, port);
        var targets = server.ListTargetDetails();
        if (!string.IsNullOrWhiteSpace(serial) && !HdcTargetListOwns(targets, serial))
        {
            if (serverOnly)
            {
                throw new IOException("The HDC server does not list the requested connect key in ServerOnly route mode.");
            }

            return openDirect();
        }

        return server.CreateSession(serial);
    }

    private static bool FrozenServerPresent(string bridge)
        => bridge == "adb" ? DebugBridgeRoute.UseAdbServer() : DebugBridgeRoute.UseHdcServer();

    private static bool AnyServerListening(string bridge)
    {
        try
        {
            return bridge == "adb"
                ? DebugBridgeRoute.IsListening(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.AdbServerPort)
                : DebugBridgeRoute.IsListening(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            CommTrace.State("route",
                $"{bridge} server liveness probe was inconclusive ({ex.GetType().Name}: {ex.Message}); treating the server as " +
                "present so direct USB access stays refused. Windows allows several handles on one WinUSB interface, so a " +
                "server that is already attached would split every frame with us.");
            return true;
        }
    }
}