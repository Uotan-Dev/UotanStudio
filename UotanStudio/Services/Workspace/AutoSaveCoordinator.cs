namespace UotanStudio.Services.Workspace;

internal sealed class AutoSaveCoordinator(Func<Task> save)
{
    private Task _pending = Task.CompletedTask;
    internal bool Enabled { get; set; }

    internal Task WaitForIdleAsync() => _pending;

    internal Task TickAsync()
    {
        if (!Enabled || !_pending.IsCompleted) return _pending;
        return _pending = save();
    }

    internal async Task FlushAsync()
    {
        await _pending;
        if (Enabled) await TickAsync();
    }
}