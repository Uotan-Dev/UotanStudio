using System.Text;
using DtTree = UotanStudio.Firmware.DeviceTree.Dt.DeviceTree;

namespace UotanStudio.Firmware.DeviceTree.Dt;

/// <summary>
/// One diagnostic reported by the DTS parser: a message plus the source position and severity.
/// <para>DTS 解析器报告的单条诊断信息：消息、源码位置与严重级别。</para>
/// </summary>
public class DtsParseError
{
    /// <summary>Human-readable description of the problem.
    /// <para>对问题的人类可读描述。</para></summary>
    /// <value>The message captured at construction, never <see langword="null"/>.
    /// <para>构造时捕获的消息，永不为 <see langword="null"/>。</para></value>
    public string Message { get; }

    /// <summary>Source line the diagnostic points at.
    /// <para>诊断信息指向的源码行号。</para></summary>
    /// <value>1-based line number counted by the parser, including within recovered parses.
    /// <para>解析器统计的从 1 开始的行号，恢复式解析中同样如此。</para></value>
    public int Line { get; }

    /// <summary>Source column the diagnostic points at.
    /// <para>诊断信息指向的源码列号。</para></summary>
    /// <value>1-based column number counted by the parser.
    /// <para>解析器统计的从 1 开始的列号。</para></value>
    public int Column { get; }

    /// <summary>Classification of the diagnostic.
    /// <para>诊断信息的分类。</para></summary>
    /// <value>One of <see cref="DtsParseErrorSeverity"/>; every severity counts the same for <see cref="DtsParseResult.IsSuccess"/>.
    /// <para><see cref="DtsParseErrorSeverity"/> 之一；各级别在 <see cref="DtsParseResult.IsSuccess"/> 中的计法相同。</para></value>
    public DtsParseErrorSeverity Severity { get; }

    /// <summary>
    /// Creates a diagnostic with the given message, position, and severity.
    /// <para>按给定消息、位置与严重级别创建诊断信息。</para>
    /// </summary>
    /// <param name="message">Problem description; must not be <see langword="null"/>. <para>问题描述；不得为 <see langword="null"/>。</para></param>
    /// <param name="line">1-based source line, stored as given. <para>从 1 开始的源码行号，按原样存储。</para></param>
    /// <param name="column">1-based source column, stored as given. <para>从 1 开始的源码列号，按原样存储。</para></param>
    /// <param name="severity">Diagnostic class; defaults to <see cref="DtsParseErrorSeverity.Error"/>. <para>诊断类别；默认为 <see cref="DtsParseErrorSeverity.Error"/>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.
    /// <para><paramref name="message"/> 为 <see langword="null"/>。</para></exception>
    public DtsParseError(string message, int line, int column, DtsParseErrorSeverity severity = DtsParseErrorSeverity.Error)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Line = line;
        Column = column;
        Severity = severity;
    }

    /// <summary>
    /// Renders the diagnostic as <c>[Severity] Line &lt;n&gt;, Col &lt;n&gt;: Message</c>.
    /// <para>将诊断信息渲染为 <c>[Severity] Line &lt;n&gt;, Col &lt;n&gt;: Message</c> 形式。</para>
    /// </summary>
    /// <returns>One line carrying the severity, the 1-based position and the message.
    /// <para>包含严重级别、从 1 开始的位置与消息的单行文本。</para></returns>
    public override string ToString()
        => $"[{Severity}] Line {Line}, Col {Column}: {Message}";
}

/// <summary>
/// Severity classes the DTS parser attaches to diagnostics.
/// <para>DTS 解析器为诊断信息划分的严重级别。</para>
/// </summary>
public enum DtsParseErrorSeverity
{
    /// <summary>Suspicious construct the parser tolerated. <para>解析器容忍的可疑构造。</para></summary>
    Warning,
    /// <summary>Invalid syntax at the reported position. <para>所报告位置处的无效语法。</para></summary>
    Error,
    /// <summary>Exception that ended the whole parse; it is recorded here rather than reaching the caller.
    /// <para>结束整个解析流程的异常；该异常记录在此而不会抛给调用方。</para></summary>
    Fatal
}

