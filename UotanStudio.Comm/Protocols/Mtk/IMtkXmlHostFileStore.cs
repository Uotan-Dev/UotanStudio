namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Host-side file callbacks for XML download agents. When attached before hardware initialization the session advertises download, upload and file-system commands and services device-initiated requests strictly through this store; without a store the capabilities are not advertised and unexpected requests fault the session.
/// <para>XML 下载代理的宿主侧文件回调。在硬件初始化之前附加时，会话会通告下载、上传与文件系统命令，并严格经此存储处理设备发起的请求；未附加时不通告这些能力，意外请求将使会话故障。</para>
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