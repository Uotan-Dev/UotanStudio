using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Host-facing ADB protocol session. The concrete protocol implementation is an internal
/// detail of the communication layer; callers only ever hold this façade.
/// <para>面向宿主的 ADB 协议会话。具体协议实现是通信层的内部细节，调用方只持有此门面。</para>
/// </summary>
public interface IAdbDeviceSession : IDisposable
{
    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    string ProtocolName { get; }

    /// <summary>Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para></summary>
    bool IsConnected { get; }

    /// <summary>Gets whether this session is mediated by a local adb server rather than raw USB.
    /// <para>获取本会话是否经本机 adb server 交互，而非原生 USB 直连。</para></summary>
    bool UsesServer { get; }

    /// <summary>Gets the peer protocol version. <para>获取对端协议版本。</para></summary>
    uint PeerVersion { get; }

    /// <summary>Connects and authenticates with the ADB peer.
    /// <para>连接并认证 ADB 对端。</para></summary>
    void Connect();

    /// <summary>Asynchronously connects and authenticates.
    /// <para>异步连接并认证。</para></summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a raw packet stream to a service. <para>打开到服务的原始数据包流。</para></summary>
    IAdbStream OpenStream(string service);

    /// <summary>Opens a byte-oriented stream to a service, ready to be copied or wrapped.
    /// <para>打开面向字节的服務流，可直接拷贝或包装。</para></summary>
    Stream OpenServiceStream(string service);

    /// <summary>Creates a shell client for the given command. <para>为给定命令创建 shell 客户端。</para></summary>
    IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false);

    /// <summary>Creates a sync client for file transfer. <para>创建用于文件传输的 sync 客户端。</para></summary>
    IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024);

    /// <summary>Creates a device-services client for one-shot services.
    /// <para>创建一次性设备服务客户端。</para></summary>
    IAdbDeviceServices CreateDeviceServices();

    /// <summary>Executes a shell command and returns the result. <para>执行 shell 命令并返回结果。</para></summary>
    ShellResult ExecuteCommand(string command, int timeoutMs = 30000);
}

/// <summary>
/// Host-facing raw ADB stream. <para>面向宿主的 ADB 原始流。</para>
/// </summary>
public interface IAdbStream : IDisposable
{
    /// <summary>Gets whether the stream is closed. <para>获取流是否已关闭。</para></summary>
    bool IsClosed { get; }

    /// <summary>Gets whether the stream has faulted. <para>获取流是否已故障。</para></summary>
    bool IsFaulted { get; }

    /// <summary>Gets the fault that terminated the stream, if any. <para>获取终止流的故障，若有。</para></summary>
    Exception? Fault { get; }

    /// <summary>Writes a payload to the stream. <para>向流写入负载。</para></summary>
    void Write(byte[] data);

    /// <summary>Reads one payload chunk, or <c>null</c> on timeout. <para>读取一个负载块，超时返回 <c>null</c>。</para></summary>
    byte[]? Read(int timeoutMs = System.Threading.Timeout.Infinite);

    /// <summary>Closes the stream. <para>关闭流。</para></summary>
    void Close();
}

/// <summary>
/// Host-facing ADB shell client. <para>面向宿主的 ADB shell 客户端。</para>
/// </summary>
public interface IAdbShellClient
{
    /// <summary>Executes the shell command and returns the captured result.
    /// <para>执行 shell 命令并返回捕获的结果。</para></summary>
    ShellResult Execute(int timeoutMs = 30000);

    /// <summary>Executes the shell command, streaming stdout/stderr until completion.
    /// <para>执行 shell 命令，持续回调 stdout/stderr 直到结束。</para></summary>
    int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-facing ADB sync (file transfer) client. <para>面向宿主的 ADB sync（文件传输）客户端。</para>
/// </summary>
public interface IAdbSyncClient : IDisposable
{
    /// <summary>Stats a remote path. <para>查询远程路径信息。</para></summary>
    SyncEntry? Stat(string remotePath);

    /// <summary>Lists a remote directory. <para>列出远程目录。</para></summary>
    IReadOnlyList<SyncEntry> List(string remotePath);

    /// <summary>Pushes a local file to the device. <para>推送本地文件到设备。</para></summary>
    void Push(string localPath, string remotePath, uint mode = 0x81A4, uint mtime = 0);

    /// <summary>Pushes a stream to the device. <para>推送流到设备。</para></summary>
    void PushStream(Stream source, string remotePath, uint mode = 0x81A4, uint mtime = 0);

    /// <summary>Pulls a remote file to a local path. <para>拉取远程文件到本地路径。</para></summary>
    void Pull(string remotePath, string localPath);

    /// <summary>Pulls a remote file into a stream. <para>拉取远程文件到流。</para></summary>
    void PullStream(string remotePath, Stream destination);
}

/// <summary>
/// Host-facing one-shot ADB device services. <para>面向宿主的一次性 ADB 设备服务。</para>
/// </summary>
public interface IAdbDeviceServices
{
    /// <summary>Runs a device service and returns its textual response.
    /// <para>运行设备服务并返回文本响应。</para></summary>
    string RunService(string service, int timeoutMs = 30000);

    /// <summary>Reboots the device, optionally into a mode. <para>重启设备，可指定模式。</para></summary>
    void Reboot(string? mode = null);

    /// <summary>Remounts the device filesystems. <para>重新挂载设备文件系统。</para></summary>
    string Remount();

    /// <summary>Reads a system property. <para>读取系统属性。</para></summary>
    string GetProp(string key);
}