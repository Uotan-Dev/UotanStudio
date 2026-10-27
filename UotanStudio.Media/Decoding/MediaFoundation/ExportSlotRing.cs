namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// One GPU presentation slot owned by the export ring: a shared keyed-mutex texture with its video
/// processor output view and legacy shared handle.
/// <para>导出环持有的一个 GPU 呈现槽位：带键互锁的共享纹理及其视频处理器输出视图与遗留共享句柄。</para>
/// </summary>
internal sealed class MediaFoundationExportSlot
{
    /// <summary>The shared BGRA texture; released with the slot. <para>共享 BGRA 纹理；随槽位释放。</para></summary>
    internal required nint Texture { get; init; }
    /// <summary>The video processor output view targeting <see cref="Texture"/>. <para>指向 <see cref="Texture"/> 的视频处理器输出视图。</para></summary>
    internal required nint OutputView { get; init; }
    /// <summary>The keyed mutex guarding cross-device access. <para>守护跨设备访问的键互锁。</para></summary>
    internal required nint KeyedMutex { get; init; }
    /// <summary>The process-global shared handle consumed by the compositor. <para>供合成器消费的进程全局共享句柄。</para></summary>
    internal required nint SharedHandle { get; init; }
}

/// <summary>
/// Tracks a fixed ring of presentation slots closed behind a double gate: the producer may rent a slot
/// only when the consumer has reported the previous presentation complete (open gate) and a non-blocking
/// keyed-mutex acquire succeeds. When every slot is gated the producer drops the frame instead of
/// blocking, which matches the newest-frame semantics of live mirroring.
/// <para>跟踪一组以双闸门把关的固定呈现槽位：仅当消费方报告上一次呈现完成（闸门打开）且非阻塞键互锁获取
/// 成功时，生产方才能租用槽位。全部槽位被关闭时生产方丢帧而不阻塞，符合实时投屏的最新帧语义。</para>
/// </summary>
/// <remarks>
/// The gate only schedules; the keyed mutex itself serializes actual GPU access. Renting, closing and
/// opening the gate are safe from any thread. After <see cref="Dispose"/> the gate opens become no-ops,
/// so a presentation completing during teardown cannot resurrect a slot. Opening a gate is idempotent:
/// only the first report after a handoff reopens it.
/// <para>闸门只负责调度；实际的 GPU 访问由键互锁串行化。租用、关闭与打开闸门均可在任意线程调用。
/// <see cref="Dispose"/> 之后打开闸门变为空操作，销毁期间完成的呈现无法复活槽位。
/// 打开闸门是幂等的：一次移交之后只有第一次报告会重新打开它。</para>
/// </remarks>
/// <typeparam name="TSlot">Caller-owned slot state. <para>调用方持有的槽位状态。</para></typeparam>
internal sealed class ExportSlotRing<TSlot> : IDisposable where TSlot : class
{
    private sealed class Entry(TSlot slot)
    {
        public readonly TSlot Slot = slot;
        public bool Open = true;
    }

    private readonly Lock _gateLock = new();
    private readonly List<Entry> _entries = [];
    private readonly Func<int, TSlot> _createSlot;
    private readonly Action<TSlot> _destroySlot;
    private readonly Func<TSlot, bool> _tryAcquire;
    private bool _disposed;
    private long _submitted, _dropped;

    /// <summary>
    /// Creates the ring; <paramref name="createSlot"/> runs once per index before the constructor returns.
    /// <para>创建槽位环；<paramref name="createSlot"/> 在构造函数返回前按索引逐个执行。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A slot creation failed after earlier slots were created; created slots are destroyed before the
    /// exception propagates.
    /// <para>在先前槽位已创建后某个槽位创建失败；已创建的槽位会在异常传播前被销毁。</para>
    /// </exception>
    internal ExportSlotRing(int capacity, Func<int, TSlot> createSlot, Action<TSlot> destroySlot, Func<TSlot, bool> tryAcquire)
    {
        _createSlot = createSlot;
        _destroySlot = destroySlot;
        _tryAcquire = tryAcquire;
        try
        {
            for (var index = 0; index < capacity; index++) _entries.Add(new Entry(createSlot(index)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal long SubmittedFrames => Interlocked.Read(ref _submitted);
    internal long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Rents the first open slot the acquire probe accepts, or <see langword="null"/> (and counts a drop)
    /// when every slot is gated or acquisition fails.
    /// <para>租用第一个探针接受且闸门打开的槽位；全部槽位被关闭或获取失败时返回
    /// <see langword="null"/> 并计一次丢帧。</para>
    /// </summary>
    internal TSlot? Rent()
    {
        lock (_gateLock)
        {
            if (_disposed) return null;
            foreach (Entry entry in _entries)
            {
                if (!entry.Open || !_tryAcquire(entry.Slot)) continue;
                return entry.Slot;
            }
        }
        Interlocked.Increment(ref _dropped);
        return null;
    }

    /// <summary>
    /// Closes the gate after the producer handed the texture off to the consumer; the slot becomes
    /// rentable again once <see cref="MarkPresented(TSlot)"/> reports the compositor update finished.
    /// <para>生产方把纹理移交给消费方后关闭闸门；待 <see cref="MarkPresented(TSlot)"/> 报告合成器更新完成后槽位方可再次租用。</para>
    /// </summary>
    internal void CloseGate(TSlot slot)
    {
        lock (_gateLock)
        {
            if (_disposed) return;
            Find(slot).Open = false;
            Interlocked.Increment(ref _submitted);
        }
    }

    /// <summary>
    /// Opens the gate after the compositor update completed; idempotent per handoff, and a no-op after disposal.
    /// <para>合成器更新完成后打开闸门；每次移交只生效一次，销毁后为空操作。</para>
    /// </summary>
    internal void MarkPresented(TSlot slot) => MarkPresented(slot, open: true);

    /// <summary>
    /// Opens the gate only when <paramref name="open"/> is set, so a late duplicate report cannot reopen a
    /// newer handoff's gate.
    /// <para>仅在 <paramref name="open"/> 为真时打开闸门，迟到的重复报告不会打开更新一次移交的闸门。</para>
    /// </summary>
    internal void MarkPresented(TSlot slot, bool open)
    {
        lock (_gateLock)
        {
            if (_disposed || !open) return;
            Find(slot).Open = true;
        }
    }

    private Entry Find(TSlot slot)
    {
        foreach (Entry entry in _entries)
            if (ReferenceEquals(entry.Slot, slot)) return entry;
        throw new InvalidOperationException("The slot does not belong to this ring.");
    }

    public void Dispose()
    {
        TSlot[] leaked;
        lock (_gateLock)
        {
            if (_disposed) return;
            _disposed = true;
            leaked = new TSlot[_entries.Count];
            for (var index = 0; index < _entries.Count; index++) leaked[index] = _entries[index].Slot;
            _entries.Clear();
        }
        foreach (TSlot slot in leaked) _destroySlot(slot);
    }
}