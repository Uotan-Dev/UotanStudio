using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Shared parsing of <c>drutil</c> XML output for the macOS optical scanner and writer.
/// <para>macOS 光驱扫描器与写入器共用的 <c>drutil</c> XML 输出解析。</para>
/// </summary>
/// <remarks>
/// <c>drutil list</c> prints a Vendor/Product/Rev/Bus/SupportLevel table with no BSD disk name; only the XML
/// form exposes the recorder ordinal that <c>drutil -drive</c> accepts.
/// <para>
/// <c>drutil list</c> 输出的是 Vendor/Product/Rev/Bus/SupportLevel 表，没有 BSD 磁盘名；只有 XML 形式
/// 暴露 <c>drutil -drive</c> 接受的刻录机序号。
/// </para>
/// <para>
/// <c>drutil -drive</c> takes a <em>1-based</em> ordinal into the recorder list (<c>drutil help driveselect</c>),
/// and <c>drutil list -xml</c> already numbers its <c>device</c> elements starting at 1, so a parsed index is
/// passed to <c>-drive</c> unchanged and is never rebased.
/// </para>
/// <para>
/// <c>drutil -drive</c> 接受刻录机列表中<em>从 1 开始</em>的序号（<c>drutil help driveselect</c>），
/// 而 <c>drutil list -xml</c> 的 <c>device</c> 元素本身就从 1 编号，因此解析出的序号原样传给 <c>-drive</c>，
/// 不做任何偏移。
/// </para>
/// <para>
/// Both XML forms are emitted with a <c>DOCTYPE</c> declaration whose internal subset carries no root element
/// name, which strict parsers reject, so the prolog is stripped before parsing.
/// </para>
/// <para>
/// 两种 XML 输出都带有内部子集缺少根元素名的 <c>DOCTYPE</c> 声明，严格的解析器会直接拒绝；因此解析前
/// 先剥离文件序言。
/// </para>
/// </remarks>
internal static class MacOpticalDevice
{
    /// <summary>Absolute path of the macOS DiscRecording command line tool.<para>macOS DiscRecording 命令行工具的绝对路径。</para></summary>
    internal const string DrutilPath = "/usr/bin/drutil";

    private const string ListRootElementName = "deviceList";
    private const string StatusRootElementName = "statusdoc";

