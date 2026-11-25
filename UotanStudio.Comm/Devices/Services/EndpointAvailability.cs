namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// A monotonic generation counter for endpoint releases. Callers rejected with
/// <see cref="DeviceEndpointBusyException"/> capture the current generation and wait for it to change; a release
/// always advances the generation and completes every waiter, so a release can never be lost between the rejected
/// open and the wait registration. No controller state is held while a waiter is blocked.
/// <para>端点释放的单调代数计数器。被 <see cref="DeviceEndpointBusyException"/> 拒绝的调用方记录当前代数并等待
/// 其变化；释放总会推进代数并完成所有等待方，因此拒绝开租约与注册等待之间的释放不会丢失。等待方阻塞期间不持有
/// 控制器状态。</para>
/// </summary>
internal sealed class EndpointAvailability
{
    private readonly Lock _gate = new();
    private long _generation;
    private TaskCompletionSource? _waiter;

    /// <summary>
    /// Captures the generation a waiter must observe changing.
    /// <para>记录等待方必须观察其变化的代数。</para>
    /// </summary>
    public long CurrentGeneration
    {
        get { lock (_gate) { return _generation; } }
    }

    /// <summary>
    /// Blocks until the generation differs from <paramref name="observedGeneration"/> or the budget expires.
    /// The check and waiter registration are one critical section, so a concurrent release is either already
    /// reflected or completes the registered waiter. The underlying task is a bare completion signal with
    /// asynchronous continuations, so blocking on it cannot deadlock on a synchronisation context.
    /// <para>阻塞直到代数变化或预算耗尽。检查与等待注册在同一临界区，并发释放要么已被观察到，要么会完成已注册的
    /// 等待方。底层任务是带异步继续的纯完成信号，因此阻塞等待不会因同步上下文而死锁。</para>
    /// </summary>
    /// <param name="observedGeneration">Generation captured before waiting.
    /// <para>等待前记录的代数。</para></param>
    /// <param name="timeout">Maximum wait; a nonpositive value checks once and returns.
    /// <para>最长等待时间；非正值只检查一次即返回。</para></param>
    /// <returns><see langword="true"/> when a release was observed.
    /// <para>观察到释放时为 <see langword="true"/>。</para></returns>
    public bool WaitForChange(long observedGeneration, TimeSpan timeout)
    {
        Task wait;
        lock (_gate)
        {
            if (_generation != observedGeneration)
            {
                return true;
            }

            wait = (_waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        return timeout > TimeSpan.Zero && wait.Wait(timeout);
    }

    /// <summary>
    /// Advances the generation and completes every registered waiter. Called after the reservation has actually
    /// been returned, never while the controller gate is held.
    /// <para>推进代数并完成所有已注册的等待方。仅在占用确实归还后调用，绝不在持有控制器锁期间调用。</para>
    /// </summary>
    public void SignalRelease()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _generation++;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }
}