namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Metadata-only view of an NTPI package produced without extracting file contents: the version triple,
/// the walked region chain, and the parsed file index.
/// <para>在不提取文件内容的情况下生成的 NTPI 包元数据视图：版本三元组、遍历得到的区域链，以及解析出的文件索引。</para>
/// </summary>
public class NtpiFileInfo
{
    /// <summary>Major version component read from the package header.
    /// <para>从包头部读取的版本主号。</para></summary>
    /// <value>The little-endian major version component. <para>小端的版本主号。</para></value>
    public ulong MajorVersion { get; init; }

    /// <summary>Minor version component read from the package header.
    /// <para>从包头部读取的版本次号。</para></summary>
    /// <value>The little-endian minor version component. <para>小端的版本次号。</para></value>
    public ulong MinorVersion { get; init; }

    /// <summary>Patch version component read from the package header.
    /// <para>从包头部读取的版本修订号。</para></summary>
    /// <value>The little-endian patch version component. <para>小端的版本修订号。</para></value>
    public ulong PatchVersion { get; init; }

    /// <summary>Regions discovered while walking the chain, each as its type and declared byte size; an entry of
    /// type <see cref="RegionType.Region6"/> records the encrypted file-data region. Empty when no region is present.
    /// <para>遍历区域链时发现的区域，每项为其类型与声明的字节大小；类型为 <see cref="RegionType.Region6"/> 的项记录加密文件数据区域。无区域时为空。</para></summary>
    /// <value>The mutable region list, empty until regions are walked. <para>可变的区域列表，在遍历区域前为空。</para></value>
    public List<(RegionType Type, long Size)> Regions { get; init; } = [];

    /// <summary>File index entries parsed from the <see cref="RegionType.FileIndex"/> region. Empty when no file
    /// index region is present.
    /// <para>从 <see cref="RegionType.FileIndex"/> 区域解析出的文件索引条目。无文件索引区域时为空。</para></summary>
    /// <value>The parsed file entries. <para>已解析的文件条目。</para></value>
    public List<FileEntry> FileEntries { get; set; } = [];
}

/// <summary>
/// Full in-memory parse of an NTPI package: header, region payloads keyed by type, the file index, and every
/// file's decrypted and decompressed contents.
/// <para>对 NTPI 包的完整内存解析：头部、按类型索引的区域负载、文件索引，以及每个文件解密并解压后的内容。</para>
/// </summary>
public class NtpiParsedFile
{
    /// <summary>The 48-byte package header, including version and first-region descriptor.
    /// <para>48 字节的包头部，包含版本与首个区域描述符。</para></summary>
    /// <value>The parsed header. <para>已解析的头部。</para></value>
    public NtpiHeader Header { get; init; }

    /// <summary>Decrypted region payloads keyed by region type. A type appears only when that region was walked;
    /// <see cref="RegionType.Region6"/> is populated only when the package carries that region.
    /// <para>按区域类型索引的已解密区域负载。仅当遍历到某区域时才出现该类型；<see cref="RegionType.Region6"/> 仅在包含有该区域时填充。</para></summary>
    /// <value>The region payload dictionary. <para>区域负载字典。</para></value>
    public Dictionary<RegionType, byte[]> Regions { get; init; } = [];

    /// <summary>File index entries parsed from the <see cref="RegionType.FileIndex"/> region. Empty when absent.
    /// <para>从 <see cref="RegionType.FileIndex"/> 区域解析出的文件索引条目；缺失时为空。</para></summary>
    /// <value>The parsed file entries. <para>已解析的文件条目。</para></value>
    public List<FileEntry> FileEntries { get; set; } = [];

    /// <summary>Decrypted and decompressed file bytes keyed by the entry name. Populated only when file entries
    /// exist alongside a Region6 and a non-empty KeyMap; otherwise the dictionary stays empty.
    /// <para>按条目名称索引的已解密并解压的文件字节。仅在文件条目与 Region6 及非空 KeyMap 同时存在时填充；否则字典保持为空。</para></summary>
    /// <value>The extracted file contents dictionary. <para>已提取文件内容字典。</para></value>
    public Dictionary<string, byte[]> ExtractedFiles { get; init; } = [];
}

/// <summary>
/// Progress snapshot reported while unpacking an NTPI package to disk.
/// <para>将 NTPI 包解包到磁盘期间报告的进度快照。</para>
/// </summary>
public class NtpiExtractionProgress
{
    /// <summary>Total number of file entries to process in this run.
    /// <para>本次运行要处理的文件条目总数。</para></summary>
    /// <value>The selected entry count. <para>已选中的条目数。</para></value>
    public int TotalFiles { get; init; }

    /// <summary>Number of entries handled so far, including those skipped by a file filter.
    /// <para>至今已处理的条目数，包含被文件过滤条件跳过的条目。</para></summary>
    /// <value>The running completed count. <para>累计已完成数。</para></value>
    public int CompletedFiles { get; init; }

    /// <summary>Name of the entry just reported.
    /// <para>刚上报的条目名称。</para></summary>
    /// <value>The current entry name. <para>当前条目名称。</para></value>
    public string CurrentFileName { get; init; } = string.Empty;
}