using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// One entry of an RKBOOT loader image (STRUCT_RKBOOT_ENTRY, see rkdeveloptool RKBoot.h).
/// <para>RKBOOT loader 镜像中的一个条目（STRUCT_RKBOOT_ENTRY，见 rkdeveloptool 的 RKBoot.h）。</para>
/// </summary>
internal sealed class RkBootEntry
{
    /// <summary>
    /// Gets the entry type (ENUM_RKBOOTENTRY). <para>获取条目类型（ENUM_RKBOOTENTRY）。</para>
    /// </summary>
    public uint Type { get; init; }

    /// <summary>
    /// Gets the entry name (UTF-16, up to 20 wchar). <para>获取条目名称（UTF-16，最多 20 个 wchar）。</para>
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the byte offset of the entry data within the loader image.
    /// <para>获取条目数据在 loader 镜像内的字节偏移。</para>
    /// </summary>
    public uint DataOffset { get; init; }

    /// <summary>
    /// Gets the byte size of the entry data. <para>获取条目数据的字节大小。</para>
    /// </summary>
    public uint DataSize { get; init; }

    /// <summary>
    /// Gets the delay in milliseconds to wait after downloading this entry
    /// (dwDataDelay in the entry header).
    /// <para>获取下载该条目后需等待的毫秒数（条目头中的 dwDataDelay）。</para>
    /// </summary>
    public uint DataDelay { get; init; }
}

/// <summary>
/// Parser for Rockchip RKBOOT loader images (.bin loader files downloaded via the 0x0471/0x0472
/// vendor requests, see rkdeveloptool RKBoot.cpp / RKImage.cpp). The image starts with a
/// STRUCT_RKBOOT_HEAD (tag "BOOT"/0x544F4F42 or "LDR "/0x2052444C) that points at three entry
/// tables (471, 472 and loader), and the whole file is validated by a Rockchip CRC-32
/// (poly 0x04C10DB7) stored in the last four bytes.
/// <para>Rockchip RKBOOT loader 镜像解析器（通过 0x0471/0x0472 vendor 请求下载的 .bin loader
/// 文件，见 rkdeveloptool 的 RKBoot.cpp / RKImage.cpp）。镜像以 STRUCT_RKBOOT_HEAD 开头
/// （标签 "BOOT"/0x544F4F42 或 "LDR "/0x2052444C），指向三个条目表（471、472 与 loader），
/// 整个文件由存储在末尾 4 字节的 Rockchip CRC-32（多项式 0x04C10DB7）校验。</para>
/// </summary>
internal sealed class RkBootImage
{
    /// <summary>
    /// Gets the 471 (first-stage) entries, in download order.
    /// <para>获取 471（第一阶段）条目，按下载顺序。</para>
    /// </summary>
    public IReadOnlyList<RkBootEntry> Entries471 { get; private set; } = [];

    /// <summary>
    /// Gets the 472 (second-stage) entries, in download order.
    /// <para>获取 472（第二阶段）条目，按下载顺序。</para>
    /// </summary>
    public IReadOnlyList<RkBootEntry> Entries472 { get; private set; } = [];

    /// <summary>
    /// Gets the loader (on-flash) entries, in download order.
    /// <para>获取 loader（落盘）条目，按下载顺序。</para>
    /// </summary>
    public IReadOnlyList<RkBootEntry> LoaderEntries { get; private set; } = [];

    /// <summary>
    /// Gets the loader header tag ("BOOT" or "LDR "). <para>获取 loader 头标签（"BOOT" 或 "LDR "）。</para>
    /// </summary>
    public string Tag { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the loader version (rk_boot_header.uiVersion, a packed u32).
    /// <para>获取 loader 版本（rk_boot_header.uiVersion，u32）。</para>
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Gets the loader merge version (rk_boot_header.mergerVersion, a packed u32).
    /// <para>获取 loader 合并版本（rk_boot_header.mergerVersion，u32）。</para>
    /// </summary>
    public uint MergeVersion { get; private set; }

    /// <summary>
    /// Gets the supported chip mask (emSupportChip). <para>获取支持的芯片掩码（emSupportChip）。</para>
    /// </summary>
    public uint SupportChip { get; private set; }

    /// <summary>
    /// Gets whether the image is RC4 encrypted (ucRc4Flag). Callers must treat a set flag as fatal:
    /// no RC4 segment decryption exists in this repository, and entry payloads are transmitted
    /// verbatim, so <see cref="RkdlSession.DownloadBootLoader"/> rejects such an image instead of
    /// sending ciphertext that the device would boot as garbage.
    /// <para>获取镜像是否 RC4 加密（ucRc4Flag）。调用方必须把置位的标志视为致命错误：本仓库没有
    /// 任何 RC4 段解密实现，且条目负载是原样传输的，因此 <see cref="RkdlSession.DownloadBootLoader"/>
    /// 会拒绝这类镜像，而不是发送设备会当成垃圾启动的密文。</para>
    /// </summary>
    /// <value><c>true</c> when ucRc4Flag (byte 44 of the loader header) is non-zero; such images are
    /// rejected by the download path rather than decrypted.
    /// <para>当 ucRc4Flag（loader 头第 44 字节）非零时为 <c>true</c>；此类镜像在下载路径被拒绝，
    /// 而不是被解密。</para></value>
    public bool Rc4Enabled { get; private set; }

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
    /// trailing four must match the stored value (rkdeveloptool CRKBoot::CrcCheck).
    /// <para>解析并校验 loader 镜像。除末尾 4 字节外全部字节的 Rockchip CRC-32 必须与存储值
    /// 一致（rkdeveloptool 的 CRKBoot::CrcCheck）。</para>
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
        var actual = RockchipCrc32(buffer.AsSpan(0, buffer.Length - 4));
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
            Rc4Enabled = buffer[44] != 0,
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
            var name = Encoding.Unicode.GetString(span.Slice(5, 40)).TrimEnd('\0');
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

    /// <summary>
    /// Computes the Rockchip CRC-32 (poly 0x04C10DB7, MSB-first, init 0, no final XOR —
    /// gTable_Crc32 in rkdeveloptool crc.cpp).
    /// <para>计算 Rockchip CRC-32（多项式 0x04C10DB7，高位优先，初值 0，无最终异或——
    /// rkdeveloptool crc.cpp 的 gTable_Crc32）。</para>
    /// </summary>
    /// <param name="data">The data to checksum. <para>待校验数据。</para></param>
    /// <returns>The Rockchip CRC-32 value. <para>Rockchip CRC-32 值。</para></returns>
    public static uint RockchipCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C10DB7u : crc << 1;
            }
        }
        return crc;
    }
}