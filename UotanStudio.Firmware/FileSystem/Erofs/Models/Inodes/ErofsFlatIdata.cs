namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Flat and inline-data addressing: the file's whole block-aligned prefix is stored at
/// <see cref="BlockAddress"/> and only the sub-block tail (inline layout) lives beside the inode
/// record (<c>u.i_blkaddr</c>).
/// <para>平坦与内联尾部的寻址：文件整个按块对齐的前缀存放在 <see cref="BlockAddress"/>，
/// 仅不完整块尾部（内联布局）位于 inode 记录旁（<c>u.i_blkaddr</c>）。</para>
/// </summary>
internal sealed class ErofsFlatIdata : IErofsIdata
{
    /// <summary>First physical block of the data.
    /// <para>数据的首个物理块。</para></summary>
    /// <value>The block address stored in <c>u.i_blkaddr</c>.
    /// <para><c>u.i_blkaddr</c> 中存放的块地址。</para></value>
    internal uint BlockAddress { get; set; }

    /// <summary>Number of blocks the stored prefix spans.
    /// <para>已存储前缀跨越的块数量。</para></summary>
    /// <value>The flat layout spans the rounded-up <c>i_size</c>; the inline layout spans only the
    /// floor-divided block-aligned prefix because its tail is stored beside the inode record.
    /// <para>平坦布局跨越向上取整的 <c>i_size</c>；内联布局只跨越向下取整的按块对齐前缀，
    /// 因为其尾部存放在 inode 记录旁。</para></value>
    internal uint Blocks { get; set; }
}