    /// <summary>
    /// Parses one <c>device</c> element from <c>drutil list -xml</c>.
    /// <para>解析 <c>drutil list -xml</c> 中的一个 <c>device</c> 元素。</para>
    /// </summary>
    /// <param name="element">The device XML element.<para>设备 XML 元素。</para></param>
    /// <param name="index">One-based recorder ordinal accepted by <c>drutil -drive</c>.
    /// <para><c>drutil -drive</c> 接受的一基刻录机序号。</para></param>
    /// <param name="name">Stable recorder name such as <c>drutil:1</c>.<para>稳定刻录机名称，例如 <c>drutil:1</c>。</para></param>
    /// <param name="description">Vendor and product description.<para>厂商与产品描述。</para></param>
    /// <returns><see langword="true"/> when the element was parsed.<para>元素解析成功时为 <see langword="true"/>。</para></returns>
    internal static bool TryParse(XElement element, out int index, out string name, out string description)
    {
        index = -1;
        name = string.Empty;
        description = string.Empty;
        if (!int.TryParse((string?)element.Attribute("index"), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal < 1)
        {
            return false;
        }

        index = ordinal;
        var vendor = ChildValue(element, "vendor", "name");
        var product = ChildValue(element, "product", "name");
        name = "drutil:" + ordinal.ToString(CultureInfo.InvariantCulture);
        description = string.Join(' ', new[] { vendor, product }.Where(part => part.Length > 0));
        if (description.Length == 0)
        {
            description = ((string?)element.Attribute("name"))?.Trim() ?? string.Empty;
        }

        if (description.Length == 0)
        {
            description = name;
        }

        return true;
    }

    /// <summary>
    /// Parses <c>drutil list -xml</c> into recorder records.
    /// <para>把 <c>drutil list -xml</c> 解析为刻录机记录。</para>
    /// </summary>
    /// <param name="xml">The XML output.<para>XML 输出。</para></param>
    /// <returns>Recorder records in listing order.<para>按列表顺序排列的刻录机记录。</para></returns>
    internal static IReadOnlyList<(int Index, string Name, string Description)> ParseList(string xml)
    {
        var records = new List<(int, string, string)>();
        var root = ParseDocumentRoot(xml, ListRootElementName);
        if (root is null)
        {
            return records;
        }

        foreach (var element in root.Elements("device"))
        {
            if (TryParse(element, out var index, out var name, out var description))
            {
                records.Add((index, name, description));
            }
        }

        return records;
    }

    /// <summary>
    /// Reads the media family and writability of one recorder from <c>drutil -drive N status -xml</c>.
    /// <para>从 <c>drutil -drive N status -xml</c> 读取一个刻录机的介质类别与可写性。</para>
    /// </summary>
    /// <param name="xml">The XML output.<para>XML 输出。</para></param>
    /// <returns>
    /// The disc family and whether the recorder accepts the loaded media for recording; a drive with no media,
    /// media in transition, busy media or an unparsable document reports <see cref="OpticalDiscFormat.Unknown"/>
    /// and <see langword="false"/>.
    /// <para>
    /// 光盘类别，以及刻录机是否接受当前介质进行刻录；无介质、介质状态切换中、介质忙或文档无法解析时返回
    /// <see cref="OpticalDiscFormat.Unknown"/> 与 <see langword="false"/>。
    /// </para>
    /// </returns>
    internal static (OpticalDiscFormat Format, bool Writable) ParseStatus(string xml)
    {
        var root = ParseDocumentRoot(xml, StatusRootElementName);
        var deviceStatus = root?.Element("statusfordevice")?.Element("deviceStatus");
        if (deviceStatus is null)
        {
            return (OpticalDiscFormat.Unknown, false);
        }

        if (deviceStatus.Element("mediaIsBusy") is not null
            || deviceStatus.Element("mediaIsInTransition") is not null)
        {
            return (OpticalDiscFormat.Unknown, false);
        }

        var mediaInfo = deviceStatus.Element("mediaInfo");
        if (mediaInfo is null)
        {
            return (OpticalDiscFormat.Unknown, false);
        }

        var blank = mediaInfo.Element("blank") is not null;
        var appendable = mediaInfo.Element("appendable") is not null;
        var overwritable = mediaInfo.Element("overwritable") is not null || mediaInfo.Element("erasable") is not null;
        var writable = blank || appendable || overwritable;
        var format = ResolveFormat(
            ChildAttribute(mediaInfo, "mediaClass", "value"),
            ChildAttribute(mediaInfo, "mediaType", "value"),
            ChildAttribute(mediaInfo.Element("discInfo")?.Element("discInfoBlock") is { } block ? block : null, "discType", "description"));
        return (format, writable);
    }

    /// <summary>
    /// Builds the <c>drutil -drive N status -xml</c> argument vector for one recorder.
    /// <para>为一个刻录机构建 <c>drutil -drive N status -xml</c> 参数列表。</para>
    /// </summary>
    /// <param name="deviceIndex">One-based recorder ordinal from <c>drutil list -xml</c>.
    /// <para><c>drutil list -xml</c> 上报的一基刻录机序号。</para></param>
    /// <returns>The argument vector with <c>-drive</c> before the verb.<para><c>-drive</c> 位于动词之前的参数列表。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deviceIndex"/> is not a one-based ordinal.
    /// <para><paramref name="deviceIndex"/> 不是一基序号。</para></exception>
    internal static IReadOnlyList<string> StatusArguments(int deviceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deviceIndex, 1);
        return ["-drive", deviceIndex.ToString(CultureInfo.InvariantCulture), "status", "-xml"];
    }

