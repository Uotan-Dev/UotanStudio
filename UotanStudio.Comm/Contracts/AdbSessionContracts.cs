using System.Diagnostics.CodeAnalysis;
using ShellResult = UotanStudio.Comm.Protocols.Adb.ShellResult;
using SyncEntry = UotanStudio.Comm.Protocols.Adb.SyncEntry;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Host-facing ADB protocol session. The concrete protocol implementation is an internal
/// detail of the communication layer; callers only ever hold this façade.
/// <para>面向宿主的 ADB 协议会话。具体协议实现是通信层的内部细节，调用方只持有此门面。</para>
/// </summary>
/// <remarks>
/// The result carriers this façade returns (<see cref="ShellResult"/> and <see cref="SyncEntry"/>)
/// still live in the protocol layer, so they are named here by explicit type alias. A namespace-wide
/// import of <c>UotanStudio.Comm.Protocols.Adb</c> is deliberately absent: it would let any later
/// protocol-layer type resolve silently inside the contract surface and widen the façade without a
/// reviewable change.
/// <para>本门面返回的结果类型（<see cref="ShellResult"/> 与 <see cref="SyncEntry"/>）仍位于协议层，
/// 因此在此以显式类型别名引用。这里刻意不导入 <c>UotanStudio.Comm.Protocols.Adb</c> 整个命名空间：
/// 一旦导入，后续任何协议层类型都可能在门面契约中悄然被解析出来，使门面在无可见改动的情况下被扩大。</para>
/// </remarks>
public interface IAdbDeviceSession : IDisposable
{
    /// <summary>Gets the protocol name. <para>获取协议名称。</para></summary>
    /// <value>Always <c>"adb"</c>. <para>固定为 <c>"adb"</c>。</para></value>
    string ProtocolName { get; }

    /// <summary>Gets whether the session is connected and authenticated.
    /// <para>获取会话是否已连接并通过认证。</para></summary>
    /// <value><see langword="true"/> while the transport is connected, authenticated and the
    /// session is not disposed. <para>传输已连接、已认证且会话未释放时为 <see langword="true"/>。</para></value>
    bool IsConnected { get; }

    /// <summary>Gets whether this session is mediated by a local adb server rather than raw USB.
    /// <para>获取本会话是否经本机 adb server 交互，而非原生 USB 直连。</para></summary>
    /// <value><see langword="true"/> when services are opened through the adb server target;
    /// <see langword="false"/> for a native USB transport.
    /// <para>经 adb server 目标打开服务时为 <see langword="true"/>；原生 USB 传输为 <see langword="false"/>。</para></value>
    bool UsesServer { get; }

    /// <summary>Gets the peer protocol version. <para>获取对端协议版本。</para></summary>
    /// <value>The version advertised by the peer in its connection reply; <c>0</c> before the
    /// handshake completes. <para>对端连接应答中通告的版本；握手完成前为 <c>0</c>。</para></value>
    uint PeerVersion { get; }

    /// <summary>Connects and authenticates with the ADB peer.
    /// <para>连接并认证 ADB 对端。</para></summary>
    void Connect();

    /// <summary>Asynchronously connects and authenticates.
    /// <para>异步连接并认证。</para></summary>
    /// <param name="cancellationToken">Cancels the connect attempt and the peer handshake wait.
    /// <para>取消连接尝试与对端握手等待。</para></param>
    /// <returns>A task that completes once the peer handshake has finished.
    /// <para>对端握手完成后完成的任务。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    /// <exception cref="TimeoutException">The peer did not complete the connection handshake
    /// within 10 seconds. <para>对端未在 10 秒内完成连接握手。</para></exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a raw packet stream to a service. <para>打开到服务的原始数据包流。</para></summary>
    /// <param name="service">The service destination string, such as <c>shell:...</c> or
    /// <c>sync:</c>. <para>服务目标字符串，如 <c>shell:...</c>、<c>sync:</c>。</para></param>
    /// <returns>A caller-owned packet stream; the caller must dispose it.
    /// <para>由调用方持有的数据包流，调用方须自行释放。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    IAdbStream OpenStream(string service);

