using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Bounded ADB smart-socket service access through an already running local adb server.
/// <para>通过已在运行的本地 adb server 进行受限的 ADB 服务访问。</para>
/// </summary>
/// <param name="serial">The server-side serial identifying the target device; device-scoped calls
/// require it to be non-blank. <para>标识目标设备的 server 侧 serial；设备级调用要求它非空。</para></param>
/// <param name="host">The adb server host to connect to. <para>要连接的 adb server 主机。</para></param>
/// <param name="port">The adb server TCP port. <para>adb server 的 TCP 端口。</para></param>
public sealed class AdbServerServices(string serial, string host = "127.0.0.1", int port = 5037)
{
    /// <summary>
    /// Lists server-managed targets without selecting a device transport.
    /// <para>列出 server 管理的目标，不选择设备传输通道。</para>
    /// </summary>
    /// <returns>One entry per non-empty <c>serial state descriptors</c> line reported by
    /// <c>host:devices-l</c>, with each line's transport descriptor preserved.
    /// <para>对应 <c>host:devices-l</c> 报告的每条非空 “serial state 描述符” 行的条目，并保留每行的传输描述符。</para></returns>
    /// <exception cref="IOException">The device list length is invalid or the server connection failed.
    /// <para>设备列表长度非法或 server 连接失败。</para></exception>
    public IReadOnlyList<AdbTargetInfo> ListTargets()
    {
        using var client = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = client.GetStream();
        stream.ReadTimeout = 10000;
        stream.WriteTimeout = 10000;
        Request(stream, "host:devices-l");
        Span<byte> lengthBytes = stackalloc byte[4];
        stream.ReadExactly(lengthBytes);
        if (!int.TryParse(Encoding.ASCII.GetString(lengthBytes), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var length) || length is < 0 or > 1048576)
            throw new IOException("Invalid ADB device list length.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return AdbTargetInfo.Parse(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// Opens an owned byte stream to a service on the explicitly selected device.
    /// <para>在显式选定的设备上打开一个由调用方持有的字节服务流。</para>
    /// </summary>
    /// <param name="service">The raw ADB service name, e.g. <c>shell:id</c> or <c>sync:</c>.
    /// <para>原始 ADB 服务名，如 <c>shell:id</c> 或 <c>sync:</c>。</para></param>
    /// <returns>A stream positioned after the OKAY handshake; the caller owns and must dispose it.
    /// <para>定位在 OKAY 握手之后的流；归调用方所有并必须由其释放。</para></returns>
    /// <exception cref="ArgumentException">The instance <c>serial</c> is blank.
    /// <para>实例的 <c>serial</c> 为空。</para></exception>
    /// <exception cref="IOException">The server rejected the transport or service request.
    /// <para>server 拒绝了传输或服务请求。</para></exception>
    public Stream Open(string service)
    {
        var stream = OpenTransport();
        try { Request(stream, service); return stream; }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>
    /// Runs a device shell command to completion and returns its interleaved stdout/stderr text.
    /// <para>运行设备 shell 命令直至结束，返回其 stdout/stderr 交织的文本。</para>
    /// </summary>
    /// <param name="command">The shell command string sent as <c>shell:</c> plus this text.
    /// <para>以 <c>shell:</c> 前缀加上该文本发送的 shell 命令字符串。</para></param>
    /// <param name="cancellationToken">Cancels connection, request and response reads.
    /// <para>取消连接、请求与响应读取。</para></param>
    /// <returns>The UTF-8 decoded output with CRLF normalized to LF and trailing newlines trimmed.
    /// <para>UTF-8 解码后的输出，CRLF 已归一化为 LF 并去除尾部换行。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is blank.
    /// <para><paramref name="command"/> 为空。</para></exception>
    /// <exception cref="IOException">The adb server reported FAIL for the transport or service request.
    /// <para>adb server 对传输或服务请求返回 FAIL。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled before completion.
    /// <para>令牌在完成前被取消。</para></exception>
    public async Task<string> RunShellAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var stream = await OpenServiceAsync("shell:" + command, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return text.Replace("\r\n", "\n").TrimEnd('\n');
    }

    /// <summary>
    /// Streams device shell output line by line; <paramref name="onLine"/> returning <c>false</c> stops early.
    /// <para>逐行流式读取设备 shell 输出；<paramref name="onLine"/> 返回 <c>false</c> 时提前停止。</para>
    /// </summary>
    /// <param name="command">The shell command string sent as <c>shell:</c> plus this text.
    /// <para>以 <c>shell:</c> 前缀加上该文本发送的 shell 命令字符串。</para></param>
    /// <param name="onLine">Invoked once per decoded line; returning <c>false</c> stops the stream.
    /// <para>每解码一行调用一次；返回 <c>false</c> 即停止流式读取。</para></param>
    /// <param name="cancellationToken">Checked before each read; a read interrupted by an
    /// <see cref="IOException"/> after cancellation ends the loop instead of throwing.
    /// <para>每次读取前检查；取消后以 <see cref="IOException"/> 形式结束的读取会退出循环而不抛出。</para></param>
    /// <returns>A task that completes when the stream ends or <paramref name="onLine"/>
    /// returned <c>false</c>. <para>当流结束或 <paramref name="onLine"/> 返回 <c>false</c> 时完成的任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is blank.
    /// <para><paramref name="command"/> 为空。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="onLine"/> is <c>null</c>.
    /// <para><paramref name="onLine"/> 为 <c>null</c>。</para></exception>
    /// <exception cref="IOException">The adb server reported FAIL for the transport or service request.
    /// <para>adb server 对传输或服务请求返回 FAIL。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public async Task StreamShellAsync(string command, Func<string, bool> onLine, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onLine);
        using var stream = await OpenServiceAsync("shell:" + command, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        var pending = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count;
            try { count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (IOException) when (cancellationToken.IsCancellationRequested) { break; }
            if (count <= 0) break;
            pending += Encoding.UTF8.GetString(buffer, 0, count);
            int newline;
            while ((newline = pending.IndexOf('\n')) >= 0)
            {
                var line = pending[..newline].TrimEnd('\r');
                if (!onLine(line)) return;
                pending = pending[(newline + 1)..];
            }
        }

        if (pending.Length > 0) onLine(pending);
    }

    /// <summary>
    /// Stops a local adb server over its control socket. Only a listening endpoint is contacted.
    /// <para>通过控制套接字停止本地 adb server。只会联系处于监听状态的端点。</para>
    /// </summary>
    /// <param name="host">The adb server host. <para>adb server 主机。</para></param>
    /// <param name="port">The adb server TCP port. <para>adb server 的 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect and the <c>host:kill</c> exchange.
    /// <para>取消连接与 <c>host:kill</c> 交互。</para></param>
    /// <returns>A task that completes once the server accepted the <c>host:kill</c> request.
    /// <para>server 接受 <c>host:kill</c> 请求后完成的任务。</para></returns>
    /// <exception cref="IOException">The server reported FAIL or sent an invalid status.
    /// <para>server 返回 FAIL 或发送了非法状态。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled before completion.
    /// <para>令牌在完成前被取消。</para></exception>
    public static async Task KillAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using var stream = await OpenHostStreamAsync(host, port, cancellationToken).ConfigureAwait(false);
        await RequestAsync(stream, "host:kill", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects one network device through an existing ADB server and checks its response.
    /// <para>通过已运行的 ADB server 连接单个网络设备，并检查响应结果。</para>
    /// </summary>
    /// <param name="address">The explicit device host and port.<para>显式设备主机与端口。</para></param>
    /// <param name="host">The ADB server host.<para>ADB server 主机。</para></param>
    /// <param name="port">The ADB server port.<para>ADB server 端口。</para></param>
    /// <param name="cancellationToken">Cancels the exchange.<para>取消协议交互。</para></param>
    /// <returns>The server's connection result.<para>server 返回的连接结果。</para></returns>
    /// <exception cref="ArgumentException">The address is invalid.<para>地址无效。</para></exception>
    /// <exception cref="IOException">The server did not report a successful connection.<para>server 未报告连接成功。</para></exception>
    public static async Task<string> ConnectAsync(string address, string host, int port,
        CancellationToken cancellationToken = default)
    {
        if (!DebugBridgeNetworkAddress.TryNormalize(address, out var target))
        {
            throw new ArgumentException("A device host:port address is required.", nameof(address));
        }

        var response = await RunNetworkCommandAsync("host:connect:" + target, host, port, null, cancellationToken)
            .ConfigureAwait(false);
        if (!response.StartsWith("connected to ", StringComparison.Ordinal) &&
            !response.StartsWith("already connected to ", StringComparison.Ordinal))
        {
            throw new IOException("ADB server: " + response);
        }
        return response;
    }

    /// <summary>
    /// Disconnects one network target from the local adb server.
    /// <para>将单个网络目标从本地 adb server 断开。</para>
    /// </summary>
    /// <param name="serial">The <c>host:port</c> connect key of the target to disconnect.
    /// <para>待断开目标的 <c>host:port</c> 连接标识。</para></param>
    /// <param name="host">The adb server host. <para>adb server 主机。</para></param>
    /// <param name="port">The adb server TCP port. <para>adb server 的 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the connect and the <c>host:disconnect:</c> exchange.
    /// <para>取消连接与 <c>host:disconnect:</c> 交互。</para></param>
    /// <returns>A task that completes once the server accepted the disconnect request.
    /// <para>server 接受断开请求后完成的任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is blank.
    /// <para><paramref name="serial"/> 为空。</para></exception>
    /// <exception cref="IOException">The server reported FAIL or sent an invalid status.
    /// <para>server 返回 FAIL 或发送了非法状态。</para></exception>
    public static async Task DisconnectAsync(string serial, string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var response = await RunNetworkCommandAsync("host:disconnect:" + serial, host, port, null, cancellationToken)
            .ConfigureAwait(false);
        if (!response.StartsWith("disconnected ", StringComparison.Ordinal))
        {
            throw new IOException("ADB server: " + response);
        }
    }

    /// <summary>
    /// Runs one host-level command and returns its payload text. Callers that poll the server in a
    /// loop pass a deadline short enough to keep the poll cadence, because a wedged server would
    /// otherwise hold the request for the network default.
    /// <para>执行一条 host 级命令并返回其应答文本。按循环轮询 server 的调用方应传入足够短的期限以保持轮询
    /// 节奏，否则卡住的 server 会把该请求占用整个网络默认超时。</para>
    /// </summary>
    /// <param name="command">The raw host command, e.g. <c>host:mdns:services</c>.
    /// <para>原始 host 命令，如 <c>host:mdns:services</c>。</para></param>
    /// <param name="host">The adb server host. <para>adb server 主机。</para></param>
    /// <param name="port">The adb server TCP port. <para>adb server 的 TCP 端口。</para></param>
    /// <param name="timeout">Deadline for connect, request and response; the 30 second network
    /// default applies when <c>null</c>. <para>连接、请求与响应的期限；为 <c>null</c> 时使用 30 秒网络默认值。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消协议交互。</para></param>
    /// <returns>The decoded, trimmed payload. <para>解码并去除首尾空白后的应答文本。</para></returns>
    /// <exception cref="IOException">The server reported FAIL or sent an invalid status.
    /// <para>server 返回 FAIL 或发送了非法状态。</para></exception>
    /// <exception cref="TimeoutException">The exchange exceeded <paramref name="timeout"/>.
    /// <para>交互超过 <paramref name="timeout"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    internal static Task<string> RunHostCommandAsync(string command, string host, int port, TimeSpan? timeout,
        CancellationToken cancellationToken = default) =>
        RunNetworkCommandAsync(command, host, port, timeout, cancellationToken);

    private static async Task<string> RunNetworkCommandAsync(string command, string host, int port,
        TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(30);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(deadline);
        try
        {
            using var stream = await OpenHostStreamAsync(host, port, linked.Token).ConfigureAwait(false);
            await RequestAsync(stream, command, linked.Token).ConfigureAwait(false);
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, linked.Token).ConfigureAwait(false);
            if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var length) || length is < 0 or > 65535)
            {
                throw new IOException("Invalid ADB server response length.");
            }
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, linked.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ADB network operation exceeded " +
                deadline.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " seconds.");
        }
    }

    private async Task<Stream> OpenServiceAsync(string service, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var stream = await OpenHostStreamAsync(host, port, cancellationToken).ConfigureAwait(false);
        try
        {
            await RequestAsync(stream, "host:transport:" + serial, cancellationToken).ConfigureAwait(false);
            await RequestAsync(stream, service, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task<Stream> OpenHostStreamAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 10000;
            return stream;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task RequestAsync(Stream stream, string request, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(request);
        if (bytes.Length > 65535) throw new ArgumentOutOfRangeException(nameof(request));
        var header = Encoding.ASCII.GetBytes(bytes.Length.ToString("X4", CultureInfo.InvariantCulture));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        byte[] status = new byte[4];
        await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        if (status.AsSpan().SequenceEqual("OKAY"u8)) return;
        if (!status.AsSpan().SequenceEqual("FAIL"u8)) throw new IOException("Invalid ADB server status.");
        await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        int length = int.Parse(Encoding.ASCII.GetString(status), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte[] message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        throw new IOException("ADB server: " + Encoding.UTF8.GetString(message));
    }

    /// <summary>
    /// Checks the selected target without opening a device service.
    /// <para>检查所选目标，但不打开设备服务。</para>
    /// </summary>
    /// <exception cref="ArgumentException">The <c>serial</c> of this instance is blank.
    /// <para>本实例的 <c>serial</c> 为空。</para></exception>
    /// <exception cref="IOException">The server rejected the transport request or the connection failed.
    /// <para>server 拒绝传输请求或连接失败。</para></exception>
    public void ValidateTarget()
    {
        using var stream = OpenTransport();
    }

    private Stream OpenTransport()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            var stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 10000;
            Request(stream, "host:transport:" + serial);
            return stream;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>
    /// Pushes a local file using the ADB sync protocol and checks the device's final status.
    /// The remote path is sent with mode bits 0644 and the DONE marker carries the current UTC time.
    /// <para>使用 ADB sync 协议推送本地文件并检查设备的最终状态。远端路径按模式位 0644 发送，
    /// DONE 标记携带当前 UTC 时间。</para>
    /// </summary>
    /// <param name="localPath">Path of the existing local file to upload.
    /// <para>待上传的现有本地文件路径。</para></param>
    /// <param name="remotePath">Destination path on the device, sent verbatim as UTF-8.
    /// <para>设备上的目标路径，按 UTF-8 原样发送。</para></param>
    /// <exception cref="FileNotFoundException">The local file does not exist.
    /// <para>本地文件不存在。</para></exception>
    /// <exception cref="IOException">The device reported FAIL for the sync operation.
    /// <para>设备对该 sync 操作返回 FAIL。</para></exception>
    public void Push(string localPath, string remotePath)
    {
        using Stream stream = Open("sync:");
        using var input = File.OpenRead(localPath);
        Send(stream, "SEND", Encoding.UTF8.GetBytes(remotePath + ",33188"));
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) != 0) Send(stream, "DATA", buffer.AsSpan(0, count));
        Span<byte> header = stackalloc byte[8];
        "DONE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        stream.Write(header);
        stream.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (header[..4].SequenceEqual("OKAY"u8) && length == 0) return;
        if (!header[..4].SequenceEqual("FAIL"u8) || length > 65536) throw new IOException("Invalid ADB sync response.");
        byte[] message = new byte[(int)length]; stream.ReadExactly(message);
        throw new IOException(Encoding.UTF8.GetString(message));
    }
    private static void Send(Stream stream, string id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.ASCII.GetBytes(id, header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        stream.Write(header); stream.Write(data);
    }
    private static void Request(Stream stream, string request)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(request);
        if (bytes.Length > 65535) throw new ArgumentOutOfRangeException(nameof(request));
        stream.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString("X4", CultureInfo.InvariantCulture)));
        stream.Write(bytes);
        Span<byte> status = stackalloc byte[4]; stream.ReadExactly(status);
        if (status.SequenceEqual("OKAY"u8)) return;
        if (!status.SequenceEqual("FAIL"u8)) throw new IOException("Invalid ADB server status.");
        stream.ReadExactly(status);
        int length = int.Parse(Encoding.ASCII.GetString(status), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte[] message = new byte[length]; stream.ReadExactly(message);
        throw new IOException("ADB server: " + Encoding.UTF8.GetString(message));
    }
}

/// <summary>Preserves unread bytes when adapting ADB packet chunks to a byte-oriented stream.</summary>
internal sealed class AdbServiceStream(UotanStudio.Comm.Contracts.IAdbStream stream) : Stream
{
    private bool _disposed;
    private byte[]? _pending;
    private int _offset;
    /// <inheritdoc />
    public override int ReadTimeout { get; set; } = 10000;
    /// <inheritdoc />
    public override bool CanTimeout => true;
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => true;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <inheritdoc />
    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        if (_pending is null)
        {
            _pending = stream.Read(ReadTimeout); _offset = 0;
            if (_pending is null)
            {
                if (stream.IsFaulted) throw new IOException("ADB stream faulted.", stream.Fault);
                if (!stream.IsClosed) throw new TimeoutException("ADB service read timed out.");
                return 0;
            }
        }
        int count = Math.Min(destination.Length, _pending.Length - _offset);
        _pending.AsSpan(_offset, count).CopyTo(destination);
        _offset += count;
        if (_offset == _pending.Length) _pending = null;
        return count;
    }
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer.AsSpan(offset, count).ToArray());
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; stream.Dispose(); }
        base.Dispose(disposing);
    }
}