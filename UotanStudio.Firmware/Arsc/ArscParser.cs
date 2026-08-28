using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UotanStudio.Firmware.Arsc;

/// <summary>
/// <para>Resources.arsc 解析器：按 AOSP <c>ResourceTypes.h</c> 定义遍历 chunk 链，
/// 构建资源表模型（全局字符串池、包、类型表、条目与配置）。纯托管实现，无反射。</para>
/// Resources.arsc parser: walks the chunk chain per AOSP <c>ResourceTypes.h</c>, building the
/// resource-table model (global string pool, packages, type tables, entries and configurations).
/// Fully managed, no reflection.
/// </summary>
public static class ArscParser
{
    // ResChunk_header.type 值（AOSP ResourceTypes.h）
    // ResChunk_header.type values (AOSP ResourceTypes.h).
    private const ushort ResTableType = 0x0002;       // 根：资源表
    private const ushort ResStringPoolType = 0x0001;  // 字符串池
    private const ushort ResTablePackageType = 0x0200; // 包
    private const ushort ResTableTypeSpecType = 0x0202; // 类型规范
    private const ushort ResTableTypeType = 0x0201;    // 类型表

    private const uint NoEntry = 0xFFFFFFFF;
    private const uint NoIndex = 0xFFFFFFFF;
    private const ushort EntryFlagComplex = 0x0001;

    /// <summary>
    /// <para>解析 ARSC 字节为资源表模型。</para>
    /// Parses ARSC bytes into a resource-table model.
    /// </summary>
    /// <param name="data">ARSC 字节。<para>ARSC bytes.</para></param>
    /// <returns>解析后的资源表。<para>The parsed resource table.</para></returns>
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
                    // 未知 chunk：跳过（向前兼容）。
                    // Unknown chunk: skip (forward compatibility).
                    break;
            }

            pos += chunkSize;
        }

        return table;
    }

    /// <summary>
    /// <para>判断头部是否为 ARSC 魔数（用于内容探测）。</para>
    /// Checks whether the header looks like ARSC magic (for content sniffing).
    /// </summary>
    public static bool IsArsc(byte[] data)
    {
        if (data is null || data.Length < 12) return false;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        return type == ResTableType;
    }

    private static void ParsePackage(byte[] data, int offset, int chunkSize, ResourceTable table)
    {
        // ResTable_package: header(8) + id(4) + name[128*2] + typeStrings(4) + lastPublicType(4)
        //                   + keyStrings(4) + lastPublicKey(4) + typeIdOffset(4)
        var id = data[offset + 8];
        var name = Encoding.UTF8.GetString(data, offset + 12, 128 * 2).TrimEnd('\0');
        var pkg = new ResPackage
        {
            Id = id,
            Name = name
        };

        var pos = offset + headerSizeOf(data, offset);
        var end = offset + chunkSize;

        // 包内子 chunk：typeStrings、keyStrings、ResTableTypeSpec、ResTableType 交替出现。
        // Sub-chunks inside the package: typeStrings, keyStrings, ResTableTypeSpec, ResTableType.
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
                        // 包内首个字符串池为 typeStrings，第二个为 keyStrings。
                        // The first string pool is typeStrings; the second is keyStrings.
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
                        // ResTable_typeSpec: header(8) + id(1) + res0(1) + res1(2)
                        //                    + entryCount(4) + flags[entryCount]
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
        // ResTable_type: header(8) + id(1) + res0(1) + res1(2) + entryCount(4)
        //                + entriesStart(4) + config(ResTable_config, 首字段 size)
        var id = data[offset + 8];
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
        var entriesStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16)));

        // config 大小取自其首个 uint32（size 字段）；异常值按标准 0x40 兜底。
        // The config size comes from its first uint32 (the size field); invalid values fall back to 0x40.
        var configSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20));
        var configActualSize = configSize is >= 0x40 and <= 0x80 ? checked((int)configSize) : 0x40;

        var type = new ResType
        {
            Id = id,
            // AOSP 约定：typeStrings 池索引 = typeId - 1。
            // AOSP convention: the typeStrings pool index equals typeId - 1.
            TypeNameIndex = Math.Max(0, id - 1),
            ConfigDescription = ParseConfig(data, offset + 20),
            ConfigHex = Convert.ToHexString(data.AsSpan(offset + 20, configActualSize))
        };

        // 条目偏移表紧跟在 config 之后：header + 20 + config 大小。
        // The entry offset table immediately follows the config: header + 20 + config size.
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
        // ResTable_entry: size(2) + flags(2) + key(4)
        var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        var keyIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)));

        var entry = new ResEntry { KeyIndex = keyIndex };

        if ((flags & EntryFlagComplex) != 0)
        {
            // ResTable_map_entry: parent(4) + count(4) + maps...
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
        // Res_value: size(2) + res0(1) + dataType(1) + data(4)
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
    /// <para>解析 ResTable_config 为可读描述：mcc/mnc、locale、orientation 等关键字段。</para>
    /// Parses a ResTable_config into a readable description: mcc/mnc, locale, orientation and other key fields.
    /// </summary>
    /// <param name="data">文件字节。<para>File bytes.</para></param>
    /// <param name="offset">config 起始偏移（首字段为 uint32 size）。<para>Config start offset (first field is the uint32 size).</para></param>
    private static string ParseConfig(byte[] data, int offset)
    {
        // ResTable_config 布局：size(4) + mcc(2) + mnc(2) + language[2](2) + country[2](2)
        //                    + orientation(1) + touchscreen(1) + density(2) + ...
        // ResTable_config layout: size(4) + mcc(2) + mnc(2) + language[2](2) + country[2](2)
        //                         + orientation(1) + touchscreen(1) + density(2) + ...
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

/// <summary>
/// <para>ARSC 解析异常。</para>
/// ARSC parse exception.
/// </summary>
public sealed class ArscException : Exception
{
    /// <summary>创建 ARSC 异常。<para>Creates an ARSC exception.</para></summary>
    public ArscException(string message) : base(message) { }
}