    /// <summary>Opens a byte-oriented stream to a service, ready to be copied or wrapped.
    /// <para>打开面向字节的服務流，可直接拷贝或包装。</para></summary>
    /// <param name="service">The service destination string, such as <c>shell:...</c> or
    /// <c>sync:</c>. <para>服务目标字符串，如 <c>shell:...</c>、<c>sync:</c>。</para></param>
    /// <returns>A seek-ineligible byte view over the packet stream; disposing it closes the
    /// underlying stream. <para>数据包流上不可寻址的字节视图；释放它即关闭底层流。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    Stream OpenServiceStream(string service);

    /// <summary>Creates a shell client for the given command. <para>为给定命令创建 shell 客户端。</para></summary>
    /// <param name="command">The command line sent to the device shell; nothing runs until the
    /// client is executed. <para>发送到设备 shell 的命令行；执行客户端前不会运行。</para></param>
    /// <param name="term">Terminal type appended as <c>TERM=</c>, or <see langword="null"/> to
    /// omit it. <para>以 <c>TERM=</c> 附加的终端类型；<see langword="null"/> 表示省略。</para></param>
    /// <param name="pty"><see langword="true"/> to request a pseudo-terminal.
    /// <para>请求伪终端时为 <see langword="true"/>。</para></param>
    /// <returns>A shell v2 client bound to this session. <para>绑定到本会话的 shell v2 客户端。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false);

    /// <summary>Creates a sync client for file transfer. <para>创建用于文件传输的 sync 客户端。</para></summary>
    /// <param name="maxChunk">Maximum file bytes carried in each sync data packet.
    /// <para>每个 sync 数据包承载的最大文件字节数。</para></param>
    /// <returns>A sync client that opens the <c>sync:</c> stream lazily; the caller must dispose
    /// it. <para>懒打开 <c>sync:</c> 流的 sync 客户端，调用方须自行释放。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024);

    /// <summary>Creates a device-services client for one-shot services.
    /// <para>创建一次性设备服务客户端。</para></summary>
    /// <returns>A client bound to this session for reboot, remount and property reads.
    /// <para>绑定到本会话的 reboot、remount 与属性读取客户端。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已被释放。</para></exception>
    IAdbDeviceServices CreateDeviceServices();

    /// <summary>Executes a shell command and returns the result. <para>执行 shell 命令并返回结果。</para></summary>
    /// <remarks>
    /// This is the blocking form of <see cref="ExecuteCommandAsync"/>: it parks the calling thread for the
    /// whole command, so it belongs on a worker thread that already owns the ADB transaction, never on a UI
    /// or request thread.
    /// <para>本方法是 <see cref="ExecuteCommandAsync"/> 的阻塞形式：整个命令期间都会占用调用线程，因此只允许用于已持有该
    /// ADB 事务的工作线程，不得在 UI 线程或请求线程上调用。</para>
    /// </remarks>
    /// <param name="command">The command line for the device shell. <para>设备 shell 的命令行。</para></param>
    /// <param name="timeoutMs">Per-read wait in milliseconds before the command is considered
    /// stuck. <para>单次读取的等待毫秒数，超时即判定命令卡住。</para></param>
    /// <returns>The captured stdout, stderr and exit code; the exit code is <c>0</c> when the
    /// stream closed without an exit frame. <para>捕获的 stdout、stderr 与退出码；流关闭而未见退出帧时退出码为 <c>0</c>。</para></returns>
    /// <exception cref="TimeoutException">A read timed out while the stream was still open.
    /// <para>流仍打开时读取超时。</para></exception>
    ShellResult ExecuteCommand(string command, int timeoutMs = 30000);

