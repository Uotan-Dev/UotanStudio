using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using UotanStudio.Firmware.Executable.Elf;

namespace UotanStudio.Firmware.AndroidMeta.Art;

/// <summary>
/// Describes an OAT DEX record matched to its companion VDEX entry.
/// <para>描述与配套 VDEX 条目相匹配的 OAT DEX 记录。</para>
/// </summary>
/// <param name="Location">The embedded DEX location, never used as an output path. 内嵌 DEX 位置，不作为输出路径。</param>
/// <param name="Dex">The matched VDEX entry. 匹配的 VDEX 条目。</param>
/// <param name="ClassOffsets">The OAT-relative class offsets. OAT 内的类偏移表。</param>
/// <param name="LayoutHints">The optional version 131 layout hints. 可选的 131 版本布局提示。</param>
/// <param name="MethodMappings">The optional version 131 method BSS mappings. 可选的 131 版本方法 BSS 映射。</param>
public sealed record OatDexEntry(string Location, VdexEntry Dex, IReadOnlyList<uint> ClassOffsets,
    IReadOnlyList<OatLayoutHint> LayoutHints, IReadOnlyList<OatMethodMapping> MethodMappings);

/// <summary>Describes a version 131 DEX layout subsection.
/// <para>描述 131 版本的 DEX 布局子区间。</para></summary>
/// <param name="Section">The code or string section index. 代码或字符串区索引。</param>
/// <param name="Kind">The layout hint kind, from zero to four. 从零到四的布局提示类型。</param>
/// <param name="Offset">The DEX-relative offset. DEX 内偏移。</param>
/// <param name="Size">The byte length. 字节长度。</param>
public sealed record OatLayoutHint(int Section, int Kind, uint Offset, uint Size);

/// <summary>Describes an OAT method BSS mapping without dereferencing runtime memory.
/// <para>描述 OAT 方法 BSS 映射，不解引用运行时内存。</para></summary>
/// <param name="MethodIndex">The highest method index. 最高方法索引。</param>
/// <param name="IndexMask">The preceding sixteen-index mask. 前十六个索引的掩码。</param>
/// <param name="BssOffset">The runtime BSS-relative offset. 运行时 BSS 内偏移。</param>
public sealed record OatMethodMapping(ushort MethodIndex, ushort IndexMask, uint BssOffset);

/// <summary>
/// Contains validated OAT metadata, not a machine-code or runtime trust verdict.
/// <para>包含经过检查的 OAT 元数据，不代表机器码或运行时信任验证结果。</para>
/// </summary>
/// <param name="Version">The supported OAT version, 124 or 131. 支持的 OAT 版本 124 或 131。</param>
/// <param name="InstructionSet">The ART instruction-set identifier. ART 指令集标识。</param>
/// <param name="Metadata">The bounded key/value store. 有界键值存储。</param>
/// <param name="DexFiles">The ordered companion DEX records. 有序配套 DEX 记录。</param>
public sealed record OatInfo(int Version, uint InstructionSet, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<OatDexEntry> DexFiles);

