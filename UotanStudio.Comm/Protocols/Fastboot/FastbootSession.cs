using System.Buffers;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Implements the fastboot protocol session over a USB transport session.
/// <para>在 USB 传输会话之上实现 fastboot 协议会话。</para>
/// </summary>
public sealed class FastbootSession : IProtocolSession
{
    private readonly IUsbDeviceSession _transport;
    private readonly int _timeoutMs;
    private bool _disposed;

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "fastboot";

    /// <summary>Gets the underlying transport session. 获取底层传输会话。</summary>
    public IUsbDeviceSession Transport => _transport;

    /// <summary>Gets a value indicating whether the session is currently connected. 获取会话是否已连接。</summary>
    public bool IsConnected => !_disposed && _transport.DeviceInfo.VendorId != 0;

    /// <summary>
    /// Creates a new fastboot session over the specified transport.
    /// <para>在指定传输上创建新的 fastboot 会话。</para>
    /// </summary>
    public FastbootSession(IUsbDeviceSession transport, int? timeoutMs = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? (transport.DefaultTimeoutMs > 0 ? transport.DefaultTimeoutMs : FastbootConstants.DefaultResponseTimeoutSeconds * 1000);
    }

    /// <summary>
    /// Sends a raw fastboot command and returns the parsed response.
    /// <para>发送原始 fastboot 命令并返回解析后的响应。</para>
    /// </summary>
    public FastbootResponse ExecuteCommand(string command)
    {
        ThrowIfDisposed();
        SendCommand(command);
        return ReceiveResponse();
    }

    /// <summary>
    /// Sends a getvar query and returns the variable value, or empty if unsupported.
    /// <para>发送 getvar 查询并返回变量值，不支持时返回空。</para>
    /// </summary>
    public string GetVariable(string key)
    {
        FastbootResponse response = ExecuteCommand(FastbootConstants.CommandGetVar + key);
        return response.State == FastbootResponseState.Success ? response.Info : string.Empty;
    }

    /// <summary>
    /// Sends a raw command string to the device (UTF-8 encoded, max 4096 bytes).
    /// <para>向设备发送原始命令字符串（UTF-8 编码，最大 4096 字节）。</para>
    /// </summary>
    public void SendCommand(string command)
    {
        ThrowIfDisposed();
        var cmdBytes = Encoding.UTF8.GetBytes(command);
        if (cmdBytes.Length > FastbootConstants.MaxCommandLength)
            throw new ArgumentException($"Command too long ({cmdBytes.Length} > {FastbootConstants.MaxCommandLength})", nameof(command));

        var written = _transport.Write(cmdBytes, cmdBytes.Length, _timeoutMs);
        if (written != cmdBytes.Length)
            throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{cmdBytes.Length}");
    }