/// <summary>
/// Outcome of a DTS parse: the tree built so far (possibly partial) and every diagnostic collected.
/// <para>DTS 解析的结果：目前已构建的树（可能不完整）以及收集到的全部诊断信息。</para>
/// </summary>
public class DtsParseResult
{
    /// <summary>Tree produced by the parse.
    /// <para>解析生成的树。</para></summary>
    /// <value><see langword="null"/> when the parse produced no structure at all; otherwise the complete or partial tree.
    /// <para>解析完全没有产出结构时为 <see langword="null"/>；否则为完整或部分树。</para></value>
    public DtTree? Tree { get; }

    /// <summary>Diagnostics recorded during the parse, in the order encountered.
    /// <para>解析期间记录的诊断信息，按遇到顺序排列。</para></summary>
    /// <value>Mutable list; an empty list means no problems were reported. Never <see langword="null"/>.
    /// <para>可变列表；空列表表示未报告任何问题，永不为 <see langword="null"/>。</para></value>
    public List<DtsParseError> Errors { get; }

    /// <summary>Whether the parse finished without any diagnostic.
    /// <para>解析是否未产生任何诊断信息而结束。</para></summary>
    /// <value><see langword="true"/> when <see cref="Errors"/> is empty; even one <see cref="DtsParseErrorSeverity.Warning"/> makes it <see langword="false"/>.
    /// <para><see cref="Errors"/> 为空时为 <see langword="true"/>；仅有一条 <see cref="DtsParseErrorSeverity.Warning"/> 也会使其为 <see langword="false"/>。</para></value>
    public bool IsSuccess => Errors.Count == 0;

    /// <summary>Whether a usable tree exists alongside reported problems.
    /// <para>是否存在可与报告问题并存且可用的树。</para></summary>
    /// <value><see langword="true"/> when <see cref="Tree"/> is not <see langword="null"/> and <see cref="Errors"/> is not empty.
    /// <para><see cref="Tree"/> 不为 <see langword="null"/> 且 <see cref="Errors"/> 非空时为 <see langword="true"/>。</para></value>
    public bool HasPartialTree => Tree != null && Errors.Count > 0;

    /// <summary>
    /// Bundles a parse outcome: the tree produced and the diagnostics collected.
    /// <para>封装一次解析结果：生成的树与收集的诊断信息。</para>
    /// </summary>
    /// <param name="tree">Tree built by the parser; <see langword="null"/> when nothing could be built.
    /// <para>解析器构建的树；无法构建任何内容时为 <see langword="null"/>。</para></param>
    /// <param name="errors">Diagnostics list; <see langword="null"/> is replaced by an empty list.
    /// <para>诊断列表；为 <see langword="null"/> 时改用空列表替代。</para></param>
    public DtsParseResult(DtTree? tree, List<DtsParseError> errors)
    {
        Tree = tree;
        Errors = errors ?? new List<DtsParseError>();
    }
}

/// <summary>
/// Knob set controlling how a <see cref="DtTree"/> is serialized back to DTS text.
/// <para>控制如何把 <see cref="DtTree"/> 序列化回 DTS 文本的开关集合。</para>
/// </summary>
public sealed class DtsWriteOptions
{
    /// <summary>Request that cross-node references be written through phandles.
    /// <para>请求通过 phandle 写出跨节点引用。</para></summary>
    /// <value>Writable flag, default <see langword="true"/>; the current DTS writer does not read it, so it does not alter emitted text.
    /// <para>可写开关，默认 <see langword="true"/>；当前 DTS 写出器并不读取它，因此不会改变输出文本。</para></value>
    public bool UsePhandleReferences { get; set; } = true;

    /// <summary>Request that parsed comments be emitted with the tree.
    /// <para>请求在输出树的同时写出解析到的注释。</para></summary>
    /// <value>Writable flag, default <see langword="true"/>; the current DTS writer does not read it, so it does not alter emitted text.
    /// <para>可写开关，默认 <see langword="true"/>；当前 DTS 写出器并不读取它，因此不会改变输出文本。</para></value>
    public bool IncludeComments { get; set; } = true;

