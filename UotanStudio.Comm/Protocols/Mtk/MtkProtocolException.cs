namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Reports an MTK protocol failure without hiding the target's original status code.
/// <para>上报 MTK 协议失败，同时不掩盖目标的原始状态码。</para></summary>
/// <param name="message">A description of the failed exchange. 失败交换的描述。</param>
/// <param name="status">The optional target status, in host byte order. 可选的目标状态，主机字节序。</param>
/// <param name="inner">The exception that caused this failure, when a lower layer already reported one.
/// 导致本次失败的底层异常；底层已上报异常时填写。</param>
internal sealed class MtkProtocolException(string message, uint? status = null, Exception? inner = null)
    : IOException(message, inner)
{
    /// <summary>Gets the original target status, when the failure was a target rejection.
    /// <para>获取原始目标状态码；失败来自目标拒绝时有值。</para></summary>
    public uint? Status { get; } = status;
}