namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// One numbered NV item record parsed from a QCN <c>NV_ITEM_ARRAY</c> stream. Each record is a fixed
/// 136 bytes: four little-endian <c>uint16</c> header fields (stream size 136, index, item id, flags)
/// followed by 128 data bytes. The 128-byte data buffer is a mutable copy owned by the item; edits
/// become the input of <see cref="QcnWriter.PatchAsync"/>.
/// <para>从 QCN <c>NV_ITEM_ARRAY</c> 流解析出的一条编号 NV 条目记录。每条记录恒定 136 字节：四个
/// 小端 <c>uint16</c> 头字段（流大小 136、索引、条目 id、flags）加 128 字节数据。128 字节数据缓冲区
/// 是该条目持有的可变副本；编辑结果作为 <see cref="QcnWriter.PatchAsync"/> 的输入。</para>
/// </summary>
public sealed class QcnNvItem
{
    /// <summary>
    /// The NV data region size in bytes, as stored by the QCN format regardless of the item's
    /// effective data length.
    /// <para>QCN 格式存储的 NV 数据区大小（字节），与条目的有效数据长度无关。</para>
    /// </summary>
    public const int DataLength = 128;

    /// <summary>
    /// Total record size in bytes: the 8-byte header plus the 128-byte data region.
    /// <para>记录总大小（字节）：8 字节头加 128 字节数据区。</para>
    /// </summary>
    public const int RecordLength = DataLength + 8;

    internal QcnNvItem(int ordinal, ushort id, ushort flags, byte[] data, string sourceStreamPath, int recordIndex)
    {
        Ordinal = ordinal;
        Id = id;
        Flags = flags;
        Data = data;
        SourceStreamPath = sourceStreamPath;
        RecordIndex = recordIndex;
    }

    /// <summary>
    /// Creates a record from raw NV data, zero-padded or truncated to the 128-byte data region.
    /// <para>从原始 NV 数据创建记录，数据补零或截断到 128 字节。</para>
    /// </summary>
    /// <param name="ordinal">Zero-based record ordinal. <para>从零的记录序号。</para></param>
    /// <param name="id">The numbered NV item id. <para>编号 NV 条目 id。</para></param>
    /// <param name="flags">The record flags field. <para>记录 flags 字段。</para></param>
    /// <param name="data">The item data; at most 128 bytes are stored.
    /// <para>条目数据；至多存储 128 字节。</para></param>
    /// <param name="sourceStreamPath">The stream path the record belongs to.
    /// <para>记录所属的流路径。</para></param>
    /// <param name="recordIndex">Zero-based index inside the stream.
    /// <para>流内从零索引。</para></param>
    /// <returns>The created record. <para>创建的记录。</para></returns>
    /// <exception cref="ArgumentException">The data exceeds 128 bytes.
    /// <para>数据超过 128 字节。</para></exception>
    public static QcnNvItem Create(int ordinal, ushort id, ushort flags, ReadOnlySpan<byte> data,
        string sourceStreamPath, int recordIndex)
    {
        if (data.Length > DataLength)
        {
            throw new ArgumentException($"The item data is {data.Length} bytes; at most {DataLength} are stored.",
                nameof(data));
        }

        var stored = new byte[DataLength];
        data.CopyTo(stored);
        return new QcnNvItem(ordinal, id, flags, stored, sourceStreamPath, recordIndex);
    }

    /// <summary>
    /// Zero-based position of this record across all <c>NV_ITEM_ARRAY</c> streams in file order.
    /// <para>该记录在文件内所有 <c>NV_ITEM_ARRAY</c> 流中按文件顺序的从零位置。</para>
    /// </summary>
    /// <value>
    /// The global record ordinal.
    /// <para>全局记录序号。</para>
    /// </value>
    public int Ordinal { get; }

    /// <summary>
    /// The numbered NV item id (the value QPST shows as "Item number").
    /// <para>编号 NV 条目 id（即 QPST 显示的 "Item number"）。</para>
    /// </summary>
    /// <value>
    /// The item id.
    /// <para>条目 id。</para>
    /// </value>
    public ushort Id { get; }

    /// <summary>
    /// The fourth header field of the record; most writers store zero but it is not guaranteed, so it
    /// is preserved verbatim on patching.
    /// <para>记录头的第四个字段；多数写入器存 0 但不作保证，补丁时原样保留。</para>
    /// </summary>
    /// <value>
    /// The raw flags field.
    /// <para>原始 flags 字段值。</para>
    /// </value>
    public ushort Flags { get; }

    /// <summary>
    /// The 128-byte NV data region; trailing bytes beyond the effective data are zero padding that
    /// must be preserved through edits.
    /// <para>128 字节 NV 数据区；有效数据之后的尾部字节是补零，编辑时必须保留。</para>
    /// </summary>
    /// <value>
    /// The mutable 128-byte data buffer.
    /// <para>可变的 128 字节数据缓冲区。</para>
    /// </value>
    public byte[] Data { get; }

    /// <summary>
    /// Full path of the <c>NV_ITEM_ARRAY</c> stream this record was parsed from.
    /// <para>本记录所来自的 <c>NV_ITEM_ARRAY</c> 流的完整路径。</para>
    /// </summary>
    /// <value>
    /// The stream path, e.g. <c>NV_NUMBERED_ITEMS/NV_ITEM_ARRAY</c>.
    /// <para>流路径，例如 <c>NV_NUMBERED_ITEMS/NV_ITEM_ARRAY</c>。</para>
    /// </value>
    public string SourceStreamPath { get; }

    /// <summary>
    /// Zero-based record index inside the source stream.
    /// <para>源流内从零计的记录索引。</para>
    /// </summary>
    /// <value>
    /// The record index inside the source stream.
    /// <para>源流内的记录索引。</para>
    /// </value>
    public int RecordIndex { get; }

    /// <summary>
    /// Heuristic effective data length: the position after the last non-zero byte, or 0 for all-zero
    /// data. Real NV lengths cannot always be recovered because trailing data bytes may legitimately
    /// be zero.
    /// <para>启发式有效数据长度：最后一个非零字节之后的位置，全零数据为 0。由于尾部数据字节可能
    /// 本身就是 0，真实的 NV 长度并不总能恢复。</para>
    /// </summary>
    /// <value>
    /// The heuristic effective length in bytes.
    /// <para>启发式有效长度（字节）。</para>
    /// </value>
    public int EffectiveLength
    {
        get
        {
            var last = Array.LastIndexOf(Data, (byte)0);
            if (last < 0)
            {
                return DataLength;
            }

            while (last >= 0 && Data[last] == 0)
            {
                last--;
            }

            return last + 1;
        }
    }
}