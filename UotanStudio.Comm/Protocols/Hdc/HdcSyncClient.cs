using System.Buffers;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Configuration payload for an HDC file transfer, serialized in a custom
/// binary format (not JSON).
/// <para>HDC 文件传输配置负载，以自定义二进制格式序列化（非 JSON）。</para>
/// </summary>
public sealed class TransferConfig
{
    /// <summary>
    /// File size in bytes.
    /// <para>文件大小（字节）。</para>
    /// </summary>
    /// <value>Declared content size of the transfer; 0 lets the receive loop accept data chunks until an end-of-file chunk arrives.
    /// <para>声明的传输内容大小；为 0 时接收循环持续接受数据块，直到收到文件结束块。</para></value>
    public long FileSize { get; set; }

    /// <summary>
    /// Last access time, counted in microseconds since the Unix epoch when timestamps are held.
    /// <para>保留时间戳时，最后访问时间以自 Unix 纪元起的微秒计数。</para>
    /// </summary>
    /// <value>The access-time stamp sent to the daemon; 0 when the transfer does not hold timestamps.
    /// <para>发送给守护进程访问时间戳；传输不保留时间戳时为 0。</para></value>
    public long Atime { get; set; }

    /// <summary>
    /// Last modification time, counted in microseconds since the Unix epoch when timestamps are held.
    /// <para>保留时间戳时，最后修改时间以自 Unix 纪元起的微秒计数。</para>
    /// </summary>
    /// <value>The modification-time stamp sent to the daemon; 0 when the transfer does not hold timestamps.
    /// <para>发送给守护进程修改时间戳；传输不保留时间戳时为 0。</para></value>
    public long Mtime { get; set; }

    /// <summary>
    /// Upstream command option string.
    /// <para>上游命令选项字符串。</para>
    /// </summary>
    /// <value>Daemon-side option switches carried verbatim; transfers started by this library send an empty string.
    /// <para>原样携带的守护进程侧选项开关；本库发起的传输发送空字符串。</para></value>
    public string Options { get; set; } = "";

    /// <summary>
    /// Remote file path.
    /// <para>远端文件路径。</para>
    /// </summary>
    /// <value>The absolute device path the transfer targets. <para>传输指向的设备绝对路径。</para></value>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional display name.
    /// <para>可选显示名。</para>
    /// </summary>
    /// <value>For directory sends, the root-relative slash-separated path of each file; for single files, the plain file name.
    /// <para>目录发送时为每个文件相对根目录、以斜杠分隔的路径；单文件传输时为纯文件名。</para></value>
    public string OptionalName { get; set; } = "";

    /// <summary>
    /// Whether to update only if the source is newer.
    /// <para>仅在源文件更新时覆盖。</para>
    /// </summary>
    /// <value>Daemon-side flag selecting update-only-if-newer semantics; transfers started by this library leave it <see langword="false"/>.
    /// <para>选择“仅源更新时覆盖”语义的守护进程侧标志；本库发起的传输保持为 <see langword="false"/>。</para></value>
    public bool UpdateIfNew { get; set; }

    /// <summary>
    /// Compression type (0 = none).
    /// <para>压缩类型（0 = 无）。</para>
    /// </summary>
    /// <value>Protocol compression id; set to 1 when LZ4 compression is enabled for the transfer, otherwise 0.
    /// <para>协议压缩标识；传输启用 LZ4 压缩时为 1，否则为 0。</para></value>
    public int CompressType { get; set; }

    /// <summary>
    /// Whether to preserve the original timestamp.
    /// <para>是否保留原始时间戳。</para>
    /// </summary>
    /// <value><see langword="true"/> asks the daemon to keep the source timestamps; it is cleared automatically for streams that are not backed by a local file.
    /// <para>为 <see langword="true"/> 时请求守护进程保留源时间戳；对非本地文件流会自动清除。</para></value>
    public bool HoldTimestamp { get; set; } = true;

