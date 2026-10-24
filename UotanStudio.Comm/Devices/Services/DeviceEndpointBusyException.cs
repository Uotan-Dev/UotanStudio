using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Reports that an endpoint is still reserved by another lease, so the open was rejected instead of
/// preempting the holder. Callers that must not fail spuriously can wait for the holder to finish and
/// retry the same acquisition.
/// <para>表示端点仍被另一个租约占用，因此开启被拒绝而不是抢占持有方；不能容忍偶发失败的调用方
/// 可以等待持有方结束后重试同一次开启。</para>
/// </summary>
public sealed class DeviceEndpointBusyException : InvalidOperationException
{
    /// <summary>
    /// Message text carried by every busy endpoint rejection.
    /// <para>每次端点占用拒绝都携带的消息文本。</para>
    /// </summary>
    public const string DefaultMessage = "Endpoint is already leased.";

    /// <summary>
    /// Creates the exception for an endpoint identity, used when the caller holds no endpoint instance.
    /// <para>为端点身份创建异常，适用于调用方没有端点实例的场合。</para>
    /// </summary>
    /// <param name="endpointId">Identity of the endpoint that stayed reserved.
    /// <para>保持占用的端点身份。</para></param>
    public DeviceEndpointBusyException(DeviceEndpointId endpointId)
        : base(DefaultMessage) => EndpointId = endpointId;

    /// <summary>
    /// Creates the exception for a known endpoint and keeps it for diagnostics.
    /// <para>为已知端点创建异常，并保留该端点用于诊断。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint that stayed reserved; must not be <see langword="null"/>.
    /// <para>保持占用的端点；不得为 <see langword="null"/>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.
    /// <para><paramref name="endpoint"/> 为 <see langword="null"/>。</para></exception>
    public DeviceEndpointBusyException(DeviceEndpoint endpoint)
        : base(DefaultMessage)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        EndpointId = endpoint.Id;
    }

    /// <summary>
    /// Identity of the endpoint whose reservation rejected the open.
    /// <para>拒绝本次开启的占用所对应的端点身份。</para>
    /// </summary>
    /// <value>The exact endpoint identity that was still leased.
    /// <para>仍处于租用状态的精确端点身份。</para></value>
    public DeviceEndpointId EndpointId { get; }

    /// <summary>
    /// The endpoint instance that stayed reserved, when the rejecting call had one.
    /// <para>拒绝调用持有端点实例时为保持占用的端点，否则为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The leased endpoint, or <see langword="null"/> when only the identity was known.
    /// <para>被占用的端点；只知道身份时为 <see langword="null"/>。</para></value>
    public DeviceEndpoint? Endpoint { get; }
}