using HdcDeviceServices = UotanStudio.Comm.Protocols.Hdc.HdcDeviceServices;
using ShellResult = UotanStudio.Comm.Protocols.Hdc.ShellResult;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Host-facing HDC protocol session. The concrete protocol implementation is an internal
/// detail of the communication layer; callers only ever hold this façade.
/// <para>面向宿主的 HDC 协议会话。具体协议实现是通信层的内部细节，调用方只持有此门面。</para>
/// </summary>
/// <remarks>
/// The result carrier this façade returns (<see cref="ShellResult"/>) and the shell-quoting helper
/// behind <see cref="QuoteShellArgument"/> still live in the protocol layer, so they are named here by
/// explicit type alias. A namespace-wide import of <c>UotanStudio.Comm.Protocols.Hdc</c> is
/// deliberately absent: it would let any later protocol-layer type resolve silently inside the contract
/// surface and widen the façade without a reviewable change.
/// <para>本门面返回的结果类型（<see cref="ShellResult"/>）以及 <see cref="QuoteShellArgument"/>
/// 背后的 shell 引用包裹助手仍位于协议层，因此在此以显式类型别名引用。这里刻意不导入
/// <c>UotanStudio.Comm.Protocols.Hdc</c> 整个命名空间：一旦导入，后续任何协议层类型都可能在门面契约中
/// 悄然被解析出来，使门面在无可见改动的情况下被扩大。</para>
/// </remarks>
public interface IHdcDeviceSession : IDisposable
{
    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    /// <value>The fixed tag <c>hdc</c> identifying this session kind.
    /// <para>标识本会话类型的固定标记 <c>hdc</c>。</para></value>
    string ProtocolName { get; }

    /// <summary>Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para></summary>
    /// <value><see langword="true"/> once a connect call has completed on a direct or server-channel
    /// session and the session is not disposed.
    /// <para>直连或 server 通道会话的某次连接调用已完成且会话未释放时为 <see langword="true"/>。</para></value>
    bool IsConnected { get; }

    /// <summary>Connects and authenticates with the HDC peer. <para>连接并认证 HDC 对端。</para></summary>
    void Connect();

    /// <summary>Asynchronously connects and authenticates. <para>异步连接并认证。</para></summary>
    /// <param name="cancellationToken">
    /// Cancels the connect and the RSA authentication exchange.
    /// <para>取消连接与 RSA 认证交互。</para>
    /// </param>
    /// <returns>A task that completes once the session is connected and authenticated.
    /// <para>在会话完成连接与认证后结束的任务。</para></returns>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes a shell command and returns the result. <para>执行 shell 命令并返回结果。</para></summary>
    /// <param name="command">
    /// Command text sent verbatim to the device shell.
    /// <para>原样发送到设备 shell 的命令文本。</para>
    /// </param>
    /// <param name="timeoutMs">
    /// Overall response deadline in milliseconds; a direct-route wait throws once it elapses.
    /// <para>总体响应时限（毫秒）；直连路由在时限耗尽后抛出异常。</para>
    /// </param>
    /// <returns>
    /// The captured stdout, stderr and exit code. On a server route the output arrives merged, so
    /// stderr is empty and the exit code is <c>-1</c>.
    /// <para>捕获的 stdout、stderr 与退出码。经由 server 路由时输出合并返回，因此 stderr 为空且退出码为
    /// <c>-1</c>。</para>
    /// </returns>
    ShellResult ExecuteCommand(string command, int timeoutMs = 30000);

    /// <summary>Creates a shell client for the given command. <para>为给定命令创建 shell 客户端。</para></summary>
    /// <param name="command">
    /// Command text the client sends to the device shell when executed.
    /// <para>客户端执行时发送到设备 shell 的命令文本。</para>
    /// </param>
    /// <returns>A client bound to this session's route that executes the command on demand.
    /// <para>绑定本会话路由、按需执行该命令的客户端。</para></returns>
    IHdcShellClient CreateShellClient(string command);

