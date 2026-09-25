using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Server-ownership decisions and routed session openers for the debug bridges. It combines the
/// TCP liveness probes from <see cref="Routing.DebugBridgeRoute"/> with the server clients, so
/// callers get one place that answers "who owns this target and which session serves it".
/// <para>调试桥的 server 归属判定与路由开启器。它把 <see cref="Routing.DebugBridgeRoute"/> 的 TCP
/// 存活探测与 server 客户端组合起来，调用方由此得到「目标归谁、由哪条会话服务」的统一答案。</para>
/// Ownership errors never grant raw USB access: a present server keeps serving its targets even
/// when they are missing or unauthorized.
/// <para>归属判定失败绝不放行裸 USB：server 存在时其目标即使缺失或未授权也继续由 server 服务。</para>
/// </summary>
public static class DebugBridgeSessionOpener
{
    /// <summary>Checks ADB ownership by exact serial; unknown identities remain protected when targets exist.</summary>
    public static bool AdbServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
    {
        if (!DebugBridgeRoute.ProbeEndpoint(host, port)) return false;
        var targets = new AdbServerServices(string.Empty, host, port).ListTargets();
        return string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target.Serial, serial, StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks HDC ownership by exact connect key: a live server owns only the targets its list
    /// actually contains, so a device whose daemon has not (re-)registered after a server
    /// restart falls back to direct USB instead of disappearing.
    /// <para>按连接键检查 HDC 归属：活着的 server 只拥有其列表中实际存在的目标，server 重启后
    /// 尚未重新注册的设备回退直连 USB，而不是从清单中消失。</para>
    /// </summary>
    public static bool HdcServerOwns(string? serial, string host = DebugBridgeEndpoints.ServerHost, int? port = null)
    {
        if (!DebugBridgeRoute.UseHdcServer(host, port)) return false;
        using var server = new HdcServerClient(host, port);
        var targets = server.ListTargets();
        return string.IsNullOrWhiteSpace(serial)
            ? targets.Count != 0
            : targets.Any(target => string.Equals(target, serial, StringComparison.Ordinal));
    }

    /// <summary>Rejects raw debug USB access when the selected interface belongs to a server target.</summary>
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

    /// <summary>Routes exact server targets through smart sockets and other USB identities through the direct factory.</summary>
    public static IAdbDeviceSession? OpenAdb(string serial, Func<IAdbDeviceSession?> openDirect,
        string host = DebugBridgeEndpoints.ServerHost, int port = DebugBridgeEndpoints.AdbServerPort)
        => AdbServerOwns(serial, host, port) ? AdbSession.ForServer(serial, host, port) : openDirect();

    /// <summary>
    /// Creates a routed HDC session: the server serves only targets it actually lists; a serial
    /// missing from the live server's list falls back to the direct factory.
    /// <para>创建路由 HDC 会话：server 只服务其列表中实际存在的目标；活着的 server 列表里
    /// 没有的 serial 回退到直连工厂。</para>
    /// </summary>
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