

using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

internal sealed class DtsParser
{
    private readonly string _text;
    private int _pos;
    private readonly List<MemoryReservation> _memReserves = new();
    private IEnumerable<string>? _includePaths;

    private readonly Dictionary<string, DeviceTreeNode> _labelToNode = new(StringComparer.Ordinal);
    private readonly List<(DeviceTreeNode Node, string PropertyName, int Offset, string Label)> _pendingCellReferences = new();
    private readonly List<(DeviceTreeNode Node, string PropertyName, string Label)> _pendingLabelValueReferences = new();
    private readonly List<(string Label, DeviceTreeNode Patch)> _overlayFragments = new();
    private readonly List<string> _pendingRootDeletes = new();
    private readonly List<string> _pendingRootDeleteProperties = new();
    private readonly List<string> _pendingRootLabelDeletes = new();
    private readonly List<(DeviceTreeNode Parent, string Label)> _pendingNodeLabelDeletes = new();
    private readonly Dictionary<DeviceTreeNode, List<string>> _overlayDeletedChildren = new();
    private readonly Dictionary<DeviceTreeNode, List<string>> _overlayDeletedProperties = new();
    private bool _inOverlayBody;
    private bool _isPlugin;

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

        DeviceTree? tree = null;

        while (_pos < _text.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length) break;

            if (Match("/dts-v1/")) { Expect(";"); SkipWhitespaceAndComments(); }
            else if (Match("/plugin/")) { _isPlugin = true; Expect(";"); SkipWhitespaceAndComments(); }
            else if (Match("/memreserve/")) { ParseMemReserve(); SkipWhitespaceAndComments(); }
            else if (Match("/delete-node/")) { ParseTopLevelDeleteNode(); SkipWhitespaceAndComments(); }
            else if (Match("/delete-property/")) { ParseTopLevelDeleteProperty(); SkipWhitespaceAndComments(); }
            else if (Match("/include/")) { SkipUntilSemicolon(); AddParseWarning("/include/ directives should be resolved by preprocessing before parsing"); SkipWhitespaceAndComments(); }
            else if (CurrentChar() == '/')
            {
                if (tree is not null) break;
                tree = ParseRootNode();
                tree.Version = FdtConstants.FdtVersion;
                SkipWhitespaceAndComments();
            }
            else if (CurrentChar() == '&')
            {
                ParseOverlayFragment();
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

        if (tree is null)
        {
            if (_isPlugin) tree = new DeviceTree();
            else
            {
                ThrowParseError("No root node found in DTS source");
                return null!;
            }
        }

        foreach (var mr in _memReserves)
            tree.MemoryReservations.Add(mr);
        ApplyPendingRootDeletes(tree.Root);
        if (_isPlugin)
        {
            var number = 0;
            foreach (var (label, patch) in _overlayFragments.ToList())
            {
                if (_labelToNode.ContainsKey(label)) continue;
                string name;
                do name = "fragment@" + number++; while (tree.Root.FindChild(name) is not null);
                var fragment = tree.Root.AddChild(name);
                if (label.StartsWith('/')) fragment.AddPropertyString("target-path", label + '\0');
                else
                {
                    fragment.AddPropertyCells("target", uint.MaxValue);
                    _pendingCellReferences.Add((fragment, "target", 0, label));
                }
                patch.Name = "__overlay__";
                fragment.AddChildNode(patch);
                _overlayFragments.Remove((label, patch));
            }
        }
        ResolveLabelReferences(tree);
        ApplyPendingLabelDeletes();
        MergeOverlayFragments();
        foreach (var (label, node) in _labelToNode)
        {
            tree.LabelPaths[label] = node.GetPath();
            if (_isPlugin)
            {
                var symbols = tree.Root.FindChild("__symbols__") ?? tree.Root.AddChild("__symbols__");
                symbols.AddPropertyString(label, node.GetPath() + '\0');
            }
        }
        return tree;
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

            if (Match("/delete-property/")) { ParseDeleteProperty(node); continue; }
            if (Match("/delete-node/")) { ParseDeleteNode(node); continue; }

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

    private void ParseOverlayFragment()
    {
        Advance();
        SkipWhitespaceAndComments();
        string label;
        if (CurrentChar() == '{')
        {
            Advance();
            var start = _pos;
            while (_pos < _text.Length && CurrentChar() != '}') Advance();
            label = _text[start.._pos];
            Expect("}");
        }
        else label = ParseIdentifier();
        SkipWhitespaceAndComments();
        if (CurrentChar() == '{')
        {
            var patch = new DeviceTreeNode("");
            Advance();
            var previousOverlayFlag = _inOverlayBody;
            _inOverlayBody = true;
            try
            {
                ParseNodeBody(patch);
                _overlayFragments.Add((label, patch));
            }
            catch (DtsRecoveryException) { TryRecoverToNextStatement(); }
            _inOverlayBody = previousOverlayFlag;
        }
        else if (CurrentChar() == ';') Advance();
    }

    private void ParseTopLevelDeleteNode()
    {
        SkipWhitespaceAndComments();
        if (CurrentChar() == '&')
        {
            Advance();
            var label = ParseIdentifier();
            SkipWhitespaceAndComments();
            Expect(";");
            _pendingRootLabelDeletes.Add(label);
            return;
        }
        var name = ParseIdentifier();
        SkipWhitespaceAndComments();
        Expect(";");
        _pendingRootDeletes.Add(name);
    }

    private void ParseTopLevelDeleteProperty()
    {
        SkipWhitespaceAndComments();
        if (CurrentChar() == '&')
        {
            Advance();
            SkipUntilSemicolon();
            ThrowParseError("/delete-property/ by label reference is not a valid dtc directive");
            return;
        }
        var name = ParseIdentifier();
        SkipWhitespaceAndComments();
        Expect(";");
        _pendingRootDeleteProperties.Add(name);
    }

    private void ApplyPendingRootDeletes(DeviceTreeNode root)
    {
        foreach (var name in _pendingRootDeletes)
            root.RemoveChild(name);
        foreach (var name in _pendingRootDeleteProperties)
            root.RemoveProperty(name);
    }

    /// <summary>
    /// Resolves and applies pending <c>/delete-node/ &amp;label;</c> directives after
    /// all labels have been registered.  Following dtc v1.7.2 <c>DT_DEL_NODE dt_ref ';'</c>,
    /// the node referenced by the label is removed from its parent.
    /// <para>在全部标签注册后解析并执行 <c>/delete-node/ &amp;label;</c> 指令，
    /// 按 dtc v1.7.2 <c>DT_DEL_NODE dt_ref ';'</c> 规则从父节点移除被引用节点。</para>
    /// </summary>
    private void ApplyPendingLabelDeletes()
    {
        foreach (var label in _pendingRootLabelDeletes)
        {
            if (!_labelToNode.TryGetValue(label, out var target))
            {
                AddParseWarning($"/delete-node/ references unknown label '&{label}'");
                continue;
            }
            target.Parent?.RemoveChild(target);
        }
        foreach (var (parent, label) in _pendingNodeLabelDeletes)
        {
            if (!_labelToNode.TryGetValue(label, out var target))
            {
                AddParseWarning($"/delete-node/ references unknown label '&{label}' in '{parent.GetPath()}'");
                continue;
            }
            target.Parent?.RemoveChild(target);
        }
    }

    private void ParseDeleteNode(DeviceTreeNode node)
    {
        SkipWhitespaceAndComments();
        if (CurrentChar() == '&')
        {
            Advance();
            var label = ParseIdentifier();
            SkipWhitespaceAndComments();
            Expect(";");
            _pendingNodeLabelDeletes.Add((node, label));
            if (_inOverlayBody) RecordOverlayDelete(_overlayDeletedChildren, node, label);
            return;
        }
        var name = ParseIdentifier();
        SkipWhitespaceAndComments();
        Expect(";");
        node.RemoveChild(name);
        if (_inOverlayBody) RecordOverlayDelete(_overlayDeletedChildren, node, name);
    }

    private void ParseDeleteProperty(DeviceTreeNode node)
    {
        SkipWhitespaceAndComments();
        if (CurrentChar() == '&')
        {
            Advance();
            SkipUntilSemicolon();
            ThrowParseError($"/delete-property/ by label reference in '{node.GetPath()}' is not a valid dtc directive");
            return;
        }
        var name = ParseIdentifier();
        SkipWhitespaceAndComments();
        Expect(";");
        node.RemoveProperty(name);
        if (_inOverlayBody) RecordOverlayDelete(_overlayDeletedProperties, node, name);
    }

    private static void RecordOverlayDelete(Dictionary<DeviceTreeNode, List<string>> map, DeviceTreeNode node, string name)
    {
        if (!map.TryGetValue(node, out var list))
        {
            list = [];
            map[node] = list;
        }
        if (!list.Contains(name)) list.Add(name);
    }

    private void MergeOverlayFragments()
    {
        foreach (var (label, patch) in _overlayFragments)
        {
            if (!_labelToNode.TryGetValue(label, out var target))
            {
                AddParseWarning($"Cannot resolve overlay target label '&{label}'; overlay fragment skipped");
                continue;
            }
            DtsFormat.MergeNode(target, patch, _overlayDeletedProperties, _overlayDeletedChildren);
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

            var posBefore = _pos;
            var value = (uint)ParseCellExpression();
            if (_pos == posBefore)
            {
                AddParseWarning($"Skipping unrecognized token '{CurrentChar()}' in cell list at {GetPositionInfo()}");
                if (_pos < _text.Length) Advance();
                continue;
            }
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
            var posBefore = _pos;
            var val = ParseCellExpression();
            if (_pos == posBefore)
            {
                AddParseWarning($"Skipping unrecognized token '{CurrentChar()}' in /bits/ list at {GetPositionInfo()}");
                if (_pos < _text.Length) Advance();
                continue;
            }
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
                if (_isPlugin)
                {
                    var fixups = tree.Root.FindChild("__fixups__") ?? tree.Root.AddChild("__fixups__");
                    var location = Encoding.UTF8.GetBytes($"{node.GetPath()}:{propName}:{offset}\0");
                    if (fixups.FindProperty(label) is { } existing)
                        existing.ReplaceValue(existing.ValueBytes.Concat(location).ToArray());
                    else fixups.AddProperty(label, location);
                    PatchCell(node, propName, offset, uint.MaxValue);
                    continue;
                }
                AddParseWarning($"Cannot resolve label reference '&{label}' in '{node.GetPath()}'");
                continue;
            }
            if (!maxScanned) { maxPhandle = FindMaxPhandleInTree(targetNode); maxScanned = true; }
            var phandle = GetOrCreatePhandle(targetNode, ref maxPhandle);
            PatchCell(node, propName, offset, phandle);
            if (_isPlugin)
            {
                var local = tree.Root.FindChild("__local_fixups__") ?? tree.Root.AddChild("__local_fixups__");
                foreach (var segment in node.GetPath().Split('/', StringSplitOptions.RemoveEmptyEntries))
                    local = local.FindChild(segment) ?? local.AddChild(segment);
                var offsets = local.FindProperty(propName)?.GetCells().ToList() ?? [];
                offsets.Add((uint)offset);
                local.RemoveProperty(propName);
                local.AddPropertyCells(propName, offsets.ToArray());
            }
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
        var existing = node.FindProperty(propName);
        existing?.ReplaceValue(newValue);
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

    /// <summary>
    /// Parses a cell-level expression: a number optionally followed by binary
    /// arithmetic operators (<c>+ - * / &lt;&lt; &gt;&gt;</c>).
    /// <para>解析单元格级表达式：数字后可选跟二元算术运算符（<c>+ - * / &lt;&lt; &gt;&gt;</c>）。</para>
    /// </summary>
    /// <summary>
    /// Parses a DTS integer expression with dtc v1.7.2 precedence:
    /// unary (+ - ~ !)  &gt; multiplicative (* / %)  &gt; additive (+ -)
    /// &gt; shift (&lt;&lt; &gt;&gt;)  &gt; relational (&lt; &gt; &lt;= &gt;=)  &gt; equality (== !=)
    /// &gt; bitwise AND (&amp;)  &gt; bitwise XOR (^)  &gt; bitwise OR (|)
    /// &gt; logical AND (&amp;&amp;)  &gt; logical OR (||)
    /// Parenthesised sub-expressions are supported.
    /// <para>按 dtc v1.7.2 优先级解析 DTS 整数表达式，支持括号子表达式。</para>
    /// </summary>
    private ulong ParseCellExpression()
    {
        return (ulong)ParseLogicalOr();
    }

    private long ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '|' && Peek(1) == '|')
            {
                Advance(); Advance();
                var right = ParseLogicalAnd();
                left = (left != 0 || right != 0) ? 1L : 0L;
            }
            else break;
        }
        return left;
    }

    private long ParseLogicalAnd()
    {
        var left = ParseBitwiseOr();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '&' && Peek(1) == '&')
            {
                Advance(); Advance();
                var right = ParseBitwiseOr();
                left = (left != 0 && right != 0) ? 1L : 0L;
            }
            else break;
        }
        return left;
    }

    private long ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '|' && Peek(1) != '|')
            {
                Advance();
                var right = ParseBitwiseXor();
                left |= right;
            }
            else break;
        }
        return left;
    }

    private long ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '^')
            {
                Advance();
                var right = ParseBitwiseAnd();
                left ^= right;
            }
            else break;
        }
        return left;
    }

    private long ParseBitwiseAnd()
    {
        var left = ParseEquality();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '&' && Peek(1) != '&')
            {
                Advance();
                var right = ParseEquality();
                left &= right;
            }
            else break;
        }
        return left;
    }

    private long ParseEquality()
    {
        var left = ParseRelational();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '=' && Peek(1) == '=')
            {
                Advance(); Advance();
                var right = ParseRelational();
                left = left == right ? 1L : 0L;
            }
            else if (CurrentChar() == '!' && Peek(1) == '=')
            {
                Advance(); Advance();
                var right = ParseRelational();
                left = left != right ? 1L : 0L;
            }
            else break;
        }
        return left;
    }

    private long ParseRelational()
    {
        var left = ParseShift();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '<' && Peek(1) == '=')
            {
                Advance(); Advance();
                var right = ParseShift();
                left = left <= right ? 1L : 0L;
            }
            else if (CurrentChar() == '>' && Peek(1) == '=')
            {
                Advance(); Advance();
                var right = ParseShift();
                left = left >= right ? 1L : 0L;
            }
            // NOTE: bare '<' and '>' are NOT parsed as relational operators here
            // because they conflict with cell-list delimiters in `<...>`.  Users who
            // need a bare '<' / '>' comparison must wrap it in parentheses, e.g.
            // `<((1 < 2))>`, where '(' context still doesn't see '<' as a delimiter
            // since ParsePrimary handles '(' recursively.  This matches dtc's
            // behaviour where the lexer inside `<...>` treats '<' / '>' as
            // delimiters, not relational tokens.
            else break;
        }
        return left;
    }

    private long ParseShift()
    {
        var left = ParseAdditive();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '<' && Peek(1) == '<')
            {
                Advance(); Advance();
                var right = ParseAdditive();
                left <<= (int)right;
            }
            else if (CurrentChar() == '>' && Peek(1) == '>')
            {
                Advance(); Advance();
                var right = ParseAdditive();
                left >>= (int)right;
            }
            else break;
        }
        return left;
    }

    private long ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '+')
            {
                Advance();
                var right = ParseMultiplicative();
                left += right;
            }
            else if (CurrentChar() == '-')
            {
                Advance();
                var right = ParseMultiplicative();
                left -= right;
            }
            else break;
        }
        return left;
    }

    private long ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (CurrentChar() == '*')
            {
                Advance();
                var right = ParseUnary();
                left *= right;
            }
            else if (CurrentChar() == '/')
            {
                Advance();
                var right = ParseUnary();
                left = right != 0 ? left / right : 0;
            }
            else if (CurrentChar() == '%')
            {
                Advance();
                var right = ParseUnary();
                left = right != 0 ? left % right : 0;
            }
            else break;
        }
        return left;
    }

    private long ParseUnary()
    {
        SkipWhitespaceAndComments();
        if (CurrentChar() == '-')
        {
            Advance();
            return -ParseUnary();
        }
        if (CurrentChar() == '+')
        {
            Advance();
            return ParseUnary();
        }
        if (CurrentChar() == '~')
        {
            Advance();
            return ~ParseUnary();
        }
        if (CurrentChar() == '!')
        {
            Advance();
            return ParseUnary() == 0 ? 1L : 0L;
        }
        return ParsePrimary();
    }

    private long ParsePrimary()
    {
        SkipWhitespaceAndComments();

        if (CurrentChar() == '(')
        {
            Advance();
            var value = ParseLogicalOr();
            SkipWhitespaceAndComments();
            Expect(")");
            return value;
        }

        // Numeric literal — delegate to existing hex/decimal parser.
        if (char.IsDigit(CurrentChar()) || CurrentChar() == '-')
        {
            return (long)ParseHexOrDecimal64();
        }

        ThrowParseError($"Expected integer expression but found '{CurrentChar()}' at {GetPositionInfo()}");
        return 0;
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