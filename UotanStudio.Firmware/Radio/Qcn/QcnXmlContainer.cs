using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Reader and patcher for the QPST XML QCN export: a <c>Storage</c> tree whose <c>Stream</c> elements
/// carry a declared <c>Length</c> and space-separated hexadecimal <c>Value</c>. The logical stream
/// tree matches the binary OLE2/CFB container, so both encodings feed the same document decoder.
/// <para>QPST XML QCN 导出的读取器与补丁器：<c>Storage</c> 树中的 <c>Stream</c> 元素带有声明的
/// <c>Length</c> 与空格分隔的十六进制 <c>Value</c>。逻辑流树与二进制 OLE2/CFB 容器一致，因此两种
/// 编码共用同一文档解码器。</para>
/// </summary>
internal sealed class QcnXmlContainer
{
    private const string StorageElement = "Storage";
    private const string StreamElement = "Stream";
    private const string NameAttribute = "Name";
    private const string LengthAttribute = "Length";
    private const string ValueAttribute = "Value";
    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"Windows-1252\" ?>\r\n\r\n";
    private const int MaxStreams = 1 << 16;

    private readonly XDocument _document;
    private readonly List<QcnXmlStream> _streams;

    private QcnXmlContainer(XDocument document, List<QcnXmlStream> streams)
    {
        _document = document;
        _streams = streams;
    }

