namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>One explicit XML download-agent operation on a named physical region.
/// <para>针对命名物理区域的一次显式 XML 下载代理操作。</para></summary>
/// <param name="RegionName">The discovered XML region token, excluding authenticated regions.
/// <para>已发现的 XML 区域标记，不含需认证区域。</para></param>
/// <param name="Kind">Raw write or erase; the XML agent has no sparse transport.
/// <para>原始写入或擦除；XML 代理没有稀疏传输。</para></param>
/// <param name="Offset">Region-relative byte offset aligned to the reported block size.
/// <para>按上报块大小对齐的区域相对字节偏移。</para></param>
/// <param name="Length">Positive byte count aligned to the reported block size; never padded implicitly.
/// <para>按上报块大小对齐的正数字节数；绝不隐式填充。</para></param>
/// <param name="SourcePath">Image path for a raw step; absent for erase.
/// <para>原始步骤的镜像路径；擦除步骤不需要。</para></param>
/// <param name="SourceOffset">Raw source offset; zero for erase.<para>原始源偏移；擦除时为零。</para></param>
/// <param name="Verify">Whether the written extent requires readback comparison.
/// <para>写出的范围是否需要回读比对。</para></param>
/// <param name="PadSourceTail">Whether a source shorter than the extent is explicitly zero-filled to that extent; without it a short source is refused.
/// <para>是否在源短于该范围时显式补零至该范围；不开启时短源会被拒绝。</para></param>
public sealed record MtkXmlFlashStep(string RegionName, MtkFlashStepKind Kind, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false, bool PadSourceTail = false);

/// <summary>One explicit XML operation addressed by the partition name in the region's GPT.
/// <para>按区域 GPT 中的分区名寻址的一次显式 XML 操作。</para></summary>
/// <param name="PartitionName">Exact case-sensitive GPT partition name.
/// <para>精确、区分大小写的 GPT 分区名。</para></param>
/// <param name="Kind">Raw write or erase.<para>原始写入或擦除。</para></param>
/// <param name="Offset">Byte offset relative to the GPT partition.
/// <para>相对于 GPT 分区的字节偏移。</para></param>
/// <param name="Length">Positive byte count; the agent never pads implicitly.
/// <para>正数字节数；代理不会隐式填充。</para></param>
/// <param name="SourcePath">Image path for a raw step; absent for erase.
/// <para>原始步骤的镜像路径；擦除步骤不需要。</para></param>
/// <param name="SourceOffset">Raw source offset; zero for erase.<para>原始源偏移；擦除时为零。</para></param>
/// <param name="Verify">Whether the written extent requires readback comparison.
/// <para>写出的范围是否需要回读比对。</para></param>
/// <param name="PadSourceTail">Whether a source shorter than the extent is explicitly zero-filled.
/// <para>是否在源短于该范围时显式补零。</para></param>
public sealed record MtkXmlPartitionFlashStep(string PartitionName, MtkFlashStepKind Kind, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false, bool PadSourceTail = false);

/// <summary>A validated XML extent after preparation.
/// <para>准备完成后的已校验 XML 范围。</para></summary>
/// <param name="StepIndex">Zero-based index of the caller step that produced this extent.
/// <para>产生该范围的调用方步骤索引，从零开始。</para></param>
/// <param name="RegionName">The discovered XML region token. <para>已发现的 XML 区域标记。</para></param>
/// <param name="Offset">Region-relative byte offset. <para>区域相对的字节偏移。</para></param>
/// <param name="Length">Byte count. <para>字节数。</para></param>
/// <param name="IsErase">Whether the extent erases instead of writing.
/// <para>该范围是擦除而非写入。</para></param>
/// <param name="Verify">Whether the extent requires readback comparison.
/// <para>该范围是否需要回读比对。</para></param>
public sealed record MtkXmlFlashOperation(int StepIndex, string RegionName, long Offset, long Length, bool IsErase,
    bool Verify);

/// <summary>
/// A bounded, preflighted one-shot plan for the MediaTek XML download agent.
/// <para>面向联发科 XML 下载代理的有界、已预检的一次性计划。</para>
/// </summary>
/// <remarks>
/// Like the Legacy plan and unlike the XFlash plan, this type has no sparse extents: the XML agent
/// transfers only raw block-aligned byte ranges, so an image that requires sparse expansion is rejected
/// rather than silently written as raw data. Preparation validates every range against the discovered
/// geometry, opens and retains each source, and never erases or pads on its own. When the plan was built
/// from a GPT observation, execution re-reads that table under the same session lease and refuses to start
/// if the metadata changed.
/// <para>与 Legacy 计划相同、与 XFlash 计划不同，本类型没有稀疏范围：XML 代理只传输按块对齐的原始字节范围，
/// 因此需要稀疏展开的镜像会被拒绝，而不是被当作原始数据静默写入。准备阶段依据已发现的几何信息校验每个范围、
/// 打开并持有每个源，且从不自行擦除或填充。若计划由 GPT 观测结果构建，执行时会在同一会话租约下重新读取该表，
/// 元数据变化则拒绝开始。</para>
/// </remarks>
public sealed class MtkXmlFlashPlan : IDisposable
{
    private readonly List<PreparedXmlOperation> _prepared = [];
    private readonly List<Stream> _sources = [];
    private readonly Lock _lifecycleGate = new();
    private MtkFlashPlanState _state;
    private bool _disposed;

