using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Parses rawprogram XML files containing program entries for EDL flashing. A program entry
/// carries its image name in the <c>filename</c> attribute; the legacy <c>file</c> attribute
/// is accepted as a fallback so manifests written by older tooling still resolve.
/// <para>解析包含 EDL 刷写 program 条目的 rawprogram XML 文件。program 条目以 <c>filename</c> 属性
/// 携带镜像名；为兼容旧版工具写出的清单，同时回退读取 <c>file</c> 属性。</para>
/// </summary>
public static class RawProgramParser
{
    /// <summary>
    /// Parses a rawprogram XML file from the specified file path.
    /// <para>从指定文件路径解析 rawprogram XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the rawprogram XML file.<para>rawprogram XML 文件路径。</para></param>
    /// <returns>A list of <see cref="RawProgramEntry"/> objects parsed from the file. Entries keep the literal <c>filename</c> value; source paths are not resolved.<para>从文件中解析的 <see cref="RawProgramEntry"/> 对象列表。条目保留字面 <c>filename</c> 值；未解析源文件路径。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.<para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    public static List<RawProgramEntry> Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "File path must not be empty. 文件路径不能为空。");
        }

        try
        {
            var doc = FlashBundleXml.Load(filePath);
            return ParseDocument(doc);
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, $"Failed to parse rawprogram XML file: {filePath} 解析 rawprogram XML 文件失败。", ex);
        }
    }

    /// <summary>
    /// Parses a rawprogram XML from the specified stream.
    /// <para>从指定流解析 rawprogram XML。</para>
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the rawprogram XML document. The caller retains ownership.<para>可读、定位到 rawprogram XML 文档起始处的流。所有权由调用方持有。</para></param>
    /// <returns>A list of <see cref="RawProgramEntry"/> objects parsed from the stream. Entries keep the literal <c>filename</c> value; source paths are not resolved.<para>从流中解析的 <see cref="RawProgramEntry"/> 对象列表。条目保留字面 <c>filename</c> 值；未解析源文件路径。</para></returns>
    /// <exception cref="EdlException">The stream is <see langword="null"/> or the document cannot be parsed.<para>流为 <see langword="null"/>，或文档无法解析。</para></exception>
    public static List<RawProgramEntry> Parse(Stream stream)
    {
        if (stream == null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "Stream must not be null. 流不能为 null。");
        }

        try
        {
            var doc = FlashBundleXml.Load(stream);
            return ParseDocument(doc);
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, "Failed to parse rawprogram XML from stream. 从流解析 rawprogram XML 失败。", ex);
        }
    }

    /// <summary>
    /// Asynchronously parses a rawprogram XML file from the specified file path.
    /// <para>从指定文件路径异步解析 rawprogram XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the rawprogram XML file.<para>rawprogram XML 文件路径。</para></param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation; only observed while the work item is queued.<para>用于取消异步操作的令牌；仅在工作项排队阶段被观察。</para></param>
    /// <returns>A task whose result contains the parsed <see cref="RawProgramEntry"/> objects.<para>任务结果包含解析出的 <see cref="RawProgramEntry"/> 对象。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.<para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled while the work item was still queued.<para>工作项仍在排队时令牌被取消。</para></exception>
    public static async Task<List<RawProgramEntry>> ParseAsync(string filePath, CancellationToken cancellationToken = default) => await Task.Run(() => Parse(filePath), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Parses multiple rawprogram XML files and resolves image file paths against the firmware
    /// directory.
    /// <para>解析多个 rawprogram XML 文件，并根据固件目录解析镜像文件路径。</para>
    /// </summary>
    /// <param name="filePaths">The list of rawprogram XML file paths to parse. Each entry is parsed with <see cref="Parse(string)"/> and its <c>filename</c> is joined against <paramref name="firmwareDirectory"/>.<para>待解析的 rawprogram XML 文件路径列表。每条通过 <see cref="Parse(string)"/> 解析，其 <c>filename</c> 会依据 <paramref name="firmwareDirectory"/> 组合。</para></param>
    /// <param name="firmwareDirectory">The base directory for resolving image file paths.<para>用于解析镜像文件路径的基础目录。</para></param>
    /// <returns>A combined list of <see cref="RawProgramEntry"/> objects in the same order as <paramref name="filePaths"/> and, within each file, the same order as the document. Entries with a non-empty <c>filename</c> carry a resolved <see cref="RawProgramEntry.SourceFile"/>.<para>按 <paramref name="filePaths"/> 顺序以及每个文件内文档顺序合并的 <see cref="RawProgramEntry"/> 列表。<c>filename</c> 非空的条目会带有已解析的 <see cref="RawProgramEntry.SourceFile"/>。</para></returns>
    /// <exception cref="EdlException"><paramref name="filePaths"/> is <see langword="null"/>, or any file cannot be loaded or parsed.<para><paramref name="filePaths"/> 为 <see langword="null"/>，或任一文件无法加载或解析。</para></exception>
    public static List<RawProgramEntry> ParseAll(IEnumerable<string> filePaths, string firmwareDirectory)
    {
        if (filePaths == null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "File paths collection must not be null. 文件路径集合不能为 null。");
        }

        var allEntries = new List<RawProgramEntry>();
        foreach (var filePath in filePaths)
        {
            var entries = Parse(filePath);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.Filename))
                {
                    entry.SourceFile = FlashBundlePath.ResolveImage(firmwareDirectory, entry.Filename);
                }

                allEntries.Add(entry);
            }
        }

        return allEntries;
    }

    /// <summary>
    /// Asynchronously parses multiple rawprogram XML files and resolves image file paths
    /// against the firmware directory.
    /// <para>异步解析多个 rawprogram XML 文件，并根据固件目录解析镜像文件路径。</para>
    /// </summary>
    /// <param name="filePaths">The list of rawprogram XML file paths to parse.<para>待解析的 rawprogram XML 文件路径列表。</para></param>
    /// <param name="firmwareDirectory">The base directory for resolving image file paths.<para>用于解析镜像文件路径的基础目录。</para></param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation; only observed while the work item is queued.<para>用于取消异步操作的令牌；仅在工作项排队阶段被观察。</para></param>
    /// <returns>A task whose result contains a combined list of <see cref="RawProgramEntry"/> objects.<para>任务结果包含合并的 <see cref="RawProgramEntry"/> 对象列表。</para></returns>
    /// <exception cref="EdlException"><paramref name="filePaths"/> is <see langword="null"/>, or any file cannot be loaded or parsed.<para><paramref name="filePaths"/> 为 <see langword="null"/>，或任一文件无法加载或解析。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled while the work item was still queued.<para>工作项仍在排队时令牌被取消。</para></exception>
    public static async Task<List<RawProgramEntry>> ParseAllAsync(
        IEnumerable<string> filePaths, string firmwareDirectory, CancellationToken cancellationToken = default) => await Task.Run(() => ParseAll(filePaths, firmwareDirectory), cancellationToken).ConfigureAwait(false);

    private static List<RawProgramEntry> ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new EdlException(EdlErrorCode.XmlParseError, "Rawprogram XML document has no root element. rawprogram XML 文档没有根元素。");
        var entries = new List<RawProgramEntry>();
        foreach (var element in root.Elements("program"))
        {
            var slotAttr = element.Attribute("slot");
            var entry = new RawProgramEntry
            {
                SectorSizeInBytes = (long?)element.Attribute("SECTOR_SIZE_IN_BYTES") ?? 0,
                NumPartitionSectors = (long?)element.Attribute("num_partition_sectors") ?? 0,
                Filename = (string?)element.Attribute("filename") ?? (string?)element.Attribute("file") ?? string.Empty,
                PhysicalPartitionNumber = (int?)element.Attribute("physical_partition_number") ?? 0,
                StartSector = (string?)element.Attribute("start_sector") ?? "0",
                FileSectorOffset = (long?)element.Attribute("file_sector_offset") ?? 0,
                Label = (string?)element.Attribute("label") ?? string.Empty,
                IsSparse = ParseSparse((string?)element.Attribute("sparse")),
                UnsparseFileSize = (long?)element.Attribute("UNSPARSE_FILE_SIZE") ?? 0,
                PagesPerBlock = (int?)element.Attribute("PAGES_PER_BLOCK") ?? 0,
                LastSector = (long?)element.Attribute("last_sector") ?? 0,
                Slot = slotAttr != null ? (int?)slotAttr : null
            };

            entries.Add(entry);
        }

        return entries;
    }

    private static bool ParseSparse(string? value)
    {
        return value != null && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase));
    }
}