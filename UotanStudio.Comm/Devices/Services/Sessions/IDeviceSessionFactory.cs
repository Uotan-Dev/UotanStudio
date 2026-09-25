using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Services.Sessions;

/// <summary>
/// Builds a protocol session on top of the exact transport already opened for one leased endpoint.
/// <para>在已为单个租约端点打开的精确传输之上构建协议会话。</para>
/// Implementations must not open alternative transports or fall back to another route on failure.
/// <para>实现不得打开替代传输，也不得在失败时回退到其他路由。</para>
/// </summary>
internal interface IDeviceSessionFactory
{
    DeviceSessionKind Kind { get; }

    Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken);
}