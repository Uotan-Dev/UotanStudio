using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Configuration payload for an HDC file transfer, serialized in a custom
/// binary format (not JSON).
/// <para>HDC 文件传输配置负载，以自定义二进制格式序列化（非 JSON）。</para>
/// </summary>
public sealed class TransferConfig
{
    /// <summary>File size in bytes. 文件大小（字节）。</summary>
    public long FileSize { get; set; }

    /// <summary>Last access time (Unix epoch nanoseconds). 最后访问时间（Unix 纪元秒）。</summary>
    public long Atime { get; set; }

    /// <summary>Last modification time (Unix epoch nanoseconds). 最后修改时间（Unix 纪元秒）。</summary>
    public long Mtime { get; set; }

    /// <summary>Upstream command option string.</summary>
    public string Options { get; set; } = "";

    /// <summary>Remote file path. 远端文件路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional display name. 可选显示名。</summary>
    public string OptionalName { get; set; } = "";

    /// <summary>Whether to update only if the source is newer. 仅在源文件更新时覆盖。</summary>
    public bool UpdateIfNew { get; set; }

    /// <summary>Compression type (0 = none). 压缩类型（0 = 无）。</summary>
    public int CompressType { get; set; }

    /// <summary>Whether to preserve the original timestamp. 是否保留原始时间戳。</summary>
    public bool HoldTimestamp { get; set; } = true;

    /// <summary>Function name for remote dispatch. 远程分派的函数名。</summary>
    public string FunctionName { get; set; } = "";

    /// <summary>Client working directory. 客户端工作目录。</summary>
    public string ClientCwd { get; set; } = "";

    /// <summary>Reserved field 1. 保留字段 1。</summary>
    public string Reserve1 { get; set; } = "";

    /// <summary>Reserved field 2. 保留字段 2。</summary>
    public string Reserve2 { get; set; } = "";
}

/// <summary>Native HDC file transfer using command envelopes, protobuf metadata and 64-byte data prefixes.</summary>
public sealed class HdcSyncClient(HdcConnection connection) : IDisposable
{
    private bool _disposed;
    /// <summary>Progress in transferred bytes.</summary>
    public event Action<long, long>? Progress;

    /// <summary>Enables upstream LZ4 block compression for this client's transfers.</summary>
    public bool Compress { get; set; }

    /// <summary>Sends one regular file. Device operations are cancellable between bounded packets.</summary>
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

    /// <summary>Sends a bounded regular-file tree using one daemon task. Like official HDC, empty trees are rejected and empty directories are omitted.</summary>
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

    /// <summary>Sends the declared number of bytes without buffering the whole file.</summary>
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

    /// <summary>Receives to a unique sibling staging file, publishing only after full protocol completion.</summary>
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

    /// <summary>Receives a single regular file, checking every chunk offset and the declared final size.</summary>
    public void RecvStream(string remotePath, Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(remotePath);
        // BeginTransfer requires both source and destination argv even though publication is local.
        using var channel = connection.OpenChannel(HdcCommand.FileInit, Encoding.UTF8.GetBytes((Compress ? "-z " : "") + "\"" + remotePath + "\" ."));
        var metadata = Expect(channel, HdcCommand.FileCheck, cancellationToken);
        var config = DeserializeTransferConfig(metadata.Payload ?? []);
        ReceiveContents(channel, config, destination, cancellationToken);
        var finished = Expect(channel, HdcCommand.FileFinish, cancellationToken);
        if (finished.Payload is not [0]) throw new InvalidDataException("Invalid HDC receive completion.");
    }

    /// <summary>Receives a directory into a new local directory with bounded entries and total size; publishes only on completion.</summary>
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
                // Upstream optionalName carries the source directory name plus each relative path.
                var segments = config.OptionalName.Replace('\\', '/').Split('/');
                if (segments.Length == 0 || segments.Any(s => s.Length == 0 || s is "." or ".."
                    || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.Contains(':') || s.EndsWith('.') || s.EndsWith(' ')))
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
        var buffer = new byte[48 * 1024];
        long offset = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - offset);
            source.ReadExactly(buffer.AsSpan(0, count));
            channel.SendCommand(command, HdcTransferCodec.EncodeChunk(offset, buffer.AsSpan(0, count), Compress));
            offset += count;
            Progress?.Invoke(offset, length);
        } while (offset < length);
    }

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
            throw new InvalidDataException($"Unexpected HDC task command {message.Command}; expected {expected}.");
        }
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] != '/' || path.Any(c => c is '\0' or '\r' or '\n' or '"'))
            throw new ArgumentException("An absolute HDC path without quotes or control characters is required.", nameof(path));
    }

    /// <summary>Serializes the upstream TransferConfig protobuf schema.</summary>
    public static byte[] SerializeTransferConfig(TransferConfig config) => HdcTransferCodec.EncodeConfig(config);
    /// <summary>Parses the upstream TransferConfig protobuf schema.</summary>
    public static TransferConfig DeserializeTransferConfig(byte[] data) => HdcTransferCodec.DecodeConfig(data);
    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}