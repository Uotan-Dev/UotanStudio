using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Starts an explicitly selected stock XFlash loader through the shared MTK bootstrap.
/// <para>通过共享 MTK 启动流程启动显式选定的标准 XFlash 加载器。</para></summary>
public static class MtkXFlashBootstrap
{
    /// <summary>Takes ownership of the transport and returns a storage-ready XFlash session.
    /// Failures after null checks dispose the transport.
    /// <para>接管传输并返回存储就绪的 XFlash 会话；空值检查之后的失败会释放传输。</para></summary>
    /// <param name="transport">The explicitly selected open transport.
    /// <para>显式选定的已打开传输。</para></param>
    /// <param name="options">The selected loader, resources and identity policy.
    /// <para>选定的加载器、资源与身份策略。</para></param>
    /// <param name="cancellationToken">Cancellation for startup.
    /// <para>启动过程的取消令牌。</para></param>
    /// <returns>A caller-owned session after storage discovery.
    /// <para>存储发现后由调用方持有的会话。</para></returns>
    public static Task<MtkSession> InitializeAsync(ICommTransport transport, MtkXFlashBootstrapOptions options,
        CancellationToken cancellationToken = default) =>
        MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.XFlash, options, cancellationToken);

    /// <summary>Preflights resources before opening one selected serial port and starting XFlash.
    /// <para>先预检资源，再打开一个选定的串口并启动 XFlash。</para></summary>
    /// <param name="serial">Explicit port and line settings.
    /// <para>显式端口与线路设置。</para></param>
    /// <param name="options">The selected loader, resources and identity policy.
    /// <para>选定的加载器、资源与身份策略。</para></param>
    /// <param name="cancellationToken">Cancellation for startup.
    /// <para>启动过程的取消令牌。</para></param>
    /// <returns>A caller-owned session after storage discovery.
    /// <para>存储发现后由调用方持有的会话。</para></returns>
    public static Task<MtkSession> ConnectSerialAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions options,
        CancellationToken cancellationToken = default) =>
        MtkBootstrap.ConnectSerialAsync(serial, MtkDaProtocol.XFlash, options, cancellationToken);

    internal static Task<MtkSession> ConnectSerialCoreAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions options,
        Func<SerialConnectionOptions, CancellationToken, Task<ICommTransport>> connect, CancellationToken cancellationToken) =>
        MtkBootstrap.ConnectSerialCoreAsync(serial, MtkDaProtocol.XFlash, options, connect, cancellationToken);
}