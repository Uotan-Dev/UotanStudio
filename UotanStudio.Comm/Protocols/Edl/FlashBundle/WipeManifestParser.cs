using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Parses the wipe elements of a rawprogram document into the destructive zero-fill requests they describe. A wipe element is a flat attribute-only command like program, erase and read, so this shares the bounded loader of the other manifest parsers and keeps literal attribute values.
/// <para>把 rawprogram 文档的 wipe 元素解析为它们描述的破坏性写零请求。wipe 元素与 program、erase、read 一样是扁平的纯属性命令，因此复用其他清单解析器的有界加载器并保留字面属性值。</para>
/// </summary>
public static class WipeManifestParser
{
    /// <summary>
    /// Parses the wipe elements of a rawprogram XML file.
    /// <para>解析 rawprogram XML 文件中的 wipe 元素。</para>
    /// </summary>
    /// <param name="filePath">The path to the rawprogram XML file. <para>rawprogram XML 文件路径。</para></param>
    /// <returns>The parsed <see cref="WipeManifestEntry"/> list in document order; a document without any wipe
    /// element yields an empty list. <para>按文档顺序排列的 <see cref="WipeManifestEntry"/> 列表；不含 wipe
    /// 元素的文档返回空列表。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.
    /// <para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    public static List<WipeManifestEntry> Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "File path must not be empty. 文件路径不能为空。");
        }

        try
        {
            return ParseDocument(FlashBundleXml.Load(filePath));
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, $"Failed to parse wipe elements from XML file: {filePath} 从文件解析 wipe 元素失败。", ex);
        }
    }

    /// <summary>
    /// Parses the wipe elements of a rawprogram XML held in a stream.
    /// <para>解析流中 rawprogram XML 的 wipe 元素。</para>
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the document; the caller retains
    /// ownership. <para>定位到文档起始处的可读流；所有权由调用方持有。</para></param>
    /// <returns>The parsed <see cref="WipeManifestEntry"/> list in document order. <para>按文档顺序排列的
    /// <see cref="WipeManifestEntry"/> 列表。</para></returns>
    /// <exception cref="EdlException">The stream is <see langword="null"/> or the document cannot be parsed.
    /// <para>流为 <see langword="null"/>，或文档无法解析。</para></exception>
    public static List<WipeManifestEntry> Parse(Stream stream)
    {
        if (stream == null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "Stream must not be null. 流不能为 null。");
        }

        try
        {
            return ParseDocument(FlashBundleXml.Load(stream));
        }
        catch (EdlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdlException(EdlErrorCode.XmlParseError, "Failed to parse wipe elements from stream. 从流解析 wipe 元素失败。", ex);
        }
    }

    /// <summary>Asynchronously parses the wipe elements of a rawprogram XML file.
    /// <para>异步解析 rawprogram XML 文件中的 wipe 元素。</para></summary>
    /// <param name="filePath">The path to the rawprogram XML file.
    /// <para>rawprogram XML 文件路径。</para></param>
    /// <param name="cancellationToken">A token observed while the work item is queued.
    /// <para>在工作项排队阶段 被观察的令牌。</para></param>
    /// <returns>A task whose result contains the parsed <see cref="WipeManifestEntry"/> objects.
    /// <para>任务结果 包含解析出的 <see cref="WipeManifestEntry"/> 对象。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.
    /// <para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the work item was queued.
    /// <para>工作项排队时令牌被取消。</para></exception>
    public static async Task<List<WipeManifestEntry>> ParseAsync(string filePath, CancellationToken cancellationToken = default)
        => await Task.Run(() => Parse(filePath), cancellationToken).ConfigureAwait(false);

    private static List<WipeManifestEntry> ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new EdlException(EdlErrorCode.XmlParseError, "Rawprogram XML document has no root element. rawprogram XML 文档没有根元素。");
        var entries = new List<WipeManifestEntry>();
        foreach (var element in root.Elements("wipe"))
        {
            var slotAttr = element.Attribute("slot");
            var zeroFilename = (string?)element.Attribute("zeros_filename");
            entries.Add(new WipeManifestEntry
            {
                SectorSizeInBytes = (long?)element.Attribute("SECTOR_SIZE_IN_BYTES") ?? 0,
                NumPartitionSectors = (long?)element.Attribute("num_partition_sectors") ?? 0,
                PhysicalPartitionNumber = (int?)element.Attribute("physical_partition_number") ?? 0,
                StartSector = (string?)element.Attribute("start_sector") ?? "0",
                Label = (string?)element.Attribute("label") ?? string.Empty,
                ZeroFilename = string.IsNullOrWhiteSpace(zeroFilename) ? WipeManifestEntry.DefaultZeroFilename : zeroFilename,
                EntireStorage = ParseFlag((string?)element.Attribute("all")),
                Slot = slotAttr != null ? (int?)slotAttr : null
            });
        }

        return entries;
    }

    private static bool ParseFlag(string? value) => value != null
        && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal));
}