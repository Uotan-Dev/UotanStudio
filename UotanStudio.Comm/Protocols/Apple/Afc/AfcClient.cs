using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Afc;

/// <summary>
/// Raised when AFC reports an error status.
/// <para>AFC 上报错误状态时抛出。</para>
/// </summary>
public sealed class AfcException : IOException
{
    /// <summary>Gets the AFC status code. <para>获取 AFC 状态码。</para></summary>
    public ulong StatusCode { get; }

    /// <summary>Gets the human-readable message from the device, if any. <para>设备给出的可读消息（如有）。</para></summary>
    public string? DeviceMessage { get; }

    /// <summary>
    /// Creates the exception.
    /// <para>创建异常。</para>
    /// </summary>
    /// <param name="statusCode">The AFC status code. <para>AFC 状态码。</para></param>
    /// <param name="deviceMessage">The optional device message. <para>可选的设备消息。</para></param>
    public AfcException(ulong statusCode, string? deviceMessage) : base(
        deviceMessage is { Length: > 0 }
            ? $"AFC error {statusCode}: {deviceMessage}."
            : $"AFC error {statusCode}.")
    {
        StatusCode = statusCode;
        DeviceMessage = deviceMessage;
    }
}

/// <summary>
/// Client for the AFC (Apple File Conduit) protocol, used for the media directory
/// (<see cref="AppleConstants.ServiceAfc"/>) and for crash-report copying
/// (<see cref="AppleConstants.ServiceCrashReportCopyMobile"/>). Every operation is bounded by explicit
/// limits because device payloads are untrusted input.
/// <para>AFC（Apple File Conduit）协议客户端，用于媒体目录（<see cref="AppleConstants.ServiceAfc"/>）
/// 与崩溃日志拷贝（<see cref="AppleConstants.ServiceCrashReportCopyMobile"/>）。设备负载属不可信输入，
/// 全部操作都有显式上限。</para>
/// </summary>
public sealed class AfcClient : IDisposable
{
    /// <summary>Gets the maximum single AFC packet size. <para>获取单个 AFC 数据包大小上限。</para></summary>
    public const int MaxPacketBytes = 4 * 1024 * 1024;

    /// <summary>Gets the default per-file read cap for <see cref="ReadFile"/>. <para><see cref="ReadFile"/> 的默认单文件读取上限。</para></summary>
    public const long DefaultMaxFileBytes = 256L * 1024 * 1024;

    private const string Magic = "CFA6LPAA";

    private const ulong OperationInvalid = 0;
    private const ulong OperationStatus = 1;
    private const ulong OperationData = 2;
    private const ulong OperationReadDir = 3;
    private const ulong OperationReadFile = 4;
    private const ulong OperationWriteFile = 5;
    private const ulong OperationMakeDir = 6;
    private const ulong OperationDelete = 7;
    private const ulong OperationRename = 8;
    private const ulong OperationFileOpen = 9;
    private const ulong OperationFileClose = 10;
    private const ulong OperationFileSeek = 11;
    private const ulong OperationGetFileInfo = 13;

    private const ushort OpenModeReadOnly = 1;
    private const ushort OpenModeWriteOnly = 3;

    private const ulong StatusSuccess = 0;
    private const ulong StatusObjectNotFound = 8;

    private const int SeekSet = 1;

    private readonly Stream _stream;
    private ulong _packetNumber;

    /// <summary>
    /// Creates a client over the connected AFC service stream.
    /// <para>在已连接的 AFC 服务流上创建客户端。</para>
    /// </summary>
    /// <param name="stream">The TLS-ready service stream. <para>就绪 TLS 的服务流。</para></param>
    public AfcClient(Stream stream) => _stream = stream;

    /// <summary>
    /// Lists the entries of a directory.
    /// <para>列出目录条目。</para>
    /// </summary>
    /// <param name="path">The directory path. <para>目录路径。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The entry names including <c>.</c> and <c>..</c>. <para>条目名，含 <c>.</c> 与 <c>..</c>。</para></returns>
    /// <exception cref="AfcException">The directory does not exist. <para>目录不存在时抛出。</para></exception>
    public IReadOnlyList<string> ReadDirectory(string path, CancellationToken cancellationToken = default)
    {
        var payload = EncodeNullTerminatedStrings(path);
        var response = Exchange(OperationReadDir, payload, cancellationToken);
        var entries = DecodeNullTerminatedStrings(response);
        return entries;
    }

