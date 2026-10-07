using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit operations supported by an XFlash plan. <para>XFlash 计划支持的显式操作。</para></summary>
public enum MtkFlashStepKind
{
    /// <summary>Write an exact raw source range. <para>写入精确的原始源范围。</para></summary>
    Raw,
    /// <summary>
    /// Write RAW/FILL sparse extents while preserving DONT_CARE ranges.
    /// <para>写入 RAW/FILL 稀疏范围，同时保留 DONT_CARE 范围。</para>
    /// </summary>
    Sparse,
    /// <summary>Request device-side erase for the exact range. <para>请求设备端擦除该精确范围。</para></summary>
    Erase
}

/// <summary>
/// One explicitly selected operation with byte-based addressing.
/// <para>一项显式选择、按字节寻址的操作。</para>
/// </summary>
/// <param name="Kind">
/// The operation type; image type is not guessed from a filename.
/// <para>操作类型；不会依据文件名猜测镜像类型。</para>
/// </param>
/// <param name="PartitionId">
/// The discovered XFlash physical partition identifier.
/// <para>已发现的 XFlash 物理分区标识符。</para>
/// </param>
/// <param name="Offset">The target byte offset. <para>目标字节偏移。</para></param>
/// <param name="Length">
/// The raw/erase byte count or sparse target capacity.
/// <para>原始/擦除字节数，或稀疏目标容量。</para>
/// </param>
/// <param name="SourcePath">
/// The raw/sparse source path; null for erase.
/// <para>原始/稀疏源路径；擦除时为 null。</para>
/// </param>
/// <param name="SourceOffset">
/// The raw source byte offset; must be zero for sparse/erase.
/// <para>原始源字节偏移；稀疏/擦除时必须为零。</para>
/// </param>
/// <param name="Verify">
/// Whether written extents must be read back; invalid for erase.
/// <para>写出的范围是否必须回读；对擦除无效。</para>
/// </param>
public sealed record MtkFlashStep(MtkFlashStepKind Kind, uint PartitionId, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

/// <summary>
/// A fully preflighted physical operation; sparse holes do not appear in this list.
/// <para>已完成预检的物理操作；稀疏空洞不会出现在此列表中。</para>
/// </summary>
/// <param name="StepIndex">The zero-based originating step index. <para>来源步骤的从零开始的索引。</para></param>
/// <param name="PartitionId">The XFlash physical partition identifier. <para>XFlash 物理分区标识符。</para></param>
/// <param name="Offset">The target byte offset. <para>目标字节偏移。</para></param>
/// <param name="Length">The exact byte count. <para>精确字节数。</para></param>
/// <param name="IsErase">Whether the operation erases rather than writes. <para>该操作是擦除而非写入。</para></param>
/// <param name="Verify">Whether a completed write requires readback. <para>已完成的写入是否需要回读。</para></param>
public sealed record MtkFlashOperation(int StepIndex, uint PartitionId, long Offset, long Length, bool IsErase, bool Verify);

/// <summary>
/// One-shot lifecycle for a retained flash plan.
/// <para>已保留刷写计划的单次生命周期。</para>
/// </summary>
public enum MtkFlashPlanState
{
    /// <summary>Prepared and not yet started. <para>已准备且尚未开始。</para></summary>
    Prepared,
    /// <summary>
    /// Owned by an executing session; disposal is rejected.
    /// <para>由正在执行的会话持有；拒绝释放请求。</para>
    /// </summary>
    Running,
    /// <summary>
    /// Every operation and requested readback completed.
    /// <para>所有操作及请求的回读均已完成。</para>
    /// </summary>
    Completed,
    /// <summary>
    /// Execution failed or was cancelled after starting; the plan cannot be replayed.
    /// <para>开始执行后失败或被取消；该计划无法重放。</para>
    /// </summary>
    Failed,
    /// <summary>Retained sources have been released. <para>已保留的源已释放。</para></summary>
    Disposed
}

/// <summary>
/// A bounded preflight result retaining all selected image handles. Preparation performs no
/// device I/O. Dispose after execution or an abandoned preview; source contents are not immutable snapshots.
/// <para>
/// 有界的预检结果，保留全部选定的镜像句柄。准备阶段不执行任何设备 I/O。执行结束或放弃预览后应调用
/// <c>Dispose</c>；源内容并非不可变快照。
/// </para>
/// </summary>
public sealed partial class MtkFlashPlan : IDisposable
{
    private readonly List<Stream> _sources = [];
    private readonly List<PreparedOperation> _prepared = [];
    private bool _disposed;
    private int _sparseChunkCount;
    private readonly Lock _lifecycleGate = new();
    private MtkFlashPlanState _state;

    private MtkFlashPlan(MtkXFlashStorageInfo storage)
    {
        Storage = storage;
        Capability = MtkStorageCapabilitySnapshot.FromXFlash(storage);
    }

    /// <summary>
    /// Gets the exact discovered geometry used to validate this plan.
    /// <para>获取用于校验本计划的精确发现几何信息。</para>
    /// </summary>
    /// <value>
    /// The storage geometry captured when the plan was created; every range in the plan has already
    /// been validated against it.
    /// <para>创建计划时捕获的存储几何信息；计划中的每个范围均已依据它完成校验。</para>
    /// </value>
    public MtkXFlashStorageInfo Storage { get; }
    /// <summary>
    /// Gets the unified storage capability snapshot this plan was prepared against.
    /// <para>获取本计划准备时所依据的统一存储能力快照。</para>
    /// </summary>
    /// <value>
    /// The immutable projection of <see cref="Storage"/> captured at preparation time; the session
    /// rejects execution when its current capability no longer describes the same geometry.
    /// <para>准备阶段对 <see cref="Storage"/> 的不可变投影；当会话当前能力不再描述相同几何信息时，执行会被拒绝。</para>
    /// </value>
    public MtkStorageCapabilitySnapshot Capability { get; }
    /// <summary>
    /// Gets the read-only executable ranges after complete preparation.
    /// <para>获取完整准备之后的只读可执行范围。</para>
    /// </summary>
    /// <value>
    /// One entry per physical extent with sparse DONT_CARE holes removed; fixed once preparation
    /// succeeds and never mutated during execution.
    /// <para>每个物理范围对应一项，稀疏 DONT_CARE 空洞已剔除；准备成功后即固定，执行期间保持恒定。</para>
    /// </value>
    public IReadOnlyList<MtkFlashOperation> Operations { get; private set; } = [];
    /// <summary>
    /// Gets the checked sum of actual write/erase bytes, excluding readback and sparse holes.
    /// <para>获取实际写入/擦除字节的防溢出累加和，不含回读与稀疏空洞。</para>
    /// </summary>
    /// <value>
    /// The total planned device traffic, accumulated with checked arithmetic across all extents.
    /// <para>计划的设备总传输量，由所有范围经 checked 算术累加得到。</para>
    /// </value>
    public long TotalBytes { get; private set; }
    /// <summary>
    /// Gets the current one-shot lifecycle state.
    /// <para>获取当前的单次生命周期状态。</para>
    /// </summary>
    /// <value>
    /// The state sampled under the lifecycle gate; a plan in <see cref="MtkFlashPlanState.Running"/>
    /// rejects disposal.
    /// <para>在生命周期锁下采样的状态；处于 <see cref="MtkFlashPlanState.Running"/> 的计划会拒绝释放。</para>
    /// </value>
    public MtkFlashPlanState State { get { lock (_lifecycleGate) { return _state; } } }

    internal IReadOnlyList<PreparedOperation> BeginExecution()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MtkFlashPlanState.Prepared)
            {
                throw new InvalidOperationException("A flash plan can be executed only once.");
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

    internal IReadOnlyList<PreparedOperation> Prepared
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _prepared;
        }
    }

    /// <summary>
    /// Validates all steps, opens their sources and expands bounded sparse extents without device commands.
    /// <para>校验所有步骤、打开其源文件，并在不发送设备命令的前提下展开有界的稀疏范围。</para>
    /// </summary>
    /// <param name="storage">
    /// Validated device geometry for the intended session.
    /// <para>目标会话对应的、已校验的设备几何信息。</para>
    /// </param>
    /// <param name="steps">
    /// At most 256 explicitly ordered steps; no hidden erase or padding is added.
    /// <para>最多 256 个显式排序的步骤；不会添加隐藏的擦除或填充。</para>
    /// </param>
    /// <param name="cancellationToken">The preflight cancellation token. <para>预检取消令牌。</para></param>
    /// <returns>
    /// An owner of source handles and at most 65536 executable extents.
    /// <para>源句柄的持有者，最多包含 65536 个可执行范围。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A step is null, an erase step carries a source, a nonzero source offset, or verification, or
    /// a raw/sparse step has a blank <c>SourcePath</c>.
    /// <para>某步骤为 null、擦除步骤携带源、非零源偏移或校验要求，或原始/稀疏步骤的 <c>SourcePath</c> 为空白。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The step count is outside one to 256, an operation type is unsupported, or a source offset is
    /// negative.
    /// <para>步骤数量不在 1 到 256 之间、操作类型不受支持，或源偏移为负。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="storage"/> or <paramref name="steps"/> is null, or a raw/sparse step has a null
    /// <c>SourcePath</c>.
    /// <para><paramref name="storage"/> 或 <paramref name="steps"/> 为 null，或原始/稀疏步骤的 <c>SourcePath</c> 为 null。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A source is shorter than its selected range, a sparse image is misaligned, has a nonzero
    /// source offset or trailing bytes, or an aggregate chunk or extent limit is exceeded. Every
    /// already-opened handle is released before the exception propagates.
    /// <para>源短于所选范围、稀疏镜像未对齐、源偏移非零或存在尾部字节，或超出聚合的块/范围数量上限。
    /// 异常向上传播前会释放所有已打开的句柄。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested during the preflight loop.
    /// <para>在预检循环期间请求了取消。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// A computed offset, length, chunk count or byte total exceeds its arithmetic bound.
    /// <para>计算得到的偏移、长度、块数或字节总数超出算术上限。</para>
    /// </exception>
    public static MtkFlashPlan Prepare(MtkXFlashStorageInfo storage, IReadOnlyList<MtkFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A flash plan requires between one and 256 steps.");
        }
        var snapshot = steps.ToArray();
        var plan = new MtkFlashPlan(storage);
        try
        {
            for (var index = 0; index < snapshot.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = snapshot[index] ?? throw new ArgumentException("A flash step is null.", nameof(steps));
                if (!Enum.IsDefined(step.Kind))
                {
                    throw new ArgumentOutOfRangeException(nameof(steps), "An operation type is unsupported.");
                }
                storage.ValidateRange(step.PartitionId, step.Offset, step.Length);
                ArgumentOutOfRangeException.ThrowIfNegative(step.SourceOffset);
                if (step.Kind == MtkFlashStepKind.Erase)
                {
                    if (step.SourcePath is not null || step.SourceOffset != 0 || step.Verify)
                    {
                        throw new ArgumentException("Erase steps cannot carry a source or claim zero-fill verification.", nameof(steps));
                    }
                    plan.Add(index, step, step.Offset, step.Length, null, 0);
                    continue;
                }
                ArgumentException.ThrowIfNullOrWhiteSpace(step.SourcePath);
                var source = new FileStream(step.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                plan._sources.Add(source);
                if (step.Kind == MtkFlashStepKind.Raw)
                {
                    if (step.SourceOffset > source.Length || step.Length > source.Length - step.SourceOffset)
                    {
                        throw new InvalidDataException("The raw image is shorter than the selected source range.");
                    }
                    plan.Add(index, step, step.Offset, step.Length, source, step.SourceOffset);
                    continue;
                }
                if (step.SourceOffset != 0)
                {
                    throw new InvalidDataException("Sparse plans do not support raw source offsets.");
                }
                var image = SparseReader.FromStream(source, true, false, 65536, cancellationToken, step.Length);
                var logical = new SparseReadStream(image, requireExactRawData: true);
                plan._sources.Add(logical);
                plan._sparseChunkCount = checked(plan._sparseChunkCount + image.Chunks.Count);
                if (plan._sparseChunkCount > 65536)
                {
                    throw new InvalidDataException("The plan exceeds the aggregate sparse-chunk limit.");
                }
                var logicalLength = checked((long)image.Header.TotalBlocks * image.Header.BlockSize);
                if (source.Position != source.Length || logicalLength <= 0 || logicalLength > step.Length
                    || logicalLength % storage.BlockSize != 0)
                {
                    throw new InvalidDataException("The sparse image has trailing bytes or an invalid target extent.");
                }
                foreach (var chunk in image.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = checked((long)chunk.StartBlock * image.Header.BlockSize);
                    var length = checked((long)chunk.Header.ChunkSize * image.Header.BlockSize);
                    if (offset % storage.BlockSize != 0 || length % storage.BlockSize != 0)
                    {
                        throw new InvalidDataException("Sparse chunk boundaries are not device-block aligned.");
                    }
                    if (chunk.Header.ChunkType == (ushort)ChunkType.DontCare || length == 0)
                    {
                        continue;
                    }
                    plan.Add(index, step, checked(step.Offset + offset), length, logical, offset);
                }
            }
            plan.Operations = Array.AsReadOnly(plan._prepared.Select(operation => operation.Description).ToArray());
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    private void Add(int index, MtkFlashStep step, long offset, long length, Stream? source, long sourceOffset)
    {
        if (_prepared.Count >= 65536)
        {
            throw new InvalidDataException("The plan exceeds the aggregate executable-extent limit.");
        }
        Storage.ValidateRange(step.PartitionId, offset, length);
        TotalBytes = checked(TotalBytes + length);
        _prepared.Add(new PreparedOperation(new MtkFlashOperation(index, step.PartitionId, offset, length,
            step.Kind == MtkFlashStepKind.Erase, step.Verify), source, sourceOffset));
    }

    /// <summary>
    /// Closes retained sources; the plan must not be disposed concurrently with execution.
    /// <para>关闭已保留的源；不得在计划执行期间并发地释放它。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plan is owned by an executing session. A repeated call after a successful disposal has no
    /// further effect.
    /// <para>该计划正被执行中的会话持有。释放成功后再次调用不会产生额外效果。</para>
    /// </exception>
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
                throw new InvalidOperationException("An executing plan cannot be disposed.");
            }
            _disposed = true;
            _state = MtkFlashPlanState.Disposed;
            for (var index = _sources.Count - 1; index >= 0; index--)
            {
                _sources[index].Dispose();
            }
            _sources.Clear();
        }
    }

    internal sealed record PreparedOperation(MtkFlashOperation Description, Stream? Source, long SourceOffset);
}