namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

/// <summary>
/// Result of parsing an NB0 archive: entry table geometry, per-entry records, MD5 records and the non-fatal issues
/// found while reading untrusted input. Entry data itself stays in the source stream.
/// <para>解析 NB0 归档的结果：条目表几何信息、各条目记录、MD5 记录，以及读取不可信输入时发现的非致命问题。条目数据本身仍留在源流中。</para>
/// </summary>
public sealed class Nb0Metadata
{

    /// <summary>
    /// Entry count decoded from the file's leading little-endian 4-byte header. Parsing rejects values above
    /// <see cref="int.MaxValue"/>; values above 1024 are accepted but add a warning.
    /// <para>由文件开头的小端 4 字节头解码出的条目数量。解析时大于 <see cref="int.MaxValue"/> 的值会被拒绝；大于 1024 的值可以接受但会追加一条警告。</para>
    /// </summary>
    /// <value>Entries declared by the archive's count header. <para>归档数量头声明的条目数。</para></value>
    public int EntryCount { get; set; }

    /// <summary>
    /// Total archive size in bytes: the seekable stream length when available, otherwise an estimate extrapolated from
    /// the last entry, padded to a 4-byte boundary. A non-seekable source with no entries yields the data-section
    /// offset alone.
    /// <para>归档总大小（字节）：流可寻址时为其长度，否则按最后一个条目外推并对齐到 4 字节边界的估计值。不可寻址且无条目时仅为数据段起始偏移。</para>
    /// </summary>
    /// <value>Total archive length in bytes, measured or estimated. <para>归档总长度（字节），实测或估算。</para></value>
    public long TotalSize { get; set; }

    /// <summary>
    /// Offset from the start of the file to the first byte of entry data, i.e. the 4-byte count header plus one fixed
    /// entry header per declared entry. Entry offsets are relative to this position.
    /// <para>从文件开头到首个条目数据字节的偏移，即 4 字节数量头加上每个声明条目的一条定长条目头。条目偏移以此为基准。</para>
    /// </summary>
    /// <value>File position where entry data begins. <para>条目数据开始的文件位置。</para></value>
    public long DataSectionOffset { get; set; }

    /// <summary>
    /// Vendor or content family guessed from entry names, such as <c>Spreadtrum/UNISOC</c> or <c>Android Boot</c>. Null
    /// when no name matched a heuristic. Names mentioning Spreadtrum, UNISOC or an SC88xx chip overwrite an earlier
    /// guess, while other markers only fill an unset value, so the result is advisory, not a format check.
    /// <para>根据条目名称猜测的厂商或内容类别，例如 <c>Spreadtrum/UNISOC</c> 或 <c>Android Boot</c>；没有名称命中启发式规则时为 null。名称中出现 Spreadtrum、UNISOC 或 SC88xx 芯片时会覆盖已有猜测，其他标记只在尚未赋值时填入，因此该值仅供参考，不构成格式校验。</para>
    /// </summary>
    /// <value>The guessed family label, or null when nothing matched. <para>猜测出的类别标签，无命中时为 null。</para></value>
    public string? InferredFirmwareType { get; set; }

    /// <summary>
    /// Device model guessed from the archive contents. Parsing leaves this null; only a caller that supplies it sets a
    /// value, and an empty string is treated as absent when metadata is serialized.
    /// <para>根据归档内容猜测的设备型号。解析过程保持其为 null，仅由调用方自行赋值；序列化元数据时空字符串按缺失处理。</para>
    /// </summary>
    /// <value>The caller-supplied model label, or null when unset. <para>调用方填入的型号标签，未设置时为 null。</para></value>
    public string? InferredDeviceModel { get; set; }

    /// <summary>
    /// Firmware version guessed from the archive contents. Parsing leaves this null; only a caller that supplies it sets
    /// a value, and an empty string is treated as absent when metadata is serialized.
    /// <para>根据归档内容猜测的固件版本。解析过程保持其为 null，仅由调用方自行赋值；序列化元数据时空字符串按缺失处理。</para>
    /// </summary>
    /// <value>The caller-supplied version string, or null when unset. <para>调用方填入的版本字符串，未设置时为 null。</para></value>
    public string? InferredVersion { get; set; }

    /// <summary>
    /// Entry records in the order they appear in the header table, including the trailing <c>.md5</c> entry when the
    /// archive carries one. Empty for an archive that declares zero entries.
    /// <para>按条目头在表中出现顺序排列的条目记录，包含归档携带时末尾的 <c>.md5</c> 条目。声明条目数为零时为空列表。</para>
    /// </summary>
    /// <value>The parsed entry records; an empty list when the archive declares no entries. <para>已解析的条目记录；归档未声明条目时为空列表。</para></value>
    public List<Nb0FileEntry> Entries { get; set; } = [];

