namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Addressing state carried by a unified inode's <c>u</c> field, one of the flat block address, the
/// compressed block count or the chunk-based format plus index table
/// (<c>struct erofs_inode.u</c>).
/// <para>统一 inode 的 <c>u</c> 字段携带的寻址状态，取平坦块地址、压缩块数量或
/// chunk-based 格式加索引表三者之一（<c>struct erofs_inode.u</c>）。</para>
/// </summary>
internal interface IErofsIdata;