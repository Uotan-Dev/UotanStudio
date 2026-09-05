// =============================================================================
// DTS parser — extracted from DtsFormat.cs.
// =============================================================================

using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>DTS 文本解析器。</para>
/// DTS text parser.
/// </summary>
internal sealed class DtsParser
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
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileLength = fs.Length;
            if (offset < 0 || offset > fileLength)
            {
                throw new InvalidOperationException($"Offset {offset} is out of range for file '{filePath}' (length {fileLength}).");
            }
            fs.Position = offset;
            var remaining = fileLength - offset;
            var toRead = size >= 0 ? Math.Min(size, remaining) : remaining;
            if (toRead > Array.MaxLength)
            {
                throw new InvalidOperationException($"Requested size {toRead} exceeds maximum array length for file '{filePath}'.");
            }
            var fileData = new byte[(int)toRead];
            fs.ReadExactly(fileData);
            return fileData;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
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
                var comment = _text.Substring(commentStart + 2, Math.Max(0, _pos - commentStart - 4)).Trim();
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