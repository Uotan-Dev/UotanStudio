using UotanStudio.Firmware.CompoundFile;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Parser for Qualcomm QCN NV backups in either supported container: a binary OLE2/CFB compound file
/// or a QPST XML export. Numbered NV records live in one or more streams named NV_ITEM_ARRAY; the
/// parser merges all such streams, verifies the fixed 136-byte record layout and decodes the
/// File_Version and Mobile_Property_Info streams.
/// <para>Qualcomm QCN NV 备份解析器，支持二进制 OLE2/CFB 复合文档与 QPST XML 导出两种容器。编号 NV
/// 记录存放于一个或多个名为 NV_ITEM_ARRAY 的流；本解析器合并全部此类流，校验 136 字节定长记录布局，
/// 并解码 File_Version 与 Mobile_Property_Info 流。</para>
/// </summary>
public static class QcnReader
{
    /// <summary>
    /// Maximum accepted QCN file size (64 MiB), far above any observed QPST backup while keeping
    /// parsed structures bounded.
    /// <para>接受的 QCN 文件大小上限（64 MiB），远高于已观测的 QPST 备份，同时保证解析结构有界。</para>
    /// </summary>
    public const long MaxSourceFileSize = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum accepted size of a single <c>NV_ITEM_ARRAY</c> stream (16 MiB).
    /// <para>单个 <c>NV_ITEM_ARRAY</c> 流的大小上限（16 MiB）。</para>
    /// </summary>
    public const long MaxStreamSize = 16L * 1024 * 1024;

    /// <summary>
    /// Maximum accepted total NV record count (262,144).
    /// <para>接受的 NV 记录总数上限（262,144）。</para>
    /// </summary>
    public const int MaxRecords = 1 << 18;

    /// <summary>Parses the QCN file at the given path into an editable <see cref="QcnDocument"/>.
    /// <para>将指定路径的 QCN 文件解析为可编辑的 <see cref="QcnDocument"/>。</para></summary>
    /// <param name="path">The QCN file path.<para>QCN 文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token carried through the container and stream reads.
    /// <para>取消令牌，贯穿容器与流读取。</para></param>
    /// <returns>The parsed document.<para>解析后的文档。</para></returns>
    /// <exception cref="QcnException">The file exceeds the size limit or is not a supported QCN.
    /// <para>文件超过大小上限或不是受支持的 QCN。</para></exception>
    /// <exception cref="InvalidQcnFormatException">The container or the NV record layout violates the format.
    /// <para>容器或 NV 记录布局违反格式。</para></exception>
    public static async Task<QcnDocument> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxSourceFileSize)
        {
            throw new QcnException($"The QCN source is {length} bytes; this parser accepts at most {MaxSourceFileSize} bytes.");
        }

        var format = await QcnFormatDetector.DetectAsync(path, cancellationToken).ConfigureAwait(false);
        if (format == QcnContainerFormat.Xml)
        {
            var xml = await QcnXmlContainer.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            return xml.BuildDocument();
        }

        using var reader = await CompoundFileReader.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await ParseAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<QcnDocument> ParseAsync(CompoundFileReader reader, CancellationToken cancellationToken)
    {
        var streams = new List<QcnStreamSource>();
        foreach (var entry in reader.Entries)
        {
            if (entry.Type != CompoundFileEntryType.Stream || !QcnDocumentFactory.IsRelevantStream(entry.Name))
            {
                continue;
            }

            if (entry.StreamSize > MaxStreamSize)
            {
                throw new InvalidQcnFormatException(
                    $"The QCN stream '{entry.FullPath}' is {entry.StreamSize} bytes; this parser accepts at most {MaxStreamSize}.");
            }

            await using var stream = reader.OpenStream(entry);
            var content = new byte[entry.StreamSize];
            await ReadExactAsync(stream, content, cancellationToken).ConfigureAwait(false);
            streams.Add(new QcnStreamSource(entry.Name, entry.FullPath, content));
        }

        return QcnDocumentFactory.Build(streams);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new InvalidQcnFormatException($"The QCN stream ends {buffer.Length - read} bytes short of the expected length.");
            }

            read += n;
        }
    }
}