    /// <summary>Executes a shell command asynchronously and returns the result.
    /// <para>异步执行 shell 命令并返回结果。</para></summary>
    /// <param name="command">The command line for the device shell. <para>设备 shell 的命令行。</para></param>
    /// <param name="cancellationToken">Cancels every stream read; pass a token armed with
    /// <c>CancelAfter</c> to bound the wait. <para>取消每次流读取；用 <c>CancelAfter</c> 装配令牌即可限制等待时长。</para></param>
    /// <returns>The captured stdout, stderr and exit code; the exit code is <c>0</c> when the
    /// stream closed without an exit frame. <para>捕获的 stdout、stderr 与退出码；流关闭而未见退出帧时退出码为 <c>0</c>。</para></returns>
    /// <exception cref="TimeoutException">A read timed out after 30 seconds while the stream was still
    /// open. <para>流仍打开时某次读取超过 30 秒。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask<ShellResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-facing raw ADB stream. Every read and write is asynchronous, so waiting for a peer
/// acknowledgement or for inbound data never parks a thread.
/// <para>面向宿主的 ADB 原始流。读写全部异步，等待对端确认或等待入站数据时不会占用线程。</para>
/// </summary>
/// <remarks>The inherited <c>DisposeAsync</c> is the preferred teardown for asynchronous callers; the inherited
/// <c>Dispose</c> stays so byte-oriented adapters can still release the stream synchronously.
/// <para>继承的 <c>DisposeAsync</c> 是异步调用方的首选拆除方式；继承的 <c>Dispose</c> 予以保留，使面向字节的适配器仍能同步释放流。</para>
/// </remarks>
/// <remarks>
/// <para>
/// Fault handling deliberately differs from the HDC channel: a faulted ADB stream keeps the payloads it
/// already queued readable, and only a read that finds the queue empty raises <see cref="IOException"/>.
/// Draining before failing is what preserves the scrcpy server log and the trailing sync DONE frame when
/// the transport dies in the middle of a transfer.
/// </para>
/// <para>
/// 故障处理有意与 HDC 通道不同：故障的 ADB 流仍允许读完已入队的负载，只有在队列已空的读取上才抛
/// <see cref="IOException"/>。先读完再失败，正是传输在传送途中断开时保留 scrcpy 服务端日志与 sync 尾部 DONE 帧的条件。
/// </para>
/// </remarks>
public interface IAdbStream : IDisposable, IAsyncDisposable
{
    /// <summary>Gets whether the stream is closed. <para>获取流是否已关闭。</para></summary>
    /// <value><see langword="true"/> after a local close, a peer close or a fault. A faulted stream is
    /// closed as well, yet its queued payloads stay readable.
    /// <para>本地关闭、对端关闭或故障后为 <see langword="true"/>。故障流同样处于关闭状态，但其已入队负载仍可读完。</para></value>
    bool IsClosed { get; }

    /// <summary>Gets whether the stream has faulted. <para>获取流是否已故障。</para></summary>
    /// <value><see langword="true"/> when a transport error or an exceeded receive budget terminated the
    /// stream. <para>传输错误或接收缓冲预算耗尽使流终止时为 <see langword="true"/>。</para></value>
    bool IsFaulted { get; }

    /// <summary>Gets the fault that terminated the stream, if any. <para>获取终止流的故障，若有。</para></summary>
    /// <value>The stored exception; <see langword="null"/> when the stream is not faulted.
    /// <para>已保存的异常；流未故障时为 <see langword="null"/>。</para></value>
    Exception? Fault { get; }

    /// <summary>Writes a payload to the stream. <para>向流异步写入负载。</para></summary>
    /// <param name="data">Bytes to send; split into chunks sized by the peer maximum payload and sent one
    /// acknowledged chunk at a time, awaiting each acknowledgement without holding a thread.
    /// <para>待发送字节；按对端最大负载分块，逐块等待确认后发送，等待期间不占用线程。</para></param>
    /// <param name="cancellationToken">Cancels a chunk wait; the cancellation never consumes the peer's
    /// acknowledgement permit, so a later write can still proceed.
    /// <para>取消某一块的等待；取消不会吞掉对端的确认许可，因此后续写入仍可继续。</para></param>
    /// <returns>A task that completes once every chunk has been handed to the transport.
    /// <para>全部负载块都已交给传输后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The stream is closed. <para>流已关闭。</para></exception>
    /// <exception cref="TimeoutException">The peer did not acknowledge the previous chunk within 10 seconds
    /// while <paramref name="cancellationToken"/> stayed unset. <para>对端未在 10 秒内确认上一块，且
    /// <paramref name="cancellationToken"/> 未触发。</para></exception>
    /// <exception cref="IOException">The stream closed or faulted while chunks were still pending.
    /// <para>仍有待发块时流被关闭或判定故障。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Reads the next payload chunk asynchronously. <para>异步读取下一个负载块。</para></summary>
    /// <param name="cancellationToken">Bounds the wait for inbound data; cancellation surfaces as
    /// <see cref="OperationCanceledException"/> rather than a timeout value, so callers express deadlines
    /// with <c>CancellationTokenSource.CancelAfter</c>.
    /// <para>限制入站数据的等待；取消表现为 <see cref="OperationCanceledException"/> 而非超时返回值，因此调用方用
    /// <c>CancellationTokenSource.CancelAfter</c> 表达期限。</para></param>
    /// <returns>The next queued payload, or <see langword="null"/> when the stream is closed with no queued
    /// data. <para>下一个排队的负载；流已关闭且无排队数据时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The stream is faulted and its queue has been drained; the stored fault
    /// is attached as the inner exception. <para>流已故障且队列已读完；已保存的故障作为内部异常附加。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled while
    /// no data was queued. <para>无排队数据时 <paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Dequeues a pending payload without waiting. <para>非阻塞地取出已排队的负载。</para></summary>
    /// <param name="chunk">The next queued payload when the return value is <see langword="true"/>;
    /// otherwise <see langword="null"/>. <para>返回 <see langword="true"/> 时为下一个排队负载；否则为
    /// <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a payload was already queued; <see langword="false"/> when the
    /// queue is empty, which never blocks and never reports a close.
    /// <para>队列中已有负载时返回 <see langword="true"/>；队列为空返回 <see langword="false"/>，既不阻塞也不报告关闭。</para></returns>
    bool TryRead([NotNullWhen(true)] out byte[]? chunk);

