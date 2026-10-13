using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.CompoundFile;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Parser for Qualcomm QCN NV backup files: an OLE2/CFB container whose numbered NV records live in one or more streams named NV_ITEM_ARRAY. The parser merges all such streams, verifies the fixed 136-byte record layout and decodes the File_Version and Mobile_Property_Info streams.
/// <para>Qualcomm QCN NV 备份文件解析器：OLE2/CFB 容器中编号 NV 记录存放于一个或多个名为 NV_ITEM_ARRAY 的流。本解析器合并全部此类流，校验 136 字节定长记录布局，并解码 File_Version 与 Mobile_Property_Info 流。</para>
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

    private const string NvItemArrayName = "NV_ITEM_ARRAY";
    private const string FileVersionName = "File_Version";
    private const string MobilePropertyInfoName = "Mobile_Property_Info";

    /// <summary>
    /// Parses the QCN file at the given path into an editable <see cref="QcnDocument"/>.
    /// <para>将指定路径的 QCN 文件解析为可编辑的 <see cref="QcnDocument"/>。</para>
    /// </summary>
    /// <param name="path">
    /// The QCN file path.
    /// <para>QCN 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token carried through the container and stream reads.
    /// <para>取消令牌，贯穿容器与流读取。</para>
    /// </param>
    /// <returns>
    /// The parsed document.
    /// <para>解析后的文档。</para>
    /// </returns>
    /// <exception cref="QcnException">
    /// The file exceeds the size limit.
    /// <para>文件超过大小上限。</para>
    /// </exception>
    /// <exception cref="UotanStudio.Firmware.CompoundFile.InvalidCompoundFileMagicException">
    /// The file is not an OLE2/CFB container.
    /// <para>文件不是 OLE2/CFB 容器。</para>
    /// </exception>
    /// <exception cref="UotanStudio.Firmware.CompoundFile.CompoundFileException">
    /// The container structure is corrupted or unsupported.
    /// <para>容器结构损坏或不受支持。</para>
    /// </exception>
    /// <exception cref="InvalidQcnFormatException">
    /// The container has no <c>NV_ITEM_ARRAY</c> record, or the record header violates the 136-byte layout.
    /// <para>容器中不存在 <c>NV_ITEM_ARRAY</c> 记录，或记录头违反 136 字节布局。</para>
    /// </exception>
    public static async Task<QcnDocument> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxSourceFileSize)
        {
            throw new QcnException($"The QCN source is {length} bytes; this parser accepts at most {MaxSourceFileSize} bytes.");
        }

        using var reader = await CompoundFileReader.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await ParseAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<QcnDocument> ParseAsync(CompoundFileReader reader, CancellationToken cancellationToken)
    {
        var fileVersion = await ReadFileVersionAsync(reader, cancellationToken).ConfigureAwait(false);
        var mobilePropertyInfo = await ReadMobilePropertyInfoAsync(reader, cancellationToken).ConfigureAwait(false);

        var items = new List<QcnNvItem>();
        var streamPaths = new List<string>();
        foreach (var entry in reader.Entries)
        {
            if (entry.Type != CompoundFileEntryType.Stream || !entry.Name.Equals(NvItemArrayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            streamPaths.Add(entry.FullPath);
            await using var stream = reader.OpenStream(entry);
            var buffer = new byte[QcnNvItem.RecordLength];
            var ordinalBase = items.Count;
            var recordIndex = 0;
            var logicalOffset = 0L;
            while (logicalOffset + QcnNvItem.RecordLength <= stream.Length)
            {
                if (ordinalBase + recordIndex >= MaxRecords)
                {
                    throw new InvalidQcnFormatException($"The QCN carries more than the accepted {MaxRecords} NV records.");
                }

                stream.Seek(logicalOffset, SeekOrigin.Begin);
                await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                var declaredSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0, 2));
                if (declaredSize != QcnNvItem.RecordLength)
                {
                    throw new InvalidQcnFormatException($"Record {recordIndex} of stream '{entry.FullPath}' declares stream size {declaredSize} instead of {QcnNvItem.RecordLength}.");
                }

                var id = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4, 2));
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2));
                var data = new byte[QcnNvItem.DataLength];
                buffer.AsSpan(8, QcnNvItem.DataLength).CopyTo(data);
                items.Add(new QcnNvItem(ordinalBase + recordIndex, id, flags, data, entry.FullPath, recordIndex));
                logicalOffset += QcnNvItem.RecordLength;
                recordIndex++;
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidQcnFormatException("The compound document carries no NV_ITEM_ARRAY records; it is not a Qualcomm QCN NV backup.");
        }

        return new QcnDocument(fileVersion, mobilePropertyInfo, items, streamPaths);
    }

    private static async Task<IReadOnlyList<ushort>> ReadFileVersionAsync(CompoundFileReader reader, CancellationToken cancellationToken)
    {
        var entry = reader.TryGetEntryByPath(FileVersionName);
        if (entry is null)
        {
            return [];
        }

        await using var stream = reader.OpenStream(entry);
        var buffer = new byte[Math.Min((long)stream.Length, 6)];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        var version = new List<ushort>(buffer.Length / 2);
        for (var i = 0; i + 2 <= buffer.Length; i += 2)
        {
            version.Add(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i, 2)));
        }

        return version;
    }

    private static async Task<QcnMobilePropertyInfo?> ReadMobilePropertyInfoAsync(CompoundFileReader reader, CancellationToken cancellationToken)
    {
        CompoundFileEntry? entry = reader.TryGetEntryByPath($"00000000/default/{MobilePropertyInfoName}");
        entry ??= FindStreamByName(reader, MobilePropertyInfoName);
        if (entry is null)
        {
            return null;
        }

        await using var stream = reader.OpenStream(entry);
        var buffer = new byte[Math.Min((long)stream.Length, 4096)];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length < 8)
        {
            return null;
        }

        var efs = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var model = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4, 2));
        var major = buffer[6];
        var minor = buffer[7];
        var offset = 8;
        var swVersion = ReadLengthPrefixedString(buffer, ref offset);
        var qpstVersion = offset <= buffer.Length - 2 ? ReadLengthPrefixedString(buffer, ref offset) : string.Empty;
        return new QcnMobilePropertyInfo(efs, model, major, minor, swVersion, qpstVersion);
    }

    private static CompoundFileEntry? FindStreamByName(CompoundFileReader reader, string name)
    {
        foreach (var entry in reader.Entries)
        {
            if (entry.Type == CompoundFileEntryType.Stream && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string ReadLengthPrefixedString(byte[] buffer, ref int offset)
    {
        if (offset > buffer.Length - 2)
        {
            return string.Empty;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
        offset += 2;
        var capped = Math.Min((int)length, 256);
        if (offset + capped > buffer.Length)
        {
            offset = buffer.Length;
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(buffer, offset, capped);
        offset += capped;
        return text;
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