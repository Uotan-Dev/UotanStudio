using DtTree = UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models.DeviceTree;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Overlay;


using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dts;

public class DtsParseError
{
    public string Message { get; }
    public int Line { get; }
    public int Column { get; }
    public DtsParseErrorSeverity Severity { get; }

    public DtsParseError(string message, int line, int column, DtsParseErrorSeverity severity = DtsParseErrorSeverity.Error)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Line = line;
        Column = column;
        Severity = severity;
    }

    public override string ToString()
        => $"[{Severity}] Line {Line}, Col {Column}: {Message}";
}

public enum DtsParseErrorSeverity
{
    Warning,
    Error,
    Fatal
}

public class DtsParseResult
{
    public DtTree? Tree { get; }
    public List<DtsParseError> Errors { get; }
    public bool IsSuccess => Errors.Count == 0;
    public bool HasPartialTree => Tree != null && Errors.Count > 0;

    public DtsParseResult(DtTree? tree, List<DtsParseError> errors)
    {
        Tree = tree;
        Errors = errors ?? new List<DtsParseError>();
    }
}

public sealed class DtsWriteOptions
{
    public bool UsePhandleReferences { get; set; } = true;
    public bool IncludeComments { get; set; } = true;
    public bool SortProperties { get; set; }
    public bool UseHexForCells { get; set; } = true;
    public int IndentSize { get; set; }
    public static DtsWriteOptions Default { get; } = new();
}

public static class DtsFormat
{
    public static DtsParseResult Parse(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var parser = new DtsParser(text);
        return parser.ParseWithErrorCapture(useRecovery: false);
    }

    public static DtsParseResult Parse(string text, IEnumerable<string>? includePaths, Dictionary<string, string>? defines = null)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var preprocessed = Preprocess(text, includePaths, defines);
        var parser = new DtsParser(preprocessed);
        parser.SetIncludePaths(includePaths);
        return parser.ParseWithErrorCapture(useRecovery: false);
    }

    public static DtsParseResult ParseWithRecovery(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));
        var parser = new DtsParser(text);
        return parser.ParseWithErrorCapture(useRecovery: true);
    }

    public static string Write(DtTree tree)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        var writer = new DtsWriter(DtsWriteOptions.Default);
        return writer.Write(tree);
    }

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
                        var sep = argTrim.IndexOfAny(new[] { ' ', '\t' });
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
        var sep = body.IndexOfAny(new[] { ' ', '\t' });
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