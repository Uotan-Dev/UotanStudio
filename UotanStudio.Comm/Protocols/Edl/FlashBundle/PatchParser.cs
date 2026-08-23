using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Parses patch XML files containing patch entries for EDL flashing.
/// <para>解析包含 EDL 刷写 patch 条目的 patch XML 文件。</para>
/// </summary>
public static class PatchParser
{
    /// <summary>
    /// Parses a patch XML file from the specified file path.
    /// <para>从指定文件路径解析 patch XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the patch XML file. patch XML 文件路径。</param>
    /// <returns>A list of <see cref="PatchEntry"/> objects parsed from the file. 从文件中解析的 <see cref="PatchEntry"/> 对象列表。</returns>
    /// <exception cref="EdlException">Thrown when the file cannot be loaded or parsed. 文件无法加载或解析时抛出。</exception>
    public static List<PatchEntry> Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "File path must not be empty. 文件路径不能为空。");
        }

        try
        {
            var doc = XDocument.Load(filePath);
            return ParseDocument(doc);
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, $"Failed to parse patch XML file: {filePath} 解析 patch XML 文件失败。", ex);
        }
    }

    /// <summary>
    /// Parses a patch XML from the specified stream.
    /// <para>从指定流解析 patch XML。</para>
    /// </summary>
    /// <param name="stream">The stream containing the patch XML data. 包含 patch XML 数据的流。</param>
    /// <returns>A list of <see cref="PatchEntry"/> objects parsed from the stream. 从流中解析的 <see cref="PatchEntry"/> 对象列表。</returns>
    /// <exception cref="EdlException">Thrown when the stream cannot be parsed. 流无法解析时抛出。</exception>
    public static List<PatchEntry> Parse(Stream stream)
    {
        if (stream == null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "Stream must not be null. 流不能为 null。");
        }

        try
        {
            var doc = XDocument.Load(stream);
            return ParseDocument(doc);
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, "Failed to parse patch XML from stream. 从流解析 patch XML 失败。", ex);
        }
    }

    /// <summary>
    /// Asynchronously parses a patch XML file from the specified file path.
    /// <para>从指定文件路径异步解析 patch XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the patch XML file. patch XML 文件路径。</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation. 用于取消异步操作的令牌。</param>
    /// <returns>A task whose result contains the parsed <see cref="PatchEntry"/> objects. 任务结果包含解析出的 <see cref="PatchEntry"/> 对象。</returns>
    /// <exception cref="EdlException">Thrown when the file cannot be loaded or parsed. 文件无法加载或解析时抛出。</exception>
    public static async Task<List<PatchEntry>> ParseAsync(string filePath, CancellationToken cancellationToken = default) => await Task.Run(() => Parse(filePath), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Parses multiple patch XML files and combines all entries into a single list.
    /// <para>解析多个 patch XML 文件，将所有条目合并为单个列表。</para>
    /// </summary>
    /// <param name="filePaths">The list of patch XML file paths to parse. 待解析的 patch XML 文件路径列表。</param>
    /// <returns>A combined list of <see cref="PatchEntry"/> objects from all files. 来自所有文件的合并 <see cref="PatchEntry"/> 对象列表。</returns>
    /// <exception cref="EdlException">Thrown when any file cannot be loaded or parsed. 任一文件无法加载或解析时抛出。</exception>
    public static List<PatchEntry> ParseAll(IEnumerable<string> filePaths)
    {
        if (filePaths == null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "File paths collection must not be null. 文件路径集合不能为 null。");
        }

        var allEntries = new List<PatchEntry>();
        foreach (var filePath in filePaths)
        {
            allEntries.AddRange(Parse(filePath));
        }

        return allEntries;
    }

    /// <summary>
    /// Asynchronously parses multiple patch XML files and combines all entries into a single
    /// list.
    /// <para>异步解析多个 patch XML 文件，将所有条目合并为单个列表。</para>
    /// </summary>
    /// <param name="filePaths">The list of patch XML file paths to parse. 待解析的 patch XML 文件路径列表。</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation. 用于取消异步操作的令牌。</param>
    /// <returns>A task whose result contains a combined list of <see cref="PatchEntry"/> objects. 任务结果包含合并的 <see cref="PatchEntry"/> 对象列表。</returns>
    /// <exception cref="EdlException">Thrown when any file cannot be loaded or parsed. 任一文件无法加载或解析时抛出。</exception>
    public static async Task<List<PatchEntry>> ParseAllAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) => await Task.Run(() => ParseAll(filePaths), cancellationToken).ConfigureAwait(false);

    private static List<PatchEntry> ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new EdlException(EdlErrorCode.XmlParseError, "Patch XML document has no root element. patch XML 文档没有根元素。");
        var entries = new List<PatchEntry>();
        foreach (var element in root.Elements("patch"))
        {
            var slotAttr = element.Attribute("slot");
            var entry = new PatchEntry
            {
                SectorSizeInBytes = (long?)element.Attribute("SECTOR_SIZE_IN_BYTES") ?? 0,
                Filename = (string?)element.Attribute("filename") ?? string.Empty,
                PhysicalPartitionNumber = (int?)element.Attribute("physical_partition_number") ?? 0,
                StartSector = (string?)element.Attribute("start_sector") ?? "0",
                ByteOffset = (long?)element.Attribute("byte_offset") ?? 0,
                Value = (string?)element.Attribute("value") ?? string.Empty,
                SizeInBytes = (long?)element.Attribute("size_in_bytes") ?? 0,
                PagesPerBlock = (int?)element.Attribute("PAGES_PER_BLOCK") ?? 0,
                Slot = slotAttr != null ? (int?)slotAttr : null,
                Label = (string?)element.Attribute("label")
            };

            entries.Add(entry);
        }

        return entries;
    }
}