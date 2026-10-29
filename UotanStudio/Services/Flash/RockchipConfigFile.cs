using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One row of an RKDevTool download-list configuration file (<c>config.cfg</c>).
/// <para>RKDevTool 下载列表配置文件（<c>config.cfg</c>）中的一行。</para>
/// </summary>
/// <remarks>
/// <para>The tool writes a fixed 610-byte record per row: the 16-bit record length (610), an 80-byte UTF-16
/// name field, a 512-byte UTF-16 path field and a 16-byte trailing block. In the trailing block the storage
/// word sits at offset 4, the row address at offset 8 and the tool's row checkbox at offset 12, all
/// little-endian 32-bit values; the word at offset 0 is carried through verbatim.</para>
/// <para>该工具为每行写入固定 610 字节记录：16 位记录长度（610）、80 字节 UTF-16 名称、512 字节 UTF-16
/// 路径，以及 16 字节尾块。尾块中存储字位于偏移 4、行地址位于偏移 8、工具的勾选位位于偏移 12，都是小端
/// 32 位值；偏移 0 处的字按原样透传。</para>
/// <para>Two samples that differ only in their storage show what that word carries:
/// <c>rock-5b-emmc.cfg</c> stores <c>2</c> and <c>rock-5b-spinor.cfg</c> stores <c>512</c>, the single bits of
/// the protocol storage identifiers (bit 1 is eMMC, bit 9 is SPI NOR, the numbering CHANGE_STORAGE and
/// READ_STORAGE use), while a <c>Loader</c> row and rows without an image store <c>0</c>. The word counts only
/// when <see cref="RockchipConfigFile.StorageMarker"/> sits at
/// <see cref="RockchipConfigFile.StorageMarkerOffset"/>: the vendor reader gates the word on that byte, and its
/// writer sets it exactly when the row names a storage. Five of the seven configurations available here carry an
/// unrelated constant in that slot (for example <c>0x778700AF</c>, <c>0xA2400110</c> or <c>0xED840000</c>,
/// identical on every row of the file) together with a marker byte that is not <c>'S'</c>, and the vendor tool
/// shows no storage for them. <see cref="StorageWord"/> keeps such bytes and
/// <see cref="RockchipConfigFile.Serialize"/> writes them back unchanged. The checkbox word is <c>1</c> on rows
/// that carry an image path and <c>0</c> on rows that do not, in every sample.</para>
/// <para>两份仅存储类型不同的样本说明了该字的取值：<c>rock-5b-emmc.cfg</c> 存 <c>2</c>、
/// <c>rock-5b-spinor.cfg</c> 存 <c>512</c>，即协议存储标识的单比特（第 1 位为 eMMC、第 9 位为 SPI NOR，
/// 与 CHANGE_STORAGE 和 READ_STORAGE 的编号一致）；<c>Loader</c> 行与无镜像的行存 <c>0</c>。该字仅在
/// <see cref="RockchipConfigFile.StorageMarkerOffset"/> 处为
/// <see cref="RockchipConfigFile.StorageMarker"/> 时才计入：厂商读取逻辑以该字节为准，写入逻辑也只在行指定了
/// 存储时写入它。本机可得的七份配置中有五份在该位置存放每行都相同、与存储无关的常数（例如
/// <c>0x778700AF</c>、<c>0xA2400110</c> 或 <c>0xED840000</c>），且标记字节不是 <c>'S'</c>，厂商工具对这些行的
/// 存储列显示为空。这类字节由 <see cref="StorageWord"/> 原样保留，
/// <see cref="RockchipConfigFile.Serialize"/> 也原样写回。</para>
/// </remarks>
public sealed record RockchipConfigEntry
{
    /// <summary>Gets the row name as shown in the download list.<para>获取下载列表中显示的行名称。</para></summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the image path the row points at.<para>获取该行指向的镜像路径。</para></summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the 32-bit row address taken from the trailing block.<para>获取从尾块中读出的 32 位行地址。</para></summary>
    public uint Address { get; init; }

