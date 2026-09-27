namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Supplies an explicitly authorized response to a Xiaomi Firehose challenge without built-in credentials.
/// <para>为小米 Firehose 挑战提供显式授权的响应，不包含内置凭据。</para>
/// </summary>
public interface IEdlXiaomiAuthenticationProvider
{
    /// <summary>Creates one wire-ready signature without reentering the session or attempting alternate credentials.
    /// <para>生成一次可传输签名，不重入会话，也不尝试其他凭据。</para></summary>
    /// <param name="identity">The optional queried Sahara identity, not cryptographic proof of the target.
    /// <para>可选的 Sahara 查询身份，并非目标的密码学证明。</para></param>
    /// <param name="challenge">UTF-8 bytes of the exact sig value reported by the target.
    /// The memory is borrowed until the returned task completes and must not be retained.
    /// <para>目标报告的精确 sig 值的 UTF-8 字节；内存仅借用至返回任务完成，不得保留。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the complete exchange deadline.
    /// <para>包含完整交换截止时间的协作式取消令牌。</para></param>
    /// <returns>A newly allocated 256-byte signature whose ownership transfers to the session for clearing.
    /// <para>新分配的 256 字节签名，所有权转移给会话并由会话清除。</para></returns>
    Task<byte[]> CreateSignatureAsync(SaharaDeviceIdentity? identity, ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken);
}