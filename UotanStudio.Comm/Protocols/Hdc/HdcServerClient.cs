using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Host channel client following upstream channel.h/channel.cpp and host/client.cpp.
/// <para>上游 HDC 主机通道客户端；每条命令独占连接，不混用设备端会话协议。</para>
/// </summary>
/// <param name="host">
/// Hostname or address each command's connection targets; defaults to loopback.
/// <para>每条命令连接所指向的主机名或地址；默认为本机回环。</para>
/// </param>
/// <param name="port">
/// Explicit TCP port; <see langword="null"/> selects <see cref="DefaultServerPort"/>.
/// <para>显式 TCP 端口；<see langword="null"/> 选择 <see cref="DefaultServerPort"/>。</para>
/// </param>
public sealed class HdcServerClient(string host = "127.0.0.1", int? port = null) : IDisposable
{
    private bool _disposed;
    /// <summary>Server host.<para>服务端主机。</para></summary>
    /// <value>Hostname or address each command's connection targets.<para>每条命令连接所指向的主机名或地址。</para></value>
    public string ServerHost { get; } = host;
    /// <summary>Server port.<para>服务端端口。</para></summary>
    /// <value>Effective TCP port, either the constructor value or <see cref="DefaultServerPort"/>.<para>生效的 TCP 端口，取自构造参数或 <see cref="DefaultServerPort"/>。</para></value>
    public int ServerPort { get; } = port ?? DefaultServerPort;
    /// <summary>Upstream OHOS_HDC_SERVER_PORT override, otherwise 8710; the single definition
    /// lives in <c>Routing.DebugBridgeEndpoints</c>.
    /// <para>遵循上游 OHOS_HDC_SERVER_PORT 环境变量，否则使用 8710；唯一定义在
    /// <c>Routing.DebugBridgeEndpoints</c>。</para></summary>
    /// <value>The default host-channel port used when no explicit port is supplied.<para>未显式提供端口时使用的默认宿主通道端口。</para></value>
    public static int DefaultServerPort => DebugBridgeEndpoints.HdcServerPort;
    /// <summary>Maximum frame allocation.<para>单帧分配上限。</para></summary>
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    /// <summary>Maximum captured output.<para>捕获输出上限。</para></summary>
    public const int MaximumOutputBytes = 16 * 1024 * 1024;

