using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Text;
using UotanStudio.Hash.RockchipCrc32;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// One entry of an RKBOOT loader image (STRUCT_RKBOOT_ENTRY).
/// <para>RKBOOT loader 镜像中的一个条目（STRUCT_RKBOOT_ENTRY）。</para>
/// </summary>
internal sealed class RkBootEntry
{
    /// <summary>
    /// Gets the entry type (ENUM_RKBOOTENTRY). <para>获取条目类型（ENUM_RKBOOTENTRY）。</para>
    /// </summary>
    /// <value>The entry type. <para>条目类型。</para></value>
    public uint Type { get; init; }

    /// <summary>
    /// Gets the entry name (UTF-16, up to 20 wchar). <para>获取条目名称（UTF-16，最多 20 个 wchar）。</para>
    /// </summary>
    /// <value>The entry name. <para>条目名称。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the byte offset of the entry data within the loader image.
    /// <para>获取条目数据在 loader 镜像内的字节偏移。</para>
    /// </summary>
    /// <value>The offset of the entry data. <para>条目数据的偏移。</para></value>
    public uint DataOffset { get; init; }

    /// <summary>
    /// Gets the byte size of the entry data. <para>获取条目数据的字节大小。</para>
    /// </summary>
    /// <value>The size in bytes. <para>字节大小。</para></value>
    public uint DataSize { get; init; }

    /// <summary>
    /// Gets the delay in milliseconds to wait after downloading this entry
    /// (dwDataDelay in the entry header).
    /// <para>获取下载该条目后需等待的毫秒数（条目头中的 dwDataDelay）。</para>
    /// </summary>
    /// <value>The post-download delay in milliseconds. <para>下载后需等待的毫秒数。</para></value>
    public uint DataDelay { get; init; }
}

/// <summary>
/// Parser for Rockchip RKBOOT loader images: a header tagged BOOT or LDR points at the
/// 471, 472 and loader entry tables, and the last four bytes hold a Rockchip CRC-32
/// covering the file.
/// <para>Rockchip RKBOOT loader 镜像解析器：标签为 BOOT 或 LDR 的头指向 471、472 与
/// loader 三个条目表，末尾 4 字节保存覆盖整文件的 Rockchip CRC-32。</para>
/// </summary>
internal sealed class RkBootImage
{
    /// <summary>
    /// Gets the 471 (first-stage) entries, in download order.
    /// <para>获取 471（第一阶段）条目，按下载顺序。</para>
    /// </summary>
    /// <value>The first-stage entries. <para>第一阶段条目。</para></value>
    public IReadOnlyList<RkBootEntry> Entries471 { get; private set; } = [];

    /// <summary>
    /// Gets the 472 (second-stage) entries, in download order.
    /// <para>获取 472（第二阶段）条目，按下载顺序。</para>
    /// </summary>
    /// <value>The second-stage entries. <para>第二阶段条目。</para></value>
    public IReadOnlyList<RkBootEntry> Entries472 { get; private set; } = [];

    /// <summary>
    /// Gets the loader (on-flash) entries, in download order.
    /// <para>获取 loader（落盘）条目，按下载顺序。</para>
    /// </summary>
    /// <value>The loader entries. <para>loader 条目。</para></value>
    public IReadOnlyList<RkBootEntry> LoaderEntries { get; private set; } = [];

