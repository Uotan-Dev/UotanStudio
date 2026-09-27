namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Native;

/// <summary>
/// Read-only SELinux policy analysis through the external <c>sesearch</c> and <c>seinfo</c> binaries, composed
/// over <see cref="NativeFileSystemTools"/> so Windows runs them inside the configured WSL distribution and no
/// second process path exists. libsepol-derived binaries are consumed strictly as external processes (their
/// LGPL-2.1 terms apply to the shipped tool, not to this assembly); this class never writes a policy, never
/// recompiles CIL, and every mutation path stays with <c>SelinuxSwitchService</c> in the boot-security workflow.
/// CIL recompilation is deliberately out of scope. Captured output is bounded by the runner at 16384 characters
/// and the parsed rule count is bounded by <see cref="NativeSepolicyTools.MaximumRules"/>.
/// <para>通过外部的 <c>sesearch</c> 与 <c>seinfo</c> 二进制对 SELinux 策略做只读分析，组合（而非继承）
/// <see cref="NativeFileSystemTools"/>，因此 Windows 在配置的 WSL 发行版内运行工具，且不存在第二条进程启动路径。
/// 基于 libsepol 的二进制仅作为外部进程消费（其 LGPL-2.1 条款约束随附的工具而非本程序集）；本类从不写策略、
/// 从不重编译 CIL，所有变更路径都保留在 boot-security 工作流的 <c>SelinuxSwitchService</c>。CIL 重编译明确不在范围内。
/// 捕获输出由运行器限制在 16384 字符内，解析的规则数量受 <see cref="NativeSepolicyTools.MaximumRules"/> 约束。</para>
/// </summary>
/// <param name="tools">Runner and path translator shared with the filesystem tools.
/// <para>与文件系统工具共用的运行器与路径转换器。</para></param>
public sealed class NativeSepolicyTools(NativeFileSystemTools tools)
{
    /// <summary>Upper bound on parsed allow-rule lines; exceeding it fails the query. <para>解析的允许规则行数上限；超出即判定查询失败。</para></summary>
    public const int MaximumRules = 4096;

    private const int MaximumSelectorLength = 128;

