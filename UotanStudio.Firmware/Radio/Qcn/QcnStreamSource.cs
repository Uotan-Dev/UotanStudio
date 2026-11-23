using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// One logical stream of a QCN container reduced to its name, path and bounded content bytes.
/// <para>QCN 容器中的单个逻辑流，仅保留名称、路径与有界内容字节。</para>
/// </summary>
/// <param name="Name">Stream name without path components.<para>不含路径成分的流名称。</para></param>
/// <param name="FullPath">Slash-separated path from the container root.<para>从容器根起的斜杠分隔路径。</para></param>
/// <param name="Content">Decoded stream bytes.<para>解码后的流字节。</para></param>
internal sealed record QcnStreamSource(string Name, string FullPath, byte[] Content);

/// <summary>
/// Shared decoder that turns QCN container streams into an editable document, independent of the
/// physical container (binary compound file or XML export).
/// <para>共享解码器，把 QCN 容器流转换为可编辑文档，与物理容器（二进制复合文档或 XML 导出）无关。</para>
/// </summary>
internal static class QcnDocumentFactory
{
    internal const string NvItemArrayName = "NV_ITEM_ARRAY";
    internal const string FileVersionName = "File_Version";
    internal const string MobilePropertyInfoName = "Mobile_Property_Info";
    internal const string MobilePropertyInfoPath = "00000000/default/Mobile_Property_Info";

    /// <summary>
    /// Whether a stream name is one of the entries this QCN decoder consumes.
    /// <para>流名称是否属于本 QCN 解码器会消费的条目。</para></summary>
    /// <param name="name">Stream name.<para>流名称。</para></param>
    /// <returns><see langword="true"/> for a recognized name.<para>可识别名称返回 <see langword="true"/>。</para></returns>
    internal static bool IsRelevantStream(string name) =>
        name.Equals(NvItemArrayName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(FileVersionName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(MobilePropertyInfoName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the stream holds numbered NV records.
    /// <para>流是否承载编号 NV 记录。</para></summary>
    /// <param name="name">Stream name.<para>流名称。</para></param>
    /// <returns><see langword="true"/> for an NV record stream.<para>为 NV 记录流时返回 <see langword="true"/>。</para></returns>
    internal static bool IsNvItemArray(string name) =>
        name.Equals(NvItemArrayName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the editable document from the decoded streams of either container encoding.
    /// <para>从任一容器编码解码出的流构建可编辑文档。</para></summary>
    /// <param name="streams">Relevant streams in container order.<para>容器顺序中的相关流。</para></param>
    /// <returns>The parsed document.<para>解析后的文档。</para></returns>
    internal static QcnDocument Build(IReadOnlyList<QcnStreamSource> streams)
    {
        var fileVersion = ReadFileVersion(streams);
        var mobilePropertyInfo = ReadMobilePropertyInfo(streams);
        var items = new List<QcnNvItem>();
        var streamPaths = new List<string>();
        foreach (var stream in streams)
        {
            if (!IsNvItemArray(stream.Name))
            {
                continue;
            }

            streamPaths.Add(stream.FullPath);
            var content = stream.Content;
            if (content.Length > QcnReader.MaxStreamSize)
            {
                throw new InvalidQcnFormatException(
                    $"The QCN stream '{stream.FullPath}' is {content.Length} bytes; this parser accepts at most {QcnReader.MaxStreamSize}.");
            }

            var ordinalBase = items.Count;
            var recordIndex = 0;
            for (var offset = 0; offset + QcnNvItem.RecordLength <= content.Length; offset += QcnNvItem.RecordLength)
            {
                if (ordinalBase + recordIndex >= QcnReader.MaxRecords)
                {
                    throw new InvalidQcnFormatException($"The QCN carries more than the accepted {QcnReader.MaxRecords} NV records.");
                }

                var declaredSize = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(offset, 2));
                if (declaredSize != QcnNvItem.RecordLength)
                {
                    throw new InvalidQcnFormatException($"Record {recordIndex} of stream '{stream.FullPath}' declares stream size {declaredSize} instead of {QcnNvItem.RecordLength}.");
                }

                var id = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(offset + 4, 2));
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(offset + 6, 2));
                var data = new byte[QcnNvItem.DataLength];
                content.AsSpan(offset + 8, QcnNvItem.DataLength).CopyTo(data);
                items.Add(new QcnNvItem(ordinalBase + recordIndex, id, flags, data, stream.FullPath, recordIndex));
                recordIndex++;
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidQcnFormatException("The QCN carries no NV_ITEM_ARRAY records; it is not a Qualcomm QCN NV backup.");
        }

        return new QcnDocument(fileVersion, mobilePropertyInfo, items, streamPaths);
    }

    private static IReadOnlyList<ushort> ReadFileVersion(IReadOnlyList<QcnStreamSource> streams)
    {
        var stream = streams.FirstOrDefault(source =>
            source.FullPath.Equals(FileVersionName, StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            return [];
        }

        var buffer = stream.Content.AsSpan(0, Math.Min(stream.Content.Length, 6));
        var version = new List<ushort>(buffer.Length / 2);
        for (var i = 0; i + 2 <= buffer.Length; i += 2)
        {
            version.Add(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(i, 2)));
        }

        return version;
    }

    private static QcnMobilePropertyInfo? ReadMobilePropertyInfo(IReadOnlyList<QcnStreamSource> streams)
    {
        var stream = streams.FirstOrDefault(source =>
                source.FullPath.Equals(MobilePropertyInfoPath, StringComparison.OrdinalIgnoreCase))
            ?? streams.FirstOrDefault(source =>
                source.Name.Equals(MobilePropertyInfoName, StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            return null;
        }

        var buffer = stream.Content.AsSpan(0, Math.Min(stream.Content.Length, 4096));
        if (buffer.Length < 8)
        {
            return null;
        }

        var efs = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var model = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
        var major = buffer[6];
        var minor = buffer[7];
        var offset = 8;
        var swVersion = ReadLengthPrefixedString(buffer, ref offset);
        var qpstVersion = offset <= buffer.Length - 2 ? ReadLengthPrefixedString(buffer, ref offset) : string.Empty;
        return new QcnMobilePropertyInfo(efs, model, major, minor, swVersion, qpstVersion);
    }

    private static string ReadLengthPrefixedString(ReadOnlySpan<byte> buffer, ref int offset)
    {
        if (offset > buffer.Length - 2)
        {
            return string.Empty;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        offset += 2;
        var capped = Math.Min((int)length, 256);
        if (offset + capped > buffer.Length)
        {
            offset = buffer.Length;
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(buffer.Slice(offset, capped));
        offset += capped;
        return text;
    }
}