    /// <summary>Closes the stream and notifies the peer asynchronously.
    /// <para>关闭流并异步通知对端。</para></summary>
    /// <remarks>Unlike a synchronous close, a failed close frame is not reported: the stream is closed either
    /// way, because a peer that stops answering must never block local teardown.
    /// <para>与同步关闭不同，关闭帧发送失败不会上报：无论成败流都已关闭，因为对端停止响应时不得阻塞本地拆除。</para></remarks>
    /// <returns>A task that completes once the close frame has been handed to the transport.
    /// <para>关闭帧已交给传输后完成的任务。</para></returns>
    Task CloseAsync();

}

/// <summary>
/// Host-facing ADB shell client. <para>面向宿主的 ADB shell 客户端。</para>
/// </summary>
public interface IAdbShellClient
{
    /// <summary>Executes the shell command asynchronously and returns the captured result.
    /// <para>异步执行 shell 命令并返回捕获的结果。</para></summary>
    /// <param name="timeoutMs">Per-read wait in milliseconds before the command is considered
    /// stuck. <para>单次读取的等待毫秒数，超时即判定命令卡住。</para></param>
    /// <param name="cancellationToken">Cancels the command between and during reads.
    /// <para>在读取之间与读取期间取消命令。</para></param>
    /// <returns>The captured stdout, stderr and exit code; the exit code is <c>0</c> when the
    /// stream closed without an exit frame. <para>捕获的 stdout、stderr 与退出码；流关闭而未见退出帧时退出码为 <c>0</c>。</para></returns>
    /// <exception cref="TimeoutException">A read timed out while the stream was still open.
    /// <para>流仍打开时读取超时。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask<ShellResult> ExecuteAsync(int timeoutMs = 30000, CancellationToken cancellationToken = default);

