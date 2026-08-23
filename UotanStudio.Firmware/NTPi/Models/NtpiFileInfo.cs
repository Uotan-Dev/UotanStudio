namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>包含 NTPI 固件包文件基本信息的类,包括版本号、区域列表和文件条目。</para>
/// Contains basic information about an NTPI firmware package file, including version numbers, region list, and file entries.
/// </summary>
public class NtpiFileInfo
{
    /// <summary>
    /// <para>主版本号。</para>
    /// The major version number.
    /// </summary>
    public ulong MajorVersion { get; init; }

    /// <summary>
    /// <para>次版本号。</para>
    /// The minor version number.
    /// </summary>
    public ulong MinorVersion { get; init; }

    /// <summary>
    /// <para>修订版本号。</para>
    /// The patch version number.
    /// </summary>
    public ulong PatchVersion { get; init; }

    /// <summary>
    /// <para>区域列表,包含区域类型和大小。</para>
    /// The list of regions, containing region type and size.
    /// </summary>
    public List<(RegionType Type, long Size)> Regions { get; init; } = [];

    /// <summary>
    /// <para>文件索引条目列表。</para>
    /// The list of file index entries.
    /// </summary>
    public List<FileEntry> FileEntries { get; set; } = [];
}

/// <summary>
/// <para>包含 NTPI 固件包完整解析结果的类,包括头部、区域数据、文件条目和提取的文件。</para>
/// Contains the complete parsing result of an NTPI firmware package, including header, region data, file entries, and extracted files.
/// </summary>
public class NtpiParsedFile
{
    /// <summary>
    /// <para>NTPI 文件头部信息。</para>
    /// The NTPI file header information.
    /// </summary>
    public NtpiHeader Header { get; init; }

    /// <summary>
    /// <para>区域数据字典,键为区域类型,值为解密后的有效载荷数据。</para>
    /// The region data dictionary, keyed by region type, with decrypted payload data as values.
    /// </summary>
    public Dictionary<RegionType, byte[]> Regions { get; init; } = [];

    /// <summary>
    /// <para>文件索引条目列表。</para>
    /// The list of file index entries.
    /// </summary>
    public List<FileEntry> FileEntries { get; set; } = [];

    /// <summary>
    /// <para>提取的文件字典,键为文件名,值为解压后的文件数据。</para>
    /// The extracted files dictionary, keyed by file name, with decompressed file data as values.
    /// </summary>
    public Dictionary<string, byte[]> ExtractedFiles { get; init; } = [];
}

/// <summary>
/// <para>表示 NTPI 固件包解包进度的类。</para>
/// Represents the extraction progress of an NTPI firmware package.
/// </summary>
public class NtpiExtractionProgress
{
    /// <summary>
    /// <para>待提取的文件总数。</para>
    /// The total number of files to extract.
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// <para>已完成提取的文件数。</para>
    /// The number of files that have been extracted.
    /// </summary>
    public int CompletedFiles { get; init; }

    /// <summary>
    /// <para>当前正在提取的文件名称。</para>
    /// The name of the file currently being extracted.
    /// </summary>
    public string CurrentFileName { get; init; } = string.Empty;
}