    /// <summary>
    /// MD5 records decoded from the trailing <c>.md5</c> entry, sized by whole <see cref="Nb0Md5Record.StructSize"/>
    /// chunks of that entry. Empty when no <c>.md5</c> entry exists or when its contents failed to parse, in which case
    /// a warning is recorded instead.
    /// <para>从末尾 <c>.md5</c> 条目解码出的 MD5 记录，条数按该条目中完整的 <see cref="Nb0Md5Record.StructSize"/> 字节块计。不存在 <c>.md5</c> 条目或其内容解析失败时为空列表，此时会记录一条警告。</para>
    /// </summary>
    /// <value>The decoded MD5 records in file order. <para>按文件顺序解码出的 MD5 记录。</para></value>
    public List<Nb0Md5Record> Md5Records { get; set; } = [];

    /// <summary>
    /// Non-fatal integrity findings from parsing: an unusually high entry count, empty or duplicate entry names, zero
    /// size, negative or out-of-order offsets, overlapping or out-of-range entry data, and an MD5 record count that
    /// does not match the number of entries it should cover. Warnings never abort a parse.
    /// <para>解析得到的非致命完整性问题：条目数量异常偏高、条目名为空或重复、大小为零、偏移为负或顺序倒置、条目数据重叠或超出范围，以及 MD5 记录数与其应覆盖的条目数不一致。警告不会中断解析。</para>
    /// </summary>
    /// <value>Human-readable warning messages; empty when the archive raised no concerns. <para>可读的警告文本；归档未触发任何问题时为空。</para></value>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// True when <see cref="Md5Records"/> holds at least one record, which is the precondition for checksum
    /// verification; a parse failure on the <c>.md5</c> entry leaves this false.
    /// <para>当 <see cref="Md5Records"/> 至少含一条记录时为 true，这是校验和验证的前提；<c>.md5</c> 条目解析失败时保持为 false。</para>
    /// </summary>
    /// <value>True when at least one MD5 record is available for verification. <para>至少有一条可用于验证的 MD5 记录时为 true。</para></value>
    public bool HasMd5Records => Md5Records.Count > 0;

    /// <summary>
    /// MD5 records keyed by <see cref="Nb0Md5Record.Offset"/> for offset lookup, where a record added with a duplicate
    /// offset loses to the one already stored. The getter builds the index from <see cref="Md5Records"/> only while no
    /// index object exists and <see cref="Md5Records"/> is non-empty, so reading it never stores an empty index; only an
    /// explicit <see cref="BuildMd5RecordIndex"/> call does. A stored index does not track later changes to
    /// <see cref="Md5Records"/> until <see cref="BuildMd5RecordIndex"/> runs again.
    /// <para>按 <see cref="Nb0Md5Record.Offset"/> 索引的 MD5 记录，用于偏移查找；偏移重复时，后加入的记录让位于已在索引中的记录。getter 仅在尚未存在索引对象且 <see cref="Md5Records"/> 非空时才从中构建索引，因此读取本身不会存入空索引，只有显式调用 <see cref="BuildMd5RecordIndex"/> 才会；索引一旦建立便不会跟随 <see cref="Md5Records"/> 的后续变化，需再次调用 <see cref="BuildMd5RecordIndex"/>。</para>
    /// </summary>
    /// <value>The offset-to-record index, or null until a non-empty <see cref="Md5Records"/> list or an explicit build supplies one. <para>偏移到记录的索引；在空的 <see cref="Md5Records"/> 列表或显式构建提供索引之前为 null。</para></value>
    public Dictionary<uint, Nb0Md5Record>? Md5RecordByOffset
    {
        get
        {
            if (field is null && Md5Records.Count > 0)
            {
                BuildMd5RecordIndex();
            }
            return field;
        }

        private set;
    }

    /// <summary>
    /// Builds the MD5 record dictionary indexed by offset.
    /// <para>构建按偏移量索引的 MD5 校验记录字典。</para>
    /// The rebuild replaces the whole dictionary from the current <see cref="Md5Records"/> in list order, so a record
    /// whose offset was already claimed by an earlier record is left out of the index; calling this while
    /// <see cref="Md5Records"/> is empty stores an empty dictionary.
    /// <para>重建会按列表顺序用当前 <see cref="Md5Records"/> 整体替换字典，因此偏移已被前面记录占用的条目不会进入索引；在 <see cref="Md5Records"/> 为空时调用会存入一个空字典。</para>
    /// </summary>
    public void BuildMd5RecordIndex()
    {
        var dict = new Dictionary<uint, Nb0Md5Record>(Md5Records.Count);
        foreach (var record in Md5Records)
        {
            dict.TryAdd(record.Offset, record);
        }
        Md5RecordByOffset = dict;
    }
}