    /// <summary>Request that each node's properties be ordered by name instead of kept in insertion order.
    /// <para>请求将每个节点的属性按名称排序，而不是保持插入顺序。</para></summary>
    /// <value>Writable flag, default <see langword="false"/>; when <see langword="true"/>, output sorts properties by <c>Name</c>.
    /// <para>可写开关，默认 <see langword="false"/>；为 <see langword="true"/> 时输出按 <c>Name</c> 排序属性。</para></value>
    public bool SortProperties { get; set; }

    /// <summary>Request that cell arrays be written as hexadecimal values.
    /// <para>请求以十六进制值写出单元数组。</para></summary>
    /// <value>Writable flag, default <see langword="true"/>; when <see langword="false"/>, cells are emitted as decimal numbers.
    /// <para>可写开关，默认 <see langword="true"/>；为 <see langword="false"/> 时以十进制数输出单元。</para></value>
    public bool UseHexForCells { get; set; } = true;

    /// <summary>Width of one indentation level in spaces.
    /// <para>一个缩进级别的空格宽度。</para></summary>
    /// <value>Writable, default <c>0</c>; <c>0</c> makes the writer indent each level with one tab character.
    /// <para>可写，默认 <c>0</c>；为 <c>0</c> 时写出器用一个制表符缩进每个级别。</para></value>
    public int IndentSize { get; set; }

    /// <summary>Shared options instance carrying the defaults above.
    /// <para>携带上述默认值的共享选项实例。</para></summary>
    /// <value>One process-wide instance, mutable like any other <see cref="DtsWriteOptions"/>; used by the parameterless overloads of <see cref="DtsFormat"/>.
    /// <para>进程范围内的单一实例，与任意 <see cref="DtsWriteOptions"/> 一样可变；供 <see cref="DtsFormat"/> 的无参重载使用。</para></value>
    public static DtsWriteOptions Default { get; } = new();
}

/// <summary>
/// Static facade for parsing, writing, and merging DTS source text.
/// <para>解析、写出与合并 DTS 源文本的静态门面。</para>
/// </summary>
public static class DtsFormat
{
    /// <summary>
    /// Parses DTS source text as given, without include or macro preprocessing, collecting but not recovering from errors.
    /// <para>按原样解析 DTS 源文本，不做 include 或宏预处理，收集错误但不从中恢复。</para>
    /// </summary>
    /// <param name="text">Complete DTS document. <para>完整的 DTS 文档。</para></param>
    /// <returns>A result carrying the parsed tree and every diagnostic; <see cref="DtsParseResult.IsSuccess"/> only when nothing was reported.
    /// <para>包含解析出的树与全部诊断信息的结果；仅在未报告任何问题时 <see cref="DtsParseResult.IsSuccess"/> 为真。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <example>
    /// <code>var result = DtsFormat.Parse("/ { compatible = \"acme,board\"; };");
    /// if (result.Tree is not null &amp;&amp; result.IsSuccess) { /* use result.Tree */ }</code>
    /// </example>
    public static DtsParseResult Parse(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var parser = new DtsParser(text);
        return parser.ParseWithErrorCapture(useRecovery: false);
    }

