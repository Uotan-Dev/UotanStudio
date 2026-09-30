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
/// Tokens are opaque agent-provided identifiers. The session first rejects rooted or absolute
/// paths, drive letters, scheme-like colons, back slashes, <c>..</c> segments, control characters
/// and over-long names, so a store only ever receives a bounded relative name; it must still treat
/// that name as untrusted, resolve it inside its own containment root, bound every transfer and
/// honour cancellation.
/// <para>令牌是代理提供的不透明标识。会话会先拒绝根路径或绝对路径、盘符、冒号式协议写法、反斜杠、
/// <c>..</c> 片段、控制字符与超长名称，因此存储只会收到有界的相对文件名；它仍须把该名称视为不可信输入，
/// 在自己的包含根目录内解析它，约束每次传输并响应取消。</para>
/// </summary>
public interface IMtkXmlHostFileStore
{
    /// <summary>
    /// Opens a bounded source for a device download request (the device reads the named file
    /// from the host). Returning null refuses the request and faults the session.
    /// <para>为设备下载请求打开有界源（设备从主机读取命名文件）。返回 null 拒绝请求并使会话 fault。</para>
    /// </summary>
    /// <param name="token">Opaque, untrusted agent-provided file identifier.
    /// <para>由代理提供的不透明、不可信文件标识符。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the open.
    /// <para>对打开操作的协作式取消。</para></param>
    /// <returns>The content stream and its bounded length, or <see langword="null"/> to refuse.
    /// <para>内容流及其有界长度；拒绝时返回 <see langword="null"/>。</para></returns>
    Task<(Stream Content, long Length)?> OpenReadAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a bounded sink for a device upload request (the device writes the named file to
    /// the host). Returning null refuses the request and faults the session.
    /// <para>为设备上传请求打开有界接收器（设备向主机写入命名文件）。返回 null 拒绝请求并使会话 fault。</para>
    /// </summary>
    /// <param name="token">Opaque, untrusted agent-provided file identifier.
    /// <para>由代理提供的不透明、不可信文件标识符。</para></param>
    /// <param name="length">Declared byte count the device intends to write.
    /// <para>设备声明将要写入的字节数。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the open.
    /// <para>对打开操作的协作式取消。</para></param>
    /// <returns>The writable sink stream, or <see langword="null"/> to refuse.
    /// <para>可写的接收流；拒绝时返回 <see langword="null"/>。</para></returns>
    Task<Stream?> OpenWriteAsync(string token, long length, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the declared size for a <c>FILE-SIZE</c> operation. Returning null faults the session.
    /// <para>解析 <c>FILE-SIZE</c> 操作声明的长度。返回 null 使会话 fault。</para>
    /// </summary>
    /// <param name="token">Opaque, untrusted agent-provided file identifier.
    /// <para>由代理提供的不透明、不可信文件标识符。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the query.
    /// <para>对该查询的协作式取消。</para></param>
    /// <returns>The declared size in bytes, or <see langword="null"/> to fault the session.
    /// <para>以字节计的声明大小；要使会话 fault 则返回 <see langword="null"/>。</para></returns>
    Task<long?> GetSizeAsync(string token, CancellationToken cancellationToken);
}