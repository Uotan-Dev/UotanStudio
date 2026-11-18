using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents a parsed shell v2 protocol frame.
/// <para>解析后的 shell v2 协议帧。</para>
/// </summary>
/// <param name="kind">The frame kind.<para>帧类型。</para></param>
/// <param name="payload">The frame payload.<para>帧负载。</para></param>
internal readonly struct ShellFrame(ShellFrameKind kind, byte[] payload)
{
    /// <summary>
    /// Gets the frame kind.
    /// <para>获取帧类型。</para>
    /// </summary>
    /// <value>The parsed frame kind.<para>解析得到的帧类型。</para></value>
    public ShellFrameKind Kind { get; } = kind;

    /// <summary>
    /// Gets the frame payload.
    /// <para>获取帧负载。</para>
    /// </summary>
    /// <value>The frame payload bytes.<para>帧负载字节。</para></value>
    public byte[] Payload { get; } = payload;
}

/// <summary>
/// Result of a shell command execution.
/// <para>Shell 命令执行结果。</para>
/// </summary>
/// <param name="stdout">The concatenated stdout frame payloads. <para>各 stdout 帧负载的拼接结果。</para></param>
/// <param name="stderr">The concatenated stderr frame payloads. <para>各 stderr 帧负载的拼接结果。</para></param>
/// <param name="exitCode">The shell exit frame value. <para>shell 退出帧的值。</para></param>
public readonly struct ShellResult(byte[] stdout, byte[] stderr, int exitCode)
{
    /// <summary>
    /// Gets the captured stdout.
    /// <para>获取捕获的 stdout。</para>
    /// </summary>
    /// <value>The concatenated stdout frame payloads; empty when the command wrote none.
    /// <para>各 stdout 帧负载的拼接结果；命令未输出时为空。</para></value>
    public byte[] Stdout { get; } = stdout;

    /// <summary>
    /// Gets the captured stderr.
    /// <para>获取捕获的 stderr。</para>
    /// </summary>
    /// <value>The concatenated stderr frame payloads; empty when the command wrote none.
    /// <para>各 stderr 帧负载的拼接结果；命令未输出时为空。</para></value>
    public byte[] Stderr { get; } = stderr;

    /// <summary>
    /// Gets the exit code.
    /// <para>获取退出码。</para>
    /// </summary>
    /// <value>The value of the shell exit frame, or 0 when the stream closed without one.
    /// <para>shell 退出帧的值；流关闭而未见退出帧时为 0。</para></value>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Client for the ADB shell protocol: executes commands on the device and exposes stdout / stderr /
/// exit code. Prefers shell v2 when the peer advertises <c>shell_v2</c>, and falls back to the legacy
/// <c>shell:</c> service for old adbd versions (Android 4.4 and earlier) whose banner has no features.
/// <para>ADB shell 协议客户端：在设备上执行命令并暴露 stdout / stderr / 退出码。对端宣告
/// <c>shell_v2</c> 时优先使用 shell v2；对特性横幅为空的旧版 adbd（Android 4.4 及更早）回退到旧版
/// <c>shell:</c> 服务。</para>
/// </summary>
/// <param name="connection">The ADB connection.<para>ADB 连接。</para></param>
/// <param name="command">The command to execute.<para>要执行的命令。</para></param>
/// <param name="term">Terminal type, or null for default.<para>终端类型，null 表示默认。</para></param>
/// <param name="pty">Whether to use a pseudo-terminal.<para>是否使用伪终端。</para></param>
internal sealed class AdbShellClient(AdbConnection connection, string command, string? term = null, bool pty = false) : IAdbShellClient
{
    private const int FrameHeaderSize = 5;
    private const int MaxFramePayloadSize = 16 * 1024 * 1024;

    /// <summary>Close-stdin frame (<c>kIdCloseStdin</c>, empty payload) sent once the shell v2 service is open.
    /// <para>shell v2 服务打开后立即发送的关闭 stdin 帧（<c>kIdCloseStdin</c>，空负载）。</para></summary>
    private static readonly byte[] CloseStdinFrame = [(byte)ShellFrameKind.CloseStdin, 0, 0, 0, 0];

    /// <summary>
    /// Incremental shell-v2 frame parser used across ADB WRTE chunk boundaries.
    /// <para>跨 ADB WRTE 块边界增量解析 shell v2 帧。</para>
    /// </summary>
    internal sealed class FrameParser
    {
        private byte[] _buffer = [];
        private int _start;
        private int _end;

        public IReadOnlyList<ShellFrame> Feed(byte[] data)
        {
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer, _end);
            _end += data.Length;

            var frames = new List<ShellFrame>();
            while (_end - _start >= FrameHeaderSize)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start + 1));
                if (length < 0 || length > MaxFramePayloadSize)
                {
                    throw new InvalidDataException($"Invalid shell v2 frame length: {length}.");
                }

                if (_end - _start - FrameHeaderSize < length)
                {
                    break;
                }

                var payload = new byte[length];
                Array.Copy(_buffer, _start + FrameHeaderSize, payload, 0, length);
                frames.Add(new ShellFrame((ShellFrameKind)_buffer[_start], payload));
                _start += FrameHeaderSize + length;
            }

            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            return frames;
        }

        public void Complete()
        {
            if (_end != _start)
            {
                throw new InvalidDataException($"Shell v2 stream ended with a truncated frame ({_end - _start} buffered bytes).");
            }
        }

        private void EnsureCapacity(int additionalLength)
        {
            var buffered = _end - _start;
            if (_buffer.Length - _end >= additionalLength)
            {
                return;
            }

            if (_start > 0 && _buffer.Length - buffered >= additionalLength)
            {
                Array.Copy(_buffer, _start, _buffer, 0, buffered);
                _start = 0;
                _end = buffered;
                return;
            }

            var required = buffered + additionalLength;
            var capacity = Math.Max(256, _buffer.Length);
            while (capacity < required)
            {
                capacity = checked(capacity * 2);
            }

            var replacement = new byte[capacity];
            Array.Copy(_buffer, _start, replacement, 0, buffered);
            _buffer = replacement;
            _start = 0;
            _end = buffered;
        }
    }

    private readonly AdbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly string _command = command ?? throw new ArgumentNullException(nameof(command));
    private readonly string? _term = term;
    private readonly bool _pty = pty;
    private readonly bool _useLegacyShell = !PeerSupportsShellV2(connection);

    /// <summary>
    /// Checks whether the peer supports shell v2: server-mediated sessions keep the v2 path, direct
    /// sessions require the peer's CNXN banner to advertise <c>shell_v2</c>.
    /// <para>检查对端是否支持 shell v2：经 server 中转的会话保留 v2 路径；直连会话要求对端 CNXN
    /// 横幅宣告 <c>shell_v2</c>。</para>
    /// </summary>
    /// <param name="connection">The ADB connection.<para>ADB 连接。</para></param>
    /// <returns><see langword="true"/> when shell v2 is supported.<para>支持 shell v2 时为真。</para></returns>
    private static bool PeerSupportsShellV2(AdbConnection connection)
    {
        if (connection.UsesServer)
        {
            return true;
        }

        var features = connection.PeerFeatures;
        return features is not null && features.Any(static f =>
            string.Equals(f, "shell_v2", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the negotiated shell service string: shell v2 when the peer supports it, legacy
    /// <c>shell:</c> for old adbd versions (Android 4.4 and earlier) whose banner has no features.
    /// <para>构建协商后的 shell 服务字符串：对端支持时用 shell v2；对特性横幅为空的旧版 adbd
    /// （Android 4.4 及更早）使用旧版 <c>shell:</c>。</para>
    /// </summary>
    /// <returns>The negotiated shell service string.<para>协商后的 shell 服务字符串。</para></returns>
    private string BuildServiceString()
    {
        if (_useLegacyShell)
        {
            return "shell:" + _command;
        }

        var sb = new StringBuilder("shell,v2");
        if (!string.IsNullOrEmpty(_term))
        {
            sb.Append(",TERM=").Append(_term);
        }

        sb.Append(_pty ? ",pty:" : ":");
        sb.Append(_command);
        return sb.ToString();
    }

    /// <summary>Executes the command asynchronously and returns captured stdout/stderr/exit code.
    /// <para>异步执行命令并返回捕获的 stdout / stderr / 退出码。</para></summary>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task resolving to the shell result.<para>结果为 shell 执行结果的任务。</para></returns>
    /// <exception cref="TimeoutException">A read timed out while the stream was still open.
    /// <para>流仍打开时读取超时。</para></exception>
    /// <exception cref="InvalidDataException">A shell v2 stream ended with a truncated frame.
    /// <para>shell v2 流在帧未完整时结束。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask<ShellResult> ExecuteAsync(int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        await using var stream = await _connection
            .OpenProtocolStreamAsync(BuildServiceString(), cancellationToken)
            .ConfigureAwait(false);
        if (!_useLegacyShell)
        {
            await WriteCloseStdinBestEffortAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var parser = new FrameParser();
        int? exitCode = null;
        var stopwatch = Stopwatch.StartNew();
        CommTrace.State("adb-shell", $"shell start: {DescribeCommand()}.");

        while (exitCode is null)
        {
            var chunk = await ReadWithDeadlineAsync(stream, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    throw new TimeoutException($"Shell command did not complete within {timeoutMs} ms.");
                }

                if (!_useLegacyShell)
                {
                    parser.Complete();
                }

                break;
            }

            if (_useLegacyShell)
            {
                stdout.Write(chunk, 0, chunk.Length);
                continue;
            }

            foreach (var frame in parser.Feed(chunk))
            {
                switch (frame.Kind)
                {
                    case ShellFrameKind.Stdout: stdout.Write(frame.Payload, 0, frame.Payload.Length); break;
                    case ShellFrameKind.Stderr: stderr.Write(frame.Payload, 0, frame.Payload.Length); break;
                    case ShellFrameKind.Exit: exitCode = ReadExitCode(frame.Payload); break;
                    case ShellFrameKind.Stdin:
                        break;
                    case ShellFrameKind.CloseStdin:
                        break;
                    case ShellFrameKind.WindowSizeChange:
                        break;
                    default:
                        break;
                }
            }
        }

        if (!_useLegacyShell && exitCode is not null)
        {
            parser.Complete();
        }

        var stdoutBytes = stdout.ToArray();
        var stderrBytes = stderr.ToArray();
        CommTrace.State("adb-shell",
            $"shell done: exit={exitCode ?? 0}, stdout={stdoutBytes.Length} B, stderr={stderrBytes.Length} B, {stopwatch.ElapsedMilliseconds} ms.");
        CommTrace.Payload("adb-shell", "stdout", stdoutBytes);
        CommTrace.Payload("adb-shell", "stderr", stderrBytes);
        return new ShellResult(stdoutBytes, stderrBytes, exitCode ?? 0);
    }

    /// <summary>Sends the close-stdin frame as a best-effort hint: a fast peer may close the stream before the
    /// client resumes, and a remotely closed stream already settled the command outcome, so a close that arrived
    /// first is not an error. User-data writes never take this path.
    /// <para>尽力发送 close-stdin 帧：高速对端可能在客户端恢复前关闭流，而远端已关闭意味着命令结局已定，
    /// 因此先到的关闭不是错误。用户数据写入不走此路径。</para></summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    private static async ValueTask WriteCloseStdinBestEffortAsync(AdbStream stream, CancellationToken cancellationToken)
    {
        try
        {
            await stream.WriteAsync(CloseStdinFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (stream.IsClosed && !cancellationToken.IsCancellationRequested)
        {
            CommTrace.State("adb-shell", "Stream closed before close-stdin was sent; treating as benign.");
        }
    }

    /// <summary>Renders the command line for the debug log, bounded so a long command cannot flood it.
    /// <para>为调试日志渲染命令行，并做长度限制，避免超长命令刷屏。</para>
    /// </summary>
    /// <returns>The service command text, truncated to 120 characters.
    /// <para>服务命令文本，超过 120 字符时截断。</para></returns>
    private string DescribeCommand()
    {
        var text = BuildServiceString();
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    /// <summary>
    /// Executes the command asynchronously, streaming stdout/stderr chunks to callbacks.
    /// On the legacy path all output is delivered to <paramref name="onStdout"/>.
    /// <para>异步流式执行命令，将 stdout / stderr 块传递给回调。旧版路径的所有输出都传给
    /// <paramref name="onStdout"/>。</para>
    /// </summary>
    /// <param name="onStdout">Callback for stdout chunks.<para>stdout 块回调。</para></param>
    /// <param name="onStderr">Callback for stderr chunks.<para>stderr 块回调。</para></param>
    /// <param name="cancellationToken">Cancels the read that is in progress; the loop stops as soon as the
    /// wait observes the cancellation. <para>取消进行中的读取；等待一旦观察到取消即停止循环。</para></param>
    /// <returns>The exit code; <c>0</c> when the stream closed or cancellation took effect before
    /// an exit frame arrived. <para>退出码；流关闭或退出帧到达前被取消时为 <c>0</c>。</para></returns>
    /// <exception cref="InvalidDataException">A shell v2 stream ended with a truncated frame.
    /// <para>shell v2 流在帧未完整时结束。</para></exception>
    public async ValueTask<int> ExecuteStreamingAsync(Action<byte[]>? onStdout, Action<byte[]>? onStderr,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await _connection
            .OpenProtocolStreamAsync(BuildServiceString(), cancellationToken)
            .ConfigureAwait(false);
        if (!_useLegacyShell)
        {
            await WriteCloseStdinBestEffortAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var parser = new FrameParser();
        int? exitCode = null;

        while (exitCode is null)
        {
            byte[]? chunk;
            try
            {
                chunk = await stream.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (chunk is null)
            {
                if (!_useLegacyShell)
                {
                    parser.Complete();
                }

                break;
            }

            if (_useLegacyShell)
            {
                onStdout?.Invoke(chunk);
                continue;
            }

            foreach (var frame in parser.Feed(chunk))
            {
                switch (frame.Kind)
                {
                    case ShellFrameKind.Stdout: onStdout?.Invoke(frame.Payload); break;
                    case ShellFrameKind.Stderr: onStderr?.Invoke(frame.Payload); break;
                    case ShellFrameKind.Exit: exitCode = ReadExitCode(frame.Payload); break;
                    case ShellFrameKind.Stdin:
                        break;
                    case ShellFrameKind.CloseStdin:
                        break;
                    case ShellFrameKind.WindowSizeChange:
                        break;
                    default:
                        break;
                }
            }
        }

        if (!_useLegacyShell && exitCode is not null)
        {
            parser.Complete();
        }

        return exitCode ?? 0;
    }

    /// <summary>
    /// Reads one chunk under a per-read deadline expressed as <c>CancelAfter</c>, so the wait is cancelled
    /// rather than parked.
    /// <para>按以 <c>CancelAfter</c> 表达的单次读取期限读取一块数据，使等待被取消而非占用线程。</para>
    /// </summary>
    /// <param name="stream">The shell stream.<para>shell 流。</para></param>
    /// <param name="timeoutMs">Per-read wait in milliseconds.<para>单次读取的等待毫秒数。</para></param>
    /// <param name="cancellationToken">Cancellation token, which outranks the internal deadline.
    /// <para>取消令牌，其优先级高于内部期限。</para></param>
    /// <returns>The next chunk, or <see langword="null"/> when the deadline expired or the stream closed after
    /// its queue drained. <para>下一块数据；期限到达，或流已关闭且队列读空时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    private static async ValueTask<byte[]?> ReadWithDeadlineAsync(AdbStream stream, int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        try
        {
            return await stream.ReadAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the exit code from a shell exit frame payload.
    /// <para>从 shell 退出帧负载读取退出码。</para>
    /// </summary>
    /// <param name="payload">The frame payload.<para>帧负载。</para></param>
    /// <returns>The exit code, or null if the payload is empty.<para>退出码，负载为空时返回 null。</para></returns>
    private static int? ReadExitCode(byte[] payload) => payload is { Length: >= 1 } ? payload[0] : null;

}