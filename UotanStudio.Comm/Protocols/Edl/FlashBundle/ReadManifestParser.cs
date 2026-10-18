using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Parses read manifests describing flash ranges to dump back to files; such a manifest is accepted as program-xml input alongside program and erase tags. Entries keep literal attribute values; source paths are unresolved and ranges are bounds-checked only when a geometry is supplied.
/// <para>解析描述待回读至文件的闪存区间的 read 清单；该清单与 program、erase 标签一样可作为 program-xml 输入。条目保留字面属性值，源路径不解析，仅当提供几何信息时才校验区间。</para>
/// </summary>
public static class ReadManifestParser
{
    /// <summary>
    /// Parses a read manifest XML file from the specified file path.
    /// <para>从指定文件路径解析 read 清单 XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the read manifest XML file. <para>read 清单 XML 文件路径。</para></param>
    /// <returns>The parsed <see cref="ReadManifestEntry"/> list in document order. <para>按文档顺序排列的
    /// <see cref="ReadManifestEntry"/> 列表。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.
    /// <para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    public static List<ReadManifestEntry> Parse(string filePath)
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
            throw new EdlException(EdlErrorCode.XmlParseError, $"Failed to parse read manifest XML file: {filePath} 解析 read 清单 XML 文件失败。", ex);
        }
    }

    /// <summary>
    /// Parses a read manifest XML from the specified stream.
    /// <para>从指定流解析 read 清单 XML。</para>
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the read manifest document; the
    /// caller retains ownership. <para>定位到 read 清单文档起始处的可读流；所有权由调用方持有。</para></param>
    /// <returns>The parsed <see cref="ReadManifestEntry"/> list in document order. <para>按文档顺序排列的
    /// <see cref="ReadManifestEntry"/> 列表。</para></returns>
    /// <exception cref="EdlException">The stream is <see langword="null"/> or the document cannot be parsed.
    /// <para>流为 <see langword="null"/>，或文档无法解析。</para></exception>
    public static List<ReadManifestEntry> Parse(Stream stream)
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
            throw new EdlException(EdlErrorCode.XmlParseError, "Failed to parse read manifest XML from stream. 从流解析 read 清单 XML 失败。", ex);
        }
    }

    /// <summary>Asynchronously parses a read manifest XML file from the specified path.
    /// <para>从指定路径异步解析 read 清单 XML 文件。</para></summary>
    /// <param name="filePath">The path to the read manifest XML file.
    /// <para>read 清单 XML 文件路径。</para></param>
    /// <param name="cancellationToken">A token observed while the work item is queued.
    /// <para>在工作项排队阶段 被观察的令牌。</para></param>
    /// <returns>A task whose result contains the parsed <see cref="ReadManifestEntry"/> objects.
    /// <para>任务结果 包含解析出的 <see cref="ReadManifestEntry"/> 对象。</para></returns>
    /// <exception cref="EdlException">The path is empty or whitespace, or the file cannot be loaded or parsed.
    /// <para>路径为空白字符串，或文件无法加载或解析。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the work item was queued.
    /// <para>工作项排队时令牌被取消。</para></exception>
    public static async Task<List<ReadManifestEntry>> ParseAsync(string filePath, CancellationToken cancellationToken = default)
        => await Task.Run(() => Parse(filePath), cancellationToken).ConfigureAwait(false);

    private static List<ReadManifestEntry> ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new EdlException(EdlErrorCode.XmlParseError, "Read manifest XML document has no root element. read 清单 XML 文档没有根元素。");
        var entries = new List<ReadManifestEntry>();
        foreach (var element in root.Elements("read"))
        {
            var slotAttr = element.Attribute("slot");
            entries.Add(new ReadManifestEntry
            {
                SectorSizeInBytes = (long?)element.Attribute("SECTOR_SIZE_IN_BYTES") ?? 0,
                NumPartitionSectors = (long?)element.Attribute("num_partition_sectors") ?? 0,
                Filename = (string?)element.Attribute("filename") ?? (string?)element.Attribute("file") ?? string.Empty,
                PhysicalPartitionNumber = (int?)element.Attribute("physical_partition_number") ?? 0,
                StartSector = (string?)element.Attribute("start_sector") ?? "0",
                Label = (string?)element.Attribute("label") ?? string.Empty,
                Slot = slotAttr != null ? (int?)slotAttr : null
            });
        }

        return entries;
    }
}