    /// <summary>
    /// Runs <c>sesearch -A</c> against a binary policy file plus the optional <c>-s</c>, <c>-t</c>, <c>-c</c> and
    /// <c>-x</c> selectors, then parses the printed <c>allow</c>, <c>audit_allow</c> and <c>dontaudit</c> lines into
    /// rules. Selector values are validated as bare type tokens before the process starts; untrusted selectors
    /// containing control characters, whitespace or path syntax are rejected. The tool is read-only by design and
    /// this wrapper never passes a writable policy path.
    /// <para>针对二进制策略文件运行 <c>sesearch -A</c>，附带可选的 <c>-s</c>、<c>-t</c>、<c>-c</c>、<c>-x</c> 选择器，
    /// 然后把打印出的 <c>allow</c>、<c>audit_allow</c> 与 <c>dontaudit</c> 行解析为规则。选择器值在进程启动前按裸类型
    /// 令牌校验；含控制字符、空白或路径语法的不可信选择器会被拒绝。该工具在设计上只读，本包装从不传入可写的策略路径。</para>
    /// </summary>
    /// <param name="policyPath">Host path of the binary policy file (for example a pre-CIL <c>sepolicy</c>); the file must exist.
    /// <para>二进制策略文件的宿主路径（例如 pre-CIL 的 <c>sepolicy</c>）；文件必须存在。</para></param>
    /// <param name="query">Optional source type, target type, object class and permission selectors; <see langword="null"/> selects every allow rule.
    /// <para>可选的源类型、目标类型、object class 与权限选择器；为 <see langword="null"/> 时返回全部允许规则。</para></param>
    /// <param name="cancellationToken">Cancels path translation and the tool run.
    /// <para>取消路径转换与工具运行。</para></param>
    /// <returns>The parsed rules; more than <see cref="MaximumRules"/> lines fail the query instead of truncating silently.
    /// <para>解析得到的规则；行数超过 <see cref="MaximumRules"/> 时直接判定查询失败，而不是静默截断。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="policyPath"/> is <see langword="null"/>.
    /// <para><paramref name="policyPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The policy file does not exist.
    /// <para>策略文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">A selector token is malformed, <c>sesearch</c> failed via <see cref="NativeFileSystemTools.RequireSuccess"/>, or the rule count exceeds <see cref="MaximumRules"/>.
    /// <para>选择器令牌格式错误、<c>sesearch</c> 经 <see cref="NativeFileSystemTools.RequireSuccess"/> 判定失败，或规则数超过 <see cref="MaximumRules"/>。</para></exception>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<IReadOnlyList<SelinuxAllowRule>> QueryAllowRulesAsync(string policyPath,
        SelinuxAllowQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyPath);
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException("SELinux policy file was not found.", policyPath);
        }
        var arguments = new List<string> { "-A" };
        AppendSelector(arguments, "-s", query?.SourceType);
        AppendSelector(arguments, "-t", query?.TargetType);
        AppendSelector(arguments, "-c", query?.ObjectClass);
        AppendSelector(arguments, "-x", query?.Permission);
        arguments.Add(tools.PathForTool(policyPath, cancellationToken));

        var result = await tools.RunAsync("sesearch", arguments, cancellationToken).ConfigureAwait(false);
        NativeFileSystemTools.RequireSuccess("sesearch", result);
        var rules = new List<SelinuxAllowRule>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SelinuxAllowRule.TryParse(line.Trim(), out var rule) && rule is not null)
            {
                rules.Add(rule);
                if (rules.Count > MaximumRules)
                {
                    throw new InvalidDataException($"sesearch reported more than {MaximumRules} rules.");
                }
            }
        }
        return rules;
    }

    /// <summary>
    /// Runs the read-only <c>seinfo -a</c> summary (types, attributes, roles and users) over a binary policy file
    /// and returns the raw <see cref="NativeToolResult"/> without interpretation. When <c>seinfo</c> is not
    /// installed the tool run ends with a non-zero exit code (typically 127) and this method surfaces it through
    /// <see cref="NativeFileSystemTools.RequireSuccess"/>; callers on distributions without the package should
    /// treat that as an unsupported environment, not as policy corruption.
    /// <para>对二进制策略文件运行只读的 <c>seinfo -a</c> 汇总（类型、属性、角色与用户），原样返回
    /// <see cref="NativeToolResult"/>，不做解释。未安装 <c>seinfo</c> 时工具以非零退出码结束（通常为 127），
    /// 本方法经 <see cref="NativeFileSystemTools.RequireSuccess"/> 抛出；在未装该包的发行版上调用方应把它视为
    /// 环境不支持，而不是策略损坏。</para>
    /// </summary>
    /// <param name="policyPath">Host path of the binary policy file; the file must exist.
    /// <para>二进制策略文件的宿主路径；文件必须存在。</para></param>
    /// <param name="cancellationToken">Cancels path translation and the tool run.
    /// <para>取消路径转换与工具运行。</para></param>
    /// <returns>The raw summary output of <c>seinfo</c>. <para><c>seinfo</c> 的原始汇总输出。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="policyPath"/> is <see langword="null"/>.
    /// <para><paramref name="policyPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The policy file does not exist.
    /// <para>策略文件不存在。</para></exception>
    /// <exception cref="InvalidDataException"><c>seinfo</c> failed via <see cref="NativeFileSystemTools.RequireSuccess"/>.
    /// <para><c>seinfo</c> 经 <see cref="NativeFileSystemTools.RequireSuccess"/> 判定失败。</para></exception>
    /// <exception cref="PlatformNotSupportedException">Windows without a configured WSL distribution.
    /// <para>Windows 下未配置 WSL 发行版。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<NativeToolResult> QueryPolicySummaryAsync(string policyPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyPath);
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException("SELinux policy file was not found.", policyPath);
        }
        var imagePath = tools.PathForTool(policyPath, cancellationToken);
        var result = await tools.RunAsync("seinfo", ["-a", imagePath], cancellationToken).ConfigureAwait(false);
        NativeFileSystemTools.RequireSuccess("seinfo", result);
        return result;
    }

    private static void AppendSelector(List<string> arguments, string flag, string? selector)
    {
        if (selector is null)
        {
            return;
        }
        if (selector.Length is 0 or > MaximumSelectorLength
            || !selector.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':'))
        {
            throw new InvalidDataException($"The sesearch selector '{flag}' requires a bare type token.");
        }
        arguments.Add(flag);
        arguments.Add(selector);
    }
}

