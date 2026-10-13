namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Chunk-based addressing: the chunk format word stored in the low half of <c>u</c>, the per-chunk
/// starting blocks decoded from the index table, and the device the payload lives on.
/// <para>chunk-based 寻址：存放在 <c>u</c> 低半部的 chunk 格式字、从索引表解码出的逐 chunk 首块，
/// 以及负载所在设备。</para>
/// </summary>
internal sealed class ErofsChunkIdata : IErofsIdata
{
    /// <summary>Chunk format word (<c>u.chunkformat</c>): chunk size log2 in the low 5 bits plus the
    /// index and 48-bit address flags.
    /// <para>chunk 格式字（<c>u.chunkformat</c>）：低 5 位为 chunk 大小 log2，另有索引与 48 位地址标志。</para>
    /// </summary>
    /// <value>The on-disk low half of <c>u</c>, including any bits the format does not define.
    /// <para>磁盘上 <c>u</c> 的低半部，含格式未定义的位。</para></value>
    internal ushort ChunkFormat { get; set; }

    /// <summary>First block of every chunk, in chunk order.
    /// <para>每个 chunk 的首块，按 chunk 顺序排列；节点未按 chunk-based 规划时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>Set when an index table is attached; an entry equal to the null address marks a hole.
    /// <para>附加索引表时设置；等于空地址的表项表示空洞。</para></value>
    internal ulong[]? ChunkIndex { get; set; }

    /// <summary>Device the payload blocks belong to.
    /// <para>负载块所属设备。</para></summary>
    /// <value>Zero for the main image; the extra-device id otherwise.
    /// <para>主镜像为零；否则为额外设备 id。</para></value>
    internal uint DeviceId { get; set; }
}