    private MtkXmlFlashPlan(MtkXmlStorageInfo storage, MtkXmlGptSnapshot? snapshot)
    {
        Storage = storage;
        GptSnapshot = snapshot;
    }

    /// <summary>Gets the exact discovered XML geometry used to validate this plan.
    /// <para>获取用于校验本计划的、精确的已发现 XML 几何信息。</para></summary>
    /// <value>The geometry every range was validated against.
    /// <para>每个范围校验时所依据的几何信息。</para></value>
    public MtkXmlStorageInfo Storage { get; }

    /// <summary>Gets the GPT observation the plan was derived from, when name-addressed.
    /// <para>获取计划所依据的 GPT 观测结果（按分区名寻址时）。</para></summary>
    /// <value>The snapshot, or <see langword="null"/> for explicitly ranged steps.
    /// <para>快照；显式范围步骤为 <see langword="null"/>。</para></value>
    public MtkXmlGptSnapshot? GptSnapshot { get; }

    /// <summary>Gets the read-only executable extents after complete preparation.
    /// <para>获取完整准备之后的只读可执行范围。</para></summary>
    /// <value>One entry per extent, fixed once preparation succeeds.
    /// <para>每个范围对应一项；准备成功后即固定。</para></value>
    public IReadOnlyList<MtkXmlFlashOperation> Operations { get; private set; } =
        [];

    /// <summary>Gets the checked sum of planned write and erase bytes.
    /// <para>获取计划写入与擦除字节的防溢出累加和。</para></summary>
    /// <value>The total planned device traffic, excluding readback.
    /// <para>计划的设备总传输量，不含回读。</para></value>
    public long TotalBytes { get; private set; }