    /// <summary>
    /// Gets the storage bitmask taken from the trailing block: bit 1 is eMMC, bit 2 is SD and bit 9 is SPI NOR;
    /// zero means the row names no storage, as the loader row does, or carried a word that is not a single
    /// storage bit.
    /// <para>获取从尾块中读出的存储位掩码：第 1 位为 eMMC、第 2 位为 SD、第 9 位为 SPI NOR；<c>0</c> 表示该行
    /// 未指定存储（loader 行即如此），或存储字不是单个存储位。</para>
    /// The numbering and the encoding are the protocol's own: the <c>cs</c> command of
    /// <c>rkdeveloptool</c> documents <c>1=EMMC, 2=SD, 9=SPINOR</c>, READ_STORAGE returns a 32-bit mask those
    /// identifiers index, and CHANGE_STORAGE takes the identifier. A stored <c>2</c> is therefore
    /// <c>1 &lt;&lt; 1</c> and not the identifier <c>2</c>.
    /// <para>A row names a storage only when the marker byte holds <see cref="RockchipConfigFile.StorageMarker"/>;
    /// any other byte there makes the word meaningless and this property reports zero.
    /// <para>只有标记字节为 <see cref="RockchipConfigFile.StorageMarker"/> 时该行才算指定了存储；该字节是别的值
    /// 时存储字无意义，本属性返回零。</para></para>
    /// <para>编号与编码都来自协议本身：<c>rkdeveloptool</c> 的 <c>cs</c> 命令记载
    /// <c>1=EMMC、2=SD、9=SPINOR</c>，READ_STORAGE 返回以这些标识为下标的 32 位掩码，CHANGE_STORAGE 则接收
    /// 标识本身。因此存储的 <c>2</c> 是 <c>1 &lt;&lt; 1</c>，不是标识 <c>2</c>。</para>
    /// </summary>
    public uint StorageMask { get; init; }

    /// <summary>
    /// Gets the storage word exactly as stored at offset 4 of the trailing block, including a value that names
    /// no storage bit. <see cref="StorageMask"/> is the part of it this repository can interpret, and
    /// <see cref="RockchipConfigFile.Serialize"/> leaves this word and its marker byte untouched unless the row
    /// names a storage.
    /// <para>获取尾块偏移 4 处原样存储的存储字，包括不表示任何存储位的值。<see cref="StorageMask"/> 是其中本仓库
    /// 能解释的部分；除非有效存储被改动，<see cref="RockchipConfigFile.Serialize"/> 会原样写回该字。</para>
    /// A word holding several bits is not a storage here even though READ_STORAGE decoding would report its
    /// lowest bit, because the values the vendor tool leaves in that slot are not device-reported masks.
    /// <para>含多个置位的字在这里不算存储，即使按 READ_STORAGE 的解码方式会回报其最低位——厂商工具留在该位置上
    /// 的值并不是设备回报的掩码。</para>
    /// </summary>
    public uint StorageWord { get; init; }

    /// <summary>
    /// Gets whether the vendor tool lists this row checked, the checkbox word at offset 12 of the trailing
    /// block. Every vendor row that carries an image path stores <c>1</c> and every row without one stores
    /// <c>0</c>.
    /// <para>获取厂商工具是否勾选该行，即尾块偏移 12 处的勾选字。厂商样本中带镜像路径的行都存 <c>1</c>，
    /// 不带路径的行都存 <c>0</c>。</para>
    /// </summary>
    public bool Selected { get; init; }

    /// <summary>Gets the trailing block exactly as stored, including bytes after the address.
    /// <para>获取原样保留的尾块，包括地址之后的字节。</para></summary>
    public byte[] Trailing { get; init; } = new byte[RockchipConfigFile.TrailingLength];
}

/// <summary>
/// Names the operation a download-list row asks for, recognized from its row name.
/// <para>由行名称识别出的下载列表行请求的操作。</para>
/// </summary>
public enum RockchipConfigOperation
{
    /// <summary>Writes the row image to a partition.<para>把行镜像写入分区。</para></summary>
    Flash,

    /// <summary>
    /// Supplies the partition table the write resolves its addresses from; such a row is never written as an
    /// image.
    /// <para>提供写入所用地址来源的分区表；这样的行从不作为镜像写入。</para>
    /// </summary>
    Parameter,

