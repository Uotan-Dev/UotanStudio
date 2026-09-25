namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>External, explicitly authorized Boot ROM SLA response generation; no keys are provided by the library.
/// <para>外部、显式授权的 Boot ROM SLA 响应生成；库不提供任何密钥。</para></summary>
public interface IMtkBootRomSlaResponder
{
    /// <summary>Produces the wire-ready response to one bounded challenge, without fallback key attempts.
    /// <para>针对单个有界挑战生成可直接上线的响应，不进行任何回退密钥尝试。</para></summary>
    /// <param name="device">Queried target identity, not cryptographic proof of device identity.
    /// <para>查询到的目标身份，并非设备身份的密码学证明。</para></param>
    /// <param name="wireChallenge">Exact target bytes, valid only until this task completes; do not retain.
    /// The provider owns any required word swapping, cryptographic encoding and signing.
    /// <para>精确的目标字节，仅在本任务完成前有效；不要保留。所需的字序交换、密码学编码与签名均由提供方负责。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the exchange deadline.
    /// <para>协作式取消，包含交互截止时间。</para></param>
    /// <returns>A newly allocated response of 1..4096 bytes. Ownership transfers to the session,
    /// which clears it on exit. Do not return shared/cached key or signature buffers.
    /// <para>新分配的 1..4096 字节响应。所有权转移给会话，会话在退出时清除它。不要返回共享/缓存的密钥或签名缓冲区。</para></returns>
    Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> wireChallenge,
        CancellationToken cancellationToken);
}