    /// <summary>Creates a device-services client. <para>创建设备服务客户端。</para></summary>
    /// <returns>A one-shot client for debug-process listing, reboot and remount on this session's route.
    /// <para>在本会话路由上执行调试进程列表、重启与重新挂载的一次性客户端。</para></returns>
    IHdcDeviceServices CreateDeviceServices();

    /// <summary>Creates a sync (file transfer) client over the direct connection.
    /// <para>在直连通道上创建 sync（文件传输）客户端。</para></summary>
    /// <returns>A sync client that opens short-lived transfer channels on the direct connection.
    /// <para>在直连上打开短生命周期传输通道的 sync 客户端。</para></returns>
    /// <exception cref="NotSupportedException">
    /// The session runs over an HDC server channel, which exposes no daemon connection.
    /// <para>会话经由 HDC server 通道运行，没有可用于文件传输的守护进程连接。</para>
    /// </exception>
    IHdcSyncClient CreateSyncClient();

    /// <summary>Creates an app install/uninstall client over the direct connection.
    /// <para>在直连通道上创建应用安装/卸载客户端。</para></summary>
    /// <returns>An app client bound to the direct daemon connection.
    /// <para>绑定直连守护进程连接的应用客户端。</para></returns>
    /// <exception cref="NotSupportedException">
    /// The session runs over an HDC server channel, which exposes no daemon connection.
    /// <para>会话经由 HDC server 通道运行，没有可用于应用安装的守护进程连接。</para>
    /// </exception>
    IHdcAppClient CreateAppClient();

    /// <summary>Gets the port-forwarding manager over the direct connection.
    /// <para>获取直连通道上的端口转发管理器。</para></summary>
    /// <value>The manager owning the mappings registered on this session's direct connection; it is
    /// shared, so later calls return the same instance.
    /// <para>管理本会话直连上已登记映射的管理器；该实例是共享的，后续调用返回同一个对象。</para></value>
    /// <exception cref="NotSupportedException">
    /// The session runs over an HDC server channel, which exposes no daemon connection.
    /// <para>会话经由 HDC server 通道运行，不存在暴露的守护进程连接。</para>
    /// </exception>
    IHdcForwardManager Forwards { get; }

    /// <summary>Quotes a single argument for an HDC <c>shell</c> invocation.
    /// <para>为 HDC <c>shell</c> 调用引用单个参数。</para></summary>
    /// <param name="value">
    /// Raw argument text; it must not be <see langword="null"/> and must not contain a NUL byte.
    /// <para>原始参数文本；不得为 <see langword="null"/>，也不得包含 NUL 字节。</para>
    /// </param>
    /// <returns>The value wrapped in single quotes with embedded quotes escaped for the device shell.
    /// <para>用单引号包裹、并按设备 shell 规则转义内嵌引号后的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.
    /// <para><paramref name="value"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a NUL byte.
    /// <para><paramref name="value"/> 包含 NUL 字节。</para></exception>
    static string QuoteShellArgument(string value) => HdcDeviceServices.QuoteShellArgument(value);
}

/// <summary>Host-facing HDC shell client. <para>面向宿主的 HDC shell 客户端。</para></summary>
public interface IHdcShellClient
{
    /// <summary>Executes the shell command and returns the captured result.
    /// <para>执行 shell 命令并返回捕获的结果。</para></summary>
    /// <param name="timeoutMs">
    /// Overall response deadline in milliseconds; a direct-route wait throws once it elapses.
    /// <para>总体响应时限（毫秒）；直连路由在时限耗尽后抛出异常。</para>
    /// </param>
    /// <returns>
    /// The captured stdout, stderr and exit code. On a server route the output arrives merged, so
    /// stderr is empty and the exit code is <c>-1</c>.
    /// <para>捕获的 stdout、stderr 与退出码。经由 server 路由时输出合并返回，因此 stderr 为空且退出码为
    /// <c>-1</c>。</para>
    /// </returns>
    ShellResult Execute(int timeoutMs = 30000);