    /// <summary>Executes the shell command asynchronously, streaming stdout/stderr until completion.
    /// <para>异步执行 shell 命令，持续回调 stdout/stderr 直到结束。</para></summary>
    /// <param name="onStdout">Callback invoked per stdout frame payload, or <see langword="null"/>
    /// to discard. <para>每个 stdout 帧负载的回调；<see langword="null"/> 表示丢弃。</para></param>
    /// <param name="onStderr">Callback invoked per stderr frame payload, or <see langword="null"/>
    /// to discard. <para>每个 stderr 帧负载的回调；<see langword="null"/> 表示丢弃。</para></param>
    /// <param name="cancellationToken">Cancels the read that is in progress; the loop stops as soon as the
    /// wait observes the cancellation. <para>取消进行中的读取；等待一旦观察到取消即停止循环。</para></param>
    /// <returns>The exit code; <c>0</c> when the stream closed or cancellation took effect before
    /// an exit frame arrived. <para>退出码；流关闭或退出帧到达前被取消时为 <c>0</c>。</para></returns>
    /// <exception cref="InvalidDataException">The stream ended with a truncated shell v2 frame.
    /// <para>流在 shell v2 帧未完整时结束。</para></exception>
    ValueTask<int> ExecuteStreamingAsync(Action<byte[]>? onStdout, Action<byte[]>? onStderr,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-facing ADB sync (file transfer) client. Every transfer is asynchronous and reports progress
/// between packets rather than by holding a thread.
/// <para>面向宿主的 ADB sync（文件传输）客户端。全部传输均为异步，并在数据包之间响应取消而不是占用线程。</para>
/// </summary>
/// <remarks>The inherited <c>DisposeAsync</c> sends the sync QUIT marker and releases the sync stream; a device
/// that rejects the marker is ignored, so disposing a finished transfer never fails.
/// <para>继承的 <c>DisposeAsync</c> 会发送 sync QUIT 标记并释放 sync 流；设备拒绝标记时被忽略，因此释放已完成的传输永不失败。</para>
/// </remarks>
public interface IAdbSyncClient : IAsyncDisposable
{
    /// <summary>Stats a remote path. <para>异步查询远程路径信息。</para></summary>
    /// <param name="remotePath">Device-side path to stat. <para>待查询的设备侧路径。</para></param>
    /// <param name="cancellationToken">Cancels the request or the reply wait.
    /// <para>取消请求或应答等待。</para></param>
    /// <returns>The mode, size and mtime entry; the mode is <c>0</c> when the peer reports the
    /// path as absent. <para>包含 mode、size 与 mtime 的条目；对端报告路径不存在时 mode 为 <c>0</c>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The peer rejected the request, answered with an unexpected id,
    /// or reported a payload longer than the sync chunk limit. <para>对端拒绝请求、返回非预期标识，或上报超过 sync
    /// 分块上限的负载。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask<SyncEntry?> StatAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Lists a remote directory. <para>异步列出远程目录。</para></summary>
    /// <param name="remotePath">Device-side directory path. <para>设备侧目录路径。</para></param>
    /// <param name="cancellationToken">Checked before each directory record and during each read; cancellation
    /// stops the LIST loop. <para>在每条目录记录前以及每次读取期间检查；取消会停止 LIST 循环。</para></param>
    /// <returns>Every entry received before the done marker; empty for an empty directory.
    /// <para>收到结束标记前的全部条目；空目录返回空列表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The peer rejected the request, answered with an unexpected id,
    /// or reported a record longer than the sync chunk limit. <para>对端拒绝请求、返回非预期标识，或上报超过 sync
    /// 分块上限的记录。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask<IReadOnlyList<SyncEntry>> ListAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Pushes a local file to the device. <para>异步推送本地文件到设备。</para></summary>
    /// <param name="localPath">Existing local file opened for reading. <para>以只读方式打开的已存在本地文件。</para></param>
    /// <param name="remotePath">Destination path on the device. <para>设备上的目标路径。</para></param>
    /// <param name="mode">Unix permission bits encoded in the sync request as octal; the default
    /// <c>0x81A4</c> is regular-file <c>0644</c>. <para>以八进制编码进 sync 请求的 Unix 权限位；默认 <c>0x81A4</c> 即普通文件 <c>0644</c>。</para></param>
    /// <param name="mtime">Device-side modification timestamp in seconds; <c>0</c> means the
    /// current UTC time at transfer. <para>设备侧修改时间（秒）；<c>0</c> 表示传输时的当前 UTC 时间。</para></param>
    /// <param name="cancellationToken">Checked before each client chunk is read and sent; cancellation
    /// stops the push between packets. <para>在读取并发送每个客户端数据块前检查；取消会在数据包之间停止推送。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="localPath"/> or
    /// <paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="localPath"/> 或 <paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="localPath"/> does not exist.
    /// <para><paramref name="localPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an
    /// unexpected id. <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask PushAsync(string localPath, string remotePath, uint mode = 0x81A4, uint mtime = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Pushes a stream to the device. <para>异步推送流到设备。</para></summary>
    /// <param name="source">Read to end-of-stream in client chunk sizes; the caller keeps
    /// ownership. <para>按客户端块大小读至流末尾；所有权仍归调用方。</para></param>
    /// <param name="remotePath">Destination path on the device. <para>设备上的目标路径。</para></param>
    /// <param name="mode">Unix permission bits encoded in the sync request as octal; the default
    /// <c>0x81A4</c> is regular-file <c>0644</c>. <para>以八进制编码进 sync 请求的 Unix 权限位；默认 <c>0x81A4</c> 即普通文件 <c>0644</c>。</para></param>
    /// <param name="mtime">Device-side modification timestamp in seconds; <c>0</c> means the
    /// current UTC time at transfer. <para>设备侧修改时间（秒）；<c>0</c> 表示传输时的当前 UTC 时间。</para></param>
    /// <param name="cancellationToken">Checked before each client chunk is read and sent; cancellation
    /// stops the push between packets. <para>在读取并发送每个客户端数据块前检查；取消会在数据包之间停止推送。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or
    /// <paramref name="remotePath"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 或 <paramref name="remotePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an
    /// unexpected id. <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask PushStreamAsync(Stream source, string remotePath, uint mode = 0x81A4, uint mtime = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Pulls a remote file to a local path. <para>异步拉取远程文件到本地路径。</para></summary>
    /// <param name="remotePath">Device-side source file. <para>设备侧源文件。</para></param>
    /// <param name="localPath">Local destination; created or truncated. <para>本地目标；不存在则创建，存在则截断。</para></param>
    /// <param name="cancellationToken">Checked before each device chunk is written; cancellation stops
    /// the pull between packets. <para>在写入每个设备数据块前检查；取消会在数据包之间停止拉取。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> or
    /// <paramref name="localPath"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 或 <paramref name="localPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an
    /// unexpected id. <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);

    /// <summary>Pulls a remote file into a stream. <para>异步拉取远程文件到流。</para></summary>
    /// <param name="remotePath">Device-side source file. <para>设备侧源文件。</para></param>
    /// <param name="destination">Written sequentially from the current position; the caller keeps
    /// ownership. <para>从当前位置顺序写入；所有权仍归调用方。</para></param>
    /// <param name="cancellationToken">Checked before each device chunk is written; cancellation stops
    /// the pull between packets. <para>在写入每个设备数据块前检查；取消会在数据包之间停止拉取。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="remotePath"/> or
    /// <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="remotePath"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device rejected the transfer or answered with an
    /// unexpected id. <para>设备拒绝传输或返回了非预期标识。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    ValueTask PullStreamAsync(string remotePath, Stream destination, CancellationToken cancellationToken = default);

}

/// <summary>
/// Host-facing one-shot ADB device services. <para>面向宿主的一次性 ADB 设备服务。</para>
/// </summary>
public interface IAdbDeviceServices
{
    /// <summary>Runs a device service and returns its textual response.
    /// <para>运行设备服务并返回文本响应。</para></summary>
    /// <param name="service">The service destination string, such as <c>remount:</c>.
    /// <para>服务目标字符串，如 <c>remount:</c>。</para></param>
    /// <param name="timeoutMs">Per-read wait in milliseconds; reading stops when the stream
    /// closes or one read times out. <para>单次读取等待毫秒数；流关闭或某次读取超时即停止。</para></param>
    /// <returns>The UTF-8 decoded output; empty when the service wrote nothing.
    /// <para>UTF-8 解码后的输出；服务无输出时为空字符串。</para></returns>
    string RunService(string service, int timeoutMs = 30000);

    /// <summary>Reboots the device, optionally into a mode. <para>重启设备，可指定模式。</para></summary>
    /// <param name="mode">Reboot target such as <c>bootloader</c> or <c>recovery</c>;
    /// <see langword="null"/> or empty performs a plain reboot.
    /// <para>重启目标（如 <c>bootloader</c>、<c>recovery</c>）；<see langword="null"/> 或空字符串表示普通重启。</para></param>
    void Reboot(string? mode = null);

    /// <summary>Remounts the device filesystems. <para>重新挂载设备文件系统。</para></summary>
    /// <returns>The <c>remount:</c> service response text describing the outcome.
    /// <para>描述结果的 <c>remount:</c> 服务响应文本。</para></returns>
    string Remount();

    /// <summary>Reads a system property. <para>读取系统属性。</para></summary>
    /// <param name="key">Property key passed to the device <c>getprop</c> command.
    /// <para>传给设备 <c>getprop</c> 命令的属性名。</para></param>
    /// <returns>The trimmed property value; empty when the property is not set.
    /// <para>去除首尾空白的属性值；属性未设置时为空字符串。</para></returns>
    string GetProp(string key);
}