namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;

public sealed class Nb0FileEntry
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public long Offset { get; set; }

    public long FileDataOffset { get; set; }

    public string? InferredType { get; set; }

    /// <summary>
    /// Creates an Nb0FileEntry instance from an Nb0EntryHeader.
    /// <para>从 Nb0EntryHeader 创建 Nb0FileEntry 实例。</para>
    /// </summary>
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

public sealed class Nb0Entry
{
    public Nb0EntryHeader Header { get; }

    public byte[]? Data { get; set; }

    public string? FilePath { get; set; }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified name and data.
    /// <para>使用指定的名称和数据初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    public Nb0Entry(string name, byte[] data)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = data.Length };
        Data = data;
    }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified name and file path.
    /// <para>使用指定的名称和文件路径初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    public Nb0Entry(string name, string filePath)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = new FileInfo(filePath).Length };
        FilePath = filePath;
    }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified entry header.
    /// <para>使用指定的条目头初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    public Nb0Entry(Nb0EntryHeader header)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
    }
}