    /// <summary>
    /// Function name for remote dispatch.
    /// <para>远程分派的函数名。</para>
    /// </summary>
    /// <value>Upstream function-dispatch slot; file transfers send an empty string. <para>上游函数分派保留位；文件传输发送空字符串。</para></value>
    public string FunctionName { get; set; } = "";

    /// <summary>
    /// Client working directory.
    /// <para>客户端工作目录。</para>
    /// </summary>
    /// <value>Working directory of the requesting client; file transfers send an empty string.
    /// <para>请求方客户端的工作目录；文件传输发送空字符串。</para></value>
    public string ClientCwd { get; set; } = "";

    /// <summary>
    /// Reserved field 1.
    /// <para>保留字段 1。</para>
    /// </summary>
    /// <value>Unused upstream slot, serialized as an empty string. <para>未使用的上游保留位，序列化为空字符串。</para></value>
    public string Reserve1 { get; set; } = "";

    /// <summary>
    /// Reserved field 2.
    /// <para>保留字段 2。</para>
    /// </summary>
    /// <value>Unused upstream slot, serialized as an empty string. <para>未使用的上游保留位，序列化为空字符串。</para></value>
    public string Reserve2 { get; set; } = "";
}

/// <summary>
/// Native HDC file transfer using command envelopes, protobuf metadata and 64-byte data prefixes.
/// Transfers open short-lived channels on the authenticated direct connection passed at construction.
/// <para>原生 HDC 文件传输，使用命令信封、protobuf 元数据与 64 字节数据前缀。
/// 传输会在构造时传入的已认证直连上打开短生命周期通道。</para>
/// </summary>
/// <param name="connection">
/// The authenticated direct HDC connection this client opens short-lived transfer channels on.
/// <para>本客户端在其上打开短生命周期传输通道的已认证 HDC 直连。</para>
/// </param>
public sealed class HdcSyncClient(HdcConnection connection) : IDisposable, Contracts.IHdcSyncClient
{
    /// <summary>
    /// Source bytes copied per transfer chunk. The stable daemon payload ceiling is 60 KiB and every
    /// chunk reserves the 64-byte transfer metadata prefix, so one read fills a frame that stays within
    /// that legacy limit; a smaller buffer would split the same file into more, shorter frames.
    /// <para>每个传输块从源读取的字节数。守护进程稳定版负载上限为 60 KiB，而每个块都要预留 64 字节传输元数据前缀，
    /// 因此一次读取填充的帧不会超过该较早协议上限；更小的缓冲区只会把同一个文件切成更多更短的帧。</para>
    /// </summary>
    internal const int ChunkSourceBufferSize = HdcConstants.MaxPayloadSizeStable - 64;

    /// <summary>
    /// Characters a daemon-reported path segment may never contain; the set is fixed for the process,
    /// so the per-entry receive loop scans against one cached instance instead of rebuilding the
    /// platform array per segment.
    /// <para>守护进程上报的路径段绝不允许包含的字符；该集合在进程内固定不变，因此逐项接收循环只需针对
    /// 一个缓存实例扫描，而不是每段重建平台数组。</para>
    /// </summary>
    private static readonly SearchValues<char> InvalidRemoteNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private bool _disposed;

    /// <summary>
    /// Progress in transferred bytes.
    /// <para>以已传输字节计的进度。</para>
    /// </summary>
    public event Action<long, long>? Progress;

    /// <summary>
    /// Enables upstream LZ4 block compression for this client's transfers.
    /// <para>为本客户端的传输启用上游 LZ4 块压缩。</para>
    /// </summary>
    /// <value>When <see langword="true"/>, outgoing chunks are LZ4-compressed behind the 64-byte prefix and receives request compressed data; each chunk falls back to raw bytes when compression does not shrink it.
    /// <para>为 <see langword="true"/> 时，出站数据块在 64 字节前缀后进行 LZ4 压缩，接收也请求压缩数据；压缩未能缩小的数据块保持原始字节。</para></value>
    public bool Compress { get; set; }

