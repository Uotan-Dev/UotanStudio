using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Host-facing HDC protocol session. The concrete protocol implementation is an internal
/// detail of the communication layer; callers only ever hold this façade.
/// <para>面向宿主的 HDC 协议会话。具体协议实现是通信层的内部细节，调用方只持有此门面。</para>
/// </summary>
public interface IHdcDeviceSession : IDisposable
{
    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    string ProtocolName { get; }

    /// <summary>Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para></summary>
    bool IsConnected { get; }

    /// <summary>Connects and authenticates with the HDC peer. <para>连接并认证 HDC 对端。</para></summary>
    void Connect();

    /// <summary>Asynchronously connects and authenticates. <para>异步连接并认证。</para></summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes a shell command and returns the result. <para>执行 shell 命令并返回结果。</para></summary>
    ShellResult ExecuteCommand(string command, int timeoutMs = 30000);

    /// <summary>Creates a shell client for the given command. <para>为给定命令创建 shell 客户端。</para></summary>
    IHdcShellClient CreateShellClient(string command);

    /// <summary>Creates a device-services client. <para>创建设备服务客户端。</para></summary>
    IHdcDeviceServices CreateDeviceServices();

    /// <summary>Creates a sync (file transfer) client over the direct connection.
    /// <para>在直连通道上创建 sync（文件传输）客户端。</para></summary>
    IHdcSyncClient CreateSyncClient();

    /// <summary>Creates an app install/uninstall client over the direct connection.
    /// <para>在直连通道上创建应用安装/卸载客户端。</para></summary>
    IHdcAppClient CreateAppClient();

    /// <summary>Gets the port-forwarding manager over the direct connection.
    /// <para>获取直连通道上的端口转发管理器。</para></summary>
    IHdcForwardManager Forwards { get; }

    /// <summary>Quotes a single argument for an HDC <c>shell</c> invocation.
    /// <para>为 HDC <c>shell</c> 调用引用单个参数。</para></summary>
    static string QuoteShellArgument(string value) => HdcDeviceServices.QuoteShellArgument(value);
}

/// <summary>Host-facing HDC shell client. <para>面向宿主的 HDC shell 客户端。</para></summary>
public interface IHdcShellClient
{
    /// <summary>Executes the shell command and returns the captured result.
    /// <para>执行 shell 命令并返回捕获的结果。</para></summary>
    ShellResult Execute(int timeoutMs = 30000);

    /// <summary>Executes the shell command, streaming stdout/stderr until completion.
    /// <para>执行 shell 命令，持续回调 stdout/stderr 直到结束。</para></summary>
    int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC sync (file transfer) client. <para>面向宿主的 HDC sync（文件传输）客户端。</para></summary>
public interface IHdcSyncClient : IDisposable
{
    /// <summary>Sends a local file to the device. <para>发送本地文件到设备。</para></summary>
    void Send(string localPath, string remotePath, bool holdTimestamp = true, CancellationToken cancellationToken = default);

    /// <summary>Receives a remote file to a local path. <para>接收远程文件到本地路径。</para></summary>
    void Recv(string remotePath, string localPath, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC app install/uninstall client. <para>面向宿主的 HDC 应用安装/卸载客户端。</para></summary>
public interface IHdcAppClient
{
    /// <summary>Installs an application package. <para>安装应用包。</para></summary>
    string Install(string path, CancellationToken cancellationToken = default);

    /// <summary>Uninstalls an application bundle. <para>卸载应用包。</para></summary>
    string Uninstall(string bundle, CancellationToken cancellationToken = default);
}

/// <summary>Host-facing HDC port-forwarding manager. <para>面向宿主的 HDC 端口转发管理器。</para></summary>
public interface IHdcForwardManager
{
    /// <summary>Adds a forward rule. <para>添加转发规则。</para></summary>
    void Add(int localPort, int remotePort, bool reverse = false, CancellationToken cancellationToken = default);

    /// <summary>Lists active forwards. <para>列出当前转发。</para></summary>
    string List();

    /// <summary>Removes a forward rule. <para>移除转发规则。</para></summary>
    bool Remove(string listenEndpoint, bool reverse = false);
}

/// <summary>Host-facing one-shot HDC device services. <para>面向宿主的一次性 HDC 设备服务。</para></summary>
public interface IHdcDeviceServices
{
    /// <summary>Lists debug (JDWP) processes. <para>列出调试（JDWP）进程。</para></summary>
    string ListDebugProcesses();

    /// <summary>Reboots to system, bootloader or recovery. <para>重启至系统、bootloader 或 recovery。</para></summary>
    void Reboot(string? mode = null);

    /// <summary>Remounts the device filesystems read-write. <para>以读写方式重新挂载设备文件系统。</para></summary>
    void Remount();
}