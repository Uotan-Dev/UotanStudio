namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Supplies an explicitly authorized DA SLA response without built-in keys or fallback credentials.
/// <para>提供显式授权的 DA SLA 响应，不包含内置密钥或回退凭据。</para>
/// </summary>
public interface IMtkDaSlaResponder
{
    /// <summary>
    /// Signs one challenge for the current session. Implementations must not reenter the session.
    /// <para>为当前会话的单个挑战签名；实现不得重入会话。</para>
    /// </summary>
    /// <param name="device">The queried device identity, not cryptographic identity proof.
    /// <para>查询所得的设备身份，并非密码学身份证明。</para></param>
    /// <param name="deviceFirmwareInfo">The exact bounded device information reply, valid until this task completes.
    /// <para>精确且有界的设备信息回复，仅在本任务完成前有效。</para></param>
    /// <param name="challenge">The challenge within the reply, valid until this task completes; do not retain it.
    /// <para>回复内的挑战，仅在本任务完成前有效，不得保留。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the exchange deadline.
    /// <para>包含交换截止时间的协作式取消令牌。</para></param>
    /// <returns>A newly allocated wire-ready response of 1..4096 bytes whose ownership transfers to the session.
    /// The session clears the response after transmission or failure.
    /// <para>新分配的可传输响应，长度为 1..4096 字节，所有权转移给会话；会话在传输或失败后清除响应。</para></returns>
    Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> deviceFirmwareInfo,
        ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken);
}