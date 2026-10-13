using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// Small, deliberately conservative parser for the shell DSL shipped inside root packages.
/// It understands the file operations used by root installer scripts without executing shell code.
/// <para>一个刻意保守的小型解析器，用于根包内置的 shell DSL。它理解根安装脚本使用的文件操作，且不执行任何 shell 代码。</para>
/// </summary>
internal static partial class BootPatchScriptParser
{
    /// <summary>
    /// Extracts recognized file operations from patch script text line by line, skipping comments and blanks.
    /// <para>逐行从补丁脚本文本中提取可识别的文件操作，跳过注释与空行。</para>
    /// </summary>
    /// <param name="script">Full script text; trailing shell comments are stripped before matching.
    /// <para>完整脚本文本；匹配前会剥离行尾的 shell 注释。</para></param>
    /// <returns>Operations in source order with their 1-based line numbers; empty when nothing matches.
    /// <para>按源码顺序返回操作及其 1 基行号；无匹配时为空。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> is <see langword="null"/>.
    /// <para><paramref name="script"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<BootPatchScriptOperation> Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var result = new List<BootPatchScriptOperation>();
        var lineNo = 0;
        foreach (var raw in script.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            line = ShellCommentRegex().Replace(line, string.Empty).Trim();
            var match = OperationRegex().Match(line);
            if (!match.Success) continue;
            var args = SplitArguments(match.Groups[2].Value);
            result.Add(new BootPatchScriptOperation(match.Groups[1].Value, args, lineNo));
        }
        return result;
    }

    private static List<string> SplitArguments(string value)
    {
        var list = new List<string>();
        foreach (Match m in ArgumentRegex().Matches(value))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return list;
    }

    [GeneratedRegex(@"^\s*(?:command\s+)?(cpio|patch|hexpatch|set_perm|set_perm_recursive|install_module|apply_patch)\b(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OperationRegex();
    [GeneratedRegex(@"(?:'([^']*)'|""([^""]*)""|(\S+))")]
    private static partial Regex ArgumentRegex();
    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex ShellCommentRegex();
}