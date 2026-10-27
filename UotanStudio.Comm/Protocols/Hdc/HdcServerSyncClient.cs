namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// HDC file transfer over a host-server channel using the remote-client mode. Each operation opens a
/// dedicated host connection, sends the command with the <c>remote -cwd</c> marker, waits for the server's
/// <c>CMD_FILE_INIT</c> echo and then performs the local file I/O itself as the task master (send) or slave (recv)
/// through little-endian task frames.
/// <para>经宿主 server 通道的 HDC 文件传输，采用 remote-client 模式。每次操作打开专用宿主连接，发送附带
/// <c>remote -cwd</c> 标记的命令，等待 server 回送 <c>CMD_FILE_INIT</c>，随后以小端任务帧自行作为任务 master（发送）
/// 或 slave（接收）执行本地文件 IO。</para>
/// </summary>
/// <remarks>
/// The server only relays frames for these transfers, so the device path never depends on a file that must exist on
/// the server host. <para>server 只中转这些传输的帧，因此设备路径不依赖 server 主机上必须存在的文件。</para>
/// </remarks>
public sealed class HdcServerSyncClient : Contracts.IHdcSyncClient
{
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly string _connectKey;
    private bool _disposed;

    /// <summary>
    /// Initializes a server-routed sync client for one target.
    /// <para>为单个目标初始化经 server 路由的 sync 客户端。</para>
    /// </summary>
    /// <param name="server">Host channel client whose address and greeting logic are reused for every task.
    /// <para>每个任务复用其地址与握手逻辑的宿主通道客户端。</para></param>
    /// <param name="connectKey">Target identifier the server must own.
    /// <para>必须由 server 拥有的目标标识。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.
    /// <para><paramref name="server"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="connectKey"/> is blank or does not fit the greeting field.
    /// <para><paramref name="connectKey"/> 为空或超出问候字段长度。</para></exception>
    public HdcServerSyncClient(HdcServerClient server, string connectKey)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        _serverHost = server.ServerHost;
        _serverPort = server.ServerPort;
        _connectKey = connectKey;
    }

    /// <summary>
    /// Progress in transferred bytes.
    /// <para>以已传输字节计的进度。</para>
    /// </summary>
    public event Action<long, long>? Progress;

    /// <summary>
    /// Enables LZ4 block compression for this client's transfers.
    /// <para>为本客户端的传输启用 LZ4 块压缩。</para>
    /// </summary>
    /// <value>When <see langword="true"/>, the check frame advertises compression and outgoing chunks are compressed
    /// behind the 64-byte prefix, falling back to raw bytes when compression does not shrink a chunk.
    /// <para>为 <see langword="true"/> 时，检查帧声明压缩，出站数据块在 64 字节前缀后压缩；压缩未能缩小时保持原始字节。</para></value>
    public bool Compress { get; set; }

    /// <summary>
    /// Sends one regular file, or a whole directory through <see cref="SendDirectory"/>.
    /// <para>发送单个常规文件；目录经 <see cref="SendDirectory"/> 发送。</para>
    /// </summary>
    /// <param name="localPath">Local file path; a directory is expanded into a bounded directory send.
    /// <para>本地文件路径；目录会展开为有界的目录发送。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="holdTimestamp">Preserve source timestamps; effective only for a local file.
    /// <para>保留源时间戳；仅对本地文件生效。</para></param>
    /// <param name="cancellationToken">Cancels between packets; a cancelled transfer may leave the server task incomplete.
    /// <para>在数据包之间取消；取消后 server 侧任务可能未完成。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="IOException">The server rejected the task or the channel failed.
    /// <para>server 拒绝了任务或通道失败。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void Send(string localPath, string remotePath, bool holdTimestamp = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(localPath))
        {
            SendDirectory(localPath, remotePath, holdTimestamp, cancellationToken: cancellationToken);
            return;
        }

        using var input = File.OpenRead(localPath);
        SendStream(input, remotePath, input.Length, holdTimestamp, cancellationToken);
    }

    /// <summary>
    /// Sends a bounded regular-file tree as one directory task; empty trees are rejected and empty
    /// directories are omitted. <para>作为单个目录任务发送有界的常规文件树；拒绝空树并省略空目录。</para>
    /// </summary>
    /// <param name="localPath">Root directory to enumerate; reparse points under it abort the walk.
    /// <para>要枚举的根目录；其下的重新解析点会中止遍历。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="holdTimestamp">Preserve each source file's timestamps on the device.
    /// <para>在设备上保留各源文件的时间戳。</para></param>
    /// <param name="maximumBytes">Total byte budget for all files; exceeding it fails the whole send.
    /// <para>全部文件的总字节预算；超出则整体发送失败。</para></param>
    /// <param name="maximumFiles">Maximum number of files in one task; at least 1.
    /// <para>单个任务的最大文件数；至少为 1。</para></param>
    /// <param name="cancellationToken">Cancels between files and between packets.
    /// <para>在文件之间与数据包之间取消。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative or <paramref name="maximumFiles"/> is below 1.
    /// <para><paramref name="maximumBytes"/> 为负或 <paramref name="maximumFiles"/> 小于 1。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="localPath"/> is not an existing directory.
    /// <para><paramref name="localPath"/> 不是现有目录。</para></exception>
    /// <exception cref="IOException">A link was encountered, a budget was exceeded, the tree is empty, or the channel failed.
    /// <para>遇到链接、预算超限、树为空或通道失败。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void SendDirectory(string localPath, string remotePath, bool holdTimestamp = true,
        long maximumBytes = 64L * 1024 * 1024 * 1024, int maximumFiles = 100000, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        HdcSyncClient.ValidatePath(remotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        var (root, files) = HdcTransferEngine.CollectDirectoryFiles(localPath, maximumBytes, maximumFiles, cancellationToken);
        using var channel = OpenChannel(HdcServerRemoteCommand.Build("file send", root.FullName, remotePath), cancellationToken);
        HdcTransferEngine.Expect(channel, HdcCommand.FileInit, cancellationToken);
        channel.SendCommand(HdcCommand.WakeupSlaveTask, []);
        HdcTransferEngine.SendDirectory(channel, root, files, remotePath, holdTimestamp, Compress, Progress, cancellationToken);
    }

    /// <summary>
    /// Sends the declared number of bytes without buffering the whole file.
    /// <para>发送声明的字节数，不缓冲整个文件。</para>
    /// </summary>
    /// <param name="source">Readable stream supplying exactly <paramref name="fileSize"/> bytes; a <see cref="FileStream"/> keeps its name and timestamps.
    /// <para>提供恰好 <paramref name="fileSize"/> 个可读字节的流；<see cref="FileStream"/> 会保留其名称与时间戳。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="fileSize">Exact byte count transferred; negative sizes are rejected up front.
    /// <para>精确的传输字节数；负值会被预先拒绝。</para></param>
    /// <param name="holdTimestamp">Preserve timestamps; effective only when <paramref name="source"/> is a <see cref="FileStream"/>.
    /// <para>保留时间戳；仅当 <paramref name="source"/> 是 <see cref="FileStream"/> 时生效。</para></param>
    /// <param name="cancellationToken">Cancels between bounded packets.
    /// <para>在有界数据包之间取消。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileSize"/> is negative.
    /// <para><paramref name="fileSize"/> 为负。</para></exception>
    /// <exception cref="EndOfStreamException"><paramref name="source"/> ends before <paramref name="fileSize"/> bytes are sent.
    /// <para><paramref name="source"/> 在发满 <paramref name="fileSize"/> 字节前提前结束。</para></exception>
    /// <exception cref="IOException">The server rejected the task or the channel failed.
    /// <para>server 拒绝了任务或通道失败。</para></exception>
    /// <exception cref="InvalidDataException">A server reply violates the transfer protocol.
    /// <para>server 回复违反传输协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void SendStream(Stream source, string remotePath, long fileSize, bool holdTimestamp = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        HdcSyncClient.ValidatePath(remotePath);
        var localPath = source is FileStream named ? named.Name : remotePath;
        using var channel = OpenChannel(HdcServerRemoteCommand.Build("file send", localPath, remotePath), cancellationToken);
        HdcTransferEngine.Expect(channel, HdcCommand.FileInit, cancellationToken);
        channel.SendCommand(HdcCommand.WakeupSlaveTask, []);
        HdcTransferEngine.SendStream(channel, source, remotePath, fileSize, holdTimestamp, Compress, Progress, cancellationToken);
    }

    /// <summary>
    /// Receives to a unique sibling staging file, publishing only after full protocol completion.
    /// <para>接收到唯一的同级暂存文件，仅在协议完整结束后发布。</para>
    /// </summary>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="localPath">Destination file path; it is replaced atomically on success and untouched when the transfer fails.
    /// <para>目标文件路径；成功时被原子替换，失败时保持原样。</para></param>
    /// <param name="cancellationToken">Cancels between chunks; the staging file is then deleted.
    /// <para>在数据块之间取消；取消后暂存文件会被删除。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="EndOfStreamException">The server ends the file before the declared size arrives.
    /// <para>server 在声明大小到达前提前结束文件。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created or published, or the channel fails.
    /// <para>无法创建或发布暂存文件，或通道失败。</para></exception>
    /// <exception cref="InvalidDataException">A server reply violates the transfer protocol.
    /// <para>server 回复违反传输协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void Recv(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        HdcSyncClient.ValidatePath(remotePath);
        var destination = Path.GetFullPath(localPath);
        var temp = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                RecvStreamCore(remotePath, temp, output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>
    /// Receives a single regular file, checking every chunk offset and the declared final size.
    /// <para>接收单个常规文件，校验每个数据块偏移与声明的最终大小。</para>
    /// </summary>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="destination">Writable stream receiving the contents in order; bytes already written stay on failure.
    /// <para>按序接收内容的可写流；失败时已写入的字节保留。</para></param>
    /// <param name="cancellationToken">Cancels between chunks. <para>在数据块之间取消。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="EndOfStreamException">A zero-length chunk arrives before the declared size is reached.
    /// <para>在达到声明大小前收到零长度数据块。</para></exception>
    /// <exception cref="InvalidDataException">A chunk offset or length violates the declared size, or the completion reply is malformed.
    /// <para>数据块偏移或长度违反声明大小，或完成回复格式错误。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void RecvStream(string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        HdcSyncClient.ValidatePath(remotePath);
        RecvStreamCore(remotePath, remotePath, destination, cancellationToken);
    }

    /// <summary>
    /// Receives a directory into a new local directory with bounded entries and total size; publishes only on
    /// completion. <para>将目录接收到新的本地目录，条目数与总大小均有上限；仅在完整结束后发布。</para>
    /// </summary>
    /// <param name="remotePath">Absolute device directory path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备目录路径。</para></param>
    /// <param name="localPath">New local destination directory; the staging tree is moved into place only after the server completes.
    /// <para>新的本地目标目录；仅在 server 完成后才将暂存树移动到该位置。</para></param>
    /// <param name="maximumBytes">Total byte budget across all announced entries; exceeding it fails the whole receive.
    /// <para>所有声明条目的总字节预算；超出则整体接收失败。</para></param>
    /// <param name="maximumFiles">Maximum number of files in one receive; at least 1.
    /// <para>单次接收的最大文件数；至少为 1。</para></param>
    /// <param name="cancellationToken">Cancels between entries and chunks; the staging tree is then deleted.
    /// <para>在条目与数据块之间取消；取消后暂存树会被删除。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative or <paramref name="maximumFiles"/> is below 1.
    /// <para><paramref name="maximumBytes"/> 为负或 <paramref name="maximumFiles"/> 小于 1。</para></exception>
    /// <exception cref="IOException"><paramref name="localPath"/> already exists.
    /// <para><paramref name="localPath"/> 已存在。</para></exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe, a limit is exceeded, or a reply violates the protocol.
    /// <para>条目名不安全、超出限制或回复违反协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public void RecvDirectory(string remotePath, string localPath, long maximumBytes, int maximumFiles = 100000,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        HdcSyncClient.ValidatePath(remotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        var destination = Path.GetFullPath(localPath);
        if (Path.Exists(destination)) throw new IOException("Directory receive requires a new destination.");
        var staging = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using var channel = OpenChannel(RecvCommand(remotePath, staging), cancellationToken);
            HdcTransferEngine.ReceiveDirectory(channel, staging, maximumBytes, maximumFiles, Progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>
    /// Marks this client as unusable; later transfers throw <see cref="ObjectDisposedException"/>.
    /// <para>将本客户端标记为不可用；后续传输会抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    public void Dispose() => _disposed = true;

    private void RecvStreamCore(string remotePath, string localArgument, Stream destination, CancellationToken cancellationToken)
    {
        using var channel = OpenChannel(RecvCommand(remotePath, localArgument), cancellationToken);
        HdcTransferEngine.ReceiveStream(channel, destination, Progress, cancellationToken);
    }

    private string RecvCommand(string remotePath, string localArgument)
        => Compress
            ? HdcServerRemoteCommand.Build("file recv", "-z", remotePath, localArgument)
            : HdcServerRemoteCommand.Build("file recv", remotePath, localArgument);

    private HdcServerTaskChannel OpenChannel(string command, CancellationToken cancellationToken)
    {
        using var server = new HdcServerClient(_serverHost, _serverPort);
        return HdcServerTaskChannel.OpenAsync(server, _connectKey, command, cancellationToken).GetAwaiter().GetResult();
    }
}