    /// <summary>
    /// Receives and parses a fastboot protocol response.
    /// <para>接收并解析 fastboot 协议响应。</para>
    /// </summary>
    public FastbootResponse ReceiveResponse(int? timeoutMs = null)
    {
        ThrowIfDisposed();
        var response = new FastbootResponse();
        var timeout = timeoutMs ?? _timeoutMs;
        var deadline = Environment.TickCount64 + timeout;
        var pending = string.Empty;

        var buffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.ResponseBufferSize);
        try
        {
            while (true)
            {
                var elapsed = (int)(deadline - Environment.TickCount64);
                if (elapsed <= 0)
                {
                    response.State = FastbootResponseState.Timeout;
                    response.Text = "response timeout";
                    return response;
                }

                var readLen = _transport.ReadInto(buffer, 0, FastbootConstants.ResponseBufferSize, Math.Max(elapsed, 100));
                if (readLen == 0)
                    continue;

                pending += Encoding.UTF8.GetString(buffer, 0, readLen);
                if (TryParseResponse(pending, out response, out var consumed))
                {
                    // Keep any unconsumed bytes for the next call
                    return response;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseResponse(string input, out FastbootResponse response, out int consumed)
    {
        response = new FastbootResponse();
        consumed = 0;

        if (input.Length < 4)
            return false;

        var prefix = input[..4];
        var content = input.Length > 4 ? input[4..].TrimEnd('\0') : string.Empty;

        switch (prefix)
        {
            case FastbootConstants.Okay:
                response.State = FastbootResponseState.Success;
                response.Info = content;
                response.Text = content;
                consumed = input.Length;
                return true;

            case FastbootConstants.Fail:
                response.State = FastbootResponseState.Fail;
                response.Info = content;
                response.Text = content;
                consumed = input.Length;
                return true;

            case FastbootConstants.Info:
                response.State = FastbootResponseState.Info;
                response.Info = content;
                response.InfoLines.Add(content);
                consumed = input.Length;
                return true;

            case FastbootConstants.Text:
                response.State = FastbootResponseState.Text;
                response.Text += content;
                consumed = input.Length;
                return true;

            case FastbootConstants.Data:
                response.State = FastbootResponseState.Data;
                if (long.TryParse(content, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    response.DataSize = size;
                consumed = input.Length;
                return true;

            default:
                response.State = FastbootResponseState.Unknown;
                response.Info = content;
                response.Text = content;
                consumed = input.Length;
                return true;
        }
    }

    /// <summary>
    /// Downloads data to the device: sends download:&lt;hex-size&gt;, streams data, and awaits OKAY/FAIL.
    /// <para>下载数据到设备：发送 download:&lt;hex-size&gt;，流式传输数据，等待 OKAY/FAIL。</para>
    /// </summary>
    public FastbootResponse DownloadData(Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (length <= 0 || length > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));

        SendCommand(FastbootConstants.CommandDownload + length.ToString("x8"));

        FastbootResponse rawResponse = ReceiveResponse();
        if (rawResponse.State != FastbootResponseState.Data || rawResponse.DataSize != length)
        {
            rawResponse.Text = $"download size mismatch: requested {length}, device accepted {rawResponse.DataSize}";
            return rawResponse;
        }

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesWritten = 0;
            while (bytesWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, length - bytesWritten);
                var read = stream.Read(chunkBuffer, 0, toRead);
                if (read <= 0)
                    throw new EndOfStreamException($"Stream ended early at {bytesWritten}/{length}");

                var written = _transport.Write(chunkBuffer, 0, read, _timeoutMs);
                if (written != read)
                    throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{read}");

                bytesWritten += written;
                progress?.Report(bytesWritten);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        return ReceiveResponse();
    }

    /// <summary>
    /// Downloads a byte array to the device in chunks.
    /// <para>将字节数组分块下载到设备。</para>
    /// </summary>
    public FastbootResponse DownloadData(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return DownloadData(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads a file to the device from disk.
    /// <para>从磁盘下载文件到设备。</para>
    /// </summary>
    public FastbootResponse DownloadFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        FileStream fs = File.OpenRead(filePath);
        try
        {
            return DownloadData(fs, fs.Length, progress, cancellationToken);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Uploads data from the device following a DATA response.
    /// <para>在 DATA 响应后从设备上传数据。</para>
    /// </summary>
    public FastbootResponse UploadData(Stream output, long size, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesRead = 0;
            while (bytesRead < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, size - bytesRead);
                var read = _transport.ReadInto(chunkBuffer, 0, toRead, _timeoutMs);
                if (read <= 0)
                    throw new EndOfStreamException($"Device EOF at {bytesRead}/{size}");

                output.Write(chunkBuffer, 0, read);
                bytesRead += read;
                progress?.Report(bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        return ReceiveResponse();
    }

    /// <summary>
    /// Flashes a partition with data from a stream.
    /// <para>用流中的数据刷写分区。</para>
    /// </summary>
    public FastbootResponse FlashPartition(string partition, Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DownloadData(stream, length, progress, cancellationToken).ThrowIfFailed();
        return ExecuteCommand(FastbootConstants.CommandFlash + partition);
    }

    /// <summary>
    /// Erases the specified partition.
    /// <para>擦除指定分区。</para>
    /// </summary>
    public FastbootResponse ErasePartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandErase + partition);

    /// <summary>
    /// Formats the specified partition.
    /// <para>格式化指定分区。</para>
    /// </summary>
    public FastbootResponse FormatPartition(string partition)
        => ExecuteCommand(FastbootConstants.CommandFormat + partition);

    /// <summary>
    /// Reboots the device. Optionally into a target (recovery, bootloader, etc.).
    /// <para>重启设备，可指定目标（recovery、bootloader 等）。</para>
    /// </summary>
    public FastbootResponse Reboot(string? target = null)
        => ExecuteCommand(string.IsNullOrEmpty(target) ? FastbootConstants.CommandReboot : FastbootConstants.CommandReboot + "-" + target);

    /// <summary>
    /// Continues boot, exiting fastboot mode.
    /// <para>继续启动，退出 fastboot 模式。</para>
    /// </summary>
    public FastbootResponse ContinueBoot()
        => ExecuteCommand(FastbootConstants.CommandContinue);

    /// <summary>
    /// Sets the active A/B slot.
    /// <para>设置活跃的 A/B 槽位。</para>
    /// </summary>
    public FastbootResponse SetActiveSlot(string slot)
        => ExecuteCommand(FastbootConstants.CommandSetActive + slot);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FastbootSession));
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
