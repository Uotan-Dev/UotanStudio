using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Opens only the requested endpoint; failure must not select another device or route.
/// <para>只打开请求的端点；失败时不得改选其他设备或路由。</para>
/// </summary>
internal interface IDeviceTransportOpener
{
    Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken);
}