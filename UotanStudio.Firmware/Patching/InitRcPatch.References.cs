namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Static evidence for named service-control references; not a complete Android init validation result.
/// <para>针对具名服务控制引用的静态检查结果；不是完整的 Android init 校验结论。</para>
/// </summary>
/// <param name="Services">Every service name known to the check: declarations found in the text plus the supplied external
/// names, sorted in ordinal order and deduplicated. <para>检查所知的全部服务名：文本中声明的服务加上外部传入的名称，
/// 按序号顺序排列并已去重。</para></param>
/// <param name="MissingServices">Referenced names that are neither declared in the text nor supplied as external, sorted in
/// ordinal order. <para>既未在文本中声明、也未作为外部名称传入的被引用服务名，按序号顺序排列。</para></param>
/// <param name="DynamicReferences">Source spans of commands whose target argument contains <c>$</c> and therefore cannot be
/// resolved statically. <para>目标参数含有 <c>$</c> 、因而无法静态解析的命令源码片段。</para></param>
/// <param name="InvalidReferences">Source spans of service-control commands with the wrong argument count or an empty target
/// name. <para>参数数量错误或目标名为空的服务控制命令源码片段。</para></param>
/// <param name="AmbiguousServices">Service names declared more than once in the text, sorted in ordinal order.
/// <para>文本中重复声明的服务名，按序号顺序排列。</para></param>
/// <param name="References">Total number of inspected service-control commands, including dynamic and invalid ones.
/// <para>被检查的服务控制命令总数，包含无法静态解析和格式无效的命令。</para></param>
public sealed record InitRcReferenceReport(
    IReadOnlyList<string> Services,
    IReadOnlyList<string> MissingServices,
    IReadOnlyList<string> DynamicReferences,
    IReadOnlyList<string> InvalidReferences,
    IReadOnlyList<string> AmbiguousServices,
    int References)
{
    /// <summary>
    /// Whether every inspected reference is statically resolved without malformed commands or ambiguous declarations.
    /// <para>所有被检查的引用是否都能静态解析，且不存在格式错误的命令或歧义声明。</para>
    /// </summary>
    /// <value><see langword="true"/> when <see cref="MissingServices"/>, <see cref="DynamicReferences"/>,
    /// <see cref="InvalidReferences"/> and <see cref="AmbiguousServices"/> are all empty; otherwise <see langword="false"/>.
    /// <para>当 <see cref="MissingServices"/>、<see cref="DynamicReferences"/>、<see cref="InvalidReferences"/> 和
    /// <see cref="AmbiguousServices"/> 全部为空时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool ReferencesValid => MissingServices.Count == 0 && DynamicReferences.Count == 0
        && InvalidReferences.Count == 0 && AmbiguousServices.Count == 0;
}

/// <summary>
/// Service-reference checking half of the init patcher.
/// <para>init 补丁器中负责服务引用检查的部分。</para>
/// </summary>
public static partial class InitRcPatch
{
    /// <summary>
    /// Checks start/stop/restart/enable/exec_start in actions and service onrestart commands against explicit declarations.
    /// <para>依据显式声明，检查动作段中的 start/stop/restart/enable/exec_start 以及 service 段中的 onrestart 命令。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// InitRcPatch.CheckServiceReferences(text, "init\nueventd", token);
    /// </code>
    /// </example>
    /// <param name="text">Init source bounded by <see cref="TextPatch.MaxCharacters"/> and free of NUL characters; imports are not
    /// expanded, so services declared in imported files must be passed through <paramref name="externalServices"/>.
    /// <para>长度不超过 <see cref="TextPatch.MaxCharacters"/> 且不含 NUL 的 init 源代码；不会展开 import，
    /// 因此在被导入文件中声明的服务必须通过 <paramref name="externalServices"/> 传入。</para></param>
    /// <param name="externalServices">Literal service names declared outside this text, one per line, at most 100000 entries;
    /// blank lines are ignored. <para>在此文本之外声明的字面服务名，每行一个，最多 100000 条；空行会被忽略。</para></param>
    /// <param name="token">Checked while parsing the text and while scanning each declaration and reference.
    /// <para>在解析文本以及扫描每个声明与引用时都会检查。</para></param>
    /// <returns>The ordered declarations and the classified reference findings; a command under <c>onrestart</c> is judged on its
    /// inner command words. <para>排序后的服务声明与分类后的引用检查结果；<c>onrestart</c> 下的命令按其内层命令词判定。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="externalServices"/> is
    /// <see langword="null"/>. <para><paramref name="text"/> 或 <paramref name="externalServices"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The init text does not parse, a text exceeds the size limit, more than 100000
    /// external services are supplied, or an external name is longer than 16384 characters or contains whitespace, NUL or <c>$</c>.
    /// <para>init 文本解析失败、文本超出大小上限、外部服务超过 100000 条，或某个外部名称长度超过 16384 个字符
    /// 或包含空白、NUL 与 <c>$</c>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static InitRcReferenceReport CheckServiceReferences(string text, string externalServices, CancellationToken token)
    {
        var sections = Parse(text, token);
        TextPatch.ValidateSize(externalServices);
        var services = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.Where(section => section.Header.Words[0] == "service"))
        {
            token.ThrowIfCancellationRequested();
            var name = section.Header.Words[1];
            if (!services.Add(name)) { ambiguous.Add(name); }
        }
        var external = externalServices.Split('\n');
        if (external.Length > 100000) { throw new PatchingCorruptedException("Too many external init services."); }
        foreach (var line in external)
        {
            token.ThrowIfCancellationRequested();
            var name = line.Trim();
            if (name.Length == 0) { continue; }
            if (name.Length > 16384 || name.Any(char.IsWhiteSpace) || name.Contains('\0') || name.Contains('$'))
            {
                throw new PatchingCorruptedException("External services must be literal names, one per line.");
            }
            services.Add(name);
        }
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var dynamic = new List<string>();
        var invalid = new List<string>();
        var count = 0;
        foreach (var section in sections)
        {
            foreach (var line in section.Body)
            {
                token.ThrowIfCancellationRequested();
                var command = line.Words;
                if (section.Header.Words[0] == "service")
                {
                    if (command[0] != "onrestart") { continue; }
                    command = command[1..];
                }
                if (command.Length == 0 || command[0] is not ("start" or "stop" or "restart" or "enable" or "exec_start"))
                {
                    continue;
                }
                count++;
                var source = text[line.Start..line.End];
                if (command.Skip(1).Any(argument => argument.Contains('$')))
                {
                    dynamic.Add(source);
                    continue;
                }
                if (command.Length != 2 && !(command[0] == "restart" && command.Length == 3 && command[1] == "--only-if-running")
                    || command[^1].Length == 0)
                {
                    invalid.Add(source);
                    continue;
                }
                if (!services.Contains(command[^1])) { missing.Add(command[^1]); }
            }
        }
        return new InitRcReferenceReport(services.Order(StringComparer.Ordinal).ToArray(), missing.Order(StringComparer.Ordinal).ToArray(),
            dynamic, invalid, ambiguous.Order(StringComparer.Ordinal).ToArray(), count);
    }
}