    /// <summary>Executes the shell command, streaming stdout/stderr until completion.
    /// <para>执行 shell 命令，持续回调 stdout/stderr 直到结束。</para></summary>
    /// <param name="onStdout">
    /// Callback receiving each output chunk as raw bytes; <see langword="null"/> discards the output.
    /// <para>以原始字节接收每个输出块的回调；为 <see langword="null"/> 时丢弃输出。</para>
    /// </param>
    /// <param name="onStderr">
    /// Callback reserved for stderr; HDC multiplexes both streams into one, so it is never invoked.
    /// <para>为 stderr 保留的回调；HDC 将两路输出复用到同一路，因此不会被调用。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels between chunks; the call checks the token before opening the channel.
    /// <para>在数据块之间取消；调用在打开通道前会检查该令牌。</para>
    /// </param>
    /// <returns>
    /// The exit code reported by the device, or <c>-1</c> when the session runs over a server route
    /// that reports no exit code.
    /// <para>设备上报的退出码；会话经由 server 路由且没有退出码时为 <c>-1</c>。</para>
    /// </returns>
    int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC sync (file transfer) client. <para>面向宿主的 HDC sync（文件传输）客户端。</para></summary>
public interface IHdcSyncClient : IDisposable
{
    /// <summary>Sends a local file to the device. <para>发送本地文件到设备。</para></summary>
    /// <param name="localPath">
    /// Local file path; a directory is expanded into a bounded directory send.
    /// <para>本地文件路径；目录会展开为有界的目录发送。</para>
    /// </param>
    /// <param name="remotePath">
    /// Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para>
    /// </param>
    /// <param name="holdTimestamp">
    /// Preserve source timestamps; effective only when the source is a local file.
    /// <para>保留源时间戳；仅当源为本地文件时生效。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels between packets; a cancelled transfer may leave the daemon task incomplete.
    /// <para>在数据包之间取消；取消后守护进程任务可能未完成。</para>
    /// </param>
    void Send(string localPath, string remotePath, bool holdTimestamp = true, CancellationToken cancellationToken = default);

