namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>表示 NB0 固件文件的元数据信息,包含条目列表、推断的固件信息及 MD5 校验记录。</para>
/// Represents metadata information of an NB0 firmware file, containing entry list, inferred firmware info, and MD5 verification records.
/// </summary>
public sealed class Nb0Metadata
{

    /// <summary>
    /// <para>获取或设置条目数量。</para>
    /// Gets or sets the number of entries.
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>
    /// <para>获取或设置文件总大小(字节)。</para>
    /// Gets or sets the total size of the file in bytes.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// <para>获取或设置数据段在文件中的起始偏移量。</para>
    /// Gets or sets the starting offset of the data section within the file.
    /// </summary>
    public long DataSectionOffset { get; set; }

    /// <summary>
    /// <para>获取或设置推断的固件类型。</para>
    /// Gets or sets the inferred firmware type.
    /// </summary>
    public string? InferredFirmwareType { get; set; }

    /// <summary>
    /// <para>获取或设置推断的设备型号。</para>
    /// Gets or sets the inferred device model.
    /// </summary>
    public string? InferredDeviceModel { get; set; }

    /// <summary>
    /// <para>获取或设置推断的版本号。</para>
    /// Gets or sets the inferred version.
    /// </summary>
    public string? InferredVersion { get; set; }

    /// <summary>
    /// <para>获取或设置 NB0 文件中的条目列表。</para>
    /// Gets or sets the list of entries in the NB0 file.
    /// </summary>
    public List<Nb0FileEntry> Entries { get; set; } = [];

    /// <summary>
    /// <para>获取或设置从 .md5 条目中解析出的 MD5 校验记录列表。</para>
    /// Gets or sets the list of MD5 verification records parsed from the .md5 entry.
    /// </summary>
    public List<Nb0Md5Record> Md5Records { get; set; } = [];

    /// <summary>
    /// <para>获取或设置解析/提取过程中的警告列表。</para>
    /// Gets or sets the list of warnings collected during parsing/extraction.
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// <para>获取是否包含 MD5 校验记录。</para>
    /// Gets whether MD5 verification records are present.
    /// </summary>
    public bool HasMd5Records => Md5Records.Count > 0;

    /// <summary>
    /// <para>获取按偏移量索引的 MD5 校验记录字典(仅构建一次)。</para>
    /// Gets the MD5 record dictionary indexed by offset (built lazily once).
    /// </summary>
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
    /// <para>构建按偏移量索引的 MD5 校验记录字典。</para>
    /// Builds the MD5 record dictionary indexed by offset.
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