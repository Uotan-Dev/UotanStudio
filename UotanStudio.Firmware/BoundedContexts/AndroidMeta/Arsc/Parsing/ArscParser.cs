using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Models;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Parsing;

public static class ArscParser
{
    /// <summary>Parses a bounded resource table stream. <para>解析有界资源表流。</para></summary>
    public static ResourceTable Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Parse(UotanStudio.Firmware.BoundedContexts.AndroidMeta.IO.AndroidMetadataReader.Read(stream));
    }

    /// <summary>Parses an Android resource table. <para>解析 Android 资源表。</para></summary>
    public static ResourceTable Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Require(data, 0, 12, data.Length);
        if (U16(data, 0) != 2) throw new ArscException("Invalid resource table magic.");
        var end = checked((int)U32(data, 4));
        Require(data, 0, end, data.Length);
        var header = U16(data, 2);
        if (header < 12 || header > end) throw new ArscException("Invalid resource table header.");
        var table = new ResourceTable();
        for (var pos = (int)header; pos < end;)
        {
            var size = ChunkSize(data, pos, end);
            switch (U16(data, pos))
            {
                case 1: table.GlobalPool.Parse(data, pos, size); break;
                case 0x200: table.Packages.Add(ParsePackage(data, pos, size)); break;
                default: table.ExtraChunks.Add(data.AsSpan(pos, size).ToArray()); break;
            }
            pos += size;
        }
        if (table.Packages.Count != U32(data, 8)) throw new ArscException("Package count mismatch.");
        return table;
    }

    /// <summary>Checks the resource table magic. <para>检查资源表魔数。</para></summary>
    public static bool IsArsc(byte[] data) => data is { Length: >= 12 } && U16(data, 0) == 2;

    private static ResPackage ParsePackage(byte[] data, int offset, int size)
    {
        var header = U16(data, offset + 2);
        if (header < 284) throw new ArscException("Truncated package header.");
        var pkg = new ResPackage
        {
            Id = checked((byte)U32(data, offset + 8)),
            Name = Encoding.Unicode.GetString(data, offset + 12, 256).Split('\0')[0],
            TypeIdOffset = header >= 288 ? U32(data, offset + 284) : 0
        };
        var typeStrings = U32(data, offset + 268);
        var keyStrings = U32(data, offset + 276);
        var end = offset + size;
        for (var pos = offset + header; pos < end;)
        {
            var subSize = ChunkSize(data, pos, end);
            switch (U16(data, pos))
            {
                case 1 when pos - offset == typeStrings: pkg.TypeStrings.Parse(data, pos, subSize); break;
                case 1 when pos - offset == keyStrings: pkg.KeyStrings.Parse(data, pos, subSize); break;
                case 0x202:
                    Require(data, pos, 16, pos + subSize);
                    var count = checked((int)U32(data, pos + 12));
                    if (count > (subSize - 16) / 4) throw new ArscException("Truncated type specification.");
                    var flags = new uint[count];
                    for (var i = 0; i < count; i++) flags[i] = U32(data, pos + 16 + i * 4);
                    pkg.TypeSpecFlags[data[pos + 8]] = flags;
                    break;
                case 0x201: ParseType(data, pos, subSize, pkg); break;
                default: pkg.ExtraChunks.Add(data.AsSpan(pos, subSize).ToArray()); break;
            }
            pos += subSize;
        }
        return pkg;
    }

    private static void ParseType(byte[] data, int offset, int size, ResPackage pkg)
    {
        Require(data, offset, 24, offset + size);
        var header = U16(data, offset + 2);
        var encoding = data[offset + 9];
        var count = checked((int)U32(data, offset + 12));
        var entriesStart = checked((int)U32(data, offset + 16));
        var configSize = checked((int)U32(data, offset + 20));
        var width = encoding == 2 ? 2 : 4;
        if (encoding > 2 || header < 24 || configSize < 4 || configSize > header - 20
            || entriesStart < header || entriesStart > size || count > (entriesStart - header) / width)
            throw new ArscException("Invalid type configuration or offsets.");
        var type = new ResType
        {
            Id = data[offset + 8],
            TypeNameIndex = Math.Max(0, data[offset + 8] - 1),
            ConfigHex = Convert.ToHexString(data.AsSpan(offset + 20, configSize)),
            ConfigDescription = DescribeConfig(data.AsSpan(offset + 20, configSize))
        };
        var previousIndex = -1;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            uint relative;
            var position = offset + header + i * width;
            if (encoding == 1)
            {
                index = U16(data, position);
                if (index <= previousIndex) throw new ArscException("Sparse entry indices are not increasing.");
                previousIndex = index;
                relative = (uint)U16(data, position + 2) * 4;
            }
            else if (encoding == 2)
            {
                var shortOffset = U16(data, position);
                relative = shortOffset == ushort.MaxValue ? uint.MaxValue : (uint)shortOffset * 4;
            }
            else relative = U32(data, position);
            if (index > ushort.MaxValue) throw new ArscException("Resource entry index exceeds 16 bits.");
            while (type.Entries.Count <= index) type.Entries.Add(null);
            if (relative == uint.MaxValue) continue;
            if (relative > (uint)(size - entriesStart)) throw new ArscException("Entry is outside its type chunk.");
            type.Entries[index] = ParseEntry(data, offset + entriesStart + (int)relative, offset + size);
        }
        pkg.Types.Add(type);
    }

    private static ResEntry ParseEntry(byte[] data, int pos, int end)
    {
        Require(data, pos, 8, end);
        var size = U16(data, pos);
        var flags = U16(data, pos + 2);
        if ((flags & 8) != 0)
        {
            if ((flags & 1) != 0) throw new ArscException("Compact entries cannot be complex.");
            return new ResEntry
            {
                KeyIndex = size,
                Flags = (ushort)(flags & 6),
                Value = new ResValue { Type = (ResValueType)(flags >> 8), Data = U32(data, pos + 4) }
            };
        }
        if (size < 8) throw new ArscException("Invalid resource entry size.");
        Require(data, pos, size, end);
        var entry = new ResEntry { KeyIndex = checked((int)U32(data, pos + 4)), Flags = flags };
        if ((flags & 1) == 0)
        {
            entry.Value = ParseValue(data, pos + size, end);
            return entry;
        }
        if (size < 16) throw new ArscException("Invalid complex resource entry size.");
        var count = U32(data, pos + 12);
        if (count > (uint)((end - pos - size) / 12)) throw new ArscException("Truncated resource map.");
        var value = new ResValue { IsComplex = true, Parent = U32(data, pos + 8) };
        for (var i = 0; i < count; i++)
        {
            var mapPos = pos + size + i * 12;
            value.Maps.Add(new ResMap { Name = U32(data, mapPos), Value = ParseValue(data, mapPos + 4, end) });
        }
        entry.Value = value;
        return entry;
    }

    private static ResValue ParseValue(byte[] data, int pos, int end)
    {
        Require(data, pos, 8, end);
        if (U16(data, pos) != 8) throw new ArscException("Invalid typed resource value size.");
        return new ResValue { Type = (ResValueType)data[pos + 3], Data = U32(data, pos + 4) };
    }

    private static string DescribeConfig(ReadOnlySpan<byte> config)
    {
        if (config.Length < 16) return "default";
        var parts = new List<string>();
        var mcc = BinaryPrimitives.ReadUInt16LittleEndian(config[4..]);
        var mnc = BinaryPrimitives.ReadUInt16LittleEndian(config[6..]);
        if (mcc != 0) parts.Add("mcc" + mcc.ToString("D3", CultureInfo.InvariantCulture));
        if (mnc != 0) parts.Add("mnc" + mnc.ToString("D3", CultureInfo.InvariantCulture));
        var language = DecodeLocale(config[8], config[9], 'a');
        var region = DecodeLocale(config[10], config[11], '0');
        if (language.Length > 0) parts.Add(language);
        if (region.Length > 0) parts.Add("r" + region);
        if (config[12] == 1) parts.Add("port");
        else if (config[12] == 2) parts.Add("land");
        var density = BinaryPrimitives.ReadUInt16LittleEndian(config[14..]);
        if (density != 0) parts.Add(density switch { 0xfffe => "anydpi", 0xffff => "nodpi", _ => "d" + density });
        return parts.Count > 0 ? string.Join('-', parts) : "default";
    }

    private static string DecodeLocale(byte first, byte second, char basis)
    {
        if (first == 0) return string.Empty;
        if ((first & 0x80) == 0) return new string([(char)first, (char)second]).TrimEnd('\0');
        return new string([(char)(basis + (second & 31)),
            (char)(basis + ((second & 224) >> 5) + ((first & 3) << 3)),
            (char)(basis + ((first & 124) >> 2))]);
    }

    private static int ChunkSize(byte[] data, int pos, int end)
    {
        Require(data, pos, 8, end);
        var size = checked((int)U32(data, pos + 4));
        var header = U16(data, pos + 2);
        if (header < 8 || size < header) throw new ArscException("Invalid resource chunk header.");
        Require(data, pos, size, end);
        return size;
    }

    private static void Require(byte[] data, int pos, int length, int end)
    {
        if (pos < 0 || length < 0 || end > data.Length || pos > end || length > end - pos)
            throw new ArscException("Truncated resource table.");
    }

    private static ushort U16(byte[] data, int pos) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
    private static uint U32(byte[] data, int pos) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
}

public sealed class ArscException : Exception
{
    /// <summary>Initializes a resource table exception. <para>初始化资源表异常。</para></summary>
    public ArscException(string message) : base(message) { }
}