    /// <summary>
    /// Parses an XML QCN from disk into bounded in-memory streams.
    /// <para>从磁盘把 XML QCN 解析为有界的内存流集合。</para></summary>
    /// <param name="path">XML QCN path.<para>XML QCN 路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The parsed container.<para>解析后的容器。</para></returns>
    /// <exception cref="QcnException">The file exceeds the accepted size limit.
    /// <para>文件超过接受的大小上限。</para></exception>
    /// <exception cref="InvalidQcnFormatException">The XML tree or a stream value violates the format.
    /// <para>XML 树或流值违反格式。</para></exception>
    public static async Task<QcnXmlContainer> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > QcnReader.MaxSourceFileSize)
        {
            throw new QcnException($"The QCN source is {length} bytes; this parser accepts at most {QcnReader.MaxSourceFileSize} bytes.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var text = Encoding.Latin1.GetString(bytes);
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidQcnFormatException($"The QCN XML is malformed: {ex.Message}", ex);
        }

        if (document.Root is null || !document.Root.Name.LocalName.Equals(StorageElement, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidQcnFormatException("The QCN XML root element is not a Storage.");
        }

        var streams = new List<QcnXmlStream>();
        CollectStreams(document.Root, string.Empty, streams);
        return new QcnXmlContainer(document, streams);
    }

    /// <summary>
    /// Decodes the container streams into an editable document.
    /// <para>把容器流解码为可编辑文档。</para></summary>
    /// <returns>The parsed document.<para>解析后的文档。</para></returns>
    public QcnDocument BuildDocument() => QcnDocumentFactory.Build(ToSources());

    /// <summary>
    /// Patches the changed NV data regions, rewrites only their hexadecimal stream values and
    /// atomically publishes the staged XML.
    /// <para>修补变化的 NV 数据区，只重写对应的十六进制流值，并以原子替换发布暂存 XML。</para></summary>
    /// <param name="path">XML QCN path to patch.<para>要补丁的 XML QCN 路径。</para></param>
    /// <param name="items">The complete edited item set originating from this file.
    /// <para>来自该文件、编辑后的完整条目集合。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The number of rewritten data regions.<para>重写的数据区数量。</para></returns>
    public async Task<int> PatchAsync(string path, IReadOnlyList<QcnNvItem> items, CancellationToken cancellationToken)
    {
        var parsed = BuildDocument();
        if (parsed.Items.Count != items.Count)
        {
            throw new QcnPatchMismatchException($"The file carries {parsed.Items.Count} NV records but {items.Count} items were supplied.");
        }

        var patched = 0;
        var changed = new HashSet<QcnXmlStream>();
        var ordinal = 0;
        foreach (var stream in _streams)
        {
            if (!QcnDocumentFactory.IsNvItemArray(stream.Name))
            {
                continue;
            }

            var content = stream.Content;
            var count = content.Length / QcnNvItem.RecordLength;
            for (var recordIndex = 0; recordIndex < count; recordIndex++)
            {
                var parsedItem = parsed.Items[ordinal];
                var item = items[ordinal];
                if (item.SourceStreamPath != parsedItem.SourceStreamPath
                    || item.RecordIndex != parsedItem.RecordIndex
                    || item.Id != parsedItem.Id
                    || item.Flags != parsedItem.Flags)
                {
                    throw new QcnPatchMismatchException($"Item {ordinal} (stream '{item.SourceStreamPath}', record {item.RecordIndex}, id {item.Id}) does not match record {ordinal} of the file (stream '{parsedItem.SourceStreamPath}', record {parsedItem.RecordIndex}, id {parsedItem.Id}).");
                }

                var dataOffset = (recordIndex * QcnNvItem.RecordLength) + 8;
                if (!content.AsSpan(dataOffset, QcnNvItem.DataLength).SequenceEqual(item.Data))
                {
                    item.Data.CopyTo(content.AsSpan(dataOffset, QcnNvItem.DataLength));
                    changed.Add(stream);
                    patched++;
                }

                ordinal++;
            }
        }

        if (patched == 0)
        {
            return 0;
        }

        foreach (var stream in changed)
        {
            stream.Element.SetAttributeValue(ValueAttribute, EncodeHex(stream.Content));
        }

        var xml = XmlHeader + _document.Root!.ToString(SaveOptions.DisableFormatting);
        var stagedPath = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            await File.WriteAllBytesAsync(stagedPath, Encoding.Latin1.GetBytes(xml), cancellationToken).ConfigureAwait(false);
            var verify = await OpenAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            Verify(verify.BuildDocument(), items);
            File.Move(stagedPath, path, overwrite: true);
            return patched;
        }
        catch
        {
            TempFileHelper.SafeDelete(stagedPath);
            throw;
        }
    }

    private static void Verify(QcnDocument reparsed, IReadOnlyList<QcnNvItem> items)
    {
        if (reparsed.Items.Count != items.Count)
        {
            throw new QcnException($"Post-patch verification failed: the staged file carries {reparsed.Items.Count} records instead of {items.Count}.");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var record = reparsed.Items[i];
            var item = items[i];
            if (record.Id != item.Id || record.Flags != item.Flags || !record.Data.AsSpan().SequenceEqual(item.Data))
            {
                throw new QcnException($"Post-patch verification failed at record {i} (id {record.Id}).");
            }
        }
    }

    private IReadOnlyList<QcnStreamSource> ToSources() =>
        _streams.Select(stream => new QcnStreamSource(stream.Name, stream.FullPath, stream.Content)).ToList();

    private static void CollectStreams(XElement element, string parentPath, List<QcnXmlStream> streams)
    {
        foreach (var child in element.Elements())
        {
            var localName = child.Name.LocalName;
            if (localName.Equals(StorageElement, StringComparison.OrdinalIgnoreCase))
            {
                var storageName = RequiredAttribute(child, NameAttribute);
                var path = parentPath.Length == 0 ? storageName : parentPath + "/" + storageName;
                CollectStreams(child, path, streams);
                continue;
            }

            if (!localName.Equals(StreamElement, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (streams.Count >= MaxStreams)
            {
                throw new InvalidQcnFormatException($"The QCN XML carries more than {MaxStreams} streams.");
            }

            var name = RequiredAttribute(child, NameAttribute);
            var lengthText = RequiredAttribute(child, LengthAttribute);
            if (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredLength)
                || declaredLength < 0 || declaredLength > QcnReader.MaxStreamSize)
            {
                throw new InvalidQcnFormatException($"Stream '{name}' declares invalid Length '{lengthText}'.");
            }

            var content = DecodeHex(child.Attribute(ValueAttribute)?.Value ?? string.Empty, declaredLength, name);
            var fullPath = parentPath.Length == 0 ? name : parentPath + "/" + name;
            streams.Add(new QcnXmlStream(name, fullPath, child, content));
        }
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidQcnFormatException($"A {element.Name.LocalName} element is missing its {name} attribute.");
        }

        return value;
    }

    private static byte[] DecodeHex(string text, long declaredLength, string streamName)
    {
        var digits = new List<char>(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (!IsHexDigit(c))
            {
                throw new InvalidQcnFormatException($"Stream '{streamName}' carries a non-hex character '{c}' in its Value.");
            }

            digits.Add(c);
        }

        if ((digits.Count & 1) != 0)
        {
            throw new InvalidQcnFormatException($"Stream '{streamName}' carries an odd number of hex digits.");
        }

        if (digits.Count / 2 != declaredLength)
        {
            throw new InvalidQcnFormatException($"Stream '{streamName}' declares {declaredLength} bytes but its Value carries {digits.Count / 2}.");
        }

        var bytes = new byte[digits.Count / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((HexValue(digits[2 * i]) << 4) | HexValue(digits[2 * i + 1]));
        }

        return bytes;
    }

    private static string EncodeHex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789ABCDEF";
        var builder = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(digits[bytes[i] >> 4]).Append(digits[bytes[i] & 0x0F]);
        }

        return builder.ToString();
    }

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    private sealed record QcnXmlStream(string Name, string FullPath, XElement Element, byte[] Content);
}