    /// <summary>
    /// Supplies the loader image that boots a maskrom device into loader mode; such a row is never written to a
    /// partition.
    /// <para>提供把 Maskrom 设备启动到 loader 模式的 loader 镜像；这样的行从不写入分区。</para>
    /// </summary>
    Loader,
}

/// <summary>
/// Reads and writes the binary download-list configuration RKDevTool stores next to its executable.
/// <para>读写 RKDevTool 存放在可执行文件旁的二进制下载列表配置。</para>
/// </summary>
/// <remarks>
/// <para>The layout is a 29-byte header followed by fixed 610-byte rows. The header is the <c>"CFG\0"</c> magic,
/// the 16-bit header length <c>29</c>, eight 16-bit date/time fields (year, month, day of week, day, hour, minute,
/// second, millisecond; a local-time stamp that matches the sample files' modification times), the row count as
/// one byte, the 32-bit header length <c>29</c> again, and the 16-bit record marker <c>0x0262</c> that every row
/// also starts with. A row is the marker, an 80-byte UTF-16 name, a 512-byte UTF-16 path and a 16-byte trailing
/// block: three padding bytes, the storage marker <c>'S'</c>, the storage bitmask, the 32-bit row address and a 32-bit flag
/// that is <c>1</c> on rows that carry a file and <c>0</c> on the rows shipped without one. The layout is checked
/// against the <c>config.cfg</c> files shipped with RKDevTool v2.96: <c>rock-5b-emmc.cfg</c>,
/// <c>rock-5b-spinor.cfg</c> and a 10399-byte <c>config.cfg</c> with 17 rows. The vendor reader takes the row
/// count from the header byte at offset 22, the first row from the 32-bit value at 23 and the stride from the
/// 16-bit value at 27, and it honours the storage word only when the byte before it is <c>'S'</c>; this
/// implementation validates those same values instead of following them, so a file with other geometry is
/// rejected rather than misread. Rows that name no storage keep the three padding bytes and the following word
/// verbatim.</para>
/// <para>布局为 29 字节头部加固定 610 字节的行。头部依次是 <c>"CFG\0"</c> 魔数、16 位头部长度 <c>29</c>、八个
/// 16 位日期时间字段（年、月、星期、日、时、分、秒、毫秒；该本地时间戳与样本文件的修改时间一致）、一个字节
/// 的行数、再次出现的 32 位头部长度 <c>29</c>，以及每行开头也有的 16 位记录标记 <c>0x0262</c>。行为标记、
/// 80 字节 UTF-16 名称、512 字节 UTF-16 路径与 16 字节尾块：三个填充字节、存储标记 <c>'S'</c>、存储位掩码、32 位行地址，以及
/// 32 位标志（携带文件的行是 <c>1</c>，随附时不带文件的行是 <c>0</c>）。该布局已对照 RKDevTool v2.96 随附的
/// <c>config.cfg</c>（<c>rock-5b-emmc.cfg</c>、<c>rock-5b-spinor.cfg>，以及一份 10399 字节、17 行的
/// <c>config.cfg</c>）核验。厂商读取逻辑用偏移 22 的单字节作行数、偏移 23 的 32 位值作首行位置、偏移 27 的
/// 16 位值作行跨度，并且只在其前一字节为 <c>'S'</c> 时才采用存储字；本实现对这几个值做校验而不直接跟随，
/// 因此几何不同的文件会被拒绝，而不会被误读。未指定存储的行原样保留三个填充字节与其后的存储字。</para>
/// </remarks>
public static class RockchipConfigFile
{
    /// <summary>Size of the fixed file header.<para>固定文件头的大小。</para></summary>
    public const int HeaderLength = 29;

    /// <summary>Size of one download-list row.<para>一行下载记录的大小。</para></summary>
    public const int RecordLength = 610;

    /// <summary>Size of the UTF-16 name field inside a row.<para>行内 UTF-16 名称字段的大小。</para></summary>
    public const int NameFieldLength = 80;

