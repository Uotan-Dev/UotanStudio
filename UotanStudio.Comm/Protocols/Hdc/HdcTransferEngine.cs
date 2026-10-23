using System.Buffers;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Shared HDC file-transfer state machine driven over an <see cref="IHdcTaskChannel"/>.
/// <para>在 <see cref="IHdcTaskChannel"/> 上驱动的共享 HDC 文件传输状态机。</para>
/// </summary>
internal static class HdcTransferEngine
{
    /// <summary>
    /// Source bytes copied per transfer chunk. The stable daemon payload ceiling is 60 KiB and every chunk reserves
    /// the 64-byte transfer metadata prefix, so one read fills a frame that stays within that legacy limit.
    /// <para>每个传输块从源读取的字节数。守护进程稳定版负载上限为 60 KiB，而每个块都要预留 64 字节传输元数据前缀，
    /// 因此一次读取填充的帧不会超过该较早协议上限。</para>
    /// </summary>
    internal const int ChunkSourceBufferSize = HdcConstants.MaxPayloadSizeStable - 64;

    /// <summary>
    /// Characters a daemon-reported path segment may never contain; the set is fixed for the process,
    /// so the per-entry receive loop scans against one cached instance.
    /// <para>守护进程上报的路径段绝不允许包含的字符；该集合在进程内固定不变，因此逐项接收循环只针对一个缓存实例扫描。</para>
    /// </summary>
    private static readonly SearchValues<char> InvalidRemoteNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

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
    internal static HdcMessage Expect(IHdcTaskChannel channel, HdcCommand expected, CancellationToken ct,
        int timeoutMs = 30000, HdcCommand? alternative = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("HDC task response timed out.");
            var message = channel.ReadMessage(250, ct);
            if (message is null)
            {
                if (channel.IsClosed) throw new EndOfStreamException("HDC task closed before completion.");
                continue;
            }

            if (message.Command == expected || message.Command == alternative) return message;
            if (message.Command is HdcCommand.Echo or HdcCommand.EchoRaw or HdcCommand.WakeupSlaveTask) continue;
            if (IsEndOfFileMarker(message, expected, alternative)) continue;
            throw new InvalidDataException($"Unexpected HDC task command {message.Command}; expected {expected}.");
        }
    }

    /// <summary>
    /// Builds the transfer configuration advertised for an outgoing file.
    /// <para>为出站文件构建所声明的传输配置。</para>
    /// </summary>
    /// <param name="source">Readable source; a <see cref="FileStream"/> keeps its name and timestamps.
    /// <para>可读源；<see cref="FileStream"/> 会保留其名称与时间戳。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="fileSize">Exact byte count to transfer. <para>要传输的精确字节数。</para></param>
    /// <param name="holdTimestamp">Preserve timestamps; effective only for a <see cref="FileStream"/>.
    /// <para>保留时间戳；仅对 <see cref="FileStream"/> 生效。</para></param>
    /// <param name="compress">Advertise upstream LZ4 block compression. <para>声明上游 LZ4 块压缩。</para></param>
    /// <returns>The configuration carried by the file check frame. <para>文件检查帧携带的配置。</para></returns>
    internal static TransferConfig CreateSendConfig(Stream source, string remotePath, long fileSize,
        bool holdTimestamp, bool compress)
    {
        var config = new TransferConfig
        {
            FileSize = fileSize,
            Path = remotePath,
            OptionalName = source is FileStream named ? Path.GetFileName(named.Name) : remotePath.TrimEnd('/').Split('/')[^1],
            CompressType = compress ? 1 : 0,
            HoldTimestamp = holdTimestamp && source is FileStream
        };
        if (config.HoldTimestamp && source is FileStream file)
        {
            config.Atime = checked((File.GetLastAccessTimeUtc(file.Name).Ticks - DateTime.UnixEpoch.Ticks) * 100);
            config.Mtime = checked((File.GetLastWriteTimeUtc(file.Name).Ticks - DateTime.UnixEpoch.Ticks) * 100);
        }

        return config;
    }

    /// <summary>
    /// Sends one file through a channel whose task was opened by this call: file check, begin, data and completion.
    /// <para>通过本次调用打开任务的通道发送单个文件：文件检查、开始、数据与完成。</para>
    /// </summary>
    /// <param name="channel">Task channel of the file task. <para>文件任务的通道。</para></param>
    /// <param name="source">Readable stream supplying exactly <paramref name="fileSize"/> bytes.
    /// <para>提供恰好 <paramref name="fileSize"/> 个可读字节的流。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="fileSize">Exact byte count transferred. <para>精确的传输字节数。</para></param>
    /// <param name="holdTimestamp">Preserve timestamps when the source is a local file.
    /// <para>源为本地文件时保留时间戳。</para></param>
    /// <param name="compress">Compress outgoing chunks. <para>压缩出站数据块。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between bounded packets. <para>在有界数据包之间取消。</para></param>
    internal static void SendStream(IHdcTaskChannel channel, Stream source, string remotePath, long fileSize,
        bool holdTimestamp, bool compress, Action<long, long>? progress, CancellationToken ct)
    {
        var config = CreateSendConfig(source, remotePath, fileSize, holdTimestamp, compress);
        channel.SendCommand(HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(config));
        SendStreamBody(channel, source, fileSize, compress, progress, ct);
    }

    /// <summary>
    /// Sends the data of one file task whose check frame is already on the wire.
    /// <para>发送文件检查帧已上线的单个文件任务的数据。</para>
    /// </summary>
    /// <param name="channel">Task channel of the file task. <para>文件任务的通道。</para></param>
    /// <param name="source">Readable stream supplying exactly <paramref name="fileSize"/> bytes.
    /// <para>提供恰好 <paramref name="fileSize"/> 个可读字节的流。</para></param>
    /// <param name="fileSize">Exact byte count transferred. <para>精确的传输字节数。</para></param>
    /// <param name="compress">Compress outgoing chunks. <para>压缩出站数据块。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between bounded packets. <para>在有界数据包之间取消。</para></param>
    internal static void SendStreamBody(IHdcTaskChannel channel, Stream source, long fileSize, bool compress,
        Action<long, long>? progress, CancellationToken ct)
    {
        Expect(channel, HdcCommand.FileBegin, ct);
        SendData(channel, source, fileSize, HdcCommand.FileData, compress, progress, ct);
        var finish = Expect(channel, HdcCommand.FileFinish, ct);
        if (finish.Payload is not [1]) throw new InvalidDataException("Invalid HDC file completion.");
        channel.SendCommand(HdcCommand.FileFinish, [0]);
    }

    /// <summary>
    /// Enumerates a bounded regular-file tree for one directory task.
    /// <para>为单个目录任务枚举有界的常规文件树。</para>
    /// </summary>
    /// <param name="localPath">Root directory to enumerate; reparse points under it fail the walk.
    /// <para>要枚举的根目录；其下的重新解析点会使遍历失败。</para></param>
    /// <param name="maximumBytes">Total byte budget for all files. <para>全部文件的总字节预算。</para></param>
    /// <param name="maximumFiles">Maximum number of files in one task; at least 1.
    /// <para>单个任务的最大文件数；至少为 1。</para></param>
    /// <param name="ct">Cancels the walk between entries. <para>在条目之间取消遍历。</para></param>
    /// <returns>The root and its files in walk order. <para>根目录及其按遍历顺序排列的文件。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="localPath"/> is not an existing directory.
    /// <para><paramref name="localPath"/> 不是现有目录。</para></exception>
    /// <exception cref="IOException">A link was encountered, a budget was exceeded or the tree is empty.
    /// <para>遇到链接、预算超限或树为空。</para></exception>
    internal static (DirectoryInfo Root, List<FileInfo> Files) CollectDirectoryFiles(string localPath,
        long maximumBytes, int maximumFiles, CancellationToken ct)
    {
        var root = new DirectoryInfo(localPath);
        if (!root.Exists) throw new DirectoryNotFoundException(localPath);
        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        long total = 0;
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("HDC directory send does not follow links.");
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 100000) throw new IOException("HDC directory entry limit exceeded.");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("HDC directory send does not follow links.");
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (item is FileInfo file)
                {
                    total = checked(total + file.Length);
                    if (total > maximumBytes || files.Count >= maximumFiles)
                        throw new IOException("HDC directory transfer budget exceeded.");
                    files.Add(file);
                }
            }
        }

        if (files.Count == 0) throw new IOException("HDC cannot send an empty directory.");
        return (root, files);
    }

    /// <summary>
    /// Sends a bounded file tree through an already opened directory task.
    /// <para>通过已打开的目录任务发送有界的文件树。</para>
    /// </summary>
    /// <param name="channel">Task channel of the directory task. <para>目录任务的通道。</para></param>
    /// <param name="root">Root directory the relative entry names are computed from.
    /// <para>计算条目相对路径所用的根目录。</para></param>
    /// <param name="files">Files enumerated from <paramref name="root"/>.
    /// <para>从 <paramref name="root"/> 枚举出的文件。</para></param>
    /// <param name="remotePath">Absolute device path without quotes or control characters.
    /// <para>不含引号或控制字符的绝对设备路径。</para></param>
    /// <param name="holdTimestamp">Preserve each source file's timestamps. <para>保留各源文件的时间戳。</para></param>
    /// <param name="compress">Compress outgoing chunks. <para>压缩出站数据块。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between files and between packets. <para>在文件之间与数据包之间取消。</para></param>
    internal static void SendDirectory(IHdcTaskChannel channel, DirectoryInfo root, List<FileInfo> files,
        string remotePath, bool holdTimestamp, bool compress, Action<long, long>? progress, CancellationToken ct)
    {
        foreach (var file in files.OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            using var input = file.OpenRead();
            if (input.Length != file.Length) throw new IOException("HDC source file changed during directory preparation.");
            var config = CreateSendConfig(input, remotePath, input.Length, holdTimestamp, compress);
            config.OptionalName = root.Name + "/" + Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            channel.SendCommand(HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(config));
            Expect(channel, HdcCommand.FileBegin, ct);
            SendData(channel, input, input.Length, HdcCommand.FileData, compress, progress, ct);
            if (Expect(channel, HdcCommand.FileFinish, ct).Payload is not [1])
                throw new InvalidDataException("Invalid HDC directory file completion.");
        }

        channel.SendCommand(HdcCommand.FileFinish, [0]);
    }

    /// <summary>
    /// Receives one regular file from an already opened receive task.
    /// <para>从已打开的接收任务中接收单个常规文件。</para>
    /// </summary>
    /// <param name="channel">Task channel of the receive task. <para>接收任务的通道。</para></param>
    /// <param name="destination">Writable stream receiving the contents in order. <para>按序接收内容的可写流。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between chunks. <para>在数据块之间取消。</para></param>
    internal static void ReceiveStream(IHdcTaskChannel channel, Stream destination, Action<long, long>? progress,
        CancellationToken ct)
    {
        var metadata = Expect(channel, HdcCommand.FileCheck, ct);
        var config = HdcTransferCodec.DecodeConfig(metadata.Payload ?? []);
        ReceiveContents(channel, config, destination, progress, ct);
        var finished = Expect(channel, HdcCommand.FileFinish, ct);
        if (finished.Payload is not [0]) throw new InvalidDataException("Invalid HDC receive completion.");
    }

    /// <summary>
    /// Receives every announced entry of a directory task into a staging directory.
    /// <para>将目录任务声明的全部条目接收到暂存目录。</para>
    /// </summary>
    /// <param name="channel">Task channel of the directory receive. <para>目录接收任务的通道。</para></param>
    /// <param name="staging">Existing staging directory that receives the tree. <para>接收该树的现有暂存目录。</para></param>
    /// <param name="maximumBytes">Total byte budget across all announced entries. <para>所有声明条目的总字节预算。</para></param>
    /// <param name="maximumFiles">Maximum number of files in one receive; at least 1. <para>单次接收的最大文件数；至少为 1。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between entries and chunks. <para>在条目与数据块之间取消。</para></param>
    /// <exception cref="InvalidDataException">An entry name is unsafe, a limit is exceeded or a reply violates the protocol.
    /// <para>条目名不安全、超出限制或回复违反协议。</para></exception>
    internal static void ReceiveDirectory(IHdcTaskChannel channel, string staging, long maximumBytes, int maximumFiles,
        Action<long, long>? progress, CancellationToken ct)
    {
        long total = 0;
        var count = 0;
        while (true)
        {
            var message = Expect(channel, HdcCommand.FileCheck, ct, alternative: HdcCommand.FileFinish);
            if (message.Command == HdcCommand.FileFinish)
            {
                if (message.Payload is not [0] || count == 0) throw new InvalidDataException("Invalid directory completion.");
                break;
            }

            var config = HdcTransferCodec.DecodeConfig(message.Payload ?? []);
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
            ReceiveContents(channel, config, output, progress, ct);
        }
    }

    /// <summary>
    /// Acknowledges one announced file and writes its chunks until the declared size arrives.
    /// <para>确认一个已声明的文件，并写入其数据块直到声明大小到达。</para>
    /// </summary>
    /// <param name="channel">Task channel of the receive task. <para>接收任务的通道。</para></param>
    /// <param name="config">Announced metadata of the file. <para>该文件声明的元数据。</para></param>
    /// <param name="destination">Writable stream receiving the contents. <para>接收内容的可写流。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between chunks. <para>在数据块之间取消。</para></param>
    internal static void ReceiveContents(IHdcTaskChannel channel, TransferConfig config, Stream destination,
        Action<long, long>? progress, CancellationToken ct)
    {
        if (config.FileSize < 0) throw new InvalidDataException("Invalid HDC file size.");
        channel.SendCommand(HdcCommand.FileBegin, []);
        long received = 0;
        while (received < config.FileSize || config.FileSize == 0)
        {
            var message = Expect(channel, HdcCommand.FileData, ct);
            var payload = message.Payload ?? [];
            var chunk = HdcTransferCodec.DecodeChunk(payload);
            if (chunk.Offset != received || chunk.Data.Length > config.FileSize - received)
                throw new InvalidDataException("HDC file chunk exceeds expected range.");
            if (chunk.Data.Length == 0 && received != config.FileSize)
                throw new EndOfStreamException("Premature HDC file EOF.");
            destination.Write(chunk.Data);
            received += chunk.Data.Length;
            progress?.Invoke(received, config.FileSize);
            if (received == config.FileSize) break;
        }

        ct.ThrowIfCancellationRequested();
        channel.SendCommand(HdcCommand.FileFinish, [1]);
    }

    /// <summary>
    /// Streams one file as ordered offset-tagged chunks.
    /// <para>将单个文件按有序的偏移标记数据块流式发送。</para>
    /// </summary>
    /// <param name="channel">Task channel of the transfer. <para>传输任务的通道。</para></param>
    /// <param name="source">Readable stream supplying exactly <paramref name="length"/> bytes.
    /// <para>提供恰好 <paramref name="length"/> 个可读字节的流。</para></param>
    /// <param name="length">Exact byte count to send. <para>要发送的精确字节数。</para></param>
    /// <param name="command">Data command carried by each chunk frame. <para>每个数据块帧携带的数据命令。</para></param>
    /// <param name="compress">Compress each chunk when it shrinks. <para>压缩每个能变小的数据块。</para></param>
    /// <param name="progress">Optional per-chunk progress callback. <para>可选的逐块进度回调。</para></param>
    /// <param name="ct">Cancels between chunks. <para>在数据块之间取消。</para></param>
    internal static void SendData(IHdcTaskChannel channel, Stream source, long length, HdcCommand command,
        bool compress, Action<long, long>? progress, CancellationToken ct)
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
                channel.SendCommand(command, HdcTransferCodec.EncodeChunk(offset, buffer.AsSpan(0, count), compress));
                offset += count;
                progress?.Invoke(offset, length);
            } while (offset < length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
}