using System.Buffers.Binary;
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

    /// <summary>Last access time (Unix epoch seconds). 最后访问时间（Unix 纪元秒）。</summary>
    public long Atime { get; set; }

    /// <summary>Last modification time (Unix epoch seconds). 最后修改时间（Unix 纪元秒）。</summary>
    public long Mtime { get; set; }

    /// <summary>Transfer options bitmask. 传输选项位掩码。</summary>
    public int Options { get; set; }

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

/// <summary>
/// Client for the HDC file transfer protocol. Supports push (send) and pull
/// (receive) operations using a custom binary framing with streaming IO.
/// <para>HDC 文件传输协议客户端。支持推送（发送）和拉取（接收）操作，
/// 使用自定义二进制帧格式与流式 IO。</para>
/// </summary>
/// <remarks>
/// Initializes a new sync client.
/// <para>初始化新的 sync 客户端。</para>
/// </remarks>
/// <param name="connection">The HDC connection. HDC 连接。</param>
public sealed class HdcSyncClient(HdcConnection connection) : IDisposable
{
    private const int MaxPayloadSize = 64 * 1024;

    private readonly HdcConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private HdcStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Ensures the file-transfer channel is open, opening it if needed.
    /// <para>确保文件传输通道已打开，需要时打开。</para>
    /// </summary>
    private HdcStream EnsureStream()
    {
        _stream ??= _connection.OpenChannel(HdcCommand.FileInit);
        return _stream;
    }

    /// <summary>
    /// Sends a local file to the device.
    /// <para>将本地文件发送到设备。</para>
    /// </summary>
    /// <param name="localPath">Path to the local file. 本地文件路径。</param>
    /// <param name="remotePath">Destination path on the device. 设备上的目标路径。</param>
    /// <param name="holdTimestamp">Whether to preserve the original timestamp. 是否保留原始时间戳。</param>
    public void Send(string localPath, string remotePath, bool holdTimestamp = true)
    {
        if (localPath is null)
        {
            throw new ArgumentNullException(nameof(localPath));
        }

        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        var fileInfo = new FileInfo(localPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Local file not found.", localPath);
        }

        using var fs = File.OpenRead(localPath);
        SendStream(fs, remotePath, fileInfo.Length, holdTimestamp);
    }

    /// <summary>
    /// Sends data from a stream to the device.
    /// <para>将流中的数据发送到设备。</para>
    /// </summary>
    /// <param name="source">The source stream. 源流。</param>
    /// <param name="remotePath">Destination path on the device. 设备上的目标路径。</param>
    /// <param name="fileSize">Total file size in bytes. 文件总大小（字节）。</param>
    /// <param name="holdTimestamp">Whether to preserve the original timestamp. 是否保留原始时间戳。</param>
    public void SendStream(Stream source, string remotePath, long fileSize, bool holdTimestamp = true)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        var stream = EnsureStream();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Step 1: Send FileInit with TransferConfig.
        // 步骤 1：发送 FileInit + TransferConfig。
        var config = new TransferConfig
        {
            FileSize = fileSize,
            Atime = now,
            Mtime = now,
            Path = remotePath,
            HoldTimestamp = holdTimestamp,
        };
        var configBytes = SerializeTransferConfig(config);
        stream.Write(BuildFrame(HdcCommand.FileInit, configBytes));

        // Step 2: Wait for FileCheck acknowledgement.
        // 步骤 2：等待 FileCheck 确认。
        ExpectCommand(HdcCommand.FileCheck, "send:init");

        // Step 3: Signal begin.
        // 步骤 3：发送开始信号。
        stream.Write(BuildFrame(HdcCommand.FileBegin, []));

        // Step 4: Stream file data in chunks.
        // 步骤 4：分块发送文件数据。
        var buffer = new byte[MaxPayloadSize];
        long totalSent = 0;
        while (totalSent < fileSize)
        {
            var toRead = (int)Math.Min(MaxPayloadSize, fileSize - totalSent);
            var bytesRead = source.Read(buffer, 0, toRead);
            if (bytesRead <= 0)
            {
                throw new EndOfStreamException(
                    $"Source stream ended after {totalSent} bytes, expected {fileSize}.");
            }

            var chunk = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
            stream.Write(BuildFrame(HdcCommand.FileData, chunk));
            totalSent += bytesRead;
        }