    /// <summary>
    /// Gets the loader header tag ("BOOT" or "LDR "). <para>获取 loader 头标签（"BOOT" 或 "LDR "）。</para>
    /// </summary>
    /// <value>The four-character header tag. <para>四字符头标签。</para></value>
    public string Tag { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the loader version (rk_boot_header.uiVersion, a packed u32).
    /// <para>获取 loader 版本（rk_boot_header.uiVersion，u32）。</para>
    /// </summary>
    /// <value>The packed loader version. <para>打包的 loader 版本。</para></value>
    public uint Version { get; private set; }

    /// <summary>
    /// Gets the loader merge version (rk_boot_header.mergerVersion, a packed u32).
    /// <para>获取 loader 合并版本（rk_boot_header.mergerVersion，u32）。</para>
    /// </summary>
    /// <value>The packed merge version. <para>打包的合并版本。</para></value>
    public uint MergeVersion { get; private set; }

    /// <summary>
    /// Gets the supported chip mask (emSupportChip). <para>获取支持的芯片掩码（emSupportChip）。</para>
    /// </summary>
    /// <value>The supported chip mask. <para>支持的芯片掩码。</para></value>
    public uint SupportChip { get; private set; }

    /// <summary>
    /// Gets whether the image declares RC4 encryption as disabled (ucRc4Flag, loader header
    /// byte 44). Upstream RKBoot.cpp interprets a non-zero flag as "RC4 disabled", and
    /// boot_merger writes 1 by default, so a normally built loader reports <c>true</c> here.
    /// <para>获取镜像是否声明已禁用 RC4 加密（ucRc4Flag，loader 头第 44 字节）。上游
    /// RKBoot.cpp 把非 0 解释为“禁用 RC4”，且 boot_merger 默认写入 1，因此通常构建出的
    /// loader 在此为 <c>true</c>。</para>
    /// </summary>
    /// <value><c>true</c> when ucRc4Flag is non-zero, which means RC4 is disabled.
    /// <para>ucRc4Flag 非 0（表示已禁用 RC4）时为 <c>true</c>。</para></value>
    public bool Rc4DisabledFlag { get; private set; }

    /// <summary>
    /// Header tag for a full loader image ("BOOT", stored as LE u32 0x544F4F42).
    /// <para>完整 loader 镜像的头标签（"BOOT"，以小端 u32 0x544F4F42 存储）。</para>
    /// </summary>
    public const uint TagBoot = 0x544F4F42;

    /// <summary>
    /// Header tag for a plain loader image ("LDR ", stored as LE u32 0x2052444C).
    /// <para>普通 loader 镜像的头标签（"LDR "，以小端 u32 0x2052444C 存储）。</para>
    /// </summary>
    public const uint TagLdr = 0x2052444C;

    /// <summary>
    /// Size of a serialized STRUCT_RKBOOT_ENTRY on disk (packed).
    /// <para>磁盘上序列化 STRUCT_RKBOOT_ENTRY 的大小（紧凑布局）。</para>
    /// </summary>
    public const int EntrySize = 57;

    /// <summary>
    /// Parses and validates a loader image. The Rockchip CRC-32 over all bytes except the
    /// trailing four must match the value stored there.
    /// <para>解析并校验 loader 镜像。除末尾 4 字节外全部字节的 Rockchip CRC-32 必须与
    /// 存放在那 4 字节的值一致。</para>
    /// </summary>
    /// <param name="buffer">The loader image bytes. <para>loader 镜像字节。</para></param>
    /// <returns>The parsed image. <para>解析后的镜像。</para></returns>
    /// <exception cref="RkdlException">Thrown when the image is truncated, has an unknown tag
    /// or fails the CRC check. <para>镜像截断、标签未知或 CRC 校验失败时抛出。</para></exception>
    public static RkBootImage Parse(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < 1024)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader image too small: {buffer.Length} bytes.");
        }

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
        if (tag is not TagBoot and not TagLdr)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Unknown loader tag 0x{tag:X8} (expected BOOT/LDR).");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(buffer.Length - 4));
        var actual = RockchipCrc32.Compute(buffer.AsSpan(0, buffer.Length - 4));
        if (expected != actual)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader CRC mismatch: stored 0x{expected:X8}, computed 0x{actual:X8}.");
        }

        var image = new RkBootImage
        {
            Tag = Encoding.ASCII.GetString(buffer, 0, 4),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(6)),
            MergeVersion = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(10)),
            SupportChip = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(21)),
            Rc4DisabledFlag = buffer[44] != 0,
        };

        var count471 = buffer[25];
        var offset471 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(26));
        var size471 = buffer[30];
        var count472 = buffer[31];
        var offset472 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(32));
        var size472 = buffer[36];
        var loaderCount = buffer[37];
        var loaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(38));
        var loaderSize = buffer[42];

        image.Entries471 = ParseEntries(buffer, offset471, size471, count471);
        image.Entries472 = ParseEntries(buffer, offset472, size472, count472);
        image.LoaderEntries = ParseEntries(buffer, loaderOffset, loaderSize, loaderCount);
        return image;
    }

    /// <summary>
    /// Parses one entry table. <para>解析一个条目表。</para>
    /// </summary>
    /// <param name="buffer">The whole loader image. <para>完整 loader 镜像。</para></param>
    /// <param name="offset">Byte offset of the table. <para>条目表的字节偏移。</para></param>
    /// <param name="entrySize">Per-entry size (≥ <see cref="EntrySize"/>). <para>每个条目的大小（≥ <see cref="EntrySize"/>）。</para></param>
    /// <param name="count">Number of entries. <para>条目数量。</para></param>
    /// <returns>The parsed entries in table order. <para>按表顺序解析出的条目。</para></returns>
    private static IReadOnlyList<RkBootEntry> ParseEntries(byte[] buffer, uint offset, ushort entrySize, int count)
    {
        if (count == 0)
        {
            return [];
        }

        if (entrySize < EntrySize)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader entry size too small: {entrySize}.");
        }

        var entries = new List<RkBootEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var baseOffset = offset + ((long)i * entrySize);
            if (baseOffset + EntrySize > buffer.Length)
            {
                throw new RkdlException(RkdlError.InvalidParameter, $"Loader entry {i} out of range.");
            }

            var span = buffer.AsSpan((int)baseOffset, EntrySize);
            var name = Encoding.Unicode.GetStringTrimNul(span.Slice(5, 40));
            entries.Add(new RkBootEntry
            {
                Type = BinaryPrimitives.ReadUInt32LittleEndian(span[1..]),
                Name = name,
                DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[45..]),
                DataSize = BinaryPrimitives.ReadUInt32LittleEndian(span[49..]),
                DataDelay = BinaryPrimitives.ReadUInt32LittleEndian(span[53..]),
            });
        }
        return entries;
    }
}