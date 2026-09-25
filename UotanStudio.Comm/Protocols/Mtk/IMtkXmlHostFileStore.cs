namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Host-side file callbacks for XML download agents. When a store is attached to
/// <see cref="MtkSession.HostFileStore"/> before hardware initialization, the session
/// advertises <c>CMD:DOWNLOAD-FILE</c>/<c>CMD:UPLOAD-FILE</c>/<c>CMD:FILE-SYS-OPERATION</c>
/// and services device-initiated requests strictly through this store; without a store the
/// capabilities are not advertised and unexpected requests keep faulting the session
/// (fail-closed).
/// <para>XML 下载代理的主机侧文件回调。在硬件初始化前向 <see cref="MtkSession.HostFileStore"/>
/// 附加存储时，会话声明 <c>CMD:DOWNLOAD-FILE</c>/<c>CMD:UPLOAD-FILE</c>/<c>CMD:FILE-SYS-OPERATION</c>
/// 能力并严格经此存储服务设备发起的请求；未附加时不声明能力，意外请求仍使会话 fault（fail-closed）。</para>
/// Tokens are opaque agent-provided identifiers; implementations must treat them as untrusted
/// input, bound every transfer and honour cancellation.
/// <para>令牌是代理提供的不透明标识；实现必须将其视为不可信输入，约束每次传输并响应取消。</para>
/// </summary>
public interface IMtkXmlHostFileStore
{
    /// <summary>
    /// Opens a bounded source for a device download request (the device reads the named file
    /// from the host). Returning null refuses the request and faults the session.
    /// <para>为设备下载请求打开有界源（设备从主机读取命名文件）。返回 null 拒绝请求并使会话 fault。</para>
    /// </summary>
    Task<(Stream Content, long Length)?> OpenReadAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a bounded sink for a device upload request (the device writes the named file to
    /// the host). Returning null refuses the request and faults the session.
    /// <para>为设备上传请求打开有界接收器（设备向主机写入命名文件）。返回 null 拒绝请求并使会话 fault。</para>
    /// </summary>
    Task<Stream?> OpenWriteAsync(string token, long length, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the declared size for a <c>FILE-SIZE</c> operation. Returning null faults the session.
    /// <para>解析 <c>FILE-SIZE</c> 操作声明的长度。返回 null 使会话 fault。</para>
    /// </summary>
    Task<long?> GetSizeAsync(string token, CancellationToken cancellationToken);
}