    /// <summary>
    /// Preprocesses C-style directives (<c>#define</c>, <c>#undef</c>, <c>#include</c>, <c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>/<c>#else</c>/<c>#endif</c>) and then parses the expanded text.
    /// <para>先预处理 C 风格指令（<c>#define</c>、<c>#undef</c>、<c>#include</c>、<c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>/<c>#else</c>/<c>#endif</c>），再解析展开后的文本。</para>
    /// </summary>
    /// <param name="text">DTS source that may contain directives. <para>可能包含指令的 DTS 源文本。</para></param>
    /// <param name="includePaths">Directories searched for quoted or angle-bracketed includes after the literal path fails; <see langword="null"/> means none.
    /// <para>在按字面路径解析失败后用于搜索引号或尖括号 include 的目录；为 <see langword="null"/> 表示无。</para></param>
    /// <param name="defines">Initial macro names to values; <see langword="null"/> starts with no macros. Keys compare case-sensitively.
    /// <para>初始宏名到值的映射；为 <see langword="null"/> 时不含任何宏。键区分大小写。</para></param>
    /// <returns>A result carrying the parsed tree and every diagnostic; directives resolved against the filesystem before parsing.
    /// <para>包含解析出的树与全部诊断信息的结果；指令在解析前先针对文件系统完成解析。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The <c>#include</c> recursion depth exceeds 16.
    /// <para><c>#include</c> 递归深度超过 16。</para></exception>
    public static DtsParseResult Parse(string text, IEnumerable<string>? includePaths, Dictionary<string, string>? defines = null)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var preprocessed = Preprocess(text, includePaths, defines);
        var parser = new DtsParser(preprocessed);
        parser.SetIncludePaths(includePaths);
        return parser.ParseWithErrorCapture(useRecovery: false);
    }

    /// <summary>
    /// Parses DTS source text with error recovery, keeping going after failures so a partial tree can survive alongside the diagnostics.
    /// <para>以错误恢复方式解析 DTS 源文本，在失败后继续，从而让部分树与诊断信息一同保留。</para>
    /// </summary>
    /// <param name="text">DTS document that may be malformed. <para>可能格式不正确的 DTS 文档。</para></param>
    /// <returns>A result whose <see cref="DtsParseResult.Tree"/> may be partial and whose errors carry one <see cref="DtsParseErrorSeverity.Error"/> entry per statement that could not be read.
    /// <para>其 <see cref="DtsParseResult.Tree"/> 可能不完整的结果；每个无法读取的语句对应一条 <see cref="DtsParseErrorSeverity.Error"/> 条目。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    public static DtsParseResult ParseWithRecovery(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var parser = new DtsParser(text);
        return parser.ParseWithErrorCapture(useRecovery: true);
    }

    /// <summary>
    /// Serializes a device tree to DTS text using <see cref="DtsWriteOptions.Default"/>.
    /// <para>使用 <see cref="DtsWriteOptions.Default"/> 将设备树序列化为 DTS 文本。</para>
    /// </summary>
    /// <param name="tree">Tree to write. <para>要写出的树。</para></param>
    /// <returns>The complete DTS document as one string. <para>以单个字符串返回的完整 DTS 文档。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is <see langword="null"/>.
    /// <para><paramref name="tree"/> 为 <see langword="null"/>。</para></exception>
    /// <example>
    /// <code>var tree = new DtTree();
    /// tree.Root.AddPropertyString("compatible", "acme,board");
    /// string dts = DtsFormat.Write(tree);</code>
    /// </example>
    public static string Write(DtTree tree)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        var writer = new DtsWriter(DtsWriteOptions.Default);
        return writer.Write(tree);
    }

    /// <summary>
    /// Serializes a device tree to DTS text with explicit formatting options.
    /// <para>按显式格式选项将设备树序列化为 DTS 文本。</para>
    /// </summary>
    /// <param name="tree">Tree to write. <para>要写出的树。</para></param>
    /// <param name="options">Formatting switches consulted by the writer. <para>写出器会读取的格式开关。</para></param>
    /// <returns>The complete DTS document as one string. <para>以单个字符串返回的完整 DTS 文档。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="tree"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    public static string Write(DtTree tree, DtsWriteOptions options)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        var writer = new DtsWriter(options);
        return writer.Write(tree);
    }

    /// <summary>
    /// Writes DTS and returns a map of node path to the 1-based line of its '{'.
    /// <para>序列化DTS并返回 节点路径-&gt;开括号行号 映射。</para>
    /// </summary>
    /// <param name="tree">Tree to serialize with <see cref="DtsWriteOptions.Default"/>. <para>以 <see cref="DtsWriteOptions.Default"/> 序列化的树。</para></param>
    /// <returns>Fresh dictionary keyed by case-sensitive node path, each value the line on which that node's opening brace was written.
    /// <para>以区分大小写的节点路径为键的新字典；每个值为该节点开括号被写入的行号。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is <see langword="null"/>.
    /// <para><paramref name="tree"/> 为 <see langword="null"/>。</para></exception>
    public static Dictionary<string, int> GetNodeLineMap(DtTree tree)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        var writer = new DtsWriter(DtsWriteOptions.Default);
        writer.Write(tree);
        return new Dictionary<string, int>(writer.NodeStartLines);
    }

    /// <summary>
    /// Merges the overlay tree into the base tree and returns the merged base tree.
    /// <para>将 overlay 树合并到基树并返回合并后的基树。</para>
    /// </summary>
    /// <param name="baseTree">Tree that receives properties and children from the overlay. <para>接收 overlay 的属性与子节点的目标基树。</para></param>
    /// <param name="overlayTree">Tree whose content is merged into the base. <para>其内容将被合并进基树的树。</para></param>
    /// <returns>
    /// The same <paramref name="baseTree"/> instance with its root content rebuilt from merged copies, so node
    /// references taken from the base tree before the call do not appear in the merged tree;
    /// <paramref name="overlayTree"/> is left untouched.
    /// <para>与 <paramref name="baseTree"/> 相同的实例，其根内容按合并后的副本重建，因此调用前从基树取得的节点引用不会出现在合并后的树中；<paramref name="overlayTree"/> 保持不变。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseTree"/> or <paramref name="overlayTree"/> is <see langword="null"/>.
    /// <para><paramref name="baseTree"/> 或 <paramref name="overlayTree"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The overlay cannot be applied: a fragment target is absent or out of range, a phandle would overflow, or a fixup symbol or cell offset does not resolve.
    /// <para>overlay 无法应用：fragment 目标缺失或越界、phandle 将溢出，或 fixup 符号与单元偏移无法解析。</para></exception>
    public static DtTree MergeOverlay(DtTree baseTree, DtTree overlayTree)
    {
        if (baseTree is null)
            throw new ArgumentNullException(nameof(baseTree));
        if (overlayTree is null)
            throw new ArgumentNullException(nameof(overlayTree));
        return DtOverlayApplier.Apply(baseTree, overlayTree);
    }

    /// <summary>
    /// Recursively merges the properties and children of the source node into the target node.
    /// <para>将 source 节点的属性与子节点递归合并到 target 节点。</para>
    /// </summary>
    internal static void MergeNode(
        DeviceTreeNode target,
        DeviceTreeNode source,
        IReadOnlyDictionary<DeviceTreeNode, List<string>>? deletedProperties = null,
        IReadOnlyDictionary<DeviceTreeNode, List<string>>? deletedChildren = null)
    {
        if (deletedProperties is not null && deletedProperties.TryGetValue(source, out var deletedPropNames))
        {
            foreach (var name in deletedPropNames)
                target.RemoveProperty(name);
        }
        if (deletedChildren is not null && deletedChildren.TryGetValue(source, out var deletedChildNames))
        {
            foreach (var name in deletedChildNames)
                target.RemoveChild(name);
        }

        foreach (var prop in source.Properties)
        {
            var existing = target.FindProperty(prop.Name);
            if (existing is null) target.AddProperty(prop.Name, prop.Value);
            else existing.ReplaceValue(prop.Value);
        }

        foreach (var child in source.Children.ToList())
        {
            var existing = target.FindChild(child.Name);
            if (existing is null)
            {
                source.RemoveChild(child);
                target.AddChildNode(child);
            }
            else MergeNode(existing, child, deletedProperties, deletedChildren);
        }
    }


    private const int MaxIncludeDepth = 16;

    private static string Preprocess(string text, IEnumerable<string>? includePaths, Dictionary<string, string>? defines, int depth = 0)
    {
        if (depth > MaxIncludeDepth)
            throw new InvalidOperationException($"#include recursion depth exceeded {MaxIncludeDepth}");

        var macros = new Dictionary<string, string>(defines ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var includeDirs = includePaths?.ToList() ?? new List<string>();
        var result = new StringBuilder(text.Length);
        var lines = text.Split('\n');
        var active = true;
        var pendingIfStack = new Stack<bool>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var body = trimmed.TrimStart('#').Trim();
                var (directive, arg) = SplitDirective(body);
                var consumed = true;
                switch (directive)
                {
                    case "define":
                        var argTrim = arg.Trim();
                        var sep = argTrim.IndexOfAny([' ', '\t']);
                        if (sep < 0) macros[argTrim] = string.Empty;
                        else { var nm = argTrim.Substring(0, sep).Trim(); var vl = argTrim.Substring(sep).Trim(); macros[nm] = vl; }
                        break;
                    case "undef":
                        macros.Remove(arg.Trim());
                        break;
                    case "include":
                        if (!active) break;
                        var filePath = ExtractIncludePath(arg);
                        var resolved = ResolveIncludePath(filePath, includeDirs);
                        if (resolved != null && File.Exists(resolved))
                        {
                            var included = File.ReadAllText(resolved);
                            result.Append(Preprocess(included, includeDirs, new Dictionary<string, string>(macros), depth + 1));
                            result.Append('\n');
                        }
                        break;
                    case "if":
                    case "ifdef":
                    case "ifndef":
                        pendingIfStack.Push(active);
                        active = active && EvalIf(directive, arg, macros);
                        break;
                    case "else":
                        if (pendingIfStack.Count > 0)
                        {
                            var wasActive = pendingIfStack.Pop();
                            active = wasActive && !active;
                            pendingIfStack.Push(wasActive);
                        }
                        break;
                    case "endif":
                        if (pendingIfStack.Count > 0)
                            active = pendingIfStack.Pop();
                        break;
                    default:
                        consumed = false;
                        break;
                }
                if (consumed)
                    continue;
            }

            if (!active)
                continue;

            result.Append(ExpandMacrosInLine(raw, macros));
            result.Append('\n');
        }

        return result.ToString();
    }

    private static bool EvalIf(string directive, string arg, Dictionary<string, string> macros)
    {
        var expr = arg.Trim();
        if (directive == "ifdef") return macros.ContainsKey(expr);
        if (directive == "ifndef") return !macros.ContainsKey(expr);

        var work = expr;
        var negated = false;
        var didx = work.IndexOf("defined(", StringComparison.Ordinal);
        if (didx >= 0)
        {
            if (didx > 0 && work[didx - 1] == '!') negated = true;
            var start = didx + "defined(".Length;
            var end = work.IndexOf(')', start);
            if (end > start)
            {
                var name = work.Substring(start, end - start).Trim();
                var defined = macros.ContainsKey(name);
                return negated ? !defined : defined;
            }
        }

        work = ExpandMacrosInLine(expr, macros).Trim();
        if (long.TryParse(work, out var num)) return num != 0;
        return work.Length > 0;
    }

    private static (string directive, string arg) SplitDirective(string body)
    {
        if (body.Length == 0) return (string.Empty, string.Empty);
        var sep = body.IndexOfAny([' ', '\t']);
        if (sep < 0) return (body, string.Empty);
        return (body.Substring(0, sep), body.Substring(sep + 1));
    }

    private static string ExtractIncludePath(string arg)
    {
        var argTrim = arg.Trim();
        if (argTrim.Length >= 2 && (argTrim[0] == '"' || argTrim[0] == '<'))
        {
            var closing = argTrim[0] == '"' ? '"' : '>';
            var end = argTrim.Length - 1;
            if (end > 0 && argTrim[end] == closing)
                return argTrim.Substring(1, argTrim.Length - 2);
        }
        return argTrim;
    }

    private static string? ResolveIncludePath(string filePath, List<string> includeDirs)
    {
        if (File.Exists(filePath)) return Path.GetFullPath(filePath);
        foreach (var dir in includeDirs)
        {
            var fullPath = Path.Combine(dir, filePath);
            if (File.Exists(fullPath)) return Path.GetFullPath(fullPath);
        }
        return null;
    }

    private static string ExpandMacrosInLine(string line, Dictionary<string, string> macros)
    {
        if (macros.Count == 0) return line;
        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                var name = line.Substring(start, i - start);
                if (macros.TryGetValue(name, out var value)) sb.Append(value);
                else sb.Append(name);
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }
        return sb.ToString();
    }
}