namespace UotanStudio.Firmware.VendorFirmware.Nb0.Models;

/// <summary>
/// Flat view of one Nb0 directory entry: name, size and the two offsets needed to locate its data.
/// <para>Nb0 单个目录条目的扁平视图：名称、大小以及定位其数据所需的两个偏移。</para>
/// </summary>
public sealed class Nb0FileEntry
{
    /// <summary>Entry name copied from the header. <para>从条目头复制的名称。</para></summary>
    /// <value>The display name of the entry. <para>条目的显示名称。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Uncompressed size of the entry data in bytes. <para>条目数据的未压缩大小，以字节计。</para></summary>
    /// <value>The size value taken from the header. <para>取自条目头的大小值。</para></value>
    public long Size { get; set; }

    /// <summary>
    /// Data offset relative to the start of the data section.
    /// <para>相对于数据段起始处的数据偏移。</para>
    /// </summary>
    /// <value>The relative offset in bytes. <para>以字节计的相对偏移。</para></value>
    public long Offset { get; set; }

    /// <summary>
    /// Absolute stream offset of the data section base; entry data begins at
    /// <c>FileDataOffset + Offset</c>.
    /// <para>数据段基址的绝对流偏移；条目数据起始于 <c>FileDataOffset + Offset</c>。</para>
    /// </summary>
    /// <value>The data section base offset in bytes. <para>以字节计的数据段基址偏移。</para></value>
    public long FileDataOffset { get; set; }

    /// <summary>
    /// Format label inferred from magic bytes at the entry start, or <see langword="null"/>
    /// when no known signature matched.
    /// <para>由条目起始处魔数字节推断出的格式标签；没有已知签名匹配时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The inferred type label, or <see langword="null"/>. <para>推断出的类型标签，或为 <see langword="null"/>。</para></value>
    public string? InferredType { get; set; }

    /// <summary>
    /// Creates an Nb0FileEntry instance from an Nb0EntryHeader.
    /// <para>从 Nb0EntryHeader 创建 Nb0FileEntry 实例。</para>
    /// </summary>
    /// <param name="header">
    /// Parsed entry header supplying name, size and relative data offset.
    /// <para>提供名称、大小和相对数据偏移的已解析条目头。</para>
    /// </param>
    /// <param name="fileDataOffset">
    /// Absolute stream offset of the data section base added to the header offset.
    /// <para>叠加到条目头偏移之上的数据段基址绝对流偏移。</para>
    /// </param>
    /// <returns>
    /// The entry with header fields copied in; <see cref="InferredType"/> stays unset.
    /// <para>已复制条目头字段的条目；<see cref="InferredType"/> 保持未设置。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="header"/> is <see langword="null"/>.
    /// <para><paramref name="header"/> 为 <see langword="null"/>。</para></exception>
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
/// Packable Nb0 entry: a header plus either an in-memory data blob or a source file on disk.
/// <para>可打包的 Nb0 条目：一个条目头，外加内存中的数据块或磁盘上的源文件之一。</para>
/// </summary>
public sealed class Nb0Entry
{
    /// <summary>
    /// Entry header carrying the name and file size.
    /// <para>承载名称与文件大小的条目头。</para>
    /// </summary>
    /// <value>The header instance owned by this entry. <para>本条目持有的条目头实例。</para></value>
    public Nb0EntryHeader Header { get; }

    /// <summary>
    /// In-memory entry payload, or <see langword="null"/> for a file-backed entry.
    /// <para>内存中的条目数据；基于文件的条目为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The payload bytes, or <see langword="null"/>. <para>数据字节，或为 <see langword="null"/>。</para></value>
    public byte[]? Data { get; set; }

    /// <summary>
    /// Source file path for a file-backed entry, or <see langword="null"/> for an in-memory entry.
    /// <para>基于文件的条目的源文件路径；内存条目的为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The path string, or <see langword="null"/>. <para>路径字符串，或为 <see langword="null"/>。</para></value>
    public string? FilePath { get; set; }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified name and data.
    /// <para>使用指定的名称和数据初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    /// <param name="name">Entry name stored in the header. <para>写入条目头的名称。</para></param>
    /// <param name="data">Payload bytes; the header size is set to <c>data.Length</c>.
    /// <para>数据字节；条目头大小设为 <c>data.Length</c>。</para></param>
    /// <exception cref="NullReferenceException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    public Nb0Entry(string name, byte[] data)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = data.Length };
        Data = data;
    }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified name and file path.
    /// <para>使用指定的名称和文件路径初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    /// <param name="name">Entry name stored in the header. <para>写入条目头的名称。</para></param>
    /// <param name="filePath">
    /// Existing file whose length becomes the header size; the file is not opened here.
    /// <para>其长度写为条目头大小的已存在文件；此处不会打开文件。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    public Nb0Entry(string name, string filePath)
    {
        Header = new Nb0EntryHeader { Name = name, FileSize = new FileInfo(filePath).Length };
        FilePath = filePath;
    }

    /// <summary>
    /// Initializes a new instance of Nb0Entry with the specified entry header.
    /// <para>使用指定的条目头初始化 Nb0Entry 的新实例。</para>
    /// </summary>
    /// <param name="header">
    /// Header to adopt; payload and file path stay unset.
    /// <para>要采用的条目头；数据与文件路径保持未设置。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="header"/> is <see langword="null"/>.
    /// <para><paramref name="header"/> 为 <see langword="null"/>。</para></exception>
    public Nb0Entry(Nb0EntryHeader header)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
    }
}