    /// <summary>
    /// Resolves the one-based <c>drutil</c> recorder ordinal for a stable recorder path.
    /// <para>为稳定刻录机路径解析一基 <c>drutil</c> 刻录机序号。</para>
    /// </summary>
    /// <param name="devicePath">Recorder path such as <c>drutil:1</c>.<para>刻录机路径，例如 <c>drutil:1</c>。</para></param>
    /// <param name="cancellationToken">Cancels the lookup.<para>取消查询。</para></param>
    /// <returns>
    /// The ordinal to pass to <c>drutil -drive</c>, or <see langword="null"/> when the drive is no longer present.
    /// <para>
    /// 传给 <c>drutil -drive</c> 的序号；光驱已不存在时为 <see langword="null"/>。
    /// </para>
    /// </returns>
    internal static async Task<int?> ResolveIndexAsync(
        string devicePath,
        CancellationToken cancellationToken)
    {
        string listing;
        try
        {
            listing = await MacCommand.RunAsync(DrutilPath, ["list", "-xml"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FlashException)
        {
            return null;
        }

        var prefix = "drutil:";
        var bare = devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? devicePath[prefix.Length..]
            : Path.GetFileName(devicePath);
        foreach (var record in ParseList(listing))
        {
            if (string.Equals(record.Index.ToString(CultureInfo.InvariantCulture), bare, StringComparison.Ordinal)
                || string.Equals(record.Name, devicePath, StringComparison.Ordinal))
            {
                return record.Index;
            }
        }

        return null;
    }

    private static XElement? ParseDocumentRoot(string xml, string rootElementName)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var root = XDocument.Parse(StripProlog(xml), LoadOptions.None).Root;
            return root is not null && string.Equals(root.Name.LocalName, rootElementName, StringComparison.OrdinalIgnoreCase)
                ? root
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string StripProlog(string xml)
    {
        var index = xml.IndexOf('<');
        while (index >= 0 && index + 1 < xml.Length)
        {
            var marker = xml[index + 1];
            if (marker == '?')
            {
                index = IndexAfter(xml, index, "?>");
                continue;
            }

            if (marker == '!')
            {
                if (string.CompareOrdinal(xml, index, "<!--", 0, 4) == 0)
                {
                    index = IndexAfter(xml, index, "-->");
                    continue;
                }

                if (string.CompareOrdinal(xml, index, "<!DOCTYPE", 0, 9) == 0)
                {
                    index = IndexAfterDoctype(xml, index);
                    continue;
                }
            }

            break;
        }

        return index > 0 && index < xml.Length ? xml[index..] : xml;
    }

    private static int IndexAfter(string xml, int start, string terminator)
    {
        var end = xml.IndexOf(terminator, start, StringComparison.Ordinal);
        return end < 0 ? -1 : xml.IndexOf('<', end + terminator.Length);
    }

    private static int IndexAfterDoctype(string xml, int start)
    {
        var index = start + "<!DOCTYPE".Length;
        var inSubset = false;
        var quote = '\0';
        while (index < xml.Length)
        {
            var current = xml[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
            }
            else if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '[')
            {
                inSubset = true;
            }
            else if (current == ']')
            {
                inSubset = false;
            }
            else if (current == '>' && !inSubset)
            {
                return xml.IndexOf('<', index + 1);
            }

            index++;
        }

        return -1;
    }

    private static OpticalDiscFormat ResolveFormat(string mediaClass, string mediaType, string discType)
    {
        var combined = string.Join(' ', new[] { discType, mediaType, mediaClass }.Where(part => part.Length > 0));
        if (combined.Contains("BD", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Blu", StringComparison.OrdinalIgnoreCase))
        {
            return OpticalDiscFormat.BluRay;
        }

        if (combined.Contains("DVD", StringComparison.OrdinalIgnoreCase))
        {
            return OpticalDiscFormat.Dvd;
        }

        return combined.Contains("CD", StringComparison.OrdinalIgnoreCase)
            ? OpticalDiscFormat.Cd
            : OpticalDiscFormat.Unknown;
    }

    private static string ChildValue(XElement parent, string elementName, string attributeName) =>
        (string?)parent.Element(elementName)?.Attribute(attributeName) ?? string.Empty;

    private static string ChildAttribute(XElement? parent, string elementName, string attributeName) =>
        parent is null
            ? string.Empty
            : (string?)parent.Element(elementName)?.Attribute(attributeName) ?? string.Empty;
}