    /// <summary>Checks the server-initiated handshake.<para>检查服务端主动握手。</para></summary>
    /// <param name="timeoutMs">Connect-and-handshake deadline in milliseconds.<para>连接与握手的超时时限（毫秒）。</para></param>
    /// <returns><see langword="true"/> when a host channel handshake completes in time; otherwise <see langword="false"/>.<para>宿主通道握手及时完成时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</para></returns>
    /// <exception cref="ObjectDisposedException">This client has been disposed.<para>此客户端已被释放。</para></exception>
    public bool DetectServer(int timeoutMs = 2000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { ConnectAsync(timeout.Token).GetAwaiter().GetResult(); return true; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }
    /// <summary>Checks server availability.<para>检查服务端可用性。</para></summary>
    /// <returns><see langword="true"/> when the default-timeout handshake succeeds; otherwise <see langword="false"/>.<para>默认超时的握手成功时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</para></returns>
    public bool IsServerRunning() => DetectServer();
    /// <summary>Validates a channel; each operation reconnects.<para>验证通道；每次操作单独重连。</para></summary>
    /// <exception cref="ObjectDisposedException">This client has been disposed.<para>此客户端已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The host channel handshake is not a valid HDC greeting.<para>宿主通道握手不是有效的 HDC 问候。</para></exception>
    public void Connect() => ConnectAsync().GetAwaiter().GetResult();
    /// <summary>Validates a channel with cancellation.<para>可取消地验证通道。</para></summary>
    /// <param name="cancellationToken">Cancels the connect-and-handshake within its own 3-second budget.<para>在其自带的 3 秒预算内取消连接与握手。</para></param>
    /// <returns>A task that completes once the host-channel handshake succeeds.<para>宿主通道握手成功后完成的task。</para></returns>
    /// <exception cref="ObjectDisposedException">This client has been disposed.<para>此客户端已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The host channel handshake is not a valid HDC greeting.<para>宿主通道握手不是有效的 HDC 问候。</para></exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = await OpenAsync(string.Empty, timeout.Token).ConfigureAwait(false);
    }
    private async Task<TcpClient> OpenAsync(string connectKey, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connectKey);
        if (connectKey.Contains('\0') || Encoding.UTF8.GetByteCount(connectKey) >= 32)
            throw new ArgumentException("HDC connect key must fit the upstream 32-byte NUL-terminated field.", nameof(connectKey));
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(ServerHost, ServerPort, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            var hello = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (hello is null || hello.Length is not (44 or 108) || !hello.AsSpan(0, 8).SequenceEqual("OHOS HDC"u8))
                throw new InvalidDataException("Invalid HDC host channel handshake.");
            var reply = hello[..44];
            Array.Clear(reply, 12, 32);
            Encoding.UTF8.GetBytes(connectKey, reply.AsSpan(12));
            await WriteFrameAsync(stream, reply, ct).ConfigureAwait(false);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException("Invalid HDC channel frame length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
    /// <summary>Streams complete frames until EOF or cancellation.<para>流式读取完整帧，直到 EOF 或取消。</para></summary>
    /// <param name="connectKey">Target identifier; empty selects a host-level command.<para>目标标识；空串选择宿主级命令。</para></param>
    /// <param name="command">Command text sent NUL-terminated; must be non-blank and free of NUL bytes.<para>以 NUL 结尾发送的命令文本；必须非空白且不含 NUL 字节。</para></param>
    /// <param name="onOutput">Callback invoked once per complete frame, in order.<para>每个完整帧按顺序调用一次的回调。</para></param>
    /// <param name="cancellationToken">Cancels the exchange; the connection closes on unwind.<para>取消交换；连接在栈展开时关闭。</para></param>
    /// <returns>A task that completes when the server closes the frame stream or the call is cancelled.<para>服务端关闭帧流或调用被取消时完成的task。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is blank, contains NUL, or is too large.<para><paramref name="command"/> 为空、含 NUL 或过大。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="onOutput"/> is null.<para><paramref name="onOutput"/> 为 null。</para></exception>
    public async Task ExecuteStreamingAsync(string connectKey, string command, Action<byte[]> onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (command.Contains('\0') || Encoding.UTF8.GetByteCount(command) >= MaximumFrameBytes)
            throw new ArgumentException("Invalid HDC command.", nameof(command));
        using var client = await OpenAsync(connectKey, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(command + '\0'), cancellationToken).ConfigureAwait(false);
        while (await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is { } payload)
            onOutput(payload);
    }

    /// <summary>Relays a target-bound interactive shell through the host channel.<para>通过宿主通道中继绑定目标的交互式 shell。</para></summary>
    /// <param name="connectKey">Target identifier the shell runs against.<para>shell 运行所针对的目标标识。</para></param>
    /// <param name="input">Readable stream of keystrokes forwarded to the device; the caller owns it.<para>转发到设备的按键输入流；由调用方持有。</para></param>
    /// <param name="output">Writable stream receiving device output frames, flushed per frame; the caller owns it.<para>接收设备输出帧的写入流，逐帧刷新；由调用方持有。</para></param>
    /// <param name="cancellationToken">Stops both directions and awaits the writer before returning.<para>停止双向传输并在返回前等待写入任务。</para></param>
    /// <returns>A task that completes when the device closes the shell or the call is cancelled.<para>设备关闭 shell 或调用被取消时完成的task。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.<para><paramref name="input"/> 或 <paramref name="output"/> 为 null。</para></exception>
    public async Task RunInteractiveShellAsync(string connectKey, Stream input, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var client = await OpenAsync(connectKey, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await WriteFrameAsync(stream, "shell\0"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (await input.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false) is var count && count > 0)
                    await WriteFrameAsync(stream, buffer[..count], lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
        }, lifetime.Token);
        try
        {
            while (await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is { } payload)
            {
                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await writer.ConfigureAwait(false);
        }
    }
    /// <summary>Captures bounded output within a total deadline.<para>在总时限内有界捕获输出。</para></summary>
    /// <param name="connectKey">Target identifier; empty selects a host-level command.<para>目标标识；空串选择宿主级命令。</para></param>
    /// <param name="command">Command text sent NUL-terminated; must be non-blank and free of NUL bytes.<para>以 NUL 结尾发送的命令文本；必须非空白且不含 NUL 字节。</para></param>
    /// <param name="timeoutMs">Total capture deadline in milliseconds; must be positive.<para>总捕获时限（毫秒）；必须为正数。</para></param>
    /// <param name="cancellationToken">Caller cancellation distinct from the timeout.<para>独立于超时的调用方取消。</para></param>
    /// <returns>The concatenated output frames, capped at <see cref="MaximumOutputBytes"/>.<para>拼接后的输出帧，上限为 <see cref="MaximumOutputBytes"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is zero or negative.<para><paramref name="timeoutMs"/> 为零或负数。</para></exception>
    /// <exception cref="TimeoutException">The deadline elapsed before the stream closed.<para>流在时限到达前未关闭。</para></exception>
    /// <exception cref="InvalidDataException">Captured output exceeded <see cref="MaximumOutputBytes"/>.<para>捕获输出超过 <see cref="MaximumOutputBytes"/>。</para></exception>
    public async Task<byte[]> ExecuteAsync(string connectKey, string command, int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var output = new MemoryStream();
        try
        {
            await ExecuteStreamingAsync(connectKey, command, bytes =>
            {
                if (output.Length + bytes.Length > MaximumOutputBytes) throw new InvalidDataException("HDC output limit exceeded.");
                output.Write(bytes);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"HDC command exceeded {timeoutMs} ms."); }
        return output.ToArray();
    }
    /// <summary>Executes a device command using HDC syntax.<para>按 HDC 语法执行设备命令。</para></summary>
    /// <param name="connectKey">Target identifier; empty selects a host-level command.<para>目标标识；空串选择宿主级命令。</para></param>
    /// <param name="command">Command text sent NUL-terminated.<para>以 NUL 结尾发送的命令文本。</para></param>
    /// <param name="timeoutMs">Total capture deadline in milliseconds.<para>总捕获时限（毫秒）。</para></param>
    /// <returns>The captured output frames, capped at <see cref="MaximumOutputBytes"/>.<para>捕获到的输出帧，上限为 <see cref="MaximumOutputBytes"/>。</para></returns>
    public byte[] ExecuteOnDevice(string connectKey, string command, int timeoutMs = 30000)
        => ExecuteAsync(connectKey, command, timeoutMs).GetAwaiter().GetResult();
    /// <summary>Lists targets, excluding the upstream empty marker.<para>列出目标并排除上游空列表标记。</para></summary>
    /// <param name="timeoutMs">Overall command deadline in milliseconds.<para>命令总体时限（毫秒）。</para></param>
    /// <returns>Distinct connect keys parsed from <c>list targets</c>.<para>由 <c>list targets</c> 解析出的去重连接键。</para></returns>
    /// <exception cref="IOException">The server returned an explicit <c>[Fail]</c> response.<para>服务端返回明确的 <c>[Fail]</c> 响应。</para></exception>
    public IReadOnlyList<string> ListTargets(int timeoutMs = 10000)
    {
        var output = Encoding.UTF8.GetStringTrimNul(ExecuteOnDevice(string.Empty, "list targets", timeoutMs));
        ThrowOnFailure(output);
        return [.. output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("[Empty]", StringComparison.Ordinal))
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0])
            .Distinct(StringComparer.Ordinal)];
    }
    /// <summary>Lists authoritative transport and authorization state from the host server.<para>从宿主服务端列出权威的传输与授权状态。</para></summary>
    /// <param name="timeoutMs">Overall command deadline in milliseconds.<para>命令总体时限（毫秒）。</para></param>
    /// <returns>Parsed target records from <c>list targets -v</c>.<para>由 <c>list targets -v</c> 解析出的目标记录。</para></returns>
    public IReadOnlyList<HdcTargetInfo> ListTargetDetails(int timeoutMs = 10000)
        => HdcTargetInfo.Parse(Encoding.UTF8.GetString(ExecuteOnDevice(string.Empty, "list targets -v", timeoutMs)));

    /// <summary>
    /// Connects or removes one TCP target using a host-level command.
    /// <para>使用宿主级命令连接或移除单个 TCP 目标。</para>
    /// </summary>
    /// <param name="address">The explicit device host and port.<para>显式设备主机与端口。</para></param>
    /// <param name="disconnect">Whether to remove the connection.<para>是否移除连接。</para></param>
    /// <param name="cancellationToken">Cancels the command.<para>取消命令。</para></param>
    /// <returns>The server's completion message.<para>server 的完成消息。</para></returns>
    /// <exception cref="ArgumentException">The address is invalid or exceeds the connect-key field.<para>地址无效或超出连接键字段长度。</para></exception>
    /// <exception cref="IOException">The server did not confirm the operation.<para>server 未确认操作成功。</para></exception>
    public async Task<string> ConnectTargetAsync(string address, bool disconnect = false,
        CancellationToken cancellationToken = default)
    {
        if (!DebugBridgeNetworkAddress.TryNormalize(address, out var target) || Encoding.UTF8.GetByteCount(target) >= 32)
        {
            throw new ArgumentException("A network address fitting the HDC connect-key field is required.", nameof(address));
        }
        var command = disconnect ? "tconn " + target + " -remove" : "tconn " + target;
        var text = Encoding.UTF8.GetStringTrimNul(await ExecuteAsync(string.Empty, command,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
        ThrowOnFailure(text);
        if (disconnect)
        {
            await VerifyTargetDisconnectedAsync(target, cancellationToken).ConfigureAwait(false);
        }
        else if (!text.Contains("Connect OK", StringComparison.Ordinal) &&
                 !text.Contains("Target is connected, repeat operation", StringComparison.Ordinal))
        {
            throw new IOException("HDC server did not confirm the operation: " + text);
        }
        return text;
    }

    private async Task VerifyTargetDisconnectedAsync(string target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                var output = await ExecuteAsync(string.Empty, "list targets -v",
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var targets = HdcTargetInfo.Parse(Encoding.UTF8.GetString(output));
                if (!targets.Any(item => item.ConnectKey == target &&
                    !string.Equals(item.State, "Offline", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("HDC server did not confirm target disconnection within 5 seconds.");
        }
    }

    /// <summary>
    /// Stops the local hdc server by reading its pid file and terminating that process, because the server ignores any wire-level kill command. A missing pid file or an already-exited process means no server is running.
    /// <para>读取本地 hdc server 的 pid 文件并终止该进程以停止 server，因为 server 忽略任何协议层的 kill 命令。pid 文件缺失或进程已退出即表示 server 未运行。</para>
    /// </summary>
    /// <returns><c>true</c> when a running server was terminated; <c>false</c> when none was found.
    /// <para>成功终止运行中的 server 返回 <c>true</c>；未发现 server 返回 <c>false</c>。</para></returns>
    /// <param name="timeoutMs">Milliseconds to wait for the terminated process to exit.<para>等待被终止进程退出的毫秒数。</para></param>
    /// <param name="cancellationToken">Checked before any pid file is read.<para>在读取任何 pid 文件前进行检查。</para></param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was already signaled.<para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<bool> KillAsync(int timeoutMs = 10000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var fileName in new[] { ".HDCServer.pid", ".hdc.pid" })
        {
            var path = Path.Combine(Path.GetTempPath(), fileName);
            int pid;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (!int.TryParse(reader.ReadToEnd().Trim(), out pid) || pid <= 0)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                var server = Process.GetProcessById(pid);
                server.Kill(entireProcessTree: true);
                server.WaitForExit(timeoutMs);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException
                && ex is not OperationCanceledException)
            {
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>Rejects explicit server failures.<para>拒绝服务端明确失败响应。</para></summary>
    /// <param name="output">Server text inspected line by line for a leading <c>[Fail]</c> marker.<para>逐行检查是否以 <c>[Fail]</c> 开头的服务端文本。</para></param>
    /// <exception cref="IOException">Any line begins with <c>[Fail]</c>.<para>存在以 <c>[Fail]</c> 开头的行。</para></exception>
    public static void ThrowOnFailure(string output)
    {
        if (output.Split('\n').Any(line => line.TrimStart().StartsWith("[Fail]", StringComparison.Ordinal)))
            throw new IOException(output.Trim());
    }
    /// <summary>Creates a target-scoped session.<para>创建目标绑定会话。</para></summary>
    /// <param name="connectKey">Non-blank target identifier the session runs commands against.<para>会话执行命令所针对的非空白目标标识。</para></param>
    /// <returns>A session over a dedicated host-channel client.<para>基于独立宿主通道客户端的会话。</para></returns>
    /// <exception cref="ObjectDisposedException">This client has been disposed.<para>此客户端已被释放。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="connectKey"/> is null, empty or whitespace.<para><paramref name="connectKey"/> 为 null、空或仅含空白。</para></exception>
    public HdcSession CreateSession(string connectKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        return new HdcSession(new HdcServerClient(ServerHost, ServerPort), connectKey);
    }
    /// <summary>Prevents further requests.<para>禁止后续请求。</para></summary>
    public void Dispose() => _disposed = true;
}