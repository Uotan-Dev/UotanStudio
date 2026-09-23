using UotanStudio.Comm.Devices.Contracts;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>Owns the opened transport until disposed. Disposal releases the endpoint reservation.</summary>
public sealed class DeviceLease : IAsyncDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal DeviceLease(DeviceEndpoint endpoint, IAsyncDisposable transport, Action release)
    {
        Endpoint = endpoint;
        Transport = transport;
        _release = release;
    }

    public DeviceEndpoint Endpoint { get; }
    public IAsyncDisposable Transport { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _release();
        }
    }
}
