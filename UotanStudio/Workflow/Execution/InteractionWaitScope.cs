namespace UotanStudio.Workflow.Execution;

/// <summary>Ends a user wait exactly once, including exceptional completion.<para>恰好结束一次用户等待，含异常完成。</para></summary>
/// <param name="onEnd">Action invoked once on completion.<para>完成时仅调用一次的操作。</para></param>
internal sealed class InteractionWaitScope(Action? onEnd) : IDisposable
{
    private Action? _onEnd = onEnd;

    public void Dispose()
    {
        Interlocked.Exchange(ref _onEnd, null)?.Invoke();
    }
}