    /// <summary>Receives a remote file to a local path. <para>接收远程文件到本地路径。</para></summary>
    /// <param name="remotePath">
    /// Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para>
    /// </param>
    /// <param name="localPath">
    /// Destination file path; it is replaced atomically on success and untouched when the transfer fails.
    /// <para>目标文件路径；成功时被原子替换，失败时保持原样。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels between chunks; the staging file is then deleted.
    /// <para>在数据块之间取消；取消后暂存文件会被删除。</para>
    /// </param>
    void Recv(string remotePath, string localPath, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC app install/uninstall client. <para>面向宿主的 HDC 应用安装/卸载客户端。</para></summary>
public interface IHdcAppClient
{
    /// <summary>Installs an application package. <para>安装应用包。</para></summary>
    /// <param name="path">
    /// Local package file, or a directory installed through the directory upload path.
    /// <para>本地包文件，或通过目录上传路径安装的目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before any device exchange begins.
    /// <para>在任何设备交换开始前取消。</para>
    /// </param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    string Install(string path, CancellationToken cancellationToken = default);

    /// <summary>Uninstalls an application bundle. <para>卸载应用包。</para></summary>
    /// <param name="bundle">
    /// Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted.
    /// <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before the uninstall task is opened.
    /// <para>在卸载任务打开前取消。</para>
    /// </param>
    /// <returns>The bundle-manager completion text on success.
    /// <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> is blank or contains rejected
    /// characters. <para><paramref name="bundle"/> 为空或含被拒绝的字符。</para></exception>
    string Uninstall(string bundle, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC port-forwarding manager. <para>面向宿主的 HDC 端口转发管理器。</para></summary>
public interface IHdcForwardManager
{
    /// <summary>Adds a forward rule. <para>添加转发规则。</para></summary>
    /// <param name="localPort">
    /// Host loopback port to listen on, in 1..65535.
    /// <para>主机回环监听端口，取值 1..65535。</para>
    /// </param>
    /// <param name="remotePort">
    /// Upstream TCP port, sent as <c>tcp:</c> plus the port number.
    /// <para>上游 TCP 端口，以 <c>tcp:</c> 加端口号的形式发送。</para>
    /// </param>
    /// <param name="reverse">
    /// <see langword="true"/> to set up a reverse (device-initiated) mapping, <see langword="false"/>
    /// for host-initiated forwarding.
    /// <para><see langword="true"/> 建立反向（设备发起）映射，<see langword="false"/> 为主机发起转发。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the synchronous endpoint handshake before the mapping is registered.
    /// <para>在映射登记前取消同步的端点握手。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localPort"/> is outside
    /// 1..65535. <para><paramref name="localPort"/> 不在 1..65535 范围内。</para></exception>
    /// <exception cref="System.IO.IOException">The listen key already has a different open mapping.
    /// <para>该监听键已存在另一个不同的打开映射。</para></exception>
    void Add(int localPort, int remotePort, bool reverse = false, CancellationToken cancellationToken = default);

    /// <summary>Lists active forwards. <para>列出当前转发。</para></summary>
    /// <returns>Newline-joined <c>fport</c>/<c>rport tcp:PORT endpoint</c> lines, empty when no
    /// mapping is open.
    /// <para>以换行拼接的 <c>fport</c>/<c>rport tcp:PORT endpoint</c> 行；无打开映射时为空串。</para></returns>
    string List();

    /// <summary>Removes a forward rule. <para>移除转发规则。</para></summary>
    /// <param name="listenEndpoint">
    /// Exact listening endpoint key, for example <c>tcp:5555</c>.
    /// <para>精确的监听端点键，例如 <c>tcp:5555</c>。</para>
    /// </param>
    /// <param name="reverse">
    /// <see langword="true"/> for a reverse mapping, <see langword="false"/> for forward.
    /// <para><see langword="true"/> 为反向映射，<see langword="false"/> 为正向映射。</para>
    /// </param>
    /// <returns><see langword="true"/> when a mapping with that listen key existed; otherwise
    /// <see langword="false"/>.
    /// <para>存在该监听键的映射时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    bool Remove(string listenEndpoint, bool reverse = false);
}

/// <summary>Host-facing one-shot HDC device services. <para>面向宿主的一次性 HDC 设备服务。</para></summary>
public interface IHdcDeviceServices
{
    /// <summary>Lists debug (JDWP) processes. <para>列出调试（JDWP）进程。</para></summary>
    /// <returns>The debug-process list as text, using the default 10000 ms deadline.
    /// <para>调试进程列表文本，使用默认 10000 毫秒时限。</para></returns>
    string ListDebugProcesses();

    /// <summary>Reboots to system, bootloader or recovery. <para>重启至系统、bootloader 或 recovery。</para></summary>
    /// <param name="mode">
    /// UI reboot mode: <see langword="null"/>, empty, <c>"system"</c>, <c>"bootloader"</c> or
    /// <c>"recovery"</c>.
    /// <para>界面重启模式：<see langword="null"/>、空串、<c>"system"</c>、<c>"bootloader"</c> 或
    /// <c>"recovery"</c>。</para>
    /// </param>
    /// <exception cref="NotSupportedException"><paramref name="mode"/> is not a supported reboot mode.
    /// <para><paramref name="mode"/> 不是受支持的重启模式。</para></exception>
    void Reboot(string? mode = null);

    /// <summary>Remounts the device filesystems read-write. <para>以读写方式重新挂载设备文件系统。</para></summary>
    void Remount();
}