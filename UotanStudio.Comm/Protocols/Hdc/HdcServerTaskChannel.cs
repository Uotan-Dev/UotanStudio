using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Duplex task channel over one HDC host-server connection, using the HDC server frame format.
/// <para>基于单条 HDC 宿主 server 连接的双工任务通道，采用 HDC server 的帧格式。</para>
/// </summary>
/// <remarks>
/// Every frame is a big-endian 32-bit length followed by its payload. Task frames prefix the payload with the
/// two-byte little-endian command id (<c>CMD_FILE_*</c>, <c>CMD_APP_*</c>, <c>CMD_KERNEL_CHANNEL_CLOSE</c>,
/// <c>CMD_KERNEL_ECHO</c>); any other frame carries server text such as a <c>[Fail]</c> rejection.
/// <para>每帧为 4 字节大端长度加负载。任务帧在负载前加 2 字节小端命令标识（<c>CMD_FILE_*</c>、<c>CMD_APP_*</c>、
/// <c>CMD_KERNEL_CHANNEL_CLOSE</c>、<c>CMD_KERNEL_ECHO</c>）；其它帧承载 server 文本，例如 <c>[Fail]</c> 拒绝信息。</para>
/// </remarks>
internal sealed class HdcServerTaskChannel : IHdcTaskChannel, IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Lock _writeLock = new();
    private bool _closed;
    private bool _disposed;

    private HdcServerTaskChannel(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>Gets whether the channel reached its end of stream.
    /// <para>获取通道是否已到达流末尾。</para></summary>
    public bool IsClosed { get { lock (_writeLock) { return _closed; } } }

    /// <summary>
    /// Opens a dedicated host connection, completes the channel greeting and sends the command text that selects the
    /// server-side task. <para>打开专用宿主连接，完成通道握手，并发送选择 server 侧任务的命令文本。</para>
    /// </summary>
    /// <param name="server">Host channel client whose address and greeting logic are reused.
    /// <para>复用其地址与握手逻辑的宿主通道客户端。</para></param>
    /// <param name="connectKey">Target identifier placed in the greeting's connect-key field.
    /// <para>写入问候报文连接键字段的目标标识。</para></param>
    /// <param name="command">Command text sent NUL-terminated; it must be non-blank and free of NUL bytes.
    /// <para>以 NUL 结尾发送的命令文本；必须非空白且不含 NUL 字节。</para></param>
    /// <param name="cancellationToken">Cancels the connect, greeting and initial write.
    /// <para>取消连接、握手与首次写入。</para></param>
    /// <returns>The channel positioned at the server's first task frame.
    /// <para>已定位到 server 首个任务帧的通道。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="connectKey"/> or <paramref name="command"/> is invalid.
    /// <para><paramref name="connectKey"/> 或 <paramref name="command"/> 无效。</para></exception>
    internal static async Task<HdcServerTaskChannel> OpenAsync(HdcServerClient server, string connectKey,
        string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\0')) throw new ArgumentException("HDC task command must not contain NUL.", nameof(command));
        var client = await server.OpenConnectionAsync(connectKey, cancellationToken).ConfigureAwait(false);
        var channel = new HdcServerTaskChannel(client);
        try
        {
            channel.WriteText(command);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>Reads the next frame within the wait budget and returns it as an envelope.
    /// <para>在等待预算内读取下一帧并作为信封返回。</para></summary>
    /// <param name="timeoutMs">Maximum wait for the next frame; <see cref="Timeout.Infinite"/> waits indefinitely.
    /// <para>等待下一帧的最大毫秒数；<see cref="Timeout.Infinite"/> 表示无限等待。</para></param>
    /// <param name="cancellationToken">Cancels the wait for a frame start and the remainder of a started frame.
    /// <para>取消等待帧起始以及已开始帧的剩余部分。</para></param>
    /// <returns>The task envelope, or <see langword="null"/> when the wait elapsed or the channel closed.
    /// <para>任务信封；等待超时或通道关闭时为 <see langword="null"/>。</para></returns>
    /// <exception cref="IOException">The server sent text instead of a task frame, or the connection failed.
    /// <para>server 发送了文本而非任务帧，或连接失败。</para></exception>
    public HdcMessage? ReadMessage(int timeoutMs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed) return null;
        var frame = ReadFrame(timeoutMs, cancellationToken);
        if (frame is null) return null;
        if (frame.Length >= 2)
        {
            var command = (HdcCommand)BinaryPrimitives.ReadUInt16LittleEndian(frame);
            if (IsTaskCommand(command))
            {
                if (command == HdcCommand.ChannelClose)
                {
                    MarkClosed();
                    return null;
                }

                return new HdcMessage(command, 0, 0, frame[2..]);
            }
        }

        var text = Encoding.UTF8.GetString(frame).TrimEnd('\0', '\r', '\n');
        throw new IOException(string.IsNullOrWhiteSpace(text) ? "The HDC server rejected the task." : text);
    }

    /// <summary>Reads server text frames until the channel closes, stopping early at an app result frame.
    /// <para>读取 server 文本帧直到通道关闭；遇到应用结果帧时提前结束。</para>
    /// </summary>
    /// <param name="timeoutMs">Overall deadline in milliseconds. <para>总体期限（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <returns>The trimmed server text and, when one arrived, the payload of the <c>CMD_APP_FINISH</c> frame.
    /// <para>去除首尾空白的 server 文本；若收到 <c>CMD_APP_FINISH</c> 帧，同时返回其负载。</para></returns>
    /// <exception cref="TimeoutException">The deadline elapsed before the channel closed.
    /// <para>通道在期限到达前未关闭。</para></exception>
    internal (string Text, byte[]? AppResult) ReadServerResult(int timeoutMs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = Environment.TickCount64 + timeoutMs;
        var text = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("HDC task response timed out.");
            var frame = ReadFrame(250, cancellationToken);
            if (frame is null)
            {
                if (IsClosed) break;
                continue;
            }

            if (frame.Length >= 2)
            {
                var command = (HdcCommand)BinaryPrimitives.ReadUInt16LittleEndian(frame);
                if (IsTaskCommand(command))
                {
                    if (command == HdcCommand.ChannelClose)
                    {
                        MarkClosed();
                        break;
                    }

                    if (command == HdcCommand.AppFinish) return (text.ToString().Trim(), frame[2..]);
                    continue;
                }
            }

            text.Append(Encoding.UTF8.GetString(frame).TrimEnd('\0'));
        }

        return (text.ToString().Trim(), null);
    }

    /// <summary>Sends one task frame with its two-byte little-endian command prefix.
    /// <para>发送一个带 2 字节小端命令前缀的任务帧。</para>
    /// </summary>
    /// <param name="command">Command carried by the frame. <para>帧携带的命令。</para></param>
    /// <param name="payload">Payload bytes; the array is not retained. <para>负载字节；不会保留该数组。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.
    /// <para><paramref name="payload"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">The channel is closed. <para>通道已关闭。</para></exception>
    public void SendCommand(HdcCommand command, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > HdcServerClient.MaximumFrameBytes - 2)
            throw new InvalidDataException("HDC task payload exceeds the frame limit.");
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)command);
        payload.CopyTo(frame.AsSpan(2));
        WriteFrame(frame);
    }

    private static bool IsTaskCommand(HdcCommand command) => command is
        HdcCommand.ChannelClose or HdcCommand.Echo or HdcCommand.EchoRaw or HdcCommand.CheckServer
        or HdcCommand.WakeupSlaveTask
        or HdcCommand.FileInit or HdcCommand.FileCheck or HdcCommand.FileBegin or HdcCommand.FileData
        or HdcCommand.FileFinish or HdcCommand.FileSideload or HdcCommand.FileMode or HdcCommand.DirMode
        or HdcCommand.AppInit or HdcCommand.AppCheck or HdcCommand.AppBegin or HdcCommand.AppData
        or HdcCommand.AppFinish or HdcCommand.AppUninstall;

    private byte[]? ReadFrame(int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WaitForData(timeoutMs, cancellationToken)) return null;
        try
        {
            Span<byte> header = stackalloc byte[4];
            if (_stream.Read(header[..1]) == 0)
            {
                MarkClosed();
                return null;
            }

            _stream.ReadExactly(header[1..]);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > HdcServerClient.MaximumFrameBytes)
                throw new InvalidDataException("Invalid HDC channel frame length.");
            var frame = new byte[length];
            ReadExactly(frame, 0, length, cancellationToken);
            return frame;
        }
        catch (OperationCanceledException)
        {
            MarkClosed();
            throw;
        }
        catch (IOException)
        {
            MarkClosed();
            throw;
        }
    }

    private void ReadExactly(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var memory = buffer.AsMemory(offset, count);
        if (cancellationToken.CanBeCanceled)
        {
            _stream.ReadExactlyAsync(memory, cancellationToken).AsTask().GetAwaiter().GetResult();
            return;
        }

        _stream.ReadExactly(memory.Span);
    }

    /// <summary>Waits until the peer sends data or closes the connection.
    /// <para>等待对端发送数据或关闭连接。</para>
    /// </summary>
    /// <param name="timeoutMs">Wait budget in milliseconds; <see cref="Timeout.Infinite"/> waits indefinitely.
    /// <para>等待预算（毫秒）；<see cref="Timeout.Infinite"/> 表示无限等待。</para></param>
    /// <param name="cancellationToken">Checked before and after the wait. <para>等待前后都会检查。</para></param>
    /// <returns><see langword="true"/> when a read would return data or an end of stream.
    /// <para>读取可返回数据或流结束标记时为 <see langword="true"/>。</para></returns>
    private bool WaitForData(int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            var microseconds = timeoutMs == Timeout.Infinite
                ? -1
                : Math.Clamp(timeoutMs, 0, int.MaxValue / 1000) * 1000;
            var ready = _client.Client.Poll(microseconds, SelectMode.SelectRead);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ready) return false;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            MarkClosed();
            return false;
        }
    }

    private void WriteText(string command)
    {
        var payload = new byte[Encoding.UTF8.GetByteCount(command) + 1];
        Encoding.UTF8.GetBytes(command, payload);
        WriteFrame(payload);
    }

    private void WriteFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > HdcServerClient.MaximumFrameBytes)
            throw new InvalidDataException("Invalid HDC channel frame length.");
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        lock (_writeLock)
        {
            if (_closed) throw new IOException("HDC server task channel is closed.");
            try
            {
                _stream.Write(header);
                _stream.Write(payload);
                _stream.Flush();
            }
            catch (IOException)
            {
                _closed = true;
                throw;
            }
        }
    }

    private void MarkClosed()
    {
        lock (_writeLock) { _closed = true; }
    }

    /// <summary>Closes the task connection. <para>关闭任务连接。</para></summary>
    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _closed = true;
        }

        try { _stream.Dispose(); } catch (IOException) { }
        try { _client.Dispose(); } catch (IOException) { }
    }
}