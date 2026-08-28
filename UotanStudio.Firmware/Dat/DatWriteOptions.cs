namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Options for the enhanced <see cref="DatWriter"/> overload that adds Brotli compression and range splitting.
/// <para>增强型 <see cref="DatWriter"/> 重载的选项，用于新增 Brotli 压缩与范围拆分能力。</para>
/// </summary>
public sealed class DatWriteOptions
{
    /// <summary>
    /// Whether to Brotli-compress the written <c>.new.dat</c> into a <c>.new.dat.br</c> file and delete the
    /// uncompressed <c>.new.dat</c>. The transfer list content is unchanged — Android natively decompresses
    /// <c>.br</c> files automatically. Defaults to <c>false</c>.
    /// <para>是否将写出的 <c>.new.dat</c> 用 Brotli 压缩为 <c>.new.dat.br</c> 并删除未压缩的 <c>.new.dat</c>。
    /// transfer list 内容不变——Android 原生支持 <c>.br</c> 自动解压。默认为 <c>false</c>。</para>
    /// </summary>
    public bool CompressWithBrotli { get; set; }

    /// <summary>
    /// Maximum number of blocks per range group in a single <c>new</c> command; <c>0</c> (default) means no
    /// splitting. When greater than zero, the merged <c>new</c> ranges are split into multiple <c>new</c>
    /// commands so that every command's total range-set block count stays within this limit, while keeping the
    /// v4 <c>count,start,end,...</c> syntax valid and the <c>.new.dat</c> byte layout unchanged.
    /// <para>单条 <c>new</c> 命令每个范围组的最大块数；<c>0</c>（默认）表示不拆分。大于 0 时，合并后的
    /// <c>new</c> 范围会被拆分为多条 <c>new</c> 命令，使每条命令范围集的总块数不超过该上限，
    /// 同时保持 v4 的 <c>count,start,end,...</c> 语法合法且 <c>.new.dat</c> 字节布局不变。</para>
    /// </summary>
    public int MaxCommandRangeBlocks { get; set; }
}