    /// <summary>Size of the UTF-16 path field inside a row.<para>行内 UTF-16 路径字段的大小。</para></summary>
    public const int PathFieldLength = 512;

    /// <summary>Size of the trailing block that follows the path field.<para>路径字段之后的尾块大小。</para></summary>
    public const int TrailingLength = 16;

    /// <summary>Offset of the storage marker inside the trailing block. The vendor reader ignores the storage
    /// word unless this byte carries <see cref="StorageMarker"/>.
    /// <para>尾块内存储标记的偏移。该字节不是 <see cref="StorageMarker"/> 时，厂商读取逻辑会忽略存储字。</para></summary>
    public const int StorageMarkerOffset = 3;

    /// <summary>Byte the vendor writer stores at <see cref="StorageMarkerOffset"/> when a row names a storage.
    /// <para>厂商写入逻辑在行指定了存储时写入 <see cref="StorageMarkerOffset"/> 的字节。</para></summary>
    public const byte StorageMarker = 0x53;

    /// <summary>Offset of the storage bitmask inside the trailing block.<para>尾块内存储位掩码的偏移。</para></summary>
    public const int StorageOffset = 4;

    /// <summary>Offset of the row address inside the trailing block.<para>尾块内行地址的偏移。</para></summary>
    public const int AddressOffset = 8;

    /// <summary>Offset of the vendor tool's row checkbox inside the trailing block.
    /// <para>尾块内厂商工具行勾选位的偏移。</para></summary>
    public const int SelectedOffset = 12;

    /// <summary>Protocol storage identifier of eMMC.<para>eMMC 的协议存储标识。</para></summary>
    public const int StorageIdEmmc = 1;

    /// <summary>Protocol storage identifier of SD.<para>SD 的协议存储标识。</para></summary>
    public const int StorageIdSd = 2;

    /// <summary>Protocol storage identifier of SPI NOR.<para>SPI NOR 的协议存储标识。</para></summary>
    public const int StorageIdSpiNor = 9;

    /// <summary>
    /// Word every row starts with: the row's own length in bytes (<c>0x0262</c> is 610), which the header also
    /// repeats at its end.
    /// <para>每行起始处的字：该行自身的字节长度（<c>0x0262</c> 即 610），头部末尾也重复该值。</para>
    /// </summary>
    public const ushort RecordMarker = 0x0262;

    /// <summary>Row name of the loader row in a vendor download list.<para>厂商下载列表中 loader 行的行名称。</para></summary>
    public const string LoaderRowName = "Loader";

    /// <summary>Row name of the partition-table row in a vendor download list.
    /// <para>厂商下载列表中分区表行的行名称。</para></summary>
    public const string ParameterRowName = "Parameter";

    /// <summary>Offset of the header's one-byte row count.<para>头部一字节行数的偏移。</para></summary>
    public const int HeaderRowCountOffset = 22;

    /// <summary>Offset of the header's 32-bit repeated length field.<para>头部再次出现的 32 位长度字段的偏移。</para></summary>
    public const int HeaderLengthWordOffset = 23;

    /// <summary>Offset of the header's 16-bit record marker.<para>头部 16 位记录标记的偏移。</para></summary>
    public const int HeaderRecordMarkerOffset = 27;

    private const int NameFieldOffset = 2;
    private const int PathFieldOffset = NameFieldOffset + NameFieldLength;
    private const int TrailingOffset = PathFieldOffset + PathFieldLength;
    private static readonly byte[] Magic = "CFG"u8.ToArray();