    /// <summary>
    /// Sends one regular file. Device operations are cancellable between bounded packets.
    /// <para>发送单个常规文件。设备侧操作可在有界数据包之间取消。</para>
    /// </summary>
    /// <param name="localPath">Local file path; a directory is expanded into a bounded directory send.<para>本地文件路径；目录会展开为有界的目录发送。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.<para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="holdTimestamp">Preserve source timestamps; effective only when the source is a local file.<para>保留源时间戳；仅当源为本地文件时生效。</para></param>
    /// <param name="cancellationToken">Cancels between packets; a cancelled transfer may leave the daemon task incomplete.<para>在数据包之间取消；取消后守护进程任务可能未完成。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path. <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="localPath"/> does not exist. <para><paramref name="localPath"/> 不存在。</para></exception>
    /// <exception cref="IOException">The daemon rejects the transfer or the channel fails. <para>守护进程拒绝传输或通道失败。</para></exception>
    /// <exception cref="InvalidDataException">A daemon reply violates the transfer protocol. <para>守护进程回复违反传输协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed. <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline. <para>守护进程未在响应期限内答复。</para></exception>
    public void Send(string localPath, string remotePath, bool holdTimestamp = true, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(localPath))
        {
            SendDirectory(localPath, remotePath, holdTimestamp, cancellationToken: cancellationToken);
            return;
        }
        using var input = File.OpenRead(localPath);
        SendStream(input, remotePath, input.Length, holdTimestamp, cancellationToken);
    }

    /// <summary>Sends a bounded regular-file tree using one daemon task. Like official HDC, empty trees are rejected and empty directories are omitted.
    /// <para>使用单个守护进程任务发送有界的常规文件树。与官方 HDC 一致：拒绝空树，省略空目录。</para></summary>
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
    /// <exception cref="IOException">A link was encountered, a directory entry or byte budget was exceeded, the tree is empty, or the channel fails.
    /// <para>遇到链接、条目数或字节预算超限、树为空或通道失败。</para></exception>
    /// <exception cref="InvalidDataException">A daemon reply violates the transfer protocol.
    /// <para>守护进程回复违反传输协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline.
    /// <para>守护进程未在响应期限内答复。</para></exception>
    public void SendDirectory(string localPath, string remotePath, bool holdTimestamp = true,
        long maximumBytes = 64L * 1024 * 1024 * 1024, int maximumFiles = 100000, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(remotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        var root = new DirectoryInfo(localPath);
        if (!root.Exists) throw new DirectoryNotFoundException(localPath);
        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        long total = 0;
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC directory send does not follow links.");
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > 100000) throw new IOException("HDC directory entry limit exceeded.");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC directory send does not follow links.");
                if (item is DirectoryInfo child) pending.Push(child);
                else if (item is FileInfo file)
                {
                    total = checked(total + file.Length);
                    if (total > maximumBytes || files.Count >= maximumFiles) throw new IOException("HDC directory transfer budget exceeded.");
                    files.Add(file);
                }
            }
        }
        if (files.Count == 0) throw new IOException("HDC cannot send an empty directory.");
        using var channel = connection.OpenChannel(HdcCommand.WakeupSlaveTask, []);
        foreach (var file in files.OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = file.OpenRead();
            if (input.Length != file.Length) throw new IOException("HDC source file changed during directory preparation.");
            var config = CreateSendConfig(input, remotePath, input.Length, holdTimestamp);
            config.OptionalName = root.Name + "/" + Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            channel.SendCommand(HdcCommand.FileCheck, SerializeTransferConfig(config));
            Expect(channel, HdcCommand.FileBegin, cancellationToken);
            SendData(channel, input, input.Length, HdcCommand.FileData, cancellationToken);
            if (Expect(channel, HdcCommand.FileFinish, cancellationToken).Payload is not [1])
                throw new InvalidDataException("Invalid HDC directory file completion.");
        }
        channel.SendCommand(HdcCommand.FileFinish, [0]);
    }

    /// <summary>Sends the declared number of bytes without buffering the whole file.
    /// <para>发送声明的字节数，不缓冲整个文件。</para></summary>
    /// <param name="source">Readable stream supplying exactly <paramref name="fileSize"/> bytes; a <see cref="FileStream"/> keeps its name for the display path and timestamps.
    /// <para>提供恰好 <paramref name="fileSize"/> 个可读字节的流；<see cref="FileStream"/> 会保留其文件名用于显示路径与时间戳。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="fileSize">Exact byte count transferred; negative sizes are rejected up front.
    /// <para>精确的传输字节数；负值会被预先拒绝。</para></param>
    /// <param name="holdTimestamp">Preserve timestamps; effective only when <paramref name="source"/> is a <see cref="FileStream"/>.
    /// <para>保留时间戳；仅当 <paramref name="source"/> 是 <see cref="FileStream"/> 时生效。</para></param>
    /// <param name="cancellationToken">Cancels between bounded packets.<para>在有界数据包之间取消。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileSize"/> is negative.
    /// <para><paramref name="fileSize"/> 为负。</para></exception>
    /// <exception cref="EndOfStreamException"><paramref name="source"/> ends before <paramref name="fileSize"/> bytes are sent.
    /// <para><paramref name="source"/> 在发满 <paramref name="fileSize"/> 字节前提前结束。</para></exception>
    /// <exception cref="InvalidDataException">The daemon completion reply is not the expected single-byte value.
    /// <para>守护进程完成回复不是预期的单字节值。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline.
    /// <para>守护进程未在响应期限内答复。</para></exception>
    public void SendStream(Stream source, string remotePath, long fileSize, bool holdTimestamp = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ValidatePath(remotePath);
        var config = CreateSendConfig(source, remotePath, fileSize, holdTimestamp);
        using var channel = connection.OpenChannel(HdcCommand.FileCheck, SerializeTransferConfig(config));
        Expect(channel, HdcCommand.FileBegin, cancellationToken);
        SendData(channel, source, fileSize, HdcCommand.FileData, cancellationToken);
        var finish = Expect(channel, HdcCommand.FileFinish, cancellationToken);
        if (finish.Payload is not [1]) throw new InvalidDataException("Invalid HDC file completion.");
        channel.SendCommand(HdcCommand.FileFinish, [0]);
    }

    private TransferConfig CreateSendConfig(Stream source, string remotePath, long fileSize, bool holdTimestamp)
    {
        var config = new TransferConfig
        {
            FileSize = fileSize,
            Path = remotePath,
            OptionalName = source is FileStream named ? Path.GetFileName(named.Name) : remotePath.TrimEnd('/').Split('/')[^1],
            CompressType = Compress ? 1 : 0,
            HoldTimestamp = holdTimestamp && source is FileStream
        };
        if (config.HoldTimestamp && source is FileStream file)
        {
            config.Atime = checked((File.GetLastAccessTimeUtc(file.Name).Ticks - DateTime.UnixEpoch.Ticks) * 100);
            config.Mtime = checked((File.GetLastWriteTimeUtc(file.Name).Ticks - DateTime.UnixEpoch.Ticks) * 100);
        }
        return config;
    }

    /// <summary>Receives to a unique sibling staging file, publishing only after full protocol completion.
    /// <para>接收到唯一的同级暂存文件，仅在协议完整结束后发布。</para></summary>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="localPath">Destination file path; it is replaced atomically on success and untouched when the transfer fails.
    /// <para>目标文件路径；成功时被原子替换，失败时保持原样。</para></param>
    /// <param name="cancellationToken">Cancels between chunks; the staging file is then deleted.
    /// <para>在数据块之间取消；取消后暂存文件会被删除。</para></param>
    /// <exception cref="ArgumentException"><paramref name="remotePath"/> is not an absolute, control-character-free device path.
    /// <para><paramref name="remotePath"/> 不是绝对且不含控制字符的设备路径。</para></exception>
    /// <exception cref="EndOfStreamException">The daemon ends the file before the declared size arrives.
    /// <para>守护进程在声明大小到达前提前结束文件。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created or published, or the channel fails.
    /// <para>无法创建或发布暂存文件，或通道失败。</para></exception>
    /// <exception cref="InvalidDataException">A daemon reply violates the transfer protocol.
    /// <para>守护进程回复违反传输协议。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline.
    /// <para>守护进程未在响应期限内答复。</para></exception>
    public void Recv(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(localPath);
        var temp = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                RecvStream(remotePath, output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Receives a single regular file, checking every chunk offset and the declared final size.
    /// <para>接收单个常规文件，校验每个数据块偏移与声明的最终大小。</para></summary>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="destination">Writable stream receiving the contents in order; bytes already written stay on failure.
    /// <para>按序接收内容的可写流；失败时已写入的字节保留。</para></param>
    /// <param name="cancellationToken">Cancels between chunks.<para>在数据块之间取消。</para></param>
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
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline.
    /// <para>守护进程未在响应期限内答复。</para></exception>
    public void RecvStream(string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(remotePath);
        using var channel = connection.OpenChannel(HdcCommand.FileInit, Encoding.UTF8.GetBytes((Compress ? "-z " : "") + "\"" + remotePath + "\" ."));
        var metadata = Expect(channel, HdcCommand.FileCheck, cancellationToken);
        var config = DeserializeTransferConfig(metadata.Payload ?? []);
        ReceiveContents(channel, config, destination, cancellationToken);
        var finished = Expect(channel, HdcCommand.FileFinish, cancellationToken);
        if (finished.Payload is not [0]) throw new InvalidDataException("Invalid HDC receive completion.");
    }

    /// <summary>Receives a directory into a new local directory with bounded entries and total size; publishes only on completion.
    /// <para>将目录接收到新的本地目录，条目数与总大小均有上限；仅在完整结束后发布。</para></summary>
    /// <param name="remotePath">Absolute device directory path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备目录路径。</para></param>
    /// <param name="localPath">New local destination directory; the staging tree is moved into place only after the daemon completes.
    /// <para>新的本地目标目录；仅在守护进程完成后才将暂存树移动到该位置。</para></param>
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
    /// <exception cref="EndOfStreamException">The daemon ends a file before its declared size arrives.
    /// <para>守护进程在声明大小到达前提前结束文件。</para></exception>
    /// <exception cref="IOException"><paramref name="localPath"/> already exists.
    /// <para><paramref name="localPath"/> 已存在。</para></exception>
    /// <exception cref="InvalidDataException">An entry name is unsafe, a limit is exceeded, or a reply violates the protocol.
    /// <para>条目名不安全、超出限制或回复违反协议。</para></exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.
    /// <para>本客户端已释放。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    /// <exception cref="TimeoutException">The daemon did not answer within the response deadline.
    /// <para>守护进程未在响应期限内答复。</para></exception>
    public void RecvDirectory(string remotePath, string localPath, long maximumBytes, int maximumFiles = 100000, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(remotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        var destination = Path.GetFullPath(localPath);
        if (Path.Exists(destination)) throw new IOException("Directory receive requires a new destination.");
        var staging = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using var channel = connection.OpenChannel(HdcCommand.FileInit,
                Encoding.UTF8.GetBytes((Compress ? "-z " : "") + "\"" + remotePath + "\" ."));
            long total = 0;
            var count = 0;
            while (true)
            {
                var message = Expect(channel, HdcCommand.FileCheck, cancellationToken, alternative: HdcCommand.FileFinish);
                if (message.Command == HdcCommand.FileFinish)
                {
                    if (message.Payload is not [0] || count == 0) throw new InvalidDataException("Invalid directory completion.");
                    break;
                }
                var config = DeserializeTransferConfig(message.Payload ?? []);
                if (++count > maximumFiles || config.FileSize > maximumBytes - total)
                    throw new InvalidDataException("HDC directory receive exceeds the requested limit.");
                total += config.FileSize;
                var segments = config.OptionalName.Replace('\\', '/').Split('/');
                if (segments.Length == 0 || segments.Any(s => s.Length == 0 || s is "." or ".."
                    || s.AsSpan().ContainsAny(InvalidRemoteNameChars) || s.Contains(':') || s.EndsWith('.') || s.EndsWith(' ')))
                    throw new InvalidDataException("Unsafe HDC directory entry name.");
                var outputPath = Path.Combine(staging, Path.Combine(segments));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ReceiveContents(channel, config, output, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private void ReceiveContents(HdcStream channel, TransferConfig config, Stream destination, CancellationToken cancellationToken)
    {
        if (config.FileSize < 0) throw new InvalidDataException("Invalid HDC file size.");
        channel.SendCommand(HdcCommand.FileBegin, []);
        long received = 0;
        while (received < config.FileSize || config.FileSize == 0)
        {
            var message = Expect(channel, HdcCommand.FileData, cancellationToken);
            var payload = message.Payload ?? [];
            var chunk = HdcTransferCodec.DecodeChunk(payload);
            if (chunk.Offset != received || chunk.Data.Length > config.FileSize - received)
                throw new InvalidDataException("HDC file chunk exceeds expected range.");
            if (chunk.Data.Length == 0 && received != config.FileSize)
                throw new EndOfStreamException("Premature HDC file EOF.");
            destination.Write(chunk.Data);
            received += chunk.Data.Length;
            Progress?.Invoke(received, config.FileSize);
            if (received == config.FileSize) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        channel.SendCommand(HdcCommand.FileFinish, [1]);
    }

    internal void SendData(HdcStream channel, Stream source, long length, HdcCommand command, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSourceBufferSize);
        long offset = 0;
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSourceBufferSize, length - offset);
                source.ReadExactly(buffer.AsSpan(0, count));
                channel.SendCommand(command, HdcTransferCodec.EncodeChunk(offset, buffer.AsSpan(0, count), Compress));
                offset += count;
                Progress?.Invoke(offset, length);
            } while (offset < length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Waits for one of the expected task commands, skipping echo traffic and the zero-length FILE_DATA that ends a
    /// file. <para>等待预期任务命令之一，跳过回显流量与结束文件的零长度 FILE_DATA。</para>
    /// </summary>
    /// <param name="channel">The transfer channel. <para>传输通道。</para></param>
    /// <param name="expected">The command being awaited. <para>正在等待的命令。</para></param>
    /// <param name="ct">Cancels the wait. <para>取消等待。</para></param>
    /// <param name="timeoutMs">Reply deadline in milliseconds. <para>应答期限（毫秒）。</para></param>
    /// <param name="alternative">An optional second accepted command. <para>可选的第二个可接受命令。</para></param>
    /// <returns>The received message. <para>收到的消息。</para></returns>
    /// <exception cref="TimeoutException">The deadline passed without a matching command. <para>期限到达仍无匹配命令。</para></exception>
    /// <exception cref="EndOfStreamException">The channel closed before the matching command. <para>通道在匹配命令之前关闭。</para></exception>
    /// <exception cref="InvalidDataException">Another command arrived first. <para>先收到了其它命令。</para></exception>
    internal static HdcMessage Expect(HdcStream channel, HdcCommand expected, CancellationToken ct, int timeoutMs = 30000, HdcCommand? alternative = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("HDC task response timed out.");
            var message = channel.ReadMessage(250);
            if (message is null)
            {
                if (channel.IsClosed) throw new EndOfStreamException("HDC task closed before completion.");
                continue;
            }
            if (message.Command == expected || message.Command == alternative) return message;
            if (message.Command is HdcCommand.Echo or HdcCommand.EchoRaw) continue;
            if (IsEndOfFileMarker(message, expected, alternative)) continue;
            throw new InvalidDataException($"Unexpected HDC task command {message.Command}; expected {expected}.");
        }
    }

    /// <summary>
    /// Reports whether a message is the zero-length FILE_DATA the daemon sends together with FILE_FINISH to end a
    /// file, while a FILE_FINISH is awaited. The marker is dropped instead of failing the transfer, because the
    /// receive loops stop at the declared size and the completion frame may follow one more empty chunk.
    /// <para>判断消息是否是在等待 FILE_FINISH 期间守护进程与 FILE_FINISH 一同发送的零长度 FILE_DATA。该标记被丢弃
    /// 而不是令传输失败：接收循环在达到声明大小时停止，而完成帧之前可能还有一个空数据块。</para>
    /// </summary>
    /// <param name="message">The message read from the channel. <para>从通道读取的消息。</para></param>
    /// <param name="expected">The command being awaited. <para>正在等待的命令。</para></param>
    /// <param name="alternative">The optional second command being awaited. <para>可选的第二个正在等待的命令。</para></param>
    /// <returns><see langword="true"/> when the message is an end-of-file marker. <para>消息是文件结束标记时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidDataException">A non-empty FILE_DATA payload is not a valid transfer chunk.
    /// <para>非空 FILE_DATA 负载不是合法的传输数据块。</para></exception>
    private static bool IsEndOfFileMarker(HdcMessage message, HdcCommand expected, HdcCommand? alternative)
    {
        if (message.Command != HdcCommand.FileData
            || (expected != HdcCommand.FileFinish && alternative != HdcCommand.FileFinish))
        {
            return false;
        }

        var payload = message.Payload;
        if (payload is not { Length: > 0 })
        {
            return true;
        }

        return HdcTransferCodec.DecodeChunk(payload).Data.Length == 0;
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] != '/' || path.Any(c => c is '\0' or '\r' or '\n' or '"'))
            throw new ArgumentException("An absolute HDC path without quotes or control characters is required.", nameof(path));
    }

    /// <summary>
    /// Serializes the upstream TransferConfig protobuf schema.
    /// <para>按上游 TransferConfig 的 protobuf 模式序列化。</para>
    /// </summary>
    /// <param name="config">Configuration to encode; fields become tagged protobuf numbers and UTF-8 strings.<para>待编码的配置；各字段编码为带标签的 protobuf 数值与 UTF-8 字符串。</para></param>
    /// <returns>The encoded configuration blob sent alongside file commands.<para>随文件命令一起发送的编码配置数据。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="config"/> has a negative <see cref="TransferConfig.FileSize"/>. <para><paramref name="config"/> 的 <see cref="TransferConfig.FileSize"/> 为负。</para></exception>
    /// <exception cref="OverflowException">A timestamp or compression value does not fit the unsigned wire range. <para>时间戳或压缩值超出无符号线格式范围。</para></exception>
    public static byte[] SerializeTransferConfig(TransferConfig config) => HdcTransferCodec.EncodeConfig(config);

    /// <summary>
    /// Parses the upstream TransferConfig protobuf schema.
    /// <para>解析上游 TransferConfig 的 protobuf 模式。</para>
    /// </summary>
    /// <param name="data">Raw envelope payload produced by the daemon.<para>由守护进程产生的原始信封负载。</para></param>
    /// <returns>The decoded configuration with every required field populated.<para>已填充全部必需字段的解码配置。</para></returns>
    /// <exception cref="InvalidDataException">The blob is malformed or a required string field is missing. <para>数据格式错误或缺少必需的字符串字段。</para></exception>
    /// <exception cref="OverflowException">A numeric field exceeds the signed range of its target property. <para>数值字段超出目标属性的有符号范围。</para></exception>
    public static TransferConfig DeserializeTransferConfig(byte[] data) => HdcTransferCodec.DecodeConfig(data);

    /// <summary>
    /// Marks this client as unusable; later transfers throw <see cref="ObjectDisposedException"/>.
    /// <para>将本客户端标记为不可用；后续传输会抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    public void Dispose() => _disposed = true;
}