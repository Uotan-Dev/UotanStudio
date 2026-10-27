using System.Globalization;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// A target line from the ADB server's verbose <c>host:devices-l</c> listing.
/// <para>来自 ADB server 的 <c>host:devices-l</c> 详细列表的一行目标条目。</para>
/// </summary>
/// <param name="Serial">Server serial that authoritatively identifies the target.
/// <para>权威标识该目标的 server 序列号。</para></param>
/// <param name="State">Authorization and readiness state text of the target.
/// <para>目标的授权与就绪状态文本。</para></param>
/// <param name="Transport">Link class of the target: <c>TCP</c> for a network serial, emulator console or
/// DNS-SD instance, and <c>USB</c> for every other serial.
/// <para>目标的链路类别：网络串号、模拟器控制台或 DNS-SD 实例为 <c>TCP</c>，其余串号为 <c>USB</c>。</para></param>
public sealed record AdbTargetInfo(string Serial, string State, string Transport)
{
    /// <summary>ADB's local emulator transport always serializes as this prefix plus the console port.
    /// <para>ADB 的本地模拟器传输始终序列化为该前缀加控制台端口。</para></summary>
    private const string EmulatorPrefix = "emulator-";

    /// <summary>
    /// Parses verbose listing text and derives each line's link class from its <c>usb:</c> descriptor or
    /// from a serial that identifies a network console, emulator or DNS-SD instance.
    /// <para>解析详细列表文本，并按 <c>usb:</c> 描述符或标识网络控制台、模拟器、DNS-SD 实例的串号判定每行的链路类别。</para>
    /// </summary>
    /// <param name="output">Raw device list text reported by the ADB server.
    /// <para>由 ADB server 报告的原始设备列表文本。</para></param>
    /// <returns>Targets in listing order; lines without a state field are skipped.
    /// <para>按列表顺序返回的目标；缺少状态字段的行被跳过。</para></returns>
    public static IReadOnlyList<AdbTargetInfo> Parse(string output)
    {
        var targets = new List<AdbTargetInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            targets.Add(new AdbTargetInfo(fields[0], fields[1], ClassifyLink(fields)));
        }

        return targets;
    }

    private static string ClassifyLink(string[] fields)
    {
        for (var index = 2; index < fields.Length; index++)
        {
            if (fields[index].StartsWith("usb:", StringComparison.Ordinal)) return "USB";
        }

        return IsNetworkSerial(fields[0]) ? "TCP" : "USB";
    }

    private static bool IsNetworkSerial(string serial) =>
        IsEmulatorConsoleKey(serial) ||
        DebugBridgeNetworkAddress.TryNormalize(serial, out _) ||
        IsMdnsInstanceName(serial);

    /// <summary>
    /// Reports whether a server serial is a DNS-SD service instance name instead of a hardware serial.
    /// <para>判断 server 串号是否为 DNS-SD 服务实例名，而不是硬件序列号。</para>
    /// </summary>
    /// <param name="serial">Serial reported for one server target. <para>server 为某目标上报的串号。</para></param>
    /// <returns><see langword="true"/> for an adb TLS or legacy adb service instance name, with or without a
    /// trailing dot. <para>adb TLS 或旧版 adb 服务实例名（带或不带结尾点）时为 true。</para></returns>
    public static bool IsMdnsInstanceName(string serial)
    {
        var instance = serial.TrimEnd('.');
        return instance.EndsWith("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
               instance.EndsWith("._adb._tcp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmulatorConsoleKey(string serial) =>
        serial.StartsWith(EmulatorPrefix, StringComparison.Ordinal) &&
        int.TryParse(serial.AsSpan(EmulatorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
            out var port) && port is >= 1 and <= 65535;
}