        // Step 5: Signal finish.
        // 步骤 5：发送完成信号。
        stream.Write(BuildFrame(HdcCommand.FileFinish, []));
    }

    /// <summary>
    /// Receives a file from the device into a local file.
    /// <para>从设备接收文件到本地文件。</para>
    /// </summary>
    /// <param name="remotePath">Source path on the device. 设备上的源路径。</param>
    /// <param name="localPath">Destination path on the host. 主机上的目标路径。</param>
    public void Recv(string remotePath, string localPath)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        if (localPath is null)
        {
            throw new ArgumentNullException(nameof(localPath));
        }

        // Write to a temporary file first, then atomically replace.
        // 先写入临时文件，再原子替换。
        var tempPath = localPath + ".tmp";
        try
        {
            using (var fs = File.Create(tempPath))
            {
                RecvStream(remotePath, fs);
            }

            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* cleanup best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Receives a file from the device into a stream.
    /// <para>从设备接收文件到流中。</para>
    /// </summary>
    /// <param name="remotePath">Source path on the device. 设备上的源路径。</param>
    /// <param name="destination">The destination stream. 目标流。</param>
    public void RecvStream(string remotePath, Stream destination)
    {
        if (remotePath is null)
        {
            throw new ArgumentNullException(nameof(remotePath));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        var stream = EnsureStream();

        // Step 1: Send FileInit with the remote path.
        // 步骤 1：发送包含远端路径的 FileInit。
        var config = new TransferConfig { Path = remotePath };
        var configBytes = SerializeTransferConfig(config);
        stream.Write(BuildFrame(HdcCommand.FileInit, configBytes));

        // Step 2: Receive FileCheck — contains TransferConfig with file metadata.
        // 步骤 2：接收 FileCheck — 包含文件元数据的 TransferConfig。
        var checkPayload = ExpectCommand(HdcCommand.FileCheck, "recv:init");
        long expectedSize = -1;
        if (checkPayload.Length > 0)
        {
            var receivedConfig = DeserializeTransferConfig(checkPayload);
            expectedSize = receivedConfig.FileSize;
        }

        // Step 3: Receive FileData chunks until FileFinish.
        // 步骤 3：接收 FileData 数据块直到 FileFinish。
        long totalReceived = 0;
        while (true)
        {
            var payload = ReadNextFrame(out var cmd);
            if (cmd == HdcCommand.FileFinish)
            {
                break;
            }

            if (cmd == HdcCommand.FileData)
            {
                destination.Write(payload, 0, payload.Length);
                totalReceived += payload.Length;
                continue;
            }

            throw new InvalidDataException(
                $"Unexpected command {cmd} during file receive (expected FileData or FileFinish).");
        }

        if (expectedSize >= 0 && totalReceived != expectedSize)
        {
            throw new InvalidDataException(
                $"File transfer size mismatch: received {totalReceived} bytes, expected {expectedSize}.");
        }
    }

    /// <summary>
    /// Closes the file-transfer channel and releases resources.
    /// <para>关闭文件传输通道并释放资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var stream = _stream;
        _stream = null;

        if (stream is not null && !stream.IsClosed)
        {
            try
            {
                stream.Write(BuildFrame(HdcCommand.ChannelClose, []));
            }
            catch { /* best-effort close */ }

            stream.Close();
        }
    }

    /// <summary>
    /// Reads the next frame from the channel, returning the command and payload.
    /// <para>从通道读取下一帧，返回命令和负载。</para>
    /// </summary>
    /// <param name="command">Receives the command identifier. 接收命令标识符。</param>
    /// <returns>The frame payload. 帧负载。</returns>
    private byte[] ReadNextFrame(out HdcCommand command)
    {
        var stream = EnsureStream();
        var data = stream.Read() ?? throw new EndOfStreamException("HDC stream closed unexpectedly during file transfer.");

        if (data.Length < 4)
        {
            throw new InvalidDataException($"HDC file frame too short ({data.Length} bytes).");
        }

        command = (HdcCommand)BinaryPrimitives.ReadInt32LittleEndian(data);
        var payload = new byte[data.Length - 4];
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(data, 4, payload, 0, payload.Length);
        }

        return payload;
    }

    /// <summary>
    /// Reads the next frame and asserts it carries the expected command.
    /// <para>读取下一帧并断言其携带预期的命令。</para>
    /// </summary>
    /// <param name="expected">The expected command. 预期命令。</param>
    /// <param name="operation">The operation name for error messages. 用于错误消息的操作名称。</param>
    /// <returns>The frame payload. 帧负载。</returns>
    private byte[] ExpectCommand(HdcCommand expected, string operation)
    {
        var payload = ReadNextFrame(out var cmd);
        if (cmd != expected)
        {
            throw new InvalidDataException(
                $"Unexpected command {cmd} during {operation} (expected {expected}).");
        }

        return payload;
    }

    /// <summary>
    /// Builds a wire frame: 4-byte command (LE) + payload.
    /// <para>构建线帧：4 字节命令（小端）+ 负载。</para>
    /// </summary>
    /// <param name="command">The command identifier. 命令标识符。</param>
    /// <param name="payload">The payload data. 负载数据。</param>
    /// <returns>The complete frame bytes. 完整帧字节。</returns>
    private static byte[] BuildFrame(HdcCommand command, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)command);
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        }

        return frame;
    }

    // ──────────────────────────────────────────────────────────────────────
    // TransferConfig binary serialization (custom wire format, big-endian).
    // TransferConfig 二进制序列化（自定义线格式，大端序）。
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a <see cref="TransferConfig"/> to the HDC binary wire format.
    /// <para>将 <see cref="TransferConfig"/> 序列化为 HDC 二进制线格式。</para>
    /// </summary>
    /// <remarks>
    /// Wire layout (sequential, big-endian for integers):
    /// <code>
    ///   long  FileSize       (8 B)
    ///   long  Atime          (8 B)
    ///   long  Mtime          (8 B)
    ///   int   Options        (4 B)
    ///   str   Path           (4 B len + UTF-8)
    ///   str   OptionalName   (4 B len + UTF-8)
    ///   bool  UpdateIfNew    (1 B)
    ///   int   CompressType   (4 B)
    ///   bool  HoldTimestamp  (1 B)
    ///   str   FunctionName   (4 B len + UTF-8)
    ///   str   ClientCwd      (4 B len + UTF-8)
    ///   str   Reserve1       (4 B len + UTF-8)
    ///   str   Reserve2       (4 B len + UTF-8)
    /// </code>
    /// </remarks>
    /// <param name="config">The configuration to serialize. 要序列化的配置。</param>
    /// <returns>The serialized bytes. 序列化后的字节。</returns>
    public static byte[] SerializeTransferConfig(TransferConfig config)
    {
        using var ms = new MemoryStream();
        WriteInt64(ms, config.FileSize);
        WriteInt64(ms, config.Atime);
        WriteInt64(ms, config.Mtime);
        WriteInt32(ms, config.Options);
        WriteString(ms, config.Path);
        WriteString(ms, config.OptionalName);
        WriteBool(ms, config.UpdateIfNew);
        WriteInt32(ms, config.CompressType);
        WriteBool(ms, config.HoldTimestamp);
        WriteString(ms, config.FunctionName);
        WriteString(ms, config.ClientCwd);
        WriteString(ms, config.Reserve1);
        WriteString(ms, config.Reserve2);
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a <see cref="TransferConfig"/> from the HDC binary wire format.
    /// <para>从 HDC 二进制线格式反序列化 <see cref="TransferConfig"/>。</para>
    /// </summary>
    /// <param name="data">The serialized bytes. 序列化的字节。</param>
    /// <returns>The deserialized configuration. 反序列化后的配置。</returns>
    public static TransferConfig DeserializeTransferConfig(byte[] data)
    {
        var offset = 0;
        var config = new TransferConfig
        {
            FileSize = ReadInt64(data, ref offset),
            Atime = ReadInt64(data, ref offset),
            Mtime = ReadInt64(data, ref offset),
            Options = ReadInt32(data, ref offset),
            Path = ReadString(data, ref offset),
            OptionalName = ReadString(data, ref offset),
            UpdateIfNew = ReadBool(data, ref offset),
            CompressType = ReadInt32(data, ref offset),
            HoldTimestamp = ReadBool(data, ref offset),
            FunctionName = ReadString(data, ref offset),
            ClientCwd = ReadString(data, ref offset),
            Reserve1 = ReadString(data, ref offset),
            Reserve2 = ReadString(data, ref offset),
        };
        return config;
    }

    // ── Serialization helpers (big-endian, no reflection) ────────────────
    // ── 序列化辅助方法（大端序，无反射）────────────────────────────────

    private static void WriteInt64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBool(Stream s, bool value)
    {
        s.WriteByte(value ? (byte)1 : (byte)0);
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static long ReadInt64(byte[] data, ref int offset)
    {
        var result = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset));
        offset += 8;
        return result;
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        var result = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        return result;
    }

    private static bool ReadBool(byte[] data, ref int offset)
    {
        var result = data[offset] != 0;
        offset += 1;
        return result;
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);
        if (length < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException(
                $"String length {length} overflows buffer at offset {offset}.");
        }

        var result = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return result;
    }
}