    /// <summary>Gets the current one-shot lifecycle state.
    /// <para>获取当前的单次生命周期状态。</para></summary>
    /// <value>The state sampled under the lifecycle gate.
    /// <para>在生命周期锁下采样的状态。</para></value>
    public MtkFlashPlanState State
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _state;
            }
        }
    }

    /// <summary>Validates explicitly ranged steps and opens their sources without device commands.
    /// <para>校验显式范围的步骤并打开其源文件，不发送设备命令。</para></summary>
    /// <param name="storage">Discovered XML geometry for the intended session.
    /// <para>目标会话对应的已发现 XML 几何信息。</para></param>
    /// <param name="steps">At most 256 explicitly ordered steps; no hidden erase or padding is added.
    /// <para>最多 256 个显式排序的步骤；不会添加隐藏的擦除或填充。</para></param>
    /// <param name="cancellationToken">The preflight cancellation token.<para>预检取消令牌。</para></param>
    /// <returns>A plan owning every opened source.<para>持有每个已打开源的计划。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> or <paramref name="steps"/> is null, or a raw step has a null <c>SourcePath</c>.
    /// <para><paramref name="storage"/> 或 <paramref name="steps"/> 为 null，或原始步骤的 <c>SourcePath</c> 为 null。</para></exception>
    /// <exception cref="ArgumentException">A step is null, a step names no region, or an erase step carries a source or verification.
    /// <para>某个步骤为 null、步骤未命名区域，或擦除步骤携带源或校验要求。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The step count, operation kind, offset or length is invalid.
    /// <para>步骤数量、操作类型、偏移或长度无效。</para></exception>
    /// <exception cref="NotSupportedException">A step names a region reserved for authenticated access.
    /// <para>某个步骤指向为认证访问保留的区域。</para></exception>
    /// <exception cref="InvalidDataException">A raw source is shorter than its selected range.
    /// <para>原始源短于其所选范围。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested during preflight.
    /// <para>预检过程中请求了取消。</para></exception>
    public static MtkXmlFlashPlan Prepare(MtkXmlStorageInfo storage,
        IReadOnlyList<MtkXmlFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps.Count, 256);
        var plan = new MtkXmlFlashPlan(storage, null);
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index] ?? throw new ArgumentException("An XML flash step is null.", nameof(steps));
                plan.Add(index, step.RegionName, step.Kind, step.Offset, step.Length, step.SourcePath,
                    step.SourceOffset, step.Verify, step.PadSourceTail);
            }

            plan.Finish();
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    /// <summary>Reconciles name-addressed steps with a GPT observation before opening any source.
    /// <para>在打开任何源之前，将按分区名寻址的步骤与 GPT 观测结果核对。</para></summary>
    /// <param name="snapshot">A validated XML GPT observation from the intended session.
    /// <para>来自目标会话、已校验的 XML GPT 观测结果。</para></param>
    /// <param name="steps">At most 256 explicitly ordered steps.<para>最多 256 个显式排序的步骤。</para></param>
    /// <param name="cancellationToken">The preflight cancellation token.<para>预检取消令牌。</para></param>
    /// <returns>A plan that revalidates the same GPT table when it executes.
    /// <para>执行时会重新校验同一张 GPT 表的计划。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="steps"/> is null.
    /// <para><paramref name="snapshot"/> 或 <paramref name="steps"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">A step is null or its partition name is blank.
    /// <para>某个步骤为 null，或其分区名为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The step count is invalid or an extent leaves its partition.
    /// <para>步骤数量无效，或某个范围越出其分区。</para></exception>
    /// <exception cref="KeyNotFoundException">A selected partition name is absent from the GPT table.
    /// <para>GPT 表中不存在某个选定的分区名。</para></exception>
    /// <exception cref="InvalidDataException">A raw source is shorter than its selected range.
    /// <para>原始源短于其所选范围。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested during preflight.
    /// <para>预检过程中请求了取消。</para></exception>
    public static MtkXmlFlashPlan Prepare(MtkXmlGptSnapshot snapshot,
        IReadOnlyList<MtkXmlPartitionFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps.Count, 256);
        var plan = new MtkXmlFlashPlan(snapshot.Storage, snapshot);
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index] ?? throw new ArgumentException("An XML flash step is null.", nameof(steps));
                ArgumentException.ThrowIfNullOrWhiteSpace(step.PartitionName);
                var partition = snapshot.Table.FindPartition(step.PartitionName);
                if (step.Offset < 0 || step.Length <= 0 || step.Offset + step.Length > partition.ByteLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(steps),
                        "The selected extent leaves the bounds of the named GPT partition.");
                }

                plan.Add(index, snapshot.RegionName, step.Kind,
                    checked(partition.ByteOffset + step.Offset), step.Length, step.SourcePath, step.SourceOffset,
                    step.Verify, step.PadSourceTail);
            }

            plan.Finish();
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    /// <summary>Disposes every retained source handle.
    /// <para>释放所有持有的源句柄。</para></summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_state == MtkFlashPlanState.Running)
            {
                throw new InvalidOperationException("A running XML flash plan cannot be disposed.");
            }

            _disposed = true;
        }

        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    internal IReadOnlyList<PreparedXmlOperation> BeginExecution()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MtkFlashPlanState.Prepared)
            {
                throw new InvalidOperationException("An XML flash plan can be executed only once.");
            }

            _state = MtkFlashPlanState.Running;
            return _prepared;
        }
    }

    internal void FinishExecution(bool succeeded)
    {
        lock (_lifecycleGate)
        {
            _state = succeeded ? MtkFlashPlanState.Completed : MtkFlashPlanState.Failed;
        }
    }

    private void Add(int index, string? regionName, MtkFlashStepKind kind, long offset, long length,
        string? sourcePath, long sourceOffset, bool verify, bool padSourceTail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        if (!Enum.IsDefined(kind) || kind == MtkFlashStepKind.Sparse)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                "The XML download agent transfers raw ranges only; sparse expansion is unsupported.");
        }

        Storage.ValidateRange(regionName, offset, length);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        if (kind == MtkFlashStepKind.Erase)
        {
            if (sourcePath is not null || sourceOffset != 0 || verify)
            {
                throw new ArgumentException(
                    "XML erase steps cannot carry a source or claim zero-fill verification.", nameof(sourcePath));
            }

            Record(index, regionName, offset, length, null, 0, erase: true, verify: false);
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (sourceOffset > file.Length)
        {
            file.Dispose();
            throw new InvalidDataException("The raw source offset is beyond the end of the image.");
        }

        if (length <= file.Length - sourceOffset)
        {
            _sources.Add(file);
            Record(index, regionName, offset, length, file, sourceOffset, erase: false, verify);
            return;
        }

        if (!padSourceTail)
        {
            file.Dispose();
            throw new InvalidDataException("The raw image is shorter than the selected source range.");
        }

        var dataLength = Math.Min(length, file.Length - sourceOffset);
        var padded = new MtkLegacyFlashPlan.TailPaddedStream(file, sourceOffset, dataLength, length);
        _sources.Add(padded);
        Record(index, regionName, offset, length, padded, 0, erase: false, verify);
    }

    private void Record(int index, string regionName, long offset, long length, Stream? source, long sourceOffset,
        bool erase, bool verify)
    {
        if (_prepared.Count >= 65536)
        {
            throw new InvalidDataException("The plan exceeds the aggregate executable-extent limit.");
        }

        TotalBytes = checked(TotalBytes + length);
        _prepared.Add(new PreparedXmlOperation(
            new MtkXmlFlashOperation(index, regionName, offset, length, erase, verify), source, sourceOffset));
    }

    private void Finish() => Operations =
        Array.AsReadOnly([.. _prepared.Select(operation => operation.Description)]);

    internal sealed record PreparedXmlOperation(MtkXmlFlashOperation Description, Stream? Source,
        long SourceOffset);
}