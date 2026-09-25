using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents a parsed shell v2 protocol frame.
/// <para>解析后的 shell v2 协议帧。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ShellFrame"/> struct.
/// <para>初始化 <see cref="ShellFrame"/> 结构的新实例。</para>
/// </remarks>
/// <param name="kind">The frame kind. 帧类型。</param>
/// <param name="payload">The frame payload. 帧负载。</param>
internal readonly struct ShellFrame(ShellFrameKind kind, byte[] payload)
{
    /// <summary>
    /// Gets the frame kind.
    /// <para>获取帧类型。</para>
    /// </summary>
    public ShellFrameKind Kind { get; } = kind;

    /// <summary>
    /// Gets the frame payload.
    /// <para>获取帧负载。</para>
    /// </summary>
    public byte[] Payload { get; } = payload;
}

/// <summary>
/// Result of a shell command execution.
/// <para>Shell 命令执行结果。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ShellResult"/> struct.
/// <para>初始化 <see cref="ShellResult"/> 结构的新实例。</para>
/// </remarks>
/// <param name="stdout">The captured stdout. 捕获的 stdout。</param>
/// <param name="stderr">The captured stderr. 捕获的 stderr。</param>
/// <param name="exitCode">The exit code. 退出码。</param>
public readonly struct ShellResult(byte[] stdout, byte[] stderr, int exitCode)
{
    /// <summary>
    /// Gets the captured stdout.
    /// <para>获取捕获的 stdout。</para>
    /// </summary>
    public byte[] Stdout { get; } = stdout;

    /// <summary>
    /// Gets the captured stderr.
    /// <para>获取捕获的 stderr。</para>
    /// </summary>
    public byte[] Stderr { get; } = stderr;

    /// <summary>
    /// Gets the exit code.
    /// <para>获取退出码。</para>
    /// </summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Client for the ADB shell v2 protocol: executes commands on the device and
/// exposes stdout / stderr / exit code.
/// <para>ADB shell v2 协议客户端：在设备上执行命令并暴露 stdout / stderr / 退出码。</para>
/// </summary>
/// <remarks>
/// Initializes a new shell v2 client for the given command.
/// <para>为给定命令初始化新的 shell v2 客户端。</para>
/// </remarks>
/// <param name="connection">The ADB connection. ADB 连接。</param>
/// <param name="command">The command to execute. 要执行的命令。</param>
/// <param name="term">Terminal type, or null for default. 终端类型，null 表示默认。</param>
/// <param name="pty">Whether to use a pseudo-terminal. 是否使用伪终端。</param>
internal sealed class AdbShellClient(AdbConnection connection, string command, string? term = null, bool pty = false) : IAdbShellClient
{
    private const int FrameHeaderSize = 5; // kind(1) + length(4)
    private const int MaxFramePayloadSize = 16 * 1024 * 1024;

    /// <summary>Incremental shell-v2 frame parser used across ADB WRTE chunk boundaries.</summary>
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
                var length = BitConverter.ToInt32(_buffer, _start + 1);
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

    /// <summary>
    /// Executes the command and returns captured stdout/stderr/exit code.
    /// <para>执行命令并返回捕获的 stdout / stderr / 退出码。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds. 超时（毫秒）。</param>
    public ShellResult Execute(int timeoutMs = 30000)
    {
        using var stream = _connection.OpenStream(BuildServiceString());
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var parser = new FrameParser();
        int? exitCode = null;

        while (exitCode is null)
        {
            var chunk = stream.Read(timeoutMs);
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
        return new ShellResult(stdout.ToArray(), stderr.ToArray(), exitCode ?? 0);
    }

    /// <summary>
    /// Executes the command, streaming stdout/stderr chunks to callbacks.
    /// <para>流式执行命令，将 stdout / stderr 块传递给回调。</para>
    /// </summary>
    /// <param name="onStdout">Callback for stdout chunks. stdout 块回调。</param>
    /// <param name="onStderr">Callback for stderr chunks. stderr 块回调。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    public int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default)
    {
        using var stream = _connection.OpenStream(BuildServiceString());
        var parser = new FrameParser();
        int? exitCode = null;

        while (exitCode is null && !cancellationToken.IsCancellationRequested)
        {
            var chunk = stream.Read(250);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    continue;
                }

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
    /// Reads the exit code from a shell exit frame payload.
    /// <para>从 shell 退出帧负载读取退出码。</para>
    /// </summary>
    /// <param name="payload">The frame payload. 帧负载。</param>
    /// <returns>The exit code, or null if the payload is empty. 退出码，负载为空时返回 null。</returns>
    /// <remarks>AOSP shell_v2_protocol sends the exit code as a single byte.</remarks>
    private static int? ReadExitCode(byte[] payload) => payload is { Length: >= 1 } ? payload[0] : null;

}