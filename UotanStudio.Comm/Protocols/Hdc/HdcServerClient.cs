using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Host channel client following upstream channel.h/channel.cpp and host/client.cpp.
/// <para>上游 HDC 主机通道客户端；每条命令独占连接，不混用设备端会话协议。</para>
/// </summary>
public sealed class HdcServerClient(string host = "127.0.0.1", int? port = null) : IDisposable
{
    private bool _disposed;
    /// <summary>Server host. 服务端主机。</summary>
    public string ServerHost { get; } = host;
    /// <summary>Server port. 服务端端口。</summary>
    public int ServerPort { get; } = port ?? DefaultServerPort;
    /// <summary>Upstream OHOS_HDC_SERVER_PORT override, otherwise 8710; the single definition
    /// lives in <c>Routing.DebugBridgeEndpoints</c>.
    /// <para>遵循上游 OHOS_HDC_SERVER_PORT 环境变量，否则使用 8710；唯一定义在
    /// <c>Routing.DebugBridgeEndpoints</c>。</para></summary>
    public static int DefaultServerPort => Routing.DebugBridgeEndpoints.HdcServerPort;
    /// <summary>Maximum frame allocation. 单帧分配上限。</summary>
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    /// <summary>Maximum captured output. 捕获输出上限。</summary>
    public const int MaximumOutputBytes = 16 * 1024 * 1024;

    /// <summary>Checks the server-initiated handshake. 检查服务端主动握手。</summary>
    public bool DetectServer(int timeoutMs = 2000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { ConnectAsync(timeout.Token).GetAwaiter().GetResult(); return true; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }
    /// <summary>Checks server availability. 检查服务端可用性。</summary>
    public bool IsServerRunning() => DetectServer();
    /// <summary>Validates a channel; each operation reconnects. 验证通道，各操作单独连接。</summary>
    public void Connect() => ConnectAsync().GetAwaiter().GetResult();
    /// <summary>Validates a channel with cancellation. 可取消地验证通道。</summary>
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
            // banner[12] + union(channelId, connectKey[32]); omit optional version as
            // upstream clients without HDC_VERSION_CHECK do; preserve feature bytes.
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
    /// <summary>Streams complete frames until EOF or cancellation. 流式读取完整帧，直到关闭或取消。</summary>
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

    /// <summary>Relays a target-bound interactive shell through the host channel.</summary>
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
    /// <summary>Captures bounded output within a total deadline. 在总时限内有界捕获输出。</summary>
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
    /// <summary>Executes a device command using HDC syntax. 按 HDC 语法执行设备命令。</summary>
    public byte[] ExecuteOnDevice(string connectKey, string command, int timeoutMs = 30000)
        => ExecuteAsync(connectKey, command, timeoutMs).GetAwaiter().GetResult();
    /// <summary>Lists targets, excluding the upstream empty marker. 列出目标并排除上游空列表标记。</summary>
    public IReadOnlyList<string> ListTargets(int timeoutMs = 10000)
    {
        var output = Encoding.UTF8.GetString(ExecuteOnDevice(string.Empty, "list targets", timeoutMs)).TrimEnd('\0');
        ThrowOnFailure(output);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("[Empty]", StringComparison.Ordinal))
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0])
            .Distinct(StringComparer.Ordinal).ToArray();
    }
    /// <summary>Lists authoritative transport and authorization state from the host server.</summary>
    public IReadOnlyList<HdcTargetInfo> ListTargetDetails(int timeoutMs = 10000)
        => HdcTargetInfo.Parse(Encoding.UTF8.GetString(ExecuteOnDevice(string.Empty, "list targets -v", timeoutMs)));

    /// <summary>
    /// <para>停止本机 hdc server。上游 <c>hdc kill</c> 不走主机通道协议：客户端读取
    /// server 写入的 pid 文件（<c>.HDCServer.pid</c> / 旧版 <c>.hdc.pid</c>）后直接终止该
    /// 进程（client.cpp KillServer + base.cpp ProgramMutex）。真机验证表明 server 会忽略
    /// 线上 kill 命令，因此这里对齐上游语义。找不到 pid 文件或进程已退出时视为无 server。</para>
    /// Stops the local hdc server the way upstream does: the client reads the server's pid
    /// file (<c>.HDCServer.pid</c> / legacy <c>.hdc.pid</c>) and terminates that process,
    /// because the server ignores any wire-level kill command (verified against a real 3.0.0e
    /// server). A missing pid file or an already-exited process means no server is running.
    /// </summary>
    /// <returns><c>true</c> when a running server was terminated; <c>false</c> when none was found.
    /// <para>成功终止运行中的 server 返回 <c>true</c>；未发现 server 返回 <c>false</c>。</para></returns>
    public Task<bool> KillAsync(int timeoutMs = 10000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var fileName in new[] { ".HDCServer.pid", ".hdc.pid" })
        {
            var path = Path.Combine(Path.GetTempPath(), fileName);
            int pid;
            try
            {
                // The running server keeps the pid file open; a shared read is required.
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
                // Stale pid file pointing at an exited or foreign process; try the next name.
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>Rejects explicit server failures. 拒绝服务端明确失败响应。</summary>
    public static void ThrowOnFailure(string output)
    {
        if (output.Split('\n').Any(line => line.TrimStart().StartsWith("[Fail]", StringComparison.Ordinal)))
            throw new IOException(output.Trim());
    }
    /// <summary>Creates a target-scoped session. 创建目标绑定会话。</summary>
    public HdcSession CreateSession(string connectKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectKey);
        return new HdcSession(new HdcServerClient(ServerHost, ServerPort), connectKey);
    }
    /// <summary>Prevents further requests. 禁止后续请求。</summary>
    public void Dispose() => _disposed = true;
}