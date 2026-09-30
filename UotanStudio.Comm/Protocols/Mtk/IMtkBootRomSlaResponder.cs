namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>External, explicitly authorized Boot ROM SLA response generation; no keys are provided by the library.
/// <para>外部、显式授权的 Boot ROM SLA 响应生成；库不提供任何密钥。</para></summary>
/// <remarks>
/// A responder carries private key material, so it is <see cref="IDisposable"/>. Ownership of the
/// key follows whoever supplied the responder: a key store such as <see cref="MtkDirectoryKeyStore"/>
/// transfers the imported key to the responder it returns, and the caller must release that responder
/// once the exchange is finished; a responder the caller built around a key it keeps is released by
/// the caller, and disposing it only stops further responses. A session never disposes a responder it
/// was handed.
/// <para>响应器携带私密密钥材料，因此实现 <see cref="IDisposable"/>。密钥归属由提供响应器的一方决定：
/// <see cref="MtkDirectoryKeyStore"/> 这类密钥库会把导入的密钥交给它返回的响应器，调用方在交换结束后必须释放该响应器；
/// 调用方围绕自有密钥构造的响应器仍由调用方释放，释放它只会停止后续应答。会话从不释放收到的响应器。</para>
/// </remarks>
public interface IMtkBootRomSlaResponder : IDisposable
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