/// <summary>
/// Parses Android 8.0/8.1 OAT/ODEX 124/131 metadata and binds it to VDEX 006/010 respectively.
/// <para>解析 Android 8.0/8.1 OAT/ODEX 124/131 元数据，分别与 VDEX 006/010 配对。</para>
/// </summary>
internal static class OatFile
{
    /// <summary>
    /// Reads raw OAT data or an ELF .rodata section and validates companion locations and class tables.
    /// <para>读取原始 OAT 数据或 ELF 的 .rodata 节，并验证配套条目与类表。</para>
    /// </summary>
    /// <param name="oatPath">The OAT or ART ODEX file, not Dalvik dey/ODEX. OAT 或 ART ODEX 文件，不是 Dalvik dey/ODEX。</param>
    /// <param name="vdexPath">The companion VDEX file. 配套 VDEX 文件。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <returns>Validated metadata and matched entries. 检查后的元数据与配对条目。</returns>
    public static OatInfo Inspect(string oatPath, string vdexPath, CancellationToken cancellationToken = default)
    {
        using var oat = File.OpenRead(oatPath);
        using var vdex = File.OpenRead(vdexPath);
        var entries = VdexFile.Inspect(vdexPath, cancellationToken);
        var companion = new ArtData(vdex, 0, vdex.Length, cancellationToken);
        Span<byte> magic = stackalloc byte[8];
        companion.Read(0, magic);
        var vdexVersion = magic.SequenceEqual("vdex006\0"u8) ? 6 : 10;
        var data = new ArtData(oat, 0, oat.Length, cancellationToken);
        data.Read(0, magic);
        ushort? elfMachine = null;
        if (magic[..4].SequenceEqual("\u007fELF"u8))
        {
            var elf = ElfReader.Read(oat, cancellationToken);
            elfMachine = elf.Header.Machine;
            var sections = elf.Sections.Where(s => s.Name == ".rodata" && s.Type == 1).ToArray();
            if (!elf.Header.IsLittleEndian || sections.Length != 1)
            {
                throw new NotSupportedException("OAT requires one little-endian ELF .rodata section.");
            }
            var section = sections[0];
            data = new ArtData(oat, (long)section.Offset, (long)section.Size, cancellationToken);
            data.Read(0, magic);
        }
        if (!magic[..4].SequenceEqual("oat\n"u8))
        {
            throw new InvalidDataException("No OAT header at the declared data section.");
        }
        var version = magic[4..].SequenceEqual("124\0"u8) ? 124 : magic[4..].SequenceEqual("131\0"u8) ? 131 : 0;
        if (version == 0)
        {
            throw new NotSupportedException("Only OAT 124 and 131 records are supported.");
        }
        if ((version == 124 ? 6 : 10) != vdexVersion)
        {
            throw new InvalidDataException("OAT version does not match its VDEX companion.");
        }
        var headerSize = version == 124 ? 72 : 76;
        data.Check(0, headerSize);
        var isa = data.U32(12);
        if (isa is 0 or > 7 || data.U32(20) != entries.Count)
        {
            throw new InvalidDataException("Invalid OAT instruction set or companion DEX count.");
        }
        var expectedMachine = isa switch { 1 or 3 => 40, 2 => 183, 4 => 3, 5 => 62, _ => 8 };
        if (elfMachine is { } machine && machine != expectedMachine)
        {
            throw new InvalidDataException("OAT instruction set disagrees with its ELF machine.");
        }
        var keySize = data.U32(headerSize - 4);
        if (keySize > 1024 * 1024)
        {
            throw new InvalidDataException("OAT key/value store exceeds the resource limit.");
        }
        var metadataBytes = new byte[(int)keySize];
        data.Read(headerSize, metadataBytes);
        var metadata = ParseMetadata(metadataBytes);
        var cursor = version == 124 ? headerSize + keySize : data.U32(24);
        if (cursor < headerSize + keySize)
        {
            throw new InvalidDataException("OAT DEX records overlap the header or metadata.");
        }
        var records = new List<OatDexEntry>(entries.Count);
        var totalClasses = 0L;
        foreach (var entry in entries)
        {
            var length = data.U32(cursor);
            cursor += 4;
            if (length is 0 or > 4096)
            {
                throw new InvalidDataException("OAT DEX location exceeds the resource limit.");
            }
            var locationBytes = new byte[(int)length];
            data.Read(cursor, locationBytes);
            cursor += length;
            if (locationBytes.Contains((byte)0))
            {
                throw new InvalidDataException("OAT DEX location contains NUL.");
            }
            var location = new UTF8Encoding(false, true).GetString(locationBytes);
            var checksum = data.U32(cursor);
            var dexOffset = data.U32(cursor + 4);
            var classesOffset = data.U32(cursor + 8);
            var lookupOffset = data.U32(cursor + 12);
            cursor += 16;
            var layoutOffset = version == 131 ? data.U32(cursor) : 0;
            var mappingOffset = version == 131 ? data.U32(cursor + 4) : 0;
            if (version == 131)
            {
                cursor += 8;
            }
            if (checksum != entry.LocationChecksum || dexOffset != entry.Offset)
            {
                throw new InvalidDataException("OAT DEX record does not match its VDEX companion.");
            }
            var classCount = companion.U32(entry.Offset + 96);
            totalClasses += classCount;
            if ((classesOffset & 3) != 0 || totalClasses > 1_000_000)
            {
                throw new InvalidDataException("Invalid OAT class offset table.");
            }
            data.Check(classesOffset, classCount * 4L);
            var offsets = new uint[(int)classCount];
            for (var i = 0; i < offsets.Length; i++)
            {
                offsets[i] = data.U32(classesOffset + 4L * i);
                data.Check(offsets[i], 4);
                if (data.U16(offsets[i] + 2) > 2)
                {
                    throw new InvalidDataException("Invalid OAT class compilation type.");
                }
            }
            if (lookupOffset != 0)
            {
                if (classCount is 0 or > 65535)
                {
                    throw new InvalidDataException("Invalid OAT type lookup table count.");
                }
                var capacity = 1;
                while (capacity < classCount)
                {
                    capacity <<= 1;
                }
                data.Check(lookupOffset, capacity * 8L);
            }
            var layouts = ReadLayouts(data, layoutOffset, entry.Size);
            var mappings = ReadMappings(data, mappingOffset, companion.U32(entry.Offset + 88), isa);
            records.Add(new OatDexEntry(location, entry, Array.AsReadOnly(offsets), layouts, mappings));
        }
        return new OatInfo(version, isa, new ReadOnlyDictionary<string, string>(metadata), records.AsReadOnly());
    }

