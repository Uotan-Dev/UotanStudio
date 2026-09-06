// =============================================================================
// DTS (Device Tree Source) text parsing and serialization for UotanStudio.Firmware.
//
// NOTICE:
// This file is a port/adaptation of the DTS parser+writer from the open-source
// project "FirmwareKit.DT" (FirmwareKit, https://github.com/Uotan-Dev/FirmwareKit),
// Copyright (c) Uotan-Dev, licensed under the MIT license.
//
// The parsing and writing logic is derived from:
//   FirmwareKit.DT/src/FirmwareKit.DT/Formatting/DtsFormat.cs
// (namespace FirmwareKit.DT.Formatting).
//
// It has been rewritten to build against the EXISTING UotanStudio.Firmware
// device-tree model (DeviceTree / DeviceTreeNode / DeviceTreeProperty /
// MemoryReservation) instead of the FirmwareKit.DT.Models implementations.
//
// --- MIT License ---
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction. Subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
// =============================================================================

using System.Text;

namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>表示DTS解析过程中遇到的解析错误，包含位置信息。</para>
/// Represents a parse error encountered during DTS parsing with position info.
/// </summary>
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
    public DeviceTree? Tree { get; }
    public List<DtsParseError> Errors { get; }
    public bool IsSuccess => Errors.Count == 0;
    public bool HasPartialTree => Tree != null && Errors.Count > 0;

    public DtsParseResult(DeviceTree? tree, List<DtsParseError> errors)
    {
        Tree = tree;
        Errors = errors ?? new List<DtsParseError>();
    }
}

/// <summary>
/// <para>控制DTS输出格式的选项。</para>
/// Options controlling DTS output formatting.
/// </summary>
public sealed class DtsWriteOptions
{
    public bool UsePhandleReferences { get; set; } = true;
    public bool IncludeComments { get; set; } = true;
    public bool SortProperties { get; set; }
    public bool UseHexForCells { get; set; } = true;
    public int IndentSize { get; set; }
    public static DtsWriteOptions Default { get; } = new();
}

/// <summary>
/// <para>提供解析与写入DTS文本格式的方法。</para>
/// Provides DTS (de)serialization. Ported from FirmwareKit.DT (MIT).
/// </summary>
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

    public static string Write(DeviceTree tree)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        var writer = new DtsWriter(DtsWriteOptions.Default);
        return writer.Write(tree);
    }

    public static string Write(DeviceTree tree, DtsWriteOptions options)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        var writer = new DtsWriter(options);
        return writer.Write(tree);
    }

    /// <summary>
    /// <para>序列化DTS并返回 节点路径-&gt;开括号行号 映射。</para>
    /// Writes DTS and returns a map of node path to the 1-based line of its '{'.
    /// </summary>
    public static Dictionary<string, int> GetNodeLineMap(DeviceTree tree)
    {
        if (tree is null)
            throw new ArgumentNullException(nameof(tree));
        var writer = new DtsWriter(DtsWriteOptions.Default);
        writer.Write(tree);
        return new Dictionary<string, int>(writer.NodeStartLines);
    }

    /// <summary>
    /// <para>将 overlay 树合并到基树并返回合并后的基树。overlay 根节点下的子节点按名称递归合并到基树
    /// 同名节点:overlay 属性覆盖基树同名属性,基树中不存在的节点/属性直接添加;同名字节点递归合并其属性
    /// 与子节点。被合并的子节点会从 overlay 树移入基树。</para>
    /// Merges the overlay tree into the base tree and returns the merged base tree. Children under
    /// the overlay root are recursively merged into the base node of the same name: overlay
    /// properties override base properties of the same name, nodes/properties that do not exist in
    /// the base are added, and children with matching names are merged recursively (properties and
    /// children). Merged children are moved from the overlay tree into the base tree.
    /// </summary>
    public static DeviceTree MergeOverlay(DeviceTree baseTree, DeviceTree overlayTree)
    {
        if (baseTree is null)
            throw new ArgumentNullException(nameof(baseTree));
        if (overlayTree is null)
            throw new ArgumentNullException(nameof(overlayTree));
        foreach (var child in overlayTree.Root.Children.ToList())
        {
            var existing = baseTree.Root.FindChild(child.Name);
            if (existing is null)
            {
                overlayTree.Root.RemoveChild(child);
                baseTree.Root.AddChildNode(child);
            }
            else MergeNode(existing, child);
        }
        return baseTree;
    }

    /// <summary>
    /// <para>将 source 节点的属性与子节点递归合并到 target 节点。overlay 删除记录
    /// (deletedProperties/deletedChildren,键为 source 子树节点)会先于属性/子节点合并应用,
    /// 与 &lt;c&gt;&amp;label&lt;/c&gt; overlay 片段的合并语义一致。</para>
    /// Recursively merges the properties and children of the source node into the target node.
    /// Overlay deletion records (deletedProperties/deletedChildren, keyed by source subtree nodes)
    /// are applied before property/child merging, matching the &lt;c&gt;&amp;label&lt;/c&gt; overlay
    /// fragment merge semantics.
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

    // -------------------------------------------------------------------------
    // Minimal DTS preprocessor: #define / #undef / #include / #if..., macro
    // substitution. Adapted from FirmwareKit.DT.DtsPreprocessor (MIT).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maximum recursion depth for #include directives to guard against infinite loops.
    /// </summary>
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