    /// <summary>
    /// Parses a configuration file and returns its rows plus the verbatim header.
    /// <para>解析配置文件，返回其行与原样保留的头部。</para>
    /// </summary>
    /// <param name="bytes">Complete file contents.<para>完整文件内容。</para></param>
    /// <returns>The parsed rows and header; the row count is taken from the file length, and the header's own
    /// count byte is preserved in the returned header rather than trusted, so a hand-edited count cannot truncate
    /// the parsed rows.<para>解析出的行与头部；行数取自文件长度，头部自带的行数字节只保留在返回的头部里而不作为
    /// 依据，因此手工改动过的行数不会截断解析结果。</para></returns>
    /// <exception cref="InvalidDataException">The magic, the 16-bit header length, a row marker or the row length
    /// does not match the format.<para>魔数、16 位头部长度、某个行标记或行长与格式不符。</para></exception>
    /// <remarks>
    /// <para>The header's redundant fields (the one-byte row count, the repeated 32-bit length and the trailing
    /// record marker) are read only through the returned header, so files whose header omits them still load while
    /// <see cref="Serialize"/> writes them for everything this repository produces.</para>
    /// <para>头部的冗余字段（一字节行数、重复的 32 位长度、末尾的记录标记）只经返回的头部透传，因此头部缺少这些
    /// 字段的文件仍可载入，而 <see cref="Serialize"/> 会为本仓库产出的文件写入它们。</para>
    /// </remarks>
    public static (byte[] Header, IReadOnlyList<RockchipConfigEntry> Entries) Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"A Rockchip configuration needs at least {HeaderLength} bytes but carries {bytes.Length}.");
        }

        if (!bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The Rockchip configuration has no CFG magic. 该瑞芯微配置缺少 CFG 魔数。");
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (headerLength != HeaderLength)
        {
            throw new InvalidDataException(
                $"The Rockchip configuration declares a {headerLength}-byte header instead of {HeaderLength}.");
        }

        var payload = bytes.Length - HeaderLength;
        if (payload % RecordLength != 0)
        {
            throw new InvalidDataException(
                $"The Rockchip configuration payload of {payload} bytes is not a multiple of {RecordLength}.");
        }

        var header = bytes[..HeaderLength].ToArray();
        var count = payload / RecordLength;
        var entries = new List<RockchipConfigEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var record = bytes.Slice(HeaderLength + (index * RecordLength), RecordLength);
            var marker = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if (marker != RecordMarker)
            {
                throw new InvalidDataException(
                    $"Row {index} starts with marker 0x{marker:X4} instead of 0x{RecordMarker:X4}.");
            }

            var trailing = record.Slice(TrailingOffset, TrailingLength).ToArray();
            var storageWord = BinaryPrimitives.ReadUInt32LittleEndian(trailing.AsSpan(StorageOffset));
            var storageNamed = trailing[StorageMarkerOffset] == StorageMarker;
            entries.Add(new RockchipConfigEntry
            {
                Name = DecodeField(record.Slice(NameFieldOffset, NameFieldLength)),
                Path = DecodeField(record.Slice(PathFieldOffset, PathFieldLength)),
                Address = BinaryPrimitives.ReadUInt32LittleEndian(trailing.AsSpan(AddressOffset)),
                StorageMask = storageNamed ? NormalizeStorage(storageWord) : 0u,
                StorageWord = storageWord,
                Selected = BinaryPrimitives.ReadUInt32LittleEndian(trailing.AsSpan(SelectedOffset)) != 0,
                Trailing = trailing
            });
        }

        return (header, entries);
    }

    /// <summary>
    /// Parses a configuration file from disk.
    /// <para>从磁盘解析配置文件。</para>
    /// </summary>
    /// <param name="path">Existing configuration file.<para>既有的配置文件。</para></param>
    /// <returns>The parsed header and rows.<para>解析出的头部与行。</para></returns>
    /// <exception cref="InvalidDataException">The file does not match the format.<para>文件与格式不符。</para></exception>
    public static (byte[] Header, IReadOnlyList<RockchipConfigEntry> Entries) Load(string path)
        => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Serializes one configuration file image with the given header and rows.
    /// <para>用给定头部与行序列化一份配置文件镜像。</para>
    /// </summary>
    /// <param name="header">Header bytes, normally taken from <see cref="Parse"/> or <see cref="CreateHeader"/>.
    /// <para>头部字节，通常来自 <see cref="Parse"/> 或 <see cref="CreateHeader"/>。</para></param>
    /// <param name="entries">Rows to store in the given order.<para>按给定顺序写入的行。</para></param>
    /// <returns>The complete file image.<para>完整的文件镜像。</para></returns>
    /// <exception cref="ArgumentException">The header is not <see cref="HeaderLength"/> bytes long, a row holds
    /// more than <c>255</c> rows, or a name or path does not fit its field.
    /// <para>头部长度不是 <see cref="HeaderLength"/> 字节、行数超过 <c>255</c>，或名称/路径放不进对应字段。</para></exception>
    /// <remarks>
    /// <para>The row count, the repeated length and the header marker are rewritten from the arguments, because
    /// they describe the file being produced; the date/time fields are kept verbatim from
    /// <paramref name="header"/>. A row's storage word is written from
    /// <see cref="RockchipConfigEntry.StorageWord"/> while that word still agrees with
    /// <see cref="RockchipConfigEntry.StorageMask"/>, so a value this repository cannot interpret survives a
    /// load-and-save untouched.<para>行数、重复长度与头部标记按参数重写，因为它们描述的是正在生成的文件；日期
    /// 时间字段则按 <paramref name="header"/> 原样保留。行的存储字由
    /// <see cref="RockchipConfigEntry.StorageWord"/> 写入，只要该字仍与
    /// <see cref="RockchipConfigEntry.StorageMask"/> 一致，本仓库无法解释的值就能在“载入再保存”后保持不变。</para>
    /// </remarks>
    public static byte[] Serialize(ReadOnlySpan<byte> header, IReadOnlyList<RockchipConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (header.Length != HeaderLength)
        {
            throw new ArgumentException(
                $"The header must be {HeaderLength} bytes but is {header.Length}.", nameof(header));
        }

        if (entries.Count > byte.MaxValue)
        {
            throw new ArgumentException(
                $"A Rockchip download list stores at most {byte.MaxValue} rows but {entries.Count} were given. 瑞芯微下载列表最多存储 {byte.MaxValue} 行，当前给出 {entries.Count} 行。",
                nameof(entries));
        }

        var image = new byte[HeaderLength + (entries.Count * RecordLength)];
        header.CopyTo(image);
        image[HeaderRowCountOffset] = (byte)entries.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(HeaderLengthWordOffset), HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(HeaderRecordMarkerOffset), RecordMarker);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            ArgumentNullException.ThrowIfNull(entry);
            var record = image.AsSpan(HeaderLength + (index * RecordLength), RecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record, RecordMarker);
            EncodeField(record.Slice(NameFieldOffset, NameFieldLength), entry.Name);
            EncodeField(record.Slice(PathFieldOffset, PathFieldLength), entry.Path);
            var trailing = record.Slice(TrailingOffset, TrailingLength);
            entry.Trailing.AsSpan(0, TrailingLength).CopyTo(trailing);
            WriteStorage(trailing, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(trailing[AddressOffset..], entry.Address);
            BinaryPrimitives.WriteUInt32LittleEndian(trailing[SelectedOffset..], entry.Selected ? 1u : 0u);
        }

        return image;
    }

    /// <summary>
    /// Writes a configuration file image to disk through a same-directory temporary file.
    /// <para>经同目录临时文件把配置镜像写入磁盘。</para>
    /// </summary>
    /// <param name="path">Destination file; an existing file is replaced.<para>目标文件；既有文件会被替换。</para></param>
    /// <param name="header">Header bytes to write.<para>要写入的头部字节。</para></param>
    /// <param name="entries">Rows to write.<para>要写入的行。</para></param>
    public static void Save(string path, ReadOnlySpan<byte> header, IReadOnlyList<RockchipConfigEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var image = Serialize(header, entries);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The configuration path has no parent directory. 配置路径没有父目录。");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, image);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Checks a bounded header sample for the configuration signature: the <c>CFG</c> magic plus the
    /// 16-bit header length the format declares.
    /// <para>检查有界头部样本是否符合配置签名：<c>CFG</c> 魔数加格式声明的 16 位头部长度。</para>
    /// </summary>
    /// <param name="header">Bounded leading bytes of a candidate file.<para>候选文件的有界起始字节。</para></param>
    /// <returns><see langword="true"/> when the sample carries a complete configuration header.
    /// <para>样本携带完整配置头部时为 <see langword="true"/>。</para></returns>
    public static bool IsConfigHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderLength
        && header[..Magic.Length].SequenceEqual(Magic)
        && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == HeaderLength;

    /// <summary>
    /// Checks a file on disk for the configuration signature without reading the whole file.
    /// <para>在不读取整个文件的前提下检查磁盘文件的配置签名。</para>
    /// </summary>
    /// <param name="path">Candidate file path; a missing file is not a configuration.
    /// <para>候选文件路径；文件不存在时不算配置。</para></param>
    /// <returns><see langword="true"/> when the leading bytes carry a complete configuration header.
    /// <para>起始字节携带完整配置头部时为 <see langword="true"/>。</para></returns>
    public static bool LooksLike(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[HeaderLength];
            return stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) == HeaderLength
                && IsConfigHeader(header);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a header for a configuration that has no source file: magic, both length fields and the header
    /// marker, with the row count left at zero for <see cref="Serialize"/> to fill and the date/time fields left
    /// at zero so the result is deterministic.
    /// <para>为没有来源文件的配置创建头部：魔数、两个长度字段与头部标记；行数留为零由 <see cref="Serialize"/>
    /// 填充，日期时间字段保持为零以保证结果确定。</para>
    /// </summary>
    /// <returns>A <see cref="HeaderLength"/>-byte header.<para><see cref="HeaderLength"/> 字节的头部。</para></returns>
    public static byte[] CreateHeader()
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(HeaderLengthWordOffset), HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HeaderRecordMarkerOffset), RecordMarker);
        return header;
    }

    /// <summary>
    /// Describes a row's storage bitmask as the medium name the vendor tool shows, leaving the cell blank for
    /// every value it does not name.
    /// <para>把行的存储位掩码描述为厂商工具显示的介质名；厂商不命名的值一律留空单元格。</para>
    /// </summary>
    /// <param name="mask">Storage bitmask read from <see cref="RockchipConfigEntry.StorageMask"/>.
    /// <para>从 <see cref="RockchipConfigEntry.StorageMask"/> 读出的存储位掩码。</para></param>
    /// <returns>The medium name for one of the three named bits, otherwise an empty string, which is what the
    /// vendor table shows for a row that names no storage.<para>三个具名 bit 之一返回介质名，其余返回空字符串，
    /// 正是厂商表格对未指定存储的行所显示的内容。</para></returns>
    public static string DescribeStorage(uint mask) => mask switch
    {
        1u << StorageIdEmmc => "EMMC",
        1u << StorageIdSd => "SD",
        1u << StorageIdSpiNor => "SPINOR",
        _ => string.Empty
    };

    /// <summary>
    /// Converts a medium name back into the bitmask form, so a row written by this repository round trips through
    /// the vendor tool with the storage it was read with.
    /// <para>把介质名转换回位掩码形式，使本仓库写出的行在厂商工具中保留其原有的存储类型。</para>
    /// </summary>
    /// <param name="storage">Medium name as shown in the vendor table, for example <c>SPINOR</c>; case-insensitive
    /// and any other value yields <c>0</c>.<para>厂商表格中显示的介质名，例如 <c>SPINOR</c>；不区分大小写，
    /// 其他值返回 <c>0</c>。</para></param>
    /// <returns>The single-bit mask of that medium, or <c>0</c> when the name is not a known one.
    /// <para>该介质的单 bit 掩码；名称未知时为 <c>0</c>。</para></returns>
    public static uint StorageMaskFromName(string? storage) => storage?.Trim().ToUpperInvariant() switch
    {
        "EMMC" => 1u << StorageIdEmmc,
        "SD" => 1u << StorageIdSd,
        "SPINOR" => 1u << StorageIdSpiNor,
        _ => 0u
    };

    /// <summary>
    /// Recognizes the operation a row name asks for: <c>Loader</c> and <c>Parameter</c> after trimming and ignoring
    /// case are the loader and partition-table operations, every other name is a partition write.
    /// <para>识别行名称请求的操作：去除空白并忽略大小写后等于 <c>Loader</c> 或 <c>Parameter</c> 的名称分别是加载
    /// loader 与分区表操作，其余名称都是分区写入。</para>
    /// </summary>
    /// <param name="name">Row name taken from <see cref="RockchipConfigEntry.Name"/>; null or blank counts as a
    /// partition write.<para>取自 <see cref="RockchipConfigEntry.Name"/> 的行名称；空值按分区写入处理。</para></param>
    /// <returns>The recognized operation.<para>识别出的操作。</para></returns>
    public static RockchipConfigOperation ClassifyOperation(string? name)
    {
        var trimmed = name?.Trim();
        if (string.Equals(trimmed, LoaderRowName, StringComparison.OrdinalIgnoreCase))
        {
            return RockchipConfigOperation.Loader;
        }

        return string.Equals(trimmed, ParameterRowName, StringComparison.OrdinalIgnoreCase)
            ? RockchipConfigOperation.Parameter
            : RockchipConfigOperation.Flash;
    }

    private static string DecodeField(ReadOnlySpan<byte> field)
    {
        var text = Encoding.Unicode.GetString(field);
        var end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).TrimEnd();
    }

    /// <summary>
    /// Accepts a storage word only when it names exactly one storage bit, because the vendor tool writes the
    /// storage it was given here and a value with several bits names no medium.
    /// <para>仅当存储字恰好表示一个存储位时才接受它，因为厂商工具把收到的存储原样写在此处，含多个置位的值不表示
    /// 任何介质。</para>
    /// </summary>
    private static uint NormalizeStorage(uint raw) => raw != 0 && (raw & (raw - 1)) == 0 ? raw : 0u;

    /// <summary>
    /// Writes the storage marker and word the way the vendor writer does: the marker and the effective storage
    /// when the row names one, an explicit clear when the row was read with the marker and names none, and no
    /// change at all when the row was read without the marker, so bytes this repository does not interpret
    /// survive a load-and-save round trip untouched.
    /// <para>按厂商写入逻辑写存储标记与存储字：行指定了存储时写标记与有效存储；该行读入时带标记而如今不指定
    /// 存储时显式清除；读入时就没有标记时完全不动，使本仓库不解释的字节在“载入再保存”后保持不变。</para>
    /// </summary>
    /// <param name="trailing">Trailing block already holding the row's raw bytes.
    /// <para>已填入该行原始字节的尾块。</para></param>
    /// <param name="entry">Row being written.<para>正在写入的行。</para></param>
    private static void WriteStorage(Span<byte> trailing, RockchipConfigEntry entry)
    {
        if (entry.StorageMask != 0)
        {
            trailing[StorageMarkerOffset] = StorageMarker;
            BinaryPrimitives.WriteUInt32LittleEndian(trailing[StorageOffset..], entry.StorageMask);
        }
        else if (entry.Trailing[StorageMarkerOffset] == StorageMarker)
        {
            trailing[StorageMarkerOffset] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(trailing[StorageOffset..], 0u);
        }
    }

    /// <summary>
    /// Writes a NUL-terminated UTF-16 value into a fixed field, rejecting values that do not fit instead of
    /// truncating them into a different path or name.
    /// <para>把以 NUL 结尾的 UTF-16 值写入固定字段；放不下时直接拒绝，而不是截断成另一个路径或名称。</para>
    /// </summary>
    /// <param name="field">Fixed field to fill.<para>要填充的固定字段。</para></param>
    /// <param name="value">Text to store; empty clears the field.
    /// <para>要存储的文本；空值清空字段。</para></param>
    /// <exception cref="ArgumentException">The text needs more characters than the field holds once its terminator
    /// is accounted for.<para>文本字符数超过字段在计入结尾符后能容纳的数量。</para></exception>
    private static void EncodeField(Span<byte> field, string value)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var maximum = (field.Length / 2) - 1;
        if (value.Length > maximum)
        {
            throw new ArgumentException(
                $"The value needs {value.Length} characters but the field holds {maximum}. 该值需要 {value.Length} 个字符，字段只能容纳 {maximum} 个。",
                nameof(value));
        }

        Encoding.Unicode.GetBytes(value, field);
    }
}