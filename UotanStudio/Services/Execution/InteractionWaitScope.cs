namespace UotanStudio.Services.Execution;

/// <summary>Ends a user wait exactly once, including exceptional completion.</summary>
internal sealed class InteractionWaitScope(Action? onEnd) : IDisposable
{
    private Action? _onEnd = onEnd;

    public void Dispose()
    {
        Interlocked.Exchange(ref _onEnd, null)?.Invoke();
    }
}