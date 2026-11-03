using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Reports an MTK protocol failure without hiding the target's original status code.
/// <para>上报 MTK 协议失败，同时不掩盖目标的原始状态码。</para></summary>
/// <remarks>
/// Hosts catch this type to distinguish a target rejection from a transport failure: the message carries
/// the documented status name when one exists, and <see cref="Status"/> keeps the raw value so callers can
/// log or display exactly what the device returned.
/// <para>宿主可捕获该类型以区分"目标拒绝"与"传输失败"：存在已记载名称时消息会带上该名称，
/// <see cref="Status"/> 保留原始值，供调用方原样记录或展示设备返回值。</para>
/// </remarks>
/// <param name="message">A description of the failed exchange. 失败交换的描述。</param>
/// <param name="status">The optional target status, in host byte order. 可选的目标状态，主机字节序。</param>
/// <param name="inner">The exception that caused this failure, when a lower layer already reported one.
/// 导致本次失败的底层异常；底层已上报异常时填写。</param>
public sealed class MtkProtocolException(string message, uint? status = null, Exception? inner = null)
    : IOException(message, inner)
{
    /// <summary>Gets the original target status, when the failure was a target rejection.
    /// <para>获取原始目标状态码；失败来自目标拒绝时有值。</para></summary>
    /// <value>The raw status in host byte order, or <see langword="null"/> when no status was reported.
    /// <para>主机字节序的原始状态码；未上报状态时为 <see langword="null"/>。</para></value>
    public uint? Status { get; } = status;

    /// <summary>Gets the catalogued name for <see cref="Status"/>, or null when the value is not catalogued.
    /// <para>获取 <see cref="Status"/> 的已编目名称；取值未被编目时为 null。</para></summary>
    /// <value>A localization-neutral name such as <c>SECCFG invalid</c>, or <see langword="null"/>.
    /// <para>与本地化无关的名称（如 <c>SECCFG invalid</c>），或 <see langword="null"/>。</para></value>
    public string? StatusDescription => Status is { } value ? MtkSecStatusCode.TryDescribe(value) : null;

    /// <summary>
    /// Builds a rejection message that names the status when the catalog has an entry for it.
    /// <para>在目录中存在对应条目时为拒绝消息附上状态名称。</para>
    /// </summary>
    /// <param name="operation">The neutral operation name. 与本地化无关的操作名。</param>
    /// <param name="status">The raw target status. 目标原始状态码。</param>
    /// <returns>A message ending with the raw status and, when known, its name.
    /// <para>以原始状态码结尾、并在已知时附上名称的消息。</para></returns>
    public static string DescribeRejection(string operation, uint status) =>
        $"The target rejected {operation} (status {MtkSecStatusCode.Describe(status)}).";
}