    private static IReadOnlyList<OatLayoutHint> ReadLayouts(ArtData data, uint offset, uint dexSize)
    {
        if (offset == 0)
        {
            return [];
        }
        data.Check(offset, 80);
        var result = new List<OatLayoutHint>(10);
        for (var i = 0; i < 10; i++)
        {
            var start = data.U32(offset + 8L * i);
            var size = data.U32(offset + 8L * i + 4);
            if (start > dexSize || size > dexSize - start)
            {
                throw new InvalidDataException("OAT layout hint extends beyond its DEX.");
            }
            result.Add(new OatLayoutHint(i / 5, i % 5, start, size));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<OatMethodMapping> ReadMappings(ArtData data, uint offset, uint methodCount, uint isa)
    {
        if (offset == 0)
        {
            return [];
        }
        var count = data.U32(offset);
        if (count > methodCount || count > 65536)
        {
            throw new InvalidDataException("Invalid OAT BSS mapping count.");
        }
        data.Check(offset + 4L, count * 8L);
        var result = new List<OatMethodMapping>((int)count);
        var pointerSize = isa is 2 or 5 or 7 ? 8 : 4;
        var previous = -1;
        for (var i = 0; i < count; i++)
        {
            var at = offset + 4L + i * 8L;
            var method = data.U16(at);
            var mask = data.U16(at + 2);
            var bss = data.U32(at + 4);
            if (method >= methodCount || method <= previous ||
                (method < 16 && (mask & ((1 << (16 - method)) - 1)) != 0) ||
                bss % pointerSize != 0 || bss < BitOperations.PopCount((uint)mask) * pointerSize)
            {
                throw new InvalidDataException("Invalid OAT method BSS mapping.");
            }
            result.Add(new OatMethodMapping(method, mask, bss));
            previous = method;
        }
        return result.AsReadOnly();
    }

    private static Dictionary<string, string> ParseMetadata(byte[] bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false, true);
        var position = 0;
        while (position < bytes.Length)
        {
            var keyEnd = Array.IndexOf(bytes, (byte)0, position);
            var valueEnd = keyEnd < 0 ? -1 : Array.IndexOf(bytes, (byte)0, keyEnd + 1);
            if (keyEnd <= position || valueEnd < 0 || result.Count >= 4096)
            {
                throw new InvalidDataException("Malformed OAT key/value store.");
            }
            if (!result.TryAdd(utf8.GetString(bytes, position, keyEnd - position),
                utf8.GetString(bytes, keyEnd + 1, valueEnd - keyEnd - 1)))
            {
                throw new InvalidDataException("Duplicate OAT metadata key.");
            }
            position = valueEnd + 1;
        }
        return result;
    }
}