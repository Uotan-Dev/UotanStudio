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
/// <param name="Transport">Link class reported by the listing: <c>USB</c> when the line carries the
/// server's <c>usb:</c> transport descriptor, <c>TCP</c> when the serial is a network endpoint or an
/// emulator console key, and an empty string when the listing carries no link evidence at all.
/// <para>列表报告的链路类别：行内带有 server 的 <c>usb:</c> 传输描述符时为 <c>USB</c>；序列号是网络端点
/// 或模拟器控制台标识时为 <c>TCP</c>；列表完全没有提供链路证据时为空字符串。</para></param>
public sealed record AdbTargetInfo(string Serial, string State, string Transport)
{
    /// <summary>ADB's local emulator transport always serializes as this prefix plus the console port.
    /// <para>ADB 的本地模拟器传输始终序列化为该前缀加控制台端口。</para></summary>
    private const string EmulatorPrefix = "emulator-";

    /// <summary>
    /// Parses verbose listing text while preserving each line's transport descriptor evidence.
    /// <para>解析详细列表文本，并保留每行的传输描述符证据。</para>
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

        return IsEmulatorConsoleKey(fields[0]) || DebugBridgeNetworkAddress.TryNormalize(fields[0], out _)
            ? "TCP"
            : string.Empty;
    }

    private static bool IsEmulatorConsoleKey(string serial) =>
        serial.StartsWith(EmulatorPrefix, StringComparison.Ordinal) &&
        int.TryParse(serial.AsSpan(EmulatorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
            out var port) && port is >= 1 and <= 65535;
}