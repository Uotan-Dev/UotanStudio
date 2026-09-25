namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Writer;

/// <summary>
/// Selects the output shape of <see cref="DatWriter"/>: optional Brotli compression and an optional cap on
/// the blocks one <c>new</c> command may cover.
/// <para>决定 <see cref="DatWriter"/> 的输出形态：可选的 Brotli 压缩，以及单条 <c>new</c> 命令可覆盖块数的上限。</para>
/// </summary>
public sealed class DatWriteOptions
{
    /// <summary>Requests a Brotli-compressed data file instead of a plain one.
    /// <para>请求输出 Brotli 压缩后的数据文件而非未压缩文件。</para></summary>
    /// <value>
    /// When <see langword="true"/>, <see cref="DatWriter"/> writes <c>.new.dat.br</c> next to the transfer list
    /// and deletes the uncompressed <c>.new.dat</c>; the plain file is kept when <see langword="false"/>.
    /// The default is <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时，<see cref="DatWriter"/> 写出 <c>.new.dat.br</c> 并删除未压缩的
    /// <c>.new.dat</c>；为 <see langword="false"/> 时保留未压缩文件。默认值为 <see langword="false"/>。</para>
    /// </value>
    public bool CompressWithBrotli { get; set; }

    /// <summary>Upper bound on the number of blocks one <c>new</c> command may list.
    /// <para>单条 <c>new</c> 命令可列出的块数上限。</para></summary>
    /// <value>
    /// A positive value splits the merged non-zero ranges into several <c>new</c> commands of at most this many
    /// blocks; <c>0</c> or a negative value emits every range in a single command. The default is <c>0</c>.
    /// <para>正数会把合并后的非零范围拆成每条至多这么多块的若干 <c>new</c> 命令；<c>0</c> 或负数则用单条命令
    /// 列出全部范围。默认值为 <c>0</c>。</para>
    /// </value>
    public int MaxCommandRangeBlocks { get; set; }
}