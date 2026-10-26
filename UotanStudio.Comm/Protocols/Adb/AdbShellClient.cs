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
/// Client for the ADB shell v2 protocol: executes commands on the device and
/// exposes stdout / stderr / exit code.
/// <para>ADB shell v2 协议客户端：在设备上执行命令并暴露 stdout / stderr / 退出码。</para>
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

    /// <summary>
    /// Builds the shell v2 service string.
    /// <para>构建 shell v2 服务字符串。</para>
    /// </summary>
    /// <returns>The shell v2 service string.<para>shell v2 服务字符串。</para></returns>
    private string BuildServiceString()
    {
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
    /// <exception cref="InvalidDataException">The stream ended with a truncated shell v2 frame.
    /// <para>流在 shell v2 帧未完整时结束。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public async ValueTask<ShellResult> ExecuteAsync(int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        await using var stream = await _connection
            .OpenProtocolStreamAsync(BuildServiceString(), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(CloseStdinFrame, cancellationToken).ConfigureAwait(false);
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

                parser.Complete();
                break;
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

        if (exitCode is not null)
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
    /// <para>异步流式执行命令，将 stdout / stderr 块传递给回调。</para>
    /// </summary>
    /// <param name="onStdout">Callback for stdout chunks.<para>stdout 块回调。</para></param>
    /// <param name="onStderr">Callback for stderr chunks.<para>stderr 块回调。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task resolving to the exit code. <para>结果为退出码的任务。</para></returns>
    /// <exception cref="InvalidDataException">The stream ended with a truncated shell v2 frame.
    /// <para>流在 shell v2 帧未完整时结束。</para></exception>
    public async ValueTask<int> ExecuteStreamingAsync(Action<byte[]>? onStdout, Action<byte[]>? onStderr,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await _connection
            .OpenProtocolStreamAsync(BuildServiceString(), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(CloseStdinFrame, cancellationToken).ConfigureAwait(false);
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
                parser.Complete();
                break;
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

        if (exitCode is not null)
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