    /// <summary>
    /// Reads the file information dictionary (sizes, timestamps and similar string keys).
    /// <para>读取文件信息字典（大小、时间戳等字符串键值）。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The key/value pairs reported by the device. <para>设备上报的键值对。</para></returns>
    /// <exception cref="AfcException">The file does not exist. <para>文件不存在时抛出。</para></exception>
    public IReadOnlyDictionary<string, string> GetFileInfo(string path, CancellationToken cancellationToken = default)
    {
        var payload = EncodeNullTerminatedStrings(path);
        var response = Exchange(OperationGetFileInfo, payload, cancellationToken);
        var fields = DecodeNullTerminatedStrings(response);
        var info = new Dictionary<string, string>(fields.Count / 2, StringComparer.Ordinal);
        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            info[fields[i]] = fields[i + 1];
        }

        return info;
    }

    /// <summary>
    /// Creates a directory.
    /// <para>创建目录。</para>
    /// </summary>
    /// <param name="path">The directory path. <para>目录路径。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    public void MakeDirectory(string path, CancellationToken cancellationToken = default) =>
        Exchange(OperationMakeDir, EncodeNullTerminatedStrings(path), cancellationToken);

    /// <summary>
    /// Removes a file or directory.
    /// <para>删除文件或目录。</para>
    /// </summary>
    /// <param name="path">The path to remove; must be validated by the caller against escapes. <para>待删除路径；调用方须自行校验路径越界。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    public void Remove(string path, CancellationToken cancellationToken = default) =>
        Exchange(OperationDelete, EncodeNullTerminatedStrings(path), cancellationToken);

    /// <summary>
    /// Renames a file or directory.
    /// <para>重命名文件或目录。</para>
    /// </summary>
    /// <param name="fromPath">The source path. <para>源路径。</para></param>
    /// <param name="toPath">The destination path. <para>目标路径。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    public void Rename(string fromPath, string toPath, CancellationToken cancellationToken = default) =>
        Exchange(OperationRename, EncodeNullTerminatedStrings(fromPath, toPath), cancellationToken);

    /// <summary>
    /// Reads a whole file into memory, bounded by <paramref name="maxBytes"/>.
    /// <para>整读一个文件到内存，受 <paramref name="maxBytes"/> 约束。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <param name="maxBytes">The upper bound; exceeding it throws instead of silently truncating. <para>上限；超出即抛出而不是静默截断。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The file content. <para>文件内容。</para></returns>
    /// <exception cref="AfcException">The file does not exist. <para>文件不存在时抛出。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <paramref name="maxBytes"/>. <para>文件超过 <paramref name="maxBytes"/> 时抛出。</para></exception>
    public byte[] ReadFile(string path, long maxBytes = DefaultMaxFileBytes,
        CancellationToken cancellationToken = default)
    {
        var handle = OpenForRead(path, cancellationToken);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = ReadHandle(handle, buffer.Length, cancellationToken);
                if (chunk.Length == 0)
                {
                    break;
                }

                if (output.Length + chunk.Length > maxBytes)
                {
                    throw new InvalidDataException($"The AFC file {path} exceeds {maxBytes} bytes.");
                }

                output.Write(chunk);
            }

            return output.ToArray();
        }
        finally
        {
            CloseHandle(handle, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a whole stream into a file; the stream is fully consumed and its length must not exceed
    /// <paramref name="maxBytes"/>.
    /// <para>把整个流写入文件；流会被完整消费且长度不得超过 <paramref name="maxBytes"/>。</para>
    /// </summary>
    /// <param name="path">The destination path. <para>目标路径。</para></param>
    /// <param name="source">The content source. <para>内容来源。</para></param>
    /// <param name="maxBytes">The write upper bound. <para>写入上限。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    public void WriteFile(string path, Stream source, long maxBytes, CancellationToken cancellationToken = default)
    {
        var handle = OpenForWrite(path, cancellationToken);
        try
        {
            var buffer = new byte[1024 * 1024];
            long written = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                written += read;
                if (written > maxBytes)
                {
                    throw new InvalidDataException($"The AFC write to {path} exceeds {maxBytes} bytes.");
                }

                WriteHandle(handle, buffer.AsSpan(0, read), cancellationToken);
            }
        }
        finally
        {
            CloseHandle(handle, cancellationToken);
        }
    }

    private ulong OpenForRead(string path, CancellationToken cancellationToken)
    {
        var payload = new byte[2 + path.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, OpenModeReadOnly);
        Encoding.UTF8.GetBytes(path).CopyTo(payload.AsSpan(2));
        payload[^1] = 0;
        var response = Exchange(OperationFileOpen, payload, cancellationToken);
        return response.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(response) : 0;
    }

    private ulong OpenForWrite(string path, CancellationToken cancellationToken)
    {
        var payload = new byte[2 + path.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, OpenModeWriteOnly);
        Encoding.UTF8.GetBytes(path).CopyTo(payload.AsSpan(2));
        payload[^1] = 0;
        var response = Exchange(OperationFileOpen, payload, cancellationToken);
        return response.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(response) : 0;
    }

    private byte[] ReadHandle(ulong handle, int maxChunk, CancellationToken cancellationToken)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, handle);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), (ulong)maxChunk);
        return Exchange(OperationReadFile, payload, cancellationToken);
    }

    private void WriteHandle(ulong handle, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        var payload = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, handle);
        data.CopyTo(payload.AsSpan(8));
        Exchange(OperationWriteFile, payload, cancellationToken);
    }

    private void CloseHandle(ulong handle, CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, handle);
        Exchange(OperationFileClose, payload, cancellationToken);
    }

    private byte[] Exchange(ulong operation, byte[] payload, CancellationToken cancellationToken)
    {
        Send(operation, payload, cancellationToken);
        var data = Array.Empty<byte>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (responseOperation, responsePayload) = Receive(cancellationToken);
            switch (responseOperation)
            {
                case OperationStatus:
                    {
                        var code = responsePayload.Length >= 8
                            ? BinaryPrimitives.ReadUInt64LittleEndian(responsePayload)
                            : ulong.MaxValue;
                        if (code == StatusSuccess)
                        {
                            return data;
                        }

                        var message = DecodeNullTerminatedStrings(responsePayload[8..]) is { Count: > 0 } parts
                            ? parts[0]
                            : null;
                        throw new AfcException(code, message);
                    }
                case OperationData:
                    data = responsePayload;
                    break;
                default:
                    throw new AfcException(ulong.MaxValue, $"Unexpected AFC packet type {responseOperation}.");
            }
        }
    }

    private void Send(ulong operation, byte[] payload, CancellationToken cancellationToken)
    {
        var entire = 40 + payload.Length;
        if (entire > MaxPacketBytes)
        {
            throw new InvalidDataException("The AFC request exceeds the packet size limit.");
        }

        var header = new byte[40];
        Encoding.ASCII.GetBytes(Magic).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)entire);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), ++_packetNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), operation);
        WriteAll(header, cancellationToken);
        if (payload.Length > 0)
        {
            WriteAll(payload, cancellationToken);
        }
    }

    private (ulong Operation, byte[] Payload) Receive(CancellationToken cancellationToken)
    {
        var header = new byte[40];
        ReadAll(header, cancellationToken);
        if (Encoding.ASCII.GetString(header, 0, 8) != Magic)
        {
            throw new AfcException(OperationInvalid, "The AFC response carries an invalid magic.");
        }

        var entire = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        var payloadLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        var operation = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        if (entire is < 40 || entire > MaxPacketBytes || payloadLength < 0 ||
            payloadLength > MaxPacketBytes || entire - 40 != payloadLength)
        {
            throw new AfcException(OperationInvalid, "The AFC response length is out of range.");
        }

        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            ReadAll(payload, cancellationToken);
        }

        return (operation, payload);
    }

    private static byte[] EncodeNullTerminatedStrings(params ReadOnlySpan<string> values)
    {
        var length = 0;
        foreach (var value in values)
        {
            length += Encoding.UTF8.GetByteCount(value) + 1;
        }

        var payload = new byte[length];
        var offset = 0;
        foreach (var value in values)
        {
            offset += Encoding.UTF8.GetBytes(value, 0, value.Length, payload, offset);
            payload[offset++] = 0;
        }

        return payload;
    }

    private static List<string> DecodeNullTerminatedStrings(ReadOnlySpan<byte> payload)
    {
        var strings = new List<string>();
        var start = 0;
        for (var i = 0; i <= payload.Length; i++)
        {
            if (i < payload.Length && payload[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                strings.Add(Encoding.UTF8.GetString(payload[start..i]));
            }

            start = i + 1;
        }

        return strings;
    }

    private void WriteAll(byte[] bytes, CancellationToken cancellationToken)
    {
        _stream.WriteAsync(bytes, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private void ReadAll(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                throw new AfcException(OperationInvalid, "The AFC stream closed during a read.");
            }

            read += count;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();
}