/// <summary>
/// Optional <c>sesearch</c> selectors restricting an allow-rule query; every <see langword="null"/> member
/// leaves its dimension unconstrained, mirroring the tool's own defaults.
/// <para>可选的 <c>sesearch</c> 选择器，用于限定允许规则查询；为 <see langword="null"/> 的成员表示该维度不设限，
/// 与工具自身默认行为一致。</para>
/// </summary>
public sealed record SelinuxAllowQuery
{
    /// <summary>Source security type matched by <c>-s</c>. <para>经 <c>-s</c> 匹配的源安全类型。</para></summary>
    public string? SourceType { get; init; }

    /// <summary>Target security type matched by <c>-t</c>. <para>经 <c>-t</c> 匹配的目标安全类型。</para></summary>
    public string? TargetType { get; init; }

    /// <summary>Object class matched by <c>-c</c>. <para>经 <c>-c</c> 匹配的 object class。</para></summary>
    public string? ObjectClass { get; init; }

    /// <summary>Permission matched by <c>-x</c>. <para>经 <c>-x</c> 匹配的权限。</para></summary>
    public string? Permission { get; init; }
}

/// <summary>
/// One parsed <c>sesearch</c> rule line such as <c>allow dom file:file { read write };</c>.
/// <para>一条解析后的 <c>sesearch</c> 规则行，例如 <c>allow dom file:file { read write };</c>。</para>
/// </summary>
/// <param name="Source">Source security type. <para>源安全类型。</para></param>
/// <param name="Target">Target security type. <para>目标安全类型。</para></param>
/// <param name="ObjectClass">Object class, or comma-separated classes as printed by the tool. <para>object class，或工具打印的逗号分隔多类。</para></param>
/// <param name="Permissions">Permission identifiers of the rule. <para>规则的权限标识。</para></param>
/// <param name="IsDontAudit"><see langword="true"/> when the line is a <c>dontaudit</c> rule rather than an allow or audit_allow rule.
/// <para>该行为 <c>dontaudit</c>（而非 allow 或 audit_allow）规则时为 <see langword="true"/>。</para></param>
public sealed record SelinuxAllowRule(string Source, string Target, string ObjectClass,
    IReadOnlyList<string> Permissions, bool IsDontAudit)
{
    /// <summary>
    /// Parses one <c>sesearch</c> output line; unrecognised lines (usage text, summaries, blank rules) return
    /// <see langword="false"/> without throwing so the bounded caller loop keeps a predictable cost.
    /// <para>解析一条 <c>sesearch</c> 输出行；无法识别的行（用法说明、汇总、空规则）返回 <see langword="false"/>
    /// 且不抛异常，使有界调用循环的开销保持可预期。</para>
    /// </summary>
    /// <param name="line">A single trimmed output line. <para>已去除首尾空白的单行输出。</para></param>
    /// <param name="rule">The parsed rule when the method returns <see langword="true"/>. <para>方法返回 <see langword="true"/> 时解析得到的规则。</para></param>
    /// <returns><see langword="true"/> when the line is a recognised allow, audit_allow or dontaudit rule.
    /// <para>该行为可识别的 allow、audit_allow 或 dontaudit 规则时返回 <see langword="true"/>。</para></returns>
    public static bool TryParse(string line, out SelinuxAllowRule? rule)
    {
        rule = null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }
        bool isDontAudit;
        if (parts[0] == "dontaudit")
        {
            isDontAudit = true;
        }
        else if (parts[0] is "allow" or "audit_allow")
        {
            isDontAudit = false;
        }
        else
        {
            return false;
        }
        if (!parts[2].Contains(':'))
        {
            return false;
        }
        if (isDontAudit && parts[1] == "from")
        {
            return false;
        }
        var classSplit = parts[2].Split(':', 2);
        var permissions = parts[3..]
            .Select(token => token.Trim(';', '{', '}', '(', ')'))
            .Where(token => token.Length > 0)
            .ToArray();
        if (classSplit[0].Length == 0 || classSplit[1].Length == 0 || permissions.Length == 0)
        {
            return false;
        }
        rule = new SelinuxAllowRule(parts[1], classSplit[0], classSplit[1], permissions, isDontAudit);
        return true;
    }
}