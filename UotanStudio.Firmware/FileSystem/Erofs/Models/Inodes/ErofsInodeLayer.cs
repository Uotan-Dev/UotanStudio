namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Origin of one unified inode inside a single build: an inode decoded from the source image, or an
/// inode the current build added.
/// <para>单个构建内统一 inode 的来源：从源镜像解码得到的 inode，或本次构建新增的 inode。</para>
/// </summary>
internal enum ErofsInodeLayer
{
    /// <summary>Inode decoded from a source image and replayed by a preserved rebuild.
    /// <para>从源镜像解码、并由保留式重建重放的 inode。</para></summary>
    Source = 0,

    /// <summary>Inode the current build created because the source image had no matching entry.
    /// <para>源镜像没有对应条目时由本次构建创建的 inode。</para></summary>
    Added = 1,
}