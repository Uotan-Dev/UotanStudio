namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>表示 NB0 固件文件中的一个条目信息。</para>
/// Represents information about an entry in an NB0 firmware file.
/// </summary>
public sealed class Nb0FileEntry
{
    /// <summary>
    /// <para>获取或设置条目名称。</para>
    /// Gets or sets the entry name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置条目数据的大小(字节)。</para>
    /// Gets or sets the size of the entry data in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// <para>获取或设置条目数据在数据段中的偏移量。</para>
    /// Gets or sets the offset of the entry data within the data section.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// <para>获取或设置数据段在文件中的起始偏移量。</para>
    /// Gets or sets the starting offset of the data section within the file.
    /// </summary>
    public long FileDataOffset { get; set; }

    /// <summary>
    /// <para>获取或设置推断的条目类型。</para>
    /// Gets or sets the inferred entry type.
    /// </summary>
    public string? InferredType { get; set; }

    /// <summary>
    /// <para>从 Nb0EntryHeader 创建 Nb0FileEntry 实例。</para>
    /// Creates an Nb0FileEntry instance from an Nb0EntryHeader.
    /// </summary>
    /// <param name="header">条目头信息。</param>
    /// <param name="fileDataOffset">数据段在文件中的起始偏移量。</param>
    public static Nb0FileEntry FromHeader(Nb0EntryHeader header, long fileDataOffset)
    {
        ArgumentNullException.ThrowIfNull(header);

        return new Nb0FileEntry
        {
            Name = header.Name,
            Size = header.FileSize,
            Offset = header.DataOffset,
            FileDataOffset = fileDataOffset
        };
    }
}

/// <summary>
/// <para>表示 NB0 固件文件中的一个条目,包含头信息和数据。</para>
/// Represents an entry in an NB0 firmware file, containing header information and data.
/// </summary>
public sealed class Nb0Entry
{
    /// <summary>
    /// <para>获取条目头信息。</para>
    /// Gets the entry header information.
    /// </summary>
    public Nb0EntryHeader Header { get; }

    /// <summary>
    /// <para>获取或设置条目的原始数据。</para>
    /// Gets or sets the raw data of the entry.
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// <para>获取或设置条目数据的文件路径。设置后,打包时将直接从文件流式读取数据。</para>
    /// Gets or sets the file path for the entry data. When set, data is streamed from the file at pack time.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// <para>使用指定的名称和数据初始化 Nb0Entry 的新实例。</para>
    /// Initializes a new instance of Nb0Entry with the specified name and data.
    /// </summary>
    public Nb0Entry(string name, byte[] data)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = data.Length };
        Data = data;
    }

    /// <summary>
    /// <para>使用指定的名称和文件路径初始化 Nb0Entry 的新实例。</para>
    /// Initializes a new instance of Nb0Entry with the specified name and file path.
    /// </summary>
    public Nb0Entry(string name, string filePath)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = new FileInfo(filePath).Length };
        FilePath = filePath;
    }

    /// <summary>
    /// <para>使用指定的条目头初始化 Nb0Entry 的新实例。</para>
    /// Initializes a new instance of Nb0Entry with the specified entry header.
    /// </summary>
    public Nb0Entry(Nb0EntryHeader header)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
    }
}