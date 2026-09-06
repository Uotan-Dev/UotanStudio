

using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Dt;

internal sealed class DtsWriter
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
