using System.Text;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// Debug bridge command line tools the wrapped executor can take over.
/// <para>被包装执行器接管的调试桥命令行工具。</para>
/// </summary>
internal enum WrappedTool
{
    /// <summary>Android Debug Bridge.<para>Android 调试桥。</para></summary>
    Adb,
    /// <summary>Android fastboot.<para>Android fastboot。</para></summary>
    Fastboot,
    /// <summary>OpenHarmony device connector.<para>OpenHarmony 设备连接器。</para></summary>
    Hdc,
}

/// <summary>
/// A parsed wrapped command line: the tool, the sub-command, the remaining arguments and the
/// device selector the user supplied before the sub-command.
/// <para>解析后的包装命令行：工具、子命令、剩余参数，以及用户在子命令前给出的设备选择器。</para>
/// </summary>
internal sealed class WrappedCommand
{
    /// <summary>Gets the tool the command line names.<para>获取命令行指定的工具。</para></summary>
    internal required WrappedTool Tool { get; init; }

    /// <summary>Gets the sub-command token such as <c>devices</c>; empty when the tool was named alone.
    /// <para>获取子命令令牌（如 <c>devices</c>）；仅输入工具名时为空。</para></summary>
    internal required string Command { get; init; }

    /// <summary>Gets the arguments after the sub-command.<para>获取子命令之后的参数。</para></summary>
    internal required string[] Arguments { get; init; }

    /// <summary>Gets the device selector from <c>-s</c>/<c>-t</c>, or <c>null</c>.
    /// <para>获取 <c>-s</c>/<c>-t</c> 的设备选择器；未指定时为 <c>null</c>。</para></summary>
    internal string? Target { get; init; }

    /// <summary>Gets whether <c>adb -d</c> requested the USB device.<para>获取 <c>adb -d</c> 是否要求 USB 设备。</para></summary>
    internal bool UseUsb { get; init; }

    /// <summary>Gets whether <c>adb -e</c> requested the TCP/IP device.<para>获取 <c>adb -e</c> 是否要求网络设备。</para></summary>
    internal bool UseTcp { get; init; }

    /// <summary>Gets the raw input line the command was parsed from.<para>获取解析出本命令的原始输入行。</para></summary>
    internal required string RawLine { get; init; }
}

/// <summary>
/// Parses a typed terminal line into a <see cref="WrappedCommand"/>. The parser stays deliberately
/// conservative: lines carrying shell metacharacters, unterminated quotes or unknown tool names are
/// rejected so the real shell keeps executing them.
/// <para>把终端输入行解析为 <see cref="WrappedCommand"/>。解析刻意保守：带 shell 元字符、未闭合引号或
/// 未知工具名的行一律拒绝，交还真实 shell 执行。</para>
/// </summary>
internal static class WrappedCommandLine
{
    /// <summary>Maps a typed token to its tool, or <c>null</c>. <para>把输入令牌映射为工具；未知时为 <c>null</c>。</para></summary>
    /// <param name="token">The first token of the line. <para>输入行的第一个令牌。</para></param>
    /// <returns>The tool, or <c>null</c> when the token does not name one. <para>工具；令牌不匹配时为 <c>null</c>。</para></returns>
    internal static WrappedTool? DetectTool(string token)
    {
        var name = token.TrimEnd();
        if (OperatingSystem.IsWindows() && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant() switch
        {
            "adb" => WrappedTool.Adb,
            "fastboot" => WrappedTool.Fastboot,
            "hdc" => WrappedTool.Hdc,
            _ => null,
        };
    }

    /// <summary>Splits the line into argument tokens honoring POSIX and Windows style quoting.
    /// <para>按 POSIX 与 Windows 两种引号风格把输入行切分为参数令牌。</para></summary>
    /// <param name="line">The typed line. <para>输入行。</para></param>
    /// <returns>The tokens, or <c>null</c> when a quote is left unterminated.
    /// <para>令牌列表；存在未闭合引号时为 <c>null</c>。</para></returns>
    internal static string[]? Tokenize(string line)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        var hasToken = false;
        var inDouble = false;
        var inSingle = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inDouble)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inDouble = false;
                }
                else if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                {
                    current.Append(line[++i]);
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                inDouble = c == '"';
                inSingle = c == '\'';
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (hasToken || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            // A backslash only escapes a quote or a backslash, so Windows paths survive verbatim.
            // 反斜杠只转义引号或反斜杠本身，Windows 路径得以原样保留。
            if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\' || line[i + 1] == '\''))
            {
                current.Append(line[++i]);
                hasToken = true;
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (inDouble || inSingle) return null;
        if (hasToken || current.Length > 0) tokens.Add(current.ToString());
        return [.. tokens];
    }

    /// <summary>Reports whether the line carries shell metacharacters outside quoted spans, meaning the
    /// real shell must keep interpreting it. <para>报告去引号后是否残留 shell 元字符；有则必须交还真实 shell。</para></summary>
    /// <param name="line">The typed line. <para>输入行。</para></param>
    /// <returns><c>true</c> when a shell metacharacter survives. <para>残留元字符时为 <c>true</c>。</para></returns>
    internal static bool ContainsShellMetacharacters(string line)
    {
        var inDouble = false;
        var inSingle = false;
        foreach (var c in line)
        {
            if (inDouble)
            {
                if (c == '"') inDouble = false;
                continue;
            }
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }
            if (c is '"' or '\'')
            {
                inDouble = c == '"';
                inSingle = c == '\'';
                continue;
            }
            if ("|&;<>()$`*?~%#^!\n\r".Contains(c)) return true;
        }
        return false;
    }

    /// <summary>Parses the line into a wrappable command. Known tools with an unknown sub-command still
    /// parse (the caller decides how to report them), but lines the shell must interpret return <c>null</c>.
    /// <para>把输入行解析为可包装命令。已知工具的未知子命令仍会解析（由调用方决定如何上报），
    /// 必须由 shell 解释的行返回 <c>null</c>。</para></summary>
    /// <param name="line">The typed line. <para>输入行。</para></param>
    /// <returns>The parsed command, or <c>null</c> when the line is not interceptable.
    /// <para>解析结果；行不可拦截时为 <c>null</c>。</para></returns>
    internal static WrappedCommand? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || ContainsShellMetacharacters(line)) return null;
        var tokens = Tokenize(line);
        if (tokens is not { Length: > 0 }) return null;
        var tool = DetectTool(tokens[0]);
        if (tool is not { } knownTool) return null;
        var target = tool switch
        {
            WrappedTool.Hdc => "-t",
            _ => "-s",
        };
        string? serial = null;
        var useUsb = false;
        var useTcp = false;
        var index = 1;
        while (index < tokens.Length)
        {
            var token = tokens[index];
            if (serial is null && token == target && index + 1 < tokens.Length)
            {
                serial = tokens[++index];
                index++;
                continue;
            }
            if (knownTool == WrappedTool.Adb)
            {
                if (token == "-d") { useUsb = true; index++; continue; }
                if (token == "-e") { useTcp = true; index++; continue; }
                if (token is "-H" or "-P" or "-L" or "--one-device" or "-t") index += 2; // server placement and transport id stay unhandled
                else if (token is "--exit-on-write-error" or "-a") index++;
                else break;
                continue;
            }
            if (knownTool == WrappedTool.Fastboot)
            {
                if (token is "-S" or "-c" or "-i" or "-p" or "--slot" or "--force" or "--all") index += 2;
                else if (token is "-l" or "-n" or "-w" or "-u" or "--force-flash") index++;
                else break;
                continue;
            }
            // hdc keeps every other global option untouched; only -t selects a target.
            // hdc 的其余全局选项保持原样；只有 -t 选择目标。
            if (token is "-h" or "-v" or "help" or "version") break;
            if (token.StartsWith("-l", StringComparison.Ordinal) && token.Length == 3) { index++; continue; }
            if (token == "-s" && index + 1 < tokens.Length) index += 2;
            else break;
        }
        var command = index < tokens.Length ? tokens[index] : string.Empty;
        string[] arguments = index + 1 < tokens.Length ? [.. tokens[(index + 1)..]] : [];
        // A bare tool name or -h/-v/--help/--version maps to the canonical help/version commands.
        // 纯工具名或 -h/-v/--help/--version 归一为 help/version 子命令。
        if (command.Length == 0) command = "help";
        else if (knownTool == WrappedTool.Adb && command is "-h" or "--help") command = "help";
        else if (knownTool == WrappedTool.Adb && command is "--version" or "version") command = "version";
        else if (knownTool == WrappedTool.Hdc && command is "-v" or "--version" or "version") command = "version";
        else if (knownTool == WrappedTool.Hdc && command is "-h" or "--help" or "help") command = "help";
        else if (knownTool == WrappedTool.Fastboot && command is "--version" or "version" or "-V") command = "version";
        else if (knownTool == WrappedTool.Fastboot && command is "-h" or "--help" or "help") command = "help";
        return new WrappedCommand
        {
            Tool = knownTool,
            Command = command,
            Arguments = arguments,
            Target = serial,
            UseUsb = useUsb,
            UseTcp = useTcp,
            RawLine = line,
        };
    }
}