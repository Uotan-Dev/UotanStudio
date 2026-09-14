namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>A host target from upstream server.cpp BuildDaemonVisableLine.</summary>
public sealed record HdcTargetInfo(string ConnectKey, string Transport, string State, string Name)
{
    /// <summary>Parses verbose targets, preserving USB/TCP/UART and authorization state.</summary>
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
        return targets.Values.OrderBy(target => target.ConnectKey, StringComparer.Ordinal).ToArray();
    }
}