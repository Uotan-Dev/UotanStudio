using UotanStudio.Comm.Devices.Contracts;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>Opens only the requested endpoint; failure must not select another device or route.</summary>
public interface IDeviceTransportOpener
{
    Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken);
}
