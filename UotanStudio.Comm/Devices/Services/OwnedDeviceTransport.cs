namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Adapts a synchronous protocol session to the controller's owned lease.
/// <para>把同步协议会话适配为控制器持有的受管租约。</para>
/// </summary>
/// <param name="value">The synchronous session to own. <para>要持有的同步会话实例。</para></param>
internal sealed class OwnedDeviceTransport<T>(T value) : IAsyncDisposable where T : IDisposable
{
    public T Value { get; } = value;
    public ValueTask DisposeAsync()
    {
        Value.Dispose();
        return ValueTask.CompletedTask;
    }
}