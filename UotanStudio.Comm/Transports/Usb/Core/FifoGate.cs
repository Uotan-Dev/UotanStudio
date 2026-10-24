namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Serializes work in strict arrival order: every entrant takes the next place in line and runs only when the
/// previous entrant has finished, so a burst of device probes cannot overtake an earlier one or open the same handle
/// twice at once.
/// <para>按严格到达顺序串行化：每个进入者领取下一个排队位置，只有前一个进入者完成后才运行，因此一批设备探测
/// 不会越过更早的探测，也不会同时打开同一个句柄。</para>
/// </summary>
internal sealed class FifoGate
{
    private readonly Lock _sync = new();
    private TaskCompletionSource _tail = Completed();
    private int _pending;

    /// <summary>Entrants currently queued or holding the gate, for diagnostics and orderly tests.
    /// <para>当前排队或持有闸门的进入者数量，用于诊断与可判定的测试。</para>
    /// </summary>
    internal int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending;
            }
        }
    }

    /// <summary>Blocks until every earlier entrant has finished and returns the scope that releases the gate.
    /// <para>阻塞到所有更早的进入者完成后，返回释放闸门的作用域。</para>
    /// </summary>
    /// <returns>The scope to dispose when the serialized work is finished.
    /// <para>串行工作完成时要释放的作用域。</para></returns>
    public Scope Enter()
    {
        Task previous;
        TaskCompletionSource mine;
        lock (_sync)
        {
            previous = _tail.Task;
            mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tail = mine;
            _pending++;
        }

        if (!previous.IsCompleted)
        {
            previous.Wait();
        }

        return new Scope(mine, this);
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>
    /// The gate held by one entrant; disposing it hands the gate to the next ticket.
    /// <para>由一个进入者持有的闸门；释放它即把闸门交给下一个排队者。</para>
    /// </summary>
    public readonly struct Scope(TaskCompletionSource completion, FifoGate gate) : IDisposable
    {
        public void Dispose()
        {
            if (!completion.TrySetResult())
            {
                return;
            }

            lock (gate._sync)
            {
                gate._pending--;
            }
        }
    }
}