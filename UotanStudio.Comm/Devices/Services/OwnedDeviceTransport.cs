namespace UotanStudio.Comm.Devices.Services;

/// <summary>Adapts a synchronous protocol session to the controller's owned lease.</summary>
internal sealed class OwnedDeviceTransport<T>(T value) : IAsyncDisposable where T : IDisposable
{
    public T Value { get; } = value;
    public ValueTask DisposeAsync()
    {
        Value.Dispose();
        return ValueTask.CompletedTask;
    }
}