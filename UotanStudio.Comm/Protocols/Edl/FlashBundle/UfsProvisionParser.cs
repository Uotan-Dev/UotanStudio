using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Parses UFS provisioning XML files containing common, body, and epilogue entries.
/// <para>解析包含 common、body 和 epilogue 条目的 UFS 配置 XML 文件。</para>
/// </summary>
public static class UfsProvisionParser
{
    /// <summary>
    /// Parses a UFS provisioning XML file from the specified file path.
    /// <para>从指定文件路径解析 UFS 配置 XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the UFS provisioning XML file. UFS 配置 XML 文件路径。</param>
    /// <returns>A <see cref="UfsProvisionFile"/> containing the parsed entries. 包含解析条目的 <see cref="UfsProvisionFile"/>。</returns>
    /// <exception cref="EdlException">Thrown when the file cannot be loaded or parsed. 文件无法加载或解析时抛出。</exception>
    public static UfsProvisionFile Parse(string filePath)
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
            throw new EdlException(EdlErrorCode.XmlParseError, $"Failed to parse UFS provisioning XML file: {filePath} 解析 UFS 配置 XML 文件失败。", ex);
        }
    }

    /// <summary>
    /// Parses a UFS provisioning XML from the specified stream.
    /// <para>从指定流解析 UFS 配置 XML。</para>
    /// </summary>
    /// <param name="stream">The stream containing the UFS provisioning XML data. 包含 UFS 配置 XML 数据的流。</param>
    /// <returns>A <see cref="UfsProvisionFile"/> containing the parsed entries. 包含解析条目的 <see cref="UfsProvisionFile"/>。</returns>
    /// <exception cref="EdlException">Thrown when the stream cannot be parsed. 流无法解析时抛出。</exception>
    public static UfsProvisionFile Parse(Stream stream)
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
            throw new EdlException(EdlErrorCode.XmlParseError, "Failed to parse UFS provisioning XML from stream. 从流解析 UFS 配置 XML 失败。", ex);
        }
    }

    /// <summary>
    /// Asynchronously parses a UFS provisioning XML file from the specified file path.
    /// <para>从指定文件路径异步解析 UFS 配置 XML 文件。</para>
    /// </summary>
    /// <param name="filePath">The path to the UFS provisioning XML file. UFS 配置 XML 文件路径。</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation. 用于取消异步操作的令牌。</param>
    /// <returns>A task whose result contains the parsed <see cref="UfsProvisionFile"/>. 任务结果包含解析出的 <see cref="UfsProvisionFile"/>。</returns>
    /// <exception cref="EdlException">Thrown when the file cannot be loaded or parsed. 文件无法加载或解析时抛出。</exception>
    public static async Task<UfsProvisionFile> ParseAsync(string filePath, CancellationToken cancellationToken = default) => await Task.Run(() => Parse(filePath), cancellationToken).ConfigureAwait(false);

    private static UfsProvisionFile ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new EdlException(EdlErrorCode.XmlParseError, "UFS provisioning XML document has no root element. UFS 配置 XML 文档没有根元素。");
        var file = new UfsProvisionFile();
        foreach (var element in root.Elements("ufs"))
        {
            file.Entries.Add(ParseEntry(element, (string?)element.Attribute("type")));
        }

        return file;
    }

    private static UfsProvisionEntry ParseEntry(XElement element, string? typeAttr)
    {
        return string.IsNullOrEmpty(typeAttr)
            ? throw new EdlException(EdlErrorCode.XmlParseError, "UFS provisioning entry missing 'type' attribute. UFS 配置条目缺少 'type' 属性。")
            : typeAttr.ToLowerInvariant() switch
            {
                "common" => ParseCommon(element),
                "body" => ParseBody(element),
                "epilogue" => ParseEpilogue(element),
                _ => throw new EdlException(EdlErrorCode.XmlParseError, $"Unknown UFS provisioning entry type: '{typeAttr}' 未知的 UFS 配置条目类型。")
            };
    }

    private static UfsProvisionCommon ParseCommon(XElement element)
    {
        return new UfsProvisionCommon
        {
            BNumberLU = (int?)Attribute(element, "bNumberLU", "BNumberLU") ?? 0,
            BBootEnable = (int?)Attribute(element, "bBootEnable", "BBootEnable") ?? 0,
            BDescrAccessEn = (int?)Attribute(element, "bDescrAccessEn", "BDescrAccessEn") ?? 0,
            BInitPowerMode = (int?)Attribute(element, "bInitPowerMode", "BInitPowerMode") ?? 0,
            BHighPriorityLUN = (int?)Attribute(element, "bHighPriorityLUN", "BHighPriorityLUN") ?? 0,
            BSecureRemovalType = (int?)Attribute(element, "bSecureRemovalType", "BSecureRemovalType") ?? 0,
            BInitActiveICCLevel = (int?)Attribute(element, "bInitActiveICCLevel", "BInitActiveICCLevel") ?? 0,
            WPeriodicRTCUpdate = (int?)Attribute(element, "wPeriodicRTCUpdate", "WPeriodicRTCUpdate") ?? 0,
            BConfigDescrLock = (int?)Attribute(element, "bConfigDescrLock", "BConfigDescrLock") ?? 0,
            Slot = (int?)element.Attribute("slot") ?? 0
        };
    }

    private static UfsProvisionBody ParseBody(XElement element)
    {
        return new UfsProvisionBody
        {
            LUNum = (int?)element.Attribute("LUNum") ?? 0,
            BLUEnable = (int?)Attribute(element, "bLUEnable", "BLUEnable") ?? 0,
            BBootLUNID = (int?)Attribute(element, "bBootLunID", "BBootLUNID") ?? 0,
            SizeInKb = (long?)element.Attribute("size_in_kb") ?? 0,
            BDataReliability = (int?)Attribute(element, "bDataReliability", "BDataReliability") ?? 0,
            BLUWriteProtect = (int?)Attribute(element, "bLUWriteProtect", "BLUWriteProtect") ?? 0,
            BMemoryType = (int?)Attribute(element, "bMemoryType", "BMemoryType") ?? 0,
            BLogicalBlockSize = (int?)Attribute(element, "bLogicalBlockSize", "BLogicalBlockSize") ?? 0,
            BProvisioningType = (int?)Attribute(element, "bProvisioningType", "BProvisioningType") ?? 0,
            WContextCapacities = (int?)Attribute(element, "wContextCapabilities", "WContextCapacities") ?? 0,
            Desc = (string?)element.Attribute("desc") ?? string.Empty,
            Slot = (int?)element.Attribute("slot") ?? 0
        };
    }

    private static UfsProvisionEpilogue ParseEpilogue(XElement element)
    {
        return new UfsProvisionEpilogue
        {
            LUNumToGrow = (int?)Attribute(element, "LUNtoGrow", "LUNumToGrow") ?? 0,
            Commit = (int?)element.Attribute("commit") ?? 0,
            Slot = (int?)element.Attribute("slot") ?? 0
        };
    }

    private static XAttribute? Attribute(XElement element, string name, string legacy)
    {
        var canonical = element.Attribute(name);
        var alias = element.Attribute(legacy);
        if (canonical is not null && alias is not null)
        {
            throw new InvalidDataException($"UFS field '{name}' has both canonical and legacy attributes.");
        }
        return canonical ?? alias;
    }
}