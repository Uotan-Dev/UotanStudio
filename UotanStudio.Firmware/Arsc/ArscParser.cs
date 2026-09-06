using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.Arsc;

public static class ArscParser
{


    private const ushort ResTableType = 0x0002;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResTablePackageType = 0x0200;
    private const ushort ResTableTypeSpecType = 0x0202;
    private const ushort ResTableTypeType = 0x0201;

    private const uint NoEntry = 0xFFFFFFFF;
    private const uint NoIndex = 0xFFFFFFFF;
    private const ushort EntryFlagComplex = 0x0001;

    /// <summary>
    /// Reads ARSC data from a stream and parses it into a resource-table model.
    /// <para>从流读取 ARSC 数据并解析为资源表模型。</para>
    /// </summary>
    public static ResourceTable Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            var length = stream.Length;
            if (length > Array.MaxLength)
            {
                throw new ArscException($"ARSC stream length {length} exceeds maximum array length.");
            }
            var data = new byte[(int)length];
            stream.ReadExactly(data);
            return Parse(data);
        }


        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    /// <summary>
    /// Parses ARSC bytes into a resource-table model.
    /// <para>解析 ARSC 字节为资源表模型。</para>
    /// </summary>
    public static ResourceTable Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            throw new ArscException("File too small to be ARSC (header is 12 bytes)");
        }

        var span = data.AsSpan(0, 12);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span);
        if (type != ResTableType)
        {
            throw new ArscException($"Invalid ARSC root chunk type 0x{type:X4} (expected 0x0002)");
        }
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        var fileSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]));
        if (fileSize > data.Length)
        {
            throw new ArscException($"ARSC header claims {fileSize} bytes but only {data.Length} are available");
        }

        var table = new ResourceTable();

        var pos = (int)headerSize;
        while (pos + 8 <= fileSize)
        {
            var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)));
            if (chunkSize < 8 || pos + chunkSize > fileSize)
            {
                throw new ArscException($"Invalid chunk at 0x{pos:X} (type 0x{chunkType:X4}, size {chunkSize})");
            }

            switch (chunkType)
            {
                case ResStringPoolType:
                    table.GlobalPool.Parse(data, pos, chunkSize);
                    break;

                case ResTablePackageType:
                    ParsePackage(data, pos, chunkSize, table);
                    break;

                default:



                    break;
            }

            pos += chunkSize;
        }

        return table;
    }

    /// <summary>
    /// Checks whether the header looks like ARSC magic (for content sniffing).
    /// <para>判断头部是否为 ARSC 魔数（用于内容探测）。</para>
    /// </summary>
    public static bool IsArsc(byte[] data)
    {
        if (data is null || data.Length < 12) return false;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        return type == ResTableType;
    }

    private static void ParsePackage(byte[] data, int offset, int chunkSize, ResourceTable table)
    {


        var id = data[offset + 8];
        var name = Encoding.UTF8.GetString(data, offset + 12, 128 * 2).TrimEnd('\0');
        var pkg = new ResPackage
        {
            Id = id,
            Name = name
        };

        var pos = offset + headerSizeOf(data, offset);
        var end = offset + chunkSize;


        var typeSpecIds = new HashSet<byte>();
        while (pos + 8 <= end)
        {
            var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            var subSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)));
            if (subSize < 8 || pos + subSize > end)
            {
                break;
            }

            switch (chunkType)
            {
                case ResStringPoolType:
                    {


                        if (pkg.TypeStrings.Count == 0 && pkg.KeyStrings.Count == 0)
                        {
                            pkg.TypeStrings.Parse(data, pos, subSize);
                        }
                        else if (pkg.KeyStrings.Count == 0)
                        {
                            pkg.KeyStrings.Parse(data, pos, subSize);
                        }
                        break;
                    }

                case ResTableTypeSpecType:
                    {



                        var specId = data[pos + 8];
                        var specEntryCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12)));
                        var flags = new uint[specEntryCount];
                        for (var f = 0; f < specEntryCount; f++)
                        {
                            flags[f] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 16 + f * 4));
                        }
                        pkg.TypeSpecFlags[specId] = flags;
                        break;
                    }

                case ResTableTypeType:
                    ParseTypeTable(data, pos, subSize, pkg);
                    break;
            }

            pos += subSize;
        }

        table.Packages.Add(pkg);
    }

    private static void ParseTypeTable(byte[] data, int offset, int chunkSize, ResPackage pkg)
    {


        var id = data[offset + 8];
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
        var entriesStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16)));


        var configSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20));
        var configActualSize = configSize is >= 0x40 and <= 0x80 ? checked((int)configSize) : 0x40;

        var type = new ResType
        {
            Id = id,


            TypeNameIndex = Math.Max(0, id - 1),
            ConfigDescription = ParseConfig(data, offset + 20),
            ConfigHex = Convert.ToHexString(data.AsSpan(offset + 20, configActualSize))
        };


        var offsetsBase = offset + 20 + configActualSize;
        for (var i = 0; i < entryCount; i++)
        {
            var entryOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsBase + i * 4));
            if (entryOff == NoEntry)
            {
                type.Entries.Add(null);
                continue;
            }

            var entryPos = offset + entriesStart + checked((int)entryOff);
            type.Entries.Add(ParseEntry(data, entryPos, pkg));
        }

        pkg.Types.Add(type);
    }

    private static ResEntry? ParseEntry(byte[] data, int offset, ResPackage pkg)
    {

        var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        var keyIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)));

        var entry = new ResEntry { KeyIndex = keyIndex };

        if ((flags & EntryFlagComplex) != 0)
        {

            var parent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
            var mapCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
            var value = new ResValue { IsComplex = true, Parent = parent };
            var mapPos = offset + 16;
            for (var m = 0; m < mapCount; m++)
            {
                var name = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(mapPos));
                var val = ParseResValue(data, mapPos + 4, pkg);
                value.Maps.Add(new ResMap { Name = name, Value = val });
                mapPos += 12;
            }
            entry.Value = value;
        }
        else
        {
            entry.Value = ParseResValue(data, offset + 8, pkg);
        }

        return entry;
    }

    private static ResValue ParseResValue(byte[] data, int offset, ResPackage pkg)
    {

        var valueType = data[offset + 3];
        var valueData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        return new ResValue
        {
            Type = (ResValueType)valueType,
            Data = valueData
        };
    }

    private static int headerSizeOf(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));

    /// <summary>
    /// Parses a ResTable_config into a readable description (mcc/mnc, locale, orientation, density).
    /// <para>解析 ResTable_config 为可读描述（mcc/mnc、locale、orientation、density）。</para>
    /// </summary>
    private static string ParseConfig(byte[] data, int offset)
    {


        var mcc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
        var mnc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6));
        var language = data[offset + 8];
        var country = data[offset + 9];
        var orientation = data[offset + 12];
        var density = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 14));

        var sb = new StringBuilder();
        if (mcc != 0) sb.Append("mcc").Append(mcc.ToString("D3", CultureInfo.InvariantCulture)).Append('-');
        if (mnc != 0) sb.Append("mnc").Append(mnc.ToString("D3", CultureInfo.InvariantCulture)).Append('-');
        if (language != 0 && language != 0xff)
        {
            sb.Append((char)language);
            if (country != 0 && country != 0xff) sb.Append((char)country);
            sb.Append('-');
        }
        if (orientation != 0 && orientation != 0xff)
        {
            sb.Append(orientation switch
            {
                1 => "port",
                2 => "land",
                _ => "orient" + orientation
            }).Append('-');
        }
        if (density != 0 && density != 0xffff)
        {
            sb.Append("d").Append(density == 0xfffe ? "anydpi" : density.ToString(CultureInfo.InvariantCulture));
            sb.Append('-');
        }
        var result = sb.ToString().TrimEnd('-');
        return result.Length > 0 ? result : "default";
    }
}

public sealed class ArscException : Exception
{
    /// <summary>
    /// Creates an ARSC exception.
    /// <para>创建 ARSC 异常。</para>
    /// </summary>
    public ArscException(string message) : base(message) { }
}
