using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Hash.RockchipCrc32;

namespace UotanStudio.Firmware.VendorFirmware.Rockchip;

/// <summary>
/// Staged entry table an RKBOOT/RKLOADER entry belongs to.
/// <para>RKBOOT/RKLOADER 条目所属的分阶段条目表。</para>
/// </summary>
internal enum RockchipLoaderEntryStage
{
    /// <summary>0x471 first-stage entries uploaded to the boot ROM (USB head and DDR init blob).
    /// <para>上传到 boot ROM 的 0x471 第一阶段条目（USB 头与 DDR 初始化数据）。</para></summary>
    Stage471,

    /// <summary>0x472 second-stage entries (USB plug).
    /// <para>0x472 第二阶段条目（USB plug）。</para></summary>
    Stage472,

    /// <summary>Loader entries stored on flash (flash head, data and boot).
    /// <para>落盘使用的 loader 条目（flash 头、数据与启动）。</para></summary>
    Loader,
}

/// <summary>
/// One RKBOOT/RKLOADER entry: its stage, table index, type code, UTF-16 name and declared payload range.
/// <para>一个 RKBOOT/RKLOADER 条目：所属阶段、表内序号、类型码、UTF-16 名称与声明的负载范围。</para>
/// </summary>
/// <param name="Stage">Staged table the entry was read from.
/// <para>读取该条目所属的分阶段表。</para></param>
/// <param name="Index">Zero-based position inside its table.
/// <para>在其所属表内的零基位置。</para></param>
/// <param name="Type">Entry type code (1 for 0x471, 2 for 0x472, 4 for loader entries).
/// <para>条目类型码（0x471 为 1，0x472 为 2，loader 条目为 4）。</para></param>
/// <param name="Name">Entry name decoded from the fixed 20-character UTF-16 field.
/// <para>由固定 20 个 UTF-16 字符字段解码出的条目名称。</para></param>
/// <param name="DataOffset">Payload offset from the start of the image.
/// <para>负载相对镜像起始的偏移。</para></param>
/// <param name="DataSize">Payload length in bytes.
/// <para>负载字节长度。</para></param>
/// <param name="DataDelay">Delay in milliseconds the boot ROM protocol waits after uploading this entry.
/// <para>上传该条目后 boot ROM 协议等待的毫秒数。</para></param>
internal sealed record RockchipLoaderEntry(
    RockchipLoaderEntryStage Stage,
    int Index,
    uint Type,
    string Name,
    uint DataOffset,
    uint DataSize,
    uint DataDelay)
{
    private const int MaximumNameLength = 48;

    /// <summary>Compact stage label used in reported names ("471", "472" or "loader").
    /// <para>上报名称中使用的阶段简称（"471"、"472" 或 "loader"）。</para>
    /// </summary>
    /// <value>The stage label text. <para>阶段简称文本。</para></value>
    internal string StageLabel => Stage switch
    {
        RockchipLoaderEntryStage.Stage471 => "471",
        RockchipLoaderEntryStage.Stage472 => "472",
        _ => "loader",
    };

    /// <summary>Name an extraction writes for this entry; it always starts with the stage label and the table index, so entries with an empty or repeated name stay distinct.
    /// <para>解包为该条目写出的文件名；始终以阶段简称与表内序号开头，因此空名或重名条目也不会相互覆盖。</para>
    /// </summary>
    /// <value>The sanitized file name. <para>净化后的文件名。</para></value>
    internal string FileName =>
        $"{StageLabel}_{Index.ToString(CultureInfo.InvariantCulture)}_{SanitizeName(Name)}.bin";

    private static string SanitizeName(string name)
    {
        var text = name.Trim();
        var separator = text.LastIndexOfAny(['/', '\\']);
        if (separator >= 0)
        {
            text = text[(separator + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) || Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var clean = builder.ToString().Trim();
        if (clean.Length == 0 || clean is "." or "..")
        {
            return "entry";
        }

        return clean.Length > MaximumNameLength ? clean[..MaximumNameLength] : clean;
    }
}

/// <summary>
/// Header and staged entry tables of a Rockchip RKBOOT/RKLOADER image (STRUCT_RKBOOT_HEAD plus the 0x471, 0x472 and
/// loader entry tables), with the trailing Rockchip CRC-32 covering every preceding byte.
/// <para>Rockchip RKBOOT/RKLOADER 镜像的头部与分阶段条目表（STRUCT_RKBOOT_HEAD 以及 0x471、0x472 与 loader 三张条目表），
/// 末尾是覆盖其前全部字节的 Rockchip CRC-32。</para>
/// </summary>
internal sealed class RockchipLoaderImage
{
    /// <summary>Size in bytes of the packed loader header that every entry-table offset is relative to.
    /// <para>全部条目表偏移所参照的紧凑 loader 头部字节数。</para>
    /// </summary>
    internal const int HeaderSize = 102;

    /// <summary>Size in bytes of one packed entry (STRUCT_RKBOOT_ENTRY); larger declared entry strides are accepted.
    /// <para>一个紧凑条目（STRUCT_RKBOOT_ENTRY）的字节数；更大的声明条目步长同样被接受。</para>
    /// </summary>
    internal const int EntrySize = 57;

    /// <summary>Size in bytes of the trailing checksum.
    /// <para>末尾校验和的字节数。</para>
    /// </summary>
    internal const int ChecksumSize = 4;

    private const int EntryNameOffset = 5;
    private const int EntryNameLength = 40;
    private const int MaximumEntrySize = 1024;
    private const int ChecksumChunkSize = 64 * 1024;

    private static ReadOnlySpan<byte> MagicBoot => "BOOT"u8;
    private static ReadOnlySpan<byte> MagicLdr => "LDR "u8;

    private RockchipLoaderImage()
    {
    }

    /// <summary>Four-character header tag exactly as stored, either <c>BOOT</c> or <c>LDR </c>.
    /// <para>按原样存储的四字符头部标签，取值为 <c>BOOT</c> 或 <c>LDR </c>。</para>
    /// </summary>
    /// <value>The raw tag text. <para>原始标签文本。</para></value>
    internal string Tag { get; private init; } = string.Empty;

    /// <summary>Loader version word; its high and low bytes are the major and minor numbers.
    /// <para>loader 版本字；其高字节与低字节分别是主版本号与次版本号。</para>
    /// </summary>
    /// <value>The packed version word. <para>打包后的版本字。</para></value>
    internal uint Version { get; private init; }

    /// <summary>Merge tool version word written by the tool that produced the image.
    /// <para>生成该镜像的工具写入的合并工具版本字。</para>
    /// </summary>
    /// <value>The packed merge version word. <para>打包后的合并版本字。</para></value>
    internal uint MergeVersion { get; private init; }

    /// <summary>Release time recorded in the header, formatted as <c>yyyy-MM-dd HH:mm:ss</c>; empty when the
    /// calendar fields are out of range.
    /// <para>头部记录的发布时间，格式为 <c>yyyy-MM-dd HH:mm:ss</c>；日历字段越界时为空字符串。</para>
    /// </summary>
    /// <value>The formatted release time or an empty string. <para>格式化后的发布时间，或空字符串。</para></value>
    internal string ReleaseTime { get; private init; } = string.Empty;

    /// <summary>Chip code stated by the header. Rockchip packs the four ASCII characters of the chip name without its
    /// <c>RK</c> prefix into the high-to-low bytes of the word, so the on-disk byte order is reversed; the field is
    /// reported as that packed code (for example <c>3588</c> or <c>330C</c>) and falls back to <c>0x</c> plus the raw
    /// value when a byte is not printable ASCII.
    /// <para>头部声明的芯片代码。瑞芯微把去掉 <c>RK</c> 前缀的芯片名四个 ASCII 字符按高位到低位打包进该字，
    /// 因此磁盘上的字节顺序是反的；此处按打包后的代码上报（例如 <c>3588</c> 或 <c>330C</c>），
    /// 当某个字节不是可打印 ASCII 时退回 <c>0x</c> 加原始值。</para>
    /// </summary>
    /// <value>The packed chip code text. <para>打包后的芯片代码文本。</para></value>
    internal string Chip { get; private init; } = string.Empty;

    /// <summary>Whether the header marks the image as signed (sign flag equal to <c>S</c>).
    /// <para>头部是否把该镜像标记为已签名（签名标志等于 <c>S</c>）。</para>
    /// </summary>
    /// <value><see langword="true"/> when the sign flag carries <c>S</c>.
    /// <para>签名标志为 <c>S</c> 时为 <see langword="true"/>。</para></value>
    internal bool Signed { get; private init; }

    /// <summary>Whether the header declares RC4 as disabled (non-zero RC4 flag).
    /// <para>头部是否声明已禁用 RC4（RC4 标志非零）。</para>
    /// </summary>
    /// <value><see langword="true"/> when the RC4 flag is non-zero.
    /// <para>RC4 标志非零时为 <see langword="true"/>。</para></value>
    internal bool Rc4Disabled { get; private init; }

    /// <summary>Header length declared by the image, in bytes.
    /// <para>镜像声明的头部字节长度。</para>
    /// </summary>
    /// <value>The declared header length. <para>声明的头部长度。</para></value>
    internal int DeclaredHeaderSize { get; private init; }

    /// <summary>Every entry of the three tables in table order (0x471, then 0x472, then loader).
    /// <para>三张表的全部条目，按表顺序排列（先 0x471，再 0x472，最后 loader）。</para>
    /// </summary>
    /// <value>The flattened entry list, never <see langword="null"/>.
    /// <para>展平后的条目列表，绝不为 <see langword="null"/>。</para></value>
    internal IReadOnlyList<RockchipLoaderEntry> Entries { get; private init; } = [];

    /// <summary>Number of 0x471 entries declared by the header.
    /// <para>头部声明的 0x471 条目数量。</para>
    /// </summary>
    /// <value>The 0x471 entry count. <para>0x471 条目数量。</para></value>
    internal int EntryCount471 { get; private init; }

    /// <summary>Number of 0x472 entries declared by the header.
    /// <para>头部声明的 0x472 条目数量。</para>
    /// </summary>
    /// <value>The 0x472 entry count. <para>0x472 条目数量。</para></value>
    internal int EntryCount472 { get; private init; }

    /// <summary>Number of loader entries declared by the header.
    /// <para>头部声明的 loader 条目数量。</para>
    /// </summary>
    /// <value>The loader entry count. <para>loader 条目数量。</para></value>
    internal int EntryCountLoader { get; private init; }

    /// <summary>Checksum stored in the last four bytes of the image.
    /// <para>存储在镜像末尾四字节中的校验和。</para>
    /// </summary>
    /// <value>The stored Rockchip CRC-32 value. <para>存储的 Rockchip CRC-32 值。</para></value>
    internal uint StoredChecksum { get; private init; }

    /// <summary>Checksum recomputed over every byte before the trailing checksum.
    /// <para>对末尾校验和之前的全部字节重新计算的校验和。</para>
    /// </summary>
    /// <value>The recomputed Rockchip CRC-32 value. <para>重算得到的 Rockchip CRC-32 值。</para></value>
    internal uint ComputedChecksum { get; private init; }

    /// <summary>Whether the stored checksum matches the recomputed one.
    /// <para>存储的校验和是否与重算值一致。</para>
    /// </summary>
    /// <value><see langword="true"/> when both checksums are equal.
    /// <para>两个校验和相等时为 <see langword="true"/>。</para></value>
    internal bool ChecksumMatches => StoredChecksum == ComputedChecksum;

    /// <summary>Loader version formatted as <c>major.minor</c> from the packed version word.
    /// <para>由打包版本字格式化出的 <c>主版本.次版本</c> 文本。</para>
    /// </summary>
    /// <value>The dotted loader version. <para>点分 loader 版本。</para></value>
    internal string VersionText => ((Version >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture) + "." +
        (Version & 0xFF).ToString(CultureInfo.InvariantCulture);

    /// <summary>Merge tool version formatted as <c>major.minor.patch</c> from the packed merge version word.
    /// <para>由打包合并版本字格式化出的 <c>主版本.次版本.补丁</c> 文本。</para>
    /// </summary>
    /// <value>The dotted merge version. <para>点分合并版本。</para></value>
    internal string MergeVersionText => string.Create(CultureInfo.InvariantCulture,
        $"{(MergeVersion >> 24) & 0xFF}.{(MergeVersion >> 16) & 0xFF}.{MergeVersion & 0xFFFF}");

    /// <summary>Reads the header, both entry tables and the trailing checksum from a loader image.
    /// <para>从 loader 镜像读取头部、两张条目表与末尾校验和。</para>
    /// </summary>
    /// <param name="stream">Seekable stream over the image; it is read from offset zero and left open.
    /// <para>镜像的可寻址流；从偏移零开始读取，读取后保持打开。</para></param>
    /// <param name="cancellationToken">Cancels between entry tables and between checksum chunks.
    /// <para>在各条目表之间以及校验和数据块之间取消。</para></param>
    /// <returns>The parsed image; a checksum mismatch is reported through <see cref="ChecksumMatches"/> rather than
    /// thrown. <para>解析结果；校验和不一致通过 <see cref="ChecksumMatches"/> 报告而不会抛出。</para></returns>
    /// <exception cref="InvalidDataException">The stream is shorter than one header plus checksum, the tag is neither
    /// <c>BOOT</c> nor <c>LDR </c>, or a declared header, entry-table or payload range lies outside the image.
    /// <para>流短于一个头部加校验和、标签既非 <c>BOOT</c> 也非 <c>LDR </c>，或声明的头部、条目表、负载范围超出镜像。</para></exception>
    internal static RockchipLoaderImage Read(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        var bodyLength = length - ChecksumSize;
        if (length < HeaderSize + ChecksumSize)
        {
            throw new InvalidDataException(
                $"Rockchip loader image is {length} bytes, below the {HeaderSize + ChecksumSize}-byte minimum.");
        }

        var header = new byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        cancellationToken.ThrowIfCancellationRequested();

        if (!header.AsSpan(0, 4).SequenceEqual(MagicBoot) && !header.AsSpan(0, 4).SequenceEqual(MagicLdr))
        {
            throw new InvalidDataException("Rockchip loader image does not start with the BOOT or LDR tag.");
        }

        var declaredHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (declaredHeaderSize < HeaderSize || declaredHeaderSize > bodyLength)
        {
            throw new InvalidDataException(
                $"Rockchip loader header declares {declaredHeaderSize} bytes, outside the {length}-byte image.");
        }

        var tables = new[]
        {
            (Count: (int)header[25], Offset: (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(26)), EntrySize: (int)header[30]),
            (Count: (int)header[31], Offset: (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32)), EntrySize: (int)header[36]),
            (Count: (int)header[37], Offset: (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(38)), EntrySize: (int)header[42]),
        };
        RockchipLoaderEntryStage[] stages =
        [
            RockchipLoaderEntryStage.Stage471,
            RockchipLoaderEntryStage.Stage472,
            RockchipLoaderEntryStage.Loader,
        ];

        List<RockchipLoaderEntry> entries = [];
        for (var index = 0; index < tables.Length; index++)
        {
            entries.AddRange(ReadEntries(stream, tables[index], stages[index], bodyLength, cancellationToken));
        }

        stream.Position = bodyLength;
        Span<byte> stored = stackalloc byte[ChecksumSize];
        stream.ReadExactly(stored);
        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(stored);

        return new RockchipLoaderImage
        {
            Tag = Encoding.ASCII.GetString(header, 0, 4),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6)),
            MergeVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10)),
            ReleaseTime = DescribeReleaseTime(header.AsSpan(14, 7)),
            Chip = DescribeChip(header),
            Signed = header[43] == (byte)'S',
            Rc4Disabled = header[44] != 0,
            DeclaredHeaderSize = declaredHeaderSize,
            Entries = entries,
            EntryCount471 = tables[0].Count,
            EntryCount472 = tables[1].Count,
            EntryCountLoader = tables[2].Count,
            StoredChecksum = storedChecksum,
            ComputedChecksum = ComputeChecksum(stream, bodyLength, cancellationToken),
        };
    }

    /// <summary>Rejects a header prefix that is not a loader header, so detection never claims another format.
    /// <para>拒绝不是 loader 头部的头部前缀，使格式识别不会误认其它格式。</para>
    /// </summary>
    /// <param name="header">Leading image bytes; shorter prefixes never match.
    /// <para>镜像起始字节；更短的前缀绝不匹配。</para></param>
    /// <returns><see langword="true"/> when the tag, the declared header length and the first entry-table stride are
    /// self-consistent. <para>当标签、声明的头部长度与首个条目表步长自洽时为 <see langword="true"/>。</para></returns>
    internal static bool HasHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            return false;
        }

        if (!header[..4].SequenceEqual(MagicBoot) && !header[..4].SequenceEqual(MagicLdr))
        {
            return false;
        }

        var declaredHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (declaredHeaderSize < HeaderSize)
        {
            return false;
        }

        return header[25] == 0 || ((int)header[30] is >= EntrySize and <= MaximumEntrySize);
    }

    private static List<RockchipLoaderEntry> ReadEntries(Stream stream, (int Count, long Offset, int EntrySize) table,
        RockchipLoaderEntryStage stage, long bodyLength, CancellationToken cancellationToken)
    {
        if (table.Count == 0)
        {
            return [];
        }

        if (table.EntrySize is < EntrySize or > MaximumEntrySize)
        {
            throw new InvalidDataException(
                $"Rockchip loader {stage} table declares {table.EntrySize}-byte entries.");
        }

        var tableLength = (long)table.Count * table.EntrySize;
        if (table.Offset < 0 || table.Offset > bodyLength || tableLength > bodyLength - table.Offset)
        {
            throw new InvalidDataException(
                $"Rockchip loader {stage} entry table of {tableLength} bytes does not fit inside the image.");
        }

        var bytes = new byte[table.Count * table.EntrySize];
        stream.Position = table.Offset;
        stream.ReadExactly(bytes);

        var entries = new List<RockchipLoaderEntry>(table.Count);
        for (var index = 0; index < table.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = bytes.AsSpan(index * table.EntrySize, EntrySize);
            if (raw[0] < EntrySize)
            {
                throw new InvalidDataException(
                    $"Rockchip loader entry {index} of {stage} declares a {raw[0]}-byte stride.");
            }

            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[45..]);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[49..]);
            if ((ulong)dataOffset + dataSize > (ulong)bodyLength)
            {
                throw new InvalidDataException(
                    $"Rockchip loader entry {index} of {stage} declares payload bytes outside the image.");
            }

            entries.Add(new RockchipLoaderEntry(
                stage,
                index,
                BinaryPrimitives.ReadUInt32LittleEndian(raw[1..]),
                Encoding.Unicode.GetStringTrimNul(raw.Slice(EntryNameOffset, EntryNameLength)),
                dataOffset,
                dataSize,
                BinaryPrimitives.ReadUInt32LittleEndian(raw[53..])));
        }

        return entries;
    }

    private static string DescribeReleaseTime(ReadOnlySpan<byte> time)
    {
        var year = BinaryPrimitives.ReadUInt16LittleEndian(time);
        int month = time[2];
        int day = time[3];
        int hour = time[4];
        int minute = time[5];
        int second = time[6];
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
        {
            return string.Empty;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{year:D4}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{second:D2}");
    }

    private static string DescribeChip(ReadOnlySpan<byte> header)
    {
        var chip = BinaryPrimitives.ReadUInt32LittleEndian(header[21..]);
        Span<char> text = stackalloc char[4];
        for (var index = 0; index < text.Length; index++)
        {
            var value = (byte)(chip >> (24 - (index * 8)));
            if (value is < 0x20 or > 0x7E)
            {
                return "0x" + chip.ToString("X8", CultureInfo.InvariantCulture);
            }

            text[index] = (char)value;
        }

        return new string(text).TrimEnd();
    }

    private static uint ComputeChecksum(Stream stream, long bodyLength, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChecksumChunkSize);
        try
        {
            stream.Position = 0;
            var checksum = 0U;
            var remaining = bodyLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(buffer.Length, remaining);
                var read = stream.ReadAtLeast(buffer.AsSpan(0, chunk), chunk, throwOnEndOfStream: false);
                if (read <= 0)
                {
                    throw new InvalidDataException("Rockchip loader image ends before its declared length.");
                }

                checksum = RockchipCrc32.Compute(buffer.AsSpan(0, read), checksum);
                remaining -= read;
            }

            return checksum;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}