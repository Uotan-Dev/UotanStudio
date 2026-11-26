namespace UotanStudio.Comm.Transports.Abstractions;

/// <summary>
/// Optional transport capability for ending a bulk transfer with an explicit zero-length packet.
/// <para>可选传输能力：以显式零长度包结束一次批量传输。</para>
/// </summary>
/// <remarks>
/// A zero-length write is not the same as writing no bytes: the former emits the terminating packet
/// that tells a device the transfer has ended, while the latter completes locally without touching the
/// wire. Protocol code that needs the terminator must obtain this capability and decide for itself what
/// to do when it is absent.
/// <para>零长度写与"不写任何字节"并不相同：前者会发出告知设备传输已结束的终止包，后者只在本地返回
/// 而不触及线路。需要该终止包的协议代码必须获取本能力，并自行决定能力缺失时的行为。</para>
/// </remarks>
public interface ICommTransportZeroLengthWrite
{
    /// <summary>
    /// Emits a zero-length packet on the outbound bulk endpoint.
    /// <para>在出站批量端点上发出一个零长度包。</para>
    /// </summary>
    /// <param name="timeoutMs">The positive completion timeout. <para>正数的完成超时。</para></param>
    /// <param name="cancellationToken">Cancellation for the packet. <para>该数据包的取消令牌。</para></param>
    /// <returns>A task completing once the packet has been handed to the link.
    /// <para>该数据包交给链路后完成的任务。</para></returns>
    /// <exception cref="NotSupportedException">The backend cannot emit an explicit zero-length packet.
    /// <para>后端无法发出显式零长度包。</para></exception>
    Task WriteZeroLengthPacketAsync(int timeoutMs, CancellationToken cancellationToken = default);
}