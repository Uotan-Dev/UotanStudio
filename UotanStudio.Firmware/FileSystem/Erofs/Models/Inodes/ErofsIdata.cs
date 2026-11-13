namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Addressing state carried by a unified inode's <c>u</c> field: exactly one of the flat block
/// address, the compressed block count or the chunk-based format plus index table
/// (<c>struct erofs_inode.u</c>).
/// <para>统一 inode 的 <c>u</c> 字段携带的寻址状态：平坦块地址、压缩块数量或
/// chunk-based 格式加索引表三者恰取其一（<c>struct erofs_inode.u</c>）。</para>
/// </summary>
/// <remarks>
/// The cases are positional: the first is the flat/inline state, the second the compressed state and
/// the third the chunk-based state. The union default carries no state; every inode assigns a case in
/// its initializer or during layout planning, so readers always pattern-match a live case.
/// <para>分支按位置排列：第一个是平坦/内联状态，第二个是压缩状态，第三个是 chunk-based 状态。
/// 联合的默认值不携带状态；每个 inode 都在初始化器或布局规划中赋了分支，读取方总能匹配到有效分支。</para>
/// </remarks>
internal union ErofsIdata(ErofsFlatIdata, ErofsCompressedIdata, ErofsChunkIdata);