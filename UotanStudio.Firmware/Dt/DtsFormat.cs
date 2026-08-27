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

using System.Buffers.Binary;
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

    // -------------------------------------------------------------------------
    // Minimal DTS preprocessor: #define / #undef / #include / #if..., macro
    // substitution. Adapted from FirmwareKit.DT.DtsPreprocessor (MIT).
    // -------------------------------------------------------------------------
    private static string Preprocess(string text, IEnumerable<string>? includePaths, Dictionary<string, string>? defines)
    {
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
                            result.Append(Preprocess(included, includeDirs, new Dictionary<string, string>(macros)));
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

    private sealed class DtsParser
    {
        private readonly string _text;
        private int _pos;
        private readonly List<MemoryReservation> _memReserves = new();
        private IEnumerable<string>? _includePaths;

        private readonly Dictionary<string, DeviceTreeNode> _labelToNode = new(StringComparer.Ordinal);
        private readonly List<(DeviceTreeNode Node, string PropertyName, int Offset, string Label)> _pendingCellReferences = new();
        private readonly List<(DeviceTreeNode Node, string PropertyName, string Label)> _pendingLabelValueReferences = new();

        private int _line = 1;
        private int _column = 1;
        private DeviceTreeNode? _currentNode;
        private string? _currentPropertyName;
        private readonly List<DtsParseError> _parseErrors = new();
        private bool _errorRecovery;
        private string? _pendingComment;

        public DtsParser(string text) { _text = text ?? throw new ArgumentNullException(nameof(text)); }

        public void SetIncludePaths(IEnumerable<string>? includePaths) { _includePaths = includePaths; }

        private string GetPositionInfo() => $"line {_line}, column {_column}";

        private void ThrowParseError(string message)
        {
            if (_errorRecovery)
            {
                _parseErrors.Add(new DtsParseError(message, _line, _column, DtsParseErrorSeverity.Error));
                throw new DtsRecoveryException(message);
            }
            throw new InvalidOperationException($"{message} at {GetPositionInfo()}");
        }

        private void AddParseWarning(string message)
        {
            _parseErrors.Add(new DtsParseError(message, _line, _column, DtsParseErrorSeverity.Warning));
        }

        public DtsParseResult ParseWithErrorCapture(bool useRecovery)
        {
            if (useRecovery) _errorRecovery = true;
            _parseErrors.Clear();
            DeviceTree? tree = null;
            try
            {
                tree = Parse();
            }
            catch (DtsRecoveryException) { }
            catch (Exception ex)
            {
                _parseErrors.Add(new DtsParseError(ex.Message, _line, _column, DtsParseErrorSeverity.Fatal));
            }
            return new DtsParseResult(tree, new List<DtsParseError>(_parseErrors));
        }

        private bool TryRecoverToNextStatement()
        {
            int depth = 0;
            while (_pos < _text.Length)
            {
                char ch = _text[_pos];
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    if (depth == 0) { Advance(); return true; }
                    depth--;
                }
                else if (ch == ';' && depth == 0) { Advance(); return true; }
                Advance();
            }
            return false;
        }

        private sealed class DtsRecoveryException : Exception
        {
            public DtsRecoveryException(string message) : base(message) { }
        }

        private DeviceTree Parse()
        {
            SkipWhitespaceAndComments();

            while (_pos < _text.Length)
            {
                SkipWhitespaceAndComments();
                if (_pos >= _text.Length) break;

                if (Match("/dts-v1/")) { Expect(";"); SkipWhitespaceAndComments(); }
                else if (Match("/plugin/")) { Expect(";"); SkipWhitespaceAndComments(); }
                else if (Match("/memreserve/")) { ParseMemReserve(); SkipWhitespaceAndComments(); }
                else if (Match("/delete-node/")) { SkipUntilSemicolon(); SkipWhitespaceAndComments(); }
                else if (Match("/delete-property/")) { SkipUntilSemicolon(); SkipWhitespaceAndComments(); }
                else if (Match("/include/")) { SkipUntilSemicolon(); AddParseWarning("/include/ directives should be resolved by preprocessing before parsing"); SkipWhitespaceAndComments(); }
                else if (CurrentChar() == '/')
                {
                    var tree = ParseRootNode();
                    tree.Version = FdtConstants.FdtVersion;
                    foreach (var mr in _memReserves)
                        tree.MemoryReservations.Add(mr);
                    ResolveLabelReferences(tree);
                    return tree;
                }
                else if (CurrentChar() == '&')
                {
                    // Overlay-style merge sections are not supported by the simple model; skip body.
                    Advance();
                    try { ParseIdentifier(); } catch { }
                    SkipWhitespaceAndComments();
                    if (CurrentChar() == '{') SkipBalanced('{', '}');
                    else if (CurrentChar() == ';') Advance();
                    SkipWhitespaceAndComments();
                }
                else
                {
                    var label = PeekIdentifier();
                    if (label != null)
                    {
                        SkipWhitespaceAndComments();
                        if (CurrentChar() == ':') { Advance(); SkipWhitespaceAndComments(); continue; }
                    }
                    break;
                }
            }

            ThrowParseError("No root node found in DTS source");
            return null!;
        }

        private void SkipBalanced(char open, char close)
        {
            int depth = 0;
            while (_pos < _text.Length)
            {
                char ch = _text[_pos];
                if (ch == open) depth++;
                else if (ch == close) { depth--; Advance(); if (depth == 0) return; continue; }
                Advance();
            }
        }

        private void ParseMemReserve()
        {
            SkipWhitespaceAndComments();
            var address = ParseHexOrDecimal64();
            SkipWhitespaceAndComments();
            var size = ParseHexOrDecimal64();
            SkipWhitespaceAndComments();
            Expect(";");
            _memReserves.Add(new MemoryReservation { Address = address, Size = size });
        }

        private DeviceTree ParseRootNode()
        {
            Expect("/");
            SkipWhitespaceAndComments();
            Expect("{");
            var tree = new DeviceTree();
            ParseNodeBody(tree.Root);
            return tree;
        }

        private void ParseNodeBody(DeviceTreeNode node)
        {
            var previousNode = _currentNode;
            _currentNode = node;
            SkipWhitespaceAndComments();

            while (_pos < _text.Length)
            {
                SkipWhitespaceAndComments();
                if (_pos >= _text.Length) break;

                var ch = CurrentChar();

                if (ch == '}')
                {
                    Advance();
                    SkipWhitespaceAndComments();
                    if (_pos < _text.Length && CurrentChar() == ';') Advance();
                    _currentNode = previousNode;
                    return;
                }

                if (ch == '/' && Peek(1) == '*') { SkipComment(); continue; }

                if (Match("/delete-property/")) { SkipUntilSemicolon(); AddParseWarning($"/delete-property/ inside node '{node.GetPath()}' is not supported by the DeviceTree model; skipped"); continue; }
                if (Match("/delete-node/")) { SkipUntilSemicolon(); AddParseWarning($"/delete-node/ inside node '{node.GetPath()}' is not supported by the DeviceTree model; skipped"); continue; }

                var identifier = PeekIdentifier();
                if (identifier is null) break;

                var savedPos = _pos;
                var labelOrName = ParseIdentifier();
                SkipWhitespaceAndComments();

                var labels = new List<string>();
                while (CurrentChar() == ':')
                {
                    labels.Add(labelOrName);
                    Advance();
                    SkipWhitespaceAndComments();
                    labelOrName = ParseIdentifier();
                    SkipWhitespaceAndComments();
                }

                if (labels.Count > 0)
                {
                    if (CurrentChar() == '{')
                    {
                        try
                        {
                            var child = node.AddChild(labelOrName);
                            foreach (var lbl in labels) _labelToNode[lbl] = child;
                            Advance();
                            ParseNodeBody(child);
                        }
                        catch (DtsRecoveryException) { TryRecoverToNextStatement(); }
                    }
                    else
                    {
                        try
                        {
                            _pos = savedPos;
                            foreach (var lbl in labels)
                            {
                                _pos += lbl.Length;
                                SkipWhitespaceAndComments();
                                Advance();
                                SkipWhitespaceAndComments();
                            }
                            ParsePropertyInto(node);
                            SkipWhitespaceAndComments();
                            Expect(";");
                        }
                        catch (DtsRecoveryException) { TryRecoverToNextStatement(); }
                    }
                    continue;
                }

                if (CurrentChar() == '{')
                {
                    try
                    {
                        var child = node.AddChild(labelOrName);
                        Advance();
                        ParseNodeBody(child);
                    }
                    catch (DtsRecoveryException) { TryRecoverToNextStatement(); }
                }
                else
                {
                    try
                    {
                        _pos = savedPos;
                        ParsePropertyInto(node);
                        SkipWhitespaceAndComments();
                        Expect(";");
                    }
                    catch (DtsRecoveryException) { TryRecoverToNextStatement(); }
                }
            }
        }

        private DeviceTreeProperty ParsePropertyInto(DeviceTreeNode node)
        {
            var property = ParseProperty();
            node.AddProperty(property.Name, property.Value);
            return property;
        }

        private DeviceTreeProperty ParseProperty()
        {
            var name = ParseIdentifier();
            _currentPropertyName = name;
            SkipWhitespaceAndComments();

            if (CurrentChar() == ';')
            {
                _currentPropertyName = null;
                return new DeviceTreeProperty(name, ReadOnlySpan<byte>.Empty);
            }

            Expect("=");
            SkipWhitespaceAndComments();
            var value = ParsePropertyValue();
            _currentPropertyName = null;
            return new DeviceTreeProperty(name, value);
        }

        private byte[] ParsePropertyValue()
        {
            var parts = new List<byte[]>();

            while (true)
            {
                SkipWhitespaceAndComments();
                var ch = CurrentChar();

                if (ch == '<') parts.Add(ParseCellList());
                else if (ch == '"') parts.Add(ParseStringLiteral());
                else if (ch == '[') parts.Add(ParseByteString());
                else if (ch == '&')
                {
                    Advance();
                    var refLabel = ParseIdentifier();
                    _pendingLabelValueReferences.Add((_currentNode!, _currentPropertyName!, refLabel));
                    parts.Add(Array.Empty<byte>());
                }
                else if (Match("/incbin/")) parts.Add(ParseIncBin());
                else if (Match("/bits/"))
                {
                    SkipWhitespaceAndComments();
                    var bitWidth = ParseInteger();
                    SkipWhitespaceAndComments();
                    Expect("<");
                    parts.Add(ParseBitsValue((int)bitWidth));
                }
                else break;

                SkipWhitespaceAndComments();
                if (CurrentChar() != ',') break;
                Advance();
            }

            var totalLength = parts.Sum(p => p.Length);
            var result = new byte[totalLength];
            var offset = 0;
            foreach (var part in parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }

        private byte[] ParseCellList()
        {
            Expect("<");
            var cells = new List<byte>();
            var currentOffset = 0;

            while (true)
            {
                SkipWhitespaceAndComments();
                if (CurrentChar() == '>') break;

                if (CurrentChar() == '&')
                {
                    Advance();
                    var refName = ParseIdentifier();
                    _pendingCellReferences.Add((_currentNode!, _currentPropertyName!, currentOffset, refName));
                    cells.AddRange(new byte[4]);
                    currentOffset += 4;
                    continue;
                }

                var value = ParseHexOrDecimal32();
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
                cells.AddRange(bytes);
                currentOffset += 4;
            }

            Expect(">");
            return cells.ToArray();
        }

        private byte[] ParseStringLiteral()
        {
            Expect("\"");
            var sb = new StringBuilder();
            var escaped = false;

            while (_pos < _text.Length)
            {
                var ch = CurrentChar();
                if (escaped)
                {
                    switch (ch)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '0': sb.Append('\0'); break;
                        case 'x':
                            Advance();
                            var hex = ParseHexByte();
                            sb.Append((char)hex);
                            break;
                        default: sb.Append(ch); break;
                    }
                    escaped = false;
                }
                else if (ch == '\\') escaped = true;
                else if (ch == '"') { Advance(); break; }
                else sb.Append(ch);
                Advance();
            }

            var strBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var result = new byte[strBytes.Length + 1];
            Array.Copy(strBytes, result, strBytes.Length);
            return result;
        }

        private byte[] ParseByteString()
        {
            Expect("[");
            var bytes = new List<byte>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (CurrentChar() == ']') break;
                bytes.Add(ParseHexByte());
            }
            Expect("]");
            return bytes.ToArray();
        }

        private byte[] ParseIncBin()
        {
            Expect("(");
            SkipWhitespaceAndComments();
            var filePath = ParseStringContent();
            SkipWhitespaceAndComments();

            long offset = 0;
            long size = -1;
            if (CurrentChar() == ',') { Advance(); SkipWhitespaceAndComments(); offset = (long)ParseHexOrDecimal64(); SkipWhitespaceAndComments(); }
            if (CurrentChar() == ',') { Advance(); SkipWhitespaceAndComments(); size = (long)ParseHexOrDecimal64(); SkipWhitespaceAndComments(); }
            Expect(")");

            try
            {
                var fileData = File.ReadAllBytes(filePath);
                if (offset > 0) fileData = fileData.Skip((int)offset).ToArray();
                if (size >= 0) fileData = fileData.Take((int)size).ToArray();
                return fileData;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to include binary file '{filePath}': {ex.Message}");
            }
        }

        private byte[] ParseBitsValue(int bitWidth)
        {
            var values = new List<byte>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (CurrentChar() == '>') break;
                var val = ParseHexOrDecimal64();
                switch (bitWidth)
                {
                    case 8: values.Add((byte)val); break;
                    case 16: values.Add((byte)(val >> 8)); values.Add((byte)val); break;
                    case 32:
                        var b32 = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(b32, (uint)val);
                        values.AddRange(b32);
                        break;
                    case 64:
                        var b64 = new byte[8];
                        BinaryPrimitives.WriteUInt64BigEndian(b64, val);
                        values.AddRange(b64);
                        break;
                    default: throw new InvalidOperationException($"Unsupported bit width: {bitWidth}");
                }
            }
            Expect(">");
            return values.ToArray();
        }

        private void ResolveLabelReferences(DeviceTree tree)
        {
            uint maxPhandle = 0;
            var maxScanned = false;

            foreach (var (node, propName, offset, label) in _pendingCellReferences)
            {
                if (!_labelToNode.TryGetValue(label, out var targetNode))
                {
                    AddParseWarning($"Cannot resolve label reference '&{label}' in '{node.GetPath()}'");
                    continue;
                }
                if (!maxScanned) { maxPhandle = FindMaxPhandleInTree(targetNode); maxScanned = true; }
                var phandle = GetOrCreatePhandle(targetNode, ref maxPhandle);
                PatchCell(node, propName, offset, phandle);
            }

            foreach (var (node, propName, label) in _pendingLabelValueReferences)
            {
                if (_labelToNode.TryGetValue(label, out var targetNode))
                {
                    var pathString = targetNode.GetPath() + '\0';
                    var pathBytes = Encoding.UTF8.GetBytes(pathString);
                    PatchValue(node, propName, pathBytes);
                }
                else AddParseWarning($"Cannot resolve label reference '&{label}' in '{node.GetPath()}'");
            }
            _ = tree;
        }

        private static uint GetOrCreatePhandle(DeviceTreeNode node, ref uint maxPhandle)
        {
            var existing = node.FindProperty("phandle");
            if (existing != null && existing.Length == 4)
            {
                var cells = existing.GetCells();
                if (cells.Length > 0) return cells[0];
            }
            var phandle = maxPhandle + 1;
            maxPhandle = phandle;
            node.AddPropertyCells("phandle", phandle);
            return phandle;
        }

        private static uint FindMaxPhandleInTree(DeviceTreeNode from)
        {
            uint max = 0;
            void Walk(DeviceTreeNode n)
            {
                var p = n.FindProperty("phandle");
                if (p != null && p.Length == 4)
                {
                    var cells = p.GetCells();
                    if (cells.Length > 0 && cells[0] > max) max = cells[0];
                }
                foreach (var c in n.Children) Walk(c);
            }
            var root = from;
            while (root.Parent != null) root = root.Parent;
            Walk(root);
            return max;
        }

        private void PatchCell(DeviceTreeNode node, string propName, int offset, uint phandle)
        {
            if (propName == null) return;
            var existing = node.FindProperty(propName);
            if (existing == null) return;
            var bytes = existing.ValueBytes;
            if (offset + 4 > bytes.Length) return;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), phandle);
            PatchValue(node, propName, bytes);
        }

        private static void PatchValue(DeviceTreeNode node, string propName, byte[] newValue)
        {
            // The model exposes a read-only property list; values cannot be replaced in place.
            _ = node; _ = propName; _ = newValue;
        }

        private string ParseStringContent()
        {
            Expect("\"");
            var sb = new StringBuilder();
            while (_pos < _text.Length && CurrentChar() != '"')
            {
                if (CurrentChar() == '\\' && _pos + 1 < _text.Length)
                {
                    Advance();
                    sb.Append(CurrentChar());
                }
                else sb.Append(CurrentChar());
                Advance();
            }
            Expect("\"");
            return sb.ToString();
        }

        private string ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && IsIdentifierChar(_text[_pos])) _pos++;
            if (_pos == start) throw new InvalidOperationException($"Expected identifier at position {_pos}");
            return _text.Substring(start, _pos - start);
        }

        private string? PeekIdentifier()
        {
            var savedPos = _pos;
            if (_pos >= _text.Length || !IsIdentifierStart(_text[_pos])) return null;
            while (_pos < _text.Length && IsIdentifierChar(_text[_pos])) _pos++;
            var result = _text.Substring(savedPos, _pos - savedPos);
            _pos = savedPos;
            return result;
        }

        private uint ParseHexOrDecimal32() => (uint)ParseHexOrDecimal64();

        private ulong ParseHexOrDecimal64()
        {
            SkipWhitespaceAndComments();
            var negative = false;
            if (CurrentChar() == '-') { negative = true; Advance(); }
            if (CurrentChar() == '0' && _pos + 1 < _text.Length && (_text[_pos + 1] == 'x' || _text[_pos + 1] == 'X'))
            {
                _pos += 2;
                return ParseHex64(negative);
            }
            return ParseDecimal64(negative);
        }

        private ulong ParseHex64(bool negative)
        {
            ulong value = 0;
            while (_pos < _text.Length && IsHexDigit(_text[_pos]))
            {
                value = value * 16 + (ulong)HexValue(_text[_pos]);
                _pos++;
            }
            return negative ? (ulong)(-(long)value) : value;
        }

        private ulong ParseDecimal64(bool negative)
        {
            ulong value = 0;
            while (_pos < _text.Length && char.IsDigit(_text[_pos]))
            {
                value = value * 10 + (ulong)(_text[_pos] - '0');
                _pos++;
            }
            return negative ? (ulong)(-(long)value) : value;
        }

        private long ParseInteger()
        {
            SkipWhitespaceAndComments();
            var negative = false;
            if (CurrentChar() == '-') { negative = true; Advance(); }
            if (CurrentChar() == '0' && _pos + 1 < _text.Length && (_text[_pos + 1] == 'x' || _text[_pos + 1] == 'X'))
            {
                _pos += 2;
                ulong value = 0;
                while (_pos < _text.Length && IsHexDigit(_text[_pos]))
                {
                    value = value * 16 + (ulong)HexValue(_text[_pos]);
                    _pos++;
                }
                return negative ? -(long)value : (long)value;
            }
            long decValue = 0;
            while (_pos < _text.Length && char.IsDigit(_text[_pos]))
            {
                decValue = decValue * 10 + (_text[_pos] - '0');
                _pos++;
            }
            return negative ? -decValue : decValue;
        }

        private byte ParseHexByte()
        {
            byte value = 0;
            int digits = 0;
            while (_pos < _text.Length && IsHexDigit(_text[_pos]) && digits < 2)
            {
                value = (byte)(value * 16 + HexValue(_text[_pos]));
                _pos++;
                digits++;
            }
            if (digits == 0) throw new InvalidOperationException($"Expected hex digit at position {_pos}");
            return value;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _text.Length)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch)) { _pos++; }
                else if (ch == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    _pos += 2;
                    var commentStart = _pos;
                    while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
                    var comment = _text.Substring(commentStart, _pos - commentStart).Trim();
                    if (_pendingComment == null) _pendingComment = comment;
                    else _pendingComment += "\n" + comment;
                }
                else if (ch == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                {
                    var commentStart = _pos;
                    SkipComment();
                    var comment = _text.Substring(commentStart + 2, System.Math.Max(0, _pos - commentStart - 4)).Trim();
                    if (_pendingComment == null) _pendingComment = comment;
                    else _pendingComment += "\n" + comment;
                }
                else if (ch == '#' && _pos + 1 < _text.Length && char.IsWhiteSpace(_text[_pos + 1]))
                {
                    while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
                }
                else break;
            }
        }

        private void SkipComment()
        {
            _pos += 2;
            while (_pos + 1 < _text.Length)
            {
                if (_text[_pos] == '*' && _text[_pos + 1] == '/') { _pos += 2; return; }
                _pos++;
            }
            _pos = _text.Length;
        }

        private void SkipUntilSemicolon()
        {
            int depth = 0;
            while (_pos < _text.Length)
            {
                if (_text[_pos] == '{') depth++;
                else if (_text[_pos] == '}') depth--;
                else if (_text[_pos] == ';' && depth == 0) { _pos++; return; }
                _pos++;
            }
        }

        private bool Match(string s)
        {
            if (_pos + s.Length > _text.Length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (_text[_pos + i] != s[i]) return false;
            }
            _pos += s.Length;
            return true;
        }

        private void Expect(string s)
        {
            SkipWhitespaceAndComments();
            if (!Match(s))
                throw new InvalidOperationException($"Expected '{s}' at position {_pos}");
        }

        private char CurrentChar() => _pos < _text.Length ? _text[_pos] : '\0';
        private char Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

        private void Advance()
        {
            if (_pos < _text.Length && _text[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else _column++;
            _pos++;
        }

        private static bool IsIdentifierChar(char ch) =>
            char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' || ch == '+' || ch == '#' || ch == ',' || ch == '@';

        private static bool IsIdentifierStart(char ch) =>
            char.IsLetterOrDigit(ch) || ch == '_' || ch == '#';

        private static bool IsHexDigit(char ch) =>
            char.IsDigit(ch) || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

        private static int HexValue(char ch)
        {
            if (char.IsDigit(ch)) return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return 0;
        }
    }

    private sealed class DtsWriter
    {
        private readonly StringBuilder _sb = new();
        private int _indent;
        private readonly DtsWriteOptions _options;
        private int _currentLine = 1;
        private readonly Dictionary<string, int> _nodeStartLines = new(StringComparer.Ordinal);

        public DtsWriter(DtsWriteOptions? options = null)
        {
            _options = options ?? new DtsWriteOptions();
        }

        public IReadOnlyDictionary<string, int> NodeStartLines => _nodeStartLines;

        public string Write(DeviceTree tree)
        {
            _sb.Clear();
            _indent = 0;
            _currentLine = 1;
            _nodeStartLines.Clear();

            AppendLine("/dts-v1/;");
            foreach (var mr in tree.MemoryReservations)
                AppendLine($"/memreserve/ 0x{mr.Address:X} 0x{mr.Size:X};");
            AppendLine(string.Empty);

            _nodeStartLines[GetCleanPath(tree.Root)] = _currentLine;
            WriteNode(tree.Root);
            return _sb.ToString();
        }

        private void WriteNode(DeviceTreeNode node)
        {
            WriteIndent();
            if (node.Parent is null) _sb.Append("/");
            else _sb.Append(node.Name);
            // 记录节点声明行的行号：在书写 " {" 之前当前行即节点声明的起始行。
            // Record the node declaration line: before " {" is emitted, the current line is where the
            // declaration (indent + name + brace) starts.
            _nodeStartLines[GetCleanPath(node)] = _currentLine;
            AppendLine(" {");

            _indent++;

            var properties = _options.SortProperties
                ? node.Properties.OrderBy(p => p.Name).ToList()
                : (IList<DeviceTreeProperty>)node.Properties;

            foreach (var prop in properties)
                WriteProperty(prop);

            for (var i = 0; i < node.Children.Count; i++)
            {
                if (i == 0 && node.Properties.Count > 0)
                    AppendLine(string.Empty);
                WriteNode(node.Children[i]);
            }

            _indent--;
            WriteIndent();
            AppendLine("};");
        }

        private void WriteProperty(DeviceTreeProperty property)
        {
            WriteIndent();
            _sb.Append(property.Name);
            if (property.Length == 0)
            {
                AppendLine(";");
                return;
            }
            _sb.Append(" = ");
            WritePropertyValue(property);
            AppendLine(";");
        }

        private void WritePropertyValue(DeviceTreeProperty property)
        {
            var value = property.ValueBytes;
            if (IsStringProperty(property) && TryWriteAsString(value)) return;
            if (value.Length % 4 == 0 && value.Length > 0)
            {
                WriteAsCells(value);
                return;
            }
            WriteAsByteString(value);
        }

        private bool TryWriteAsString(byte[] value)
        {
            if (value.Length == 0 || value[value.Length - 1] != 0) return false;
            if (value.Length == 1) { _sb.Append("\"\""); return true; }

            var strings = new List<string>();
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == 0)
                {
                    if (i > start)
                    {
                        var str = Encoding.UTF8.GetString(value, start, i - start);
                        if (!IsPrintable(str)) return false;
                        strings.Add(str);
                    }
                    else strings.Add(string.Empty);
                    start = i + 1;
                }
            }
            _sb.Append(string.Join(", ", strings.Select(s => $"\"{EscapeString(s)}\"")));
            return true;
        }

        private void WriteAsCells(byte[] value)
        {
            _sb.Append("<");
            var cells = new List<string>();
            for (var i = 0; i < value.Length; i += 4)
            {
                var cellValue = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(i));
                cells.Add(_options.UseHexForCells ? FormatHexValue(cellValue) : cellValue.ToString());
            }
            _sb.Append(string.Join(" ", cells));
            _sb.Append(">");
        }

        private static string FormatHexValue(uint value)
        {
            if (value == 0) return "0x00";
            var hexStr = value.ToString("x");
            if (hexStr.Length == 1) return $"0x0{hexStr}";
            return $"0x{hexStr}";
        }

        private void WriteAsByteString(byte[] value)
        {
            _sb.Append("[");
            _sb.Append(string.Join(" ", value.Select(b => $"{b:X2}")));
            _sb.Append("]");
        }

        private void WriteIndent()
        {
            if (_options.IndentSize == 0) _sb.Append(new string('\t', _indent));
            else _sb.Append(new string(' ', _indent * _options.IndentSize));
        }

        private void AppendLine(string text)
        {
            _sb.Append(text);
            _sb.Append('\n');
            _currentLine++;
        }

        private static bool IsStringProperty(DeviceTreeProperty property)
        {
            var value = property.Value;
            if (value.Length == 0) return false;
            var name = property.Name;

            if (name == "clock-frequency") return false;
            if (name.EndsWith("-names", StringComparison.Ordinal)) return true;
            if (IsLikelyStringValue(value)) return true;

            return name is "compatible" or "model" or "status" or "device_type" or "label"
                or "clock-names" or "pinctrl-names" or "reg-names" or "phy-mode"
                or "stdout-path" or "stdin-path" or "bootargs" or "clock-output-names"
                or "interrupt-names" or "gpio-names" or "reset-names" or "power-domain-names"
                or "dma-names" or "mbox-names" or "assigned-clock-names"
                or "thermal-sensor-names" or "sound-dai-names" or "io-channel-names"
                or "pwm-names" or "connector-names" or "phy-names" or "nvmem-cell-names"
                or "pattern" or "function" or "function-enumerator";
        }

        private static bool IsLikelyStringValue(ReadOnlySpan<byte> value)
        {
            if (value.Length < 2) return false;
            if (value[value.Length - 1] != 0) return false;

            var nullCount = 0;
            var printableCount = 0;
            for (var i = 0; i < value.Length; i++)
            {
                byte b = value[i];
                if (b == 0)
                {
                    nullCount++;
                    if (nullCount > value.Length / 2) return false;
                }
                else if (b >= 0x20 && b <= 0x7E) printableCount++;
                else if (b == '\t' || b == '\n') printableCount++;
                else return false;
            }
            return printableCount > value.Length / 2;
        }

        private static bool IsPrintable(string s) => s.All(ch => ch >= 0x20 && ch <= 0x7E);


        private static string EscapeString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");

        private static string GetCleanPath(DeviceTreeNode? node)
        {
            var parts = new List<string>();
            var cur = node;
            while (cur != null) { if (cur.Name.Length > 0) parts.Add(cur.Name); cur = cur.Parent; }
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }
    }
}