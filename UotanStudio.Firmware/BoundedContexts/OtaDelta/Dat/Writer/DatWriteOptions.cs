namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Writer;

/// <summary>
/// Selects which command the incremental writer uses for a run of differing non-zero blocks, mirroring the
/// AOSP blockimgdiff vocabulary but conservative: only correctness-equivalent choices are offered.
/// <para>决定增量写手对差异非零块区段使用哪种命令，对应 AOSP blockimgdiff 词汇表但保持保守：仅提供正确性等价的选项。</para>
/// </summary>
public enum DatIncrementalCommandStyle
{
    /// <summary>
    /// Wrap every run's bsdiff bytes in an IMGDIFF2 container and emit <c>imgdiff</c> commands (the default and
    /// previously the only behavior). AOSP reserves <c>imgdiff</c> for intact APK/JAR/ZIP images; recovery
    /// updaters accept the container for any block range, so this stays correct for raw partitions.
    /// <para>把每个区段的 bsdiff 字节包进 IMGDIFF2 容器并发射 <c>imgdiff</c> 命令（默认值，也是此前唯一行为）。
    /// AOSP 将 <c>imgdiff</c> 保留给完整 APK/JAR/ZIP 镜像；recovery 更新器对任意块区间都接受该容器，故对原始分区依然正确。</para>
    /// </summary>
    Imgdiff,

    /// <summary>
    /// Emit positional <c>bsdiff</c> commands whose patch bytes are bare BSDIFF1 streams, the choice blockimgdiff
    /// makes for non-archive targets.
    /// <para>发射位置式 <c>bsdiff</c> 命令，其补丁字节为裸 BSDIFF1 流，即 blockimgdiff 对非归档目标的默认选择。</para>
    /// </summary>
    Bsdiff,

    /// <summary>
    /// Compute both encodings per run and keep the shorter one, so archive-like runs gain the IMGDIFF2 RAW-tail
    /// layout while plain runs avoid its header overhead.
    /// <para>对每个区段同时计算两种编码并保留较短者：归档样区段可受益于 IMGDIFF2 的 RAW 尾部布局，普通区段则避免其头部开销。</para>
    /// </summary>
    ShortestWins
}

/// <summary>
/// Selects the output shape of <see cref="DatWriter"/>: optional Brotli compression, an optional cap on
/// the blocks one <c>new</c> command may cover, and the incremental command-generation settings.
/// <para>决定 <see cref="DatWriter"/> 的输出形态：可选的 Brotli 压缩、单条 <c>new</c> 命令可覆盖块数的上限，以及增量命令生成设置。</para>
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

    /// <summary>Command style used for differing non-zero runs in <see cref="DatWriter.WriteIncremental"/>.
    /// <para><see cref="DatWriter.WriteIncremental"/> 中差异非零区段使用的命令风格。</para></summary>
    /// <value>
    /// Defaults to <see cref="DatIncrementalCommandStyle.Imgdiff"/>, the historical behavior.
    /// <para>默认 <see cref="DatIncrementalCommandStyle.Imgdiff"/>，即历史行为。</para>
    /// </value>
    public DatIncrementalCommandStyle IncrementalCommandStyle { get; set; } = DatIncrementalCommandStyle.Imgdiff;

    /// <summary>Allows a differing run to be emitted as a raw <c>new</c> command when the run's bytes are no
    /// larger than its smallest patch. <para>当区段字节数不大于其最短补丁时，允许把该区段发射为原始 <c>new</c> 命令。</para></summary>
    /// <value>
    /// <see langword="true"/> lets very short or incompressible runs skip patching entirely; the run's target
    /// bytes then stream into <c>.new.dat</c>. The default is <see langword="false"/>, matching AOSP
    /// blockimgdiff, which never re-emits block-diffable data as new.
    /// <para>为 <see langword="true"/> 时，极短或不可压的区段可完全跳过差分，其目标字节流入 <c>.new.dat</c>。
    /// 默认 <see langword="false"/>，与 AOSP blockimgdiff 一致——它从不把可差分数据重发为 new。</para>
    /// </value>
    public bool AllowRawNewCommands { get; set; }

    /// <summary>Replays the generated triple through <c>DatReader</c> after writing and throws unless the
    /// rebuild equals the target image. <para>写出后用 <c>DatReader</c> 回放生成的三元组，重建结果与目标不一致即抛出。</para></summary>
    /// <value>
    /// <see langword="true"/> (the default) makes <see cref="DatWriter.WriteIncremental"/> and
    /// <c>DatWriter.Write</c> self-verifying: generation implies a full apply-side check, so a writer bug
    /// cannot silently publish an unrebuildable dataset. Set to <see langword="false"/> only when the caller
    /// performs its own external verification pass.
    /// <para>默认 <see langword="true"/>：<see cref="DatWriter.WriteIncremental"/> 与
    /// <c>DatWriter.Write</c> 自带校验——生成即意味完成 apply 端全量校验，写手缺陷不会悄然发布不可重建的数据集。
    /// 仅当调用方自行执行外部校验时才设为 <see langword="false"/>。</para>
    /// </value>
    public bool SelfVerifyIncremental { get; set; } = true;
}