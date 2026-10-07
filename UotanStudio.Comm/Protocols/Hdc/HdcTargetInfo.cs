namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// A host target from upstream server.cpp BuildDaemonVisableLine.
/// <para>来自上游 server.cpp BuildDaemonVisableLine 的宿主目标条目。</para>
/// </summary>
/// <param name="ConnectKey">Server connect key that authoritatively identifies the target.<para>唯一标识该目标的 server connect key。</para></param>
/// <param name="Transport">Transport class reported by the listing, such as USB, TCP or UART.<para>列表报告的传输类型，例如 USB、TCP 或 UART。</para></param>
/// <param name="State">Authorization and readiness state text of the target.<para>目标的授权与就绪状态文本。</para></param>
/// <param name="Name">Display product name; empty when the listing line omits it.<para>显示产品名；列表行未提供时为空字符串。</para></param>
public sealed record HdcTargetInfo(string ConnectKey, string Transport, string State, string Name)
{
    /// <summary>
    /// Parses verbose targets, preserving USB/TCP/UART and authorization state.
    /// <para>解析详细目标列表，保留 USB/TCP/UART 及授权状态。</para>
    /// </summary>
    /// <param name="output">Raw verbose list text produced by the HDC server.<para>由 HDC server 生成的原始详细列表文本。</para></param>
    /// <returns>Distinct targets ordered by connect key using ordinal comparison.<para>去重后的目标列表，按 connect key 以序数比较排序。</para></returns>
    /// <exception cref="IOException">The output contains a <c>[Fail]</c> line. <para>输出包含 <c>[Fail]</c> 行。</para></exception>
    /// <exception cref="InvalidDataException">A non-empty line has fewer than three whitespace-separated fields. <para>非空行的空白分隔字段少于三个。</para></exception>
    public static IReadOnlyList<HdcTargetInfo> Parse(string output)
    {
        HdcServerClient.ThrowOnFailure(output);
        var targets = new Dictionary<string, HdcTargetInfo>(StringComparer.Ordinal);
        foreach (var line in output.TrimEnd('\0').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("[Empty]", StringComparison.Ordinal)) continue;
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) throw new InvalidDataException("Malformed HDC verbose target entry.");
            targets[fields[0]] = new(fields[0], fields[1], fields[2], fields.Length > 3 ? fields[3] : string.Empty);
        }
        return [.. targets.Values.OrderBy(target => target.ConnectKey, StringComparer.Ordinal)];
    }
}