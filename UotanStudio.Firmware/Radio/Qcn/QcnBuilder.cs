using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.CompoundFile;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Builds a Qualcomm QCN NV backup file (an OLE2/CFB container) from numbered NV item records plus
/// the optional <c>File_Version</c> and <c>Mobile_Property_Info</c> streams, matching the stream
/// names <see cref="QcnReader"/> consumes.
/// <para>从编号 NV 条目记录及可选的 <c>File_Version</c> 与 <c>Mobile_Property_Info</c> 流构建
/// Qualcomm QCN NV 备份文件（OLE2/CFB 容器），流命名与 <see cref="QcnReader"/> 的读取约定一致。</para>
/// </summary>
public static class QcnBuilder
{
    /// <summary>
    /// Maximum accepted NV record count, matching <see cref="QcnReader.MaxRecords"/>.
    /// <para>接受的 NV 记录数上限，与 <see cref="QcnReader.MaxRecords"/> 一致。</para>
    /// </summary>
    public const int MaxRecords = QcnReader.MaxRecords;

    /// <summary>
    /// Builds a QCN container from the given NV records and writes it to <paramref name="path"/>.
    /// <para>从给定 NV 记录构建 QCN 容器并写入 <paramref name="path"/>。</para></summary>
    /// <param name="path">Destination QCN file path. <para>目标 QCN 文件路径。</para></param>
    /// <param name="items">NV records; ids may repeat across streams but every record must carry
    /// 128 data bytes. <para>NV 记录；id 可跨流重复，但每条记录必须携带 128 字节数据。</para></param>
    /// <param name="fileVersion">Optional three-<c>uint16</c> file version; defaults to
    /// <c>{2, 0, 0}</c>. <para>可选的三分量文件版本；默认 <c>{2, 0, 0}</c>。</para></param>
    /// <param name="mobilePropertyInfo">Optional mobile property summary encoded into the
    /// <c>Mobile_Property_Info</c> stream. <para>可选的设备属性摘要，编码进
    /// <c>Mobile_Property_Info</c> 流。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The item set is empty or a record carries data that is
    /// not exactly 128 bytes. <para>条目集为空或某条记录的数据不是 128 字节。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The item count exceeds the accepted record
    /// budget. <para>条目数超出接受的记录预算。</para></exception>
    public static async Task BuildAsync(string path, IReadOnlyList<QcnNvItem> items,
        IReadOnlyList<ushort>? fileVersion = null, QcnMobilePropertyInfo? mobilePropertyInfo = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one NV record is required.", nameof(items));
        }

        if (items.Count > MaxRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(items),
                $"The item set carries {items.Count} records; at most {MaxRecords} are accepted.");
        }

        foreach (var item in items)
        {
            if (item.Data.Length != QcnNvItem.DataLength)
            {
                throw new ArgumentException(
                    $"Record {item.Ordinal} (id {item.Id}) carries {item.Data.Length} data bytes; " +
                    $"a QCN record stores exactly {QcnNvItem.DataLength}.", nameof(items));
            }
        }

        var streams = new List<CompoundFileStreamWriterEntry>
        {
            new("00000000/default/NV_ITEM_ARRAY", EncodeNvItemArray(items)),
        };
        var version = fileVersion is { Count: > 0 } ? fileVersion : [2, 0, 0];
        var versionBytes = new byte[version.Count * 2];
        for (var i = 0; i < version.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(versionBytes.AsSpan(2 * i), version[i]);
        }

        streams.Add(new CompoundFileStreamWriterEntry("File_Version", versionBytes));
        if (mobilePropertyInfo is { } property)
        {
            streams.Add(new CompoundFileStreamWriterEntry(
                "00000000/default/Mobile_Property_Info", EncodeMobilePropertyInfo(property)));
        }

        await CompoundFileWriter.BuildAsync(path, streams, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] EncodeNvItemArray(IReadOnlyList<QcnNvItem> items)
    {
        var stream = new MemoryStream(items.Count * QcnNvItem.RecordLength);
        foreach (var item in items)
        {
            var record = new byte[QcnNvItem.RecordLength];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), QcnNvItem.RecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2, 2), (ushort)item.Ordinal);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), item.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), item.Flags);
            item.Data.CopyTo(record, 8);
            stream.Write(record);
        }

        return stream.ToArray();
    }

    private static byte[] EncodeMobilePropertyInfo(QcnMobilePropertyInfo property)
    {
        var swVersion = Encoding.ASCII.GetBytes(property.SwVersion);
        var qpstVersion = Encoding.ASCII.GetBytes(property.QpstVersion);
        var stream = new MemoryStream(16 + swVersion.Length + qpstVersion.Length);
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, property.Efs);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), property.MobileModelNo);
        header[6] = property.VersionMajor;
        header[7] = property.VersionMinor;
        stream.Write(header);
        WriteLengthPrefixedString(stream, swVersion);
        WriteLengthPrefixedString(stream, qpstVersion);
        return stream.ToArray();
    }

    private static void WriteLengthPrefixedString(MemoryStream stream, ReadOnlySpan<byte> value)
    {
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)value.Length);
        stream.Write(prefix);
        stream.Write(value);
    }
}