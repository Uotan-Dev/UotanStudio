using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Result of an HDC shell command execution.
/// <para>HDC shell 命令执行结果。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ShellResult"/> struct.
/// <para>初始化 <see cref="ShellResult"/> 结构的新实例。</para>
/// </remarks>
/// <param name="stdout">The captured stdout.<para>捕获的 stdout。</para></param>
/// <param name="stderr">The captured stderr.<para>捕获的 stderr。</para></param>
/// <param name="exitCode">The exit code.<para>退出码。</para></param>
public readonly struct ShellResult(byte[] stdout, byte[] stderr, int exitCode)
{
    /// <summary>
    /// Gets the captured stdout.
    /// <para>获取捕获的 stdout。</para>
    /// </summary>
    /// <value>All bytes the command produced; HDC merges stdout and stderr into this array.
    /// <para>命令产生的全部字节；HDC 将 stdout 与 stderr 合并到此数组。</para></value>
    public byte[] Stdout { get; } = stdout;

    /// <summary>
    /// Gets the captured stderr.
    /// <para>获取捕获的 stderr。</para>
    /// </summary>
    /// <value>An empty array, because the HDC transport does not separate stderr from stdout.
    /// <para>始终为空数组，因为 HDC 传输不区分 stdout 与 stderr。</para></value>
    public byte[] Stderr { get; } = stderr;

    /// <summary>
    /// Gets the exit code.
    /// <para>获取退出码。</para>
    /// </summary>
    /// <value>-1, since the HDC shell channel exposes no explicit process exit code.
    /// <para>为 -1；HDC shell 通道不暴露显式的进程退出码。</para></value>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Client for the HDC shell protocol: executes commands on the device and
/// exposes stdout / stderr / exit code. Unlike ADB shell_v2, HDC carries
/// shell data directly in channel payloads with no frame header.
/// <para>HDC shell 协议客户端：在设备上执行命令并暴露 stdout / stderr / 退出码。
/// 与 ADB shell_v2 不同，HDC 将 shell 数据直接放在通道负载中，无帧头。</para>
/// </summary>
/// <remarks>
/// Initializes a new shell client for the given command.
/// <para>为给定命令初始化新的 shell 客户端。</para>
/// </remarks>
public sealed class HdcShellClient : Contracts.IHdcShellClient
{
    private readonly HdcConnection? _connection;
    private readonly HdcServerClient? _server;
    private readonly string? _connectKey;
    private readonly string _command;

    /// <summary>
    /// Creates a direct client.
    /// <para>创建直连客户端。</para>
    /// </summary>
    /// <param name="connection">The authenticated direct HDC connection used to open the shell channel.<para>用于打开 shell 通道的已认证直连 HDC 连接。</para></param>
    /// <param name="command">The shell command to execute on the device.<para>要在设备上执行的 shell 命令。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="command"/> is <see langword="null"/>.
    /// <para><paramref name="connection"/> 或 <paramref name="command"/> 为 <see langword="null"/>。</para></exception>
    public HdcShellClient(HdcConnection connection, string command)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _command = command ?? throw new ArgumentNullException(nameof(command));
    }
    internal HdcShellClient(HdcServerClient server, string connectKey, string command)
    {
        _server = server;
        _connectKey = connectKey;
        _command = command;
    }

    /// <summary>
    /// Executes the command and returns captured stdout/stderr/exit code.
    /// <para>执行命令并返回捕获的 stdout / stderr / 退出码。</para>
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.<para>超时（毫秒）。</para></param>
    /// <returns>The shell result containing stdout, stderr and exit code.
    /// <para>包含 stdout、stderr 和退出码的 shell 结果。</para></returns>
    public ShellResult Execute(int timeoutMs = 30000)
    {
        if (_server is not null)
        {
            var output = _server.ExecuteOnDevice(_connectKey!, "shell " + _command, timeoutMs);
            HdcServerClient.ThrowOnFailure(Encoding.UTF8.GetString(output));
            return new ShellResult(output, [], -1);
        }
        using var stream = _connection!.OpenChannel(HdcCommand.UnityExecute, Encoding.UTF8.GetBytes(_command));
        using var stdout = new MemoryStream();
        var exitCode = -1;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new TimeoutException("HDC shell deadline exceeded.");
            var chunk = stream.Read((int)remaining);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    throw new TimeoutException($"Shell command did not complete within {timeoutMs} ms.");
                }

                break;
            }

            if (stdout.Length + chunk.Length > HdcServerClient.MaximumOutputBytes)
                throw new InvalidDataException("HDC shell output limit exceeded.");
            stdout.Write(chunk, 0, chunk.Length);
        }

        return new ShellResult(stdout.ToArray(), [], exitCode);
    }

    /// <summary>
    /// Executes the command, streaming stdout chunks to a callback.
    /// <para>流式执行命令，将 stdout 块传递给回调。</para>
    /// </summary>
    /// <param name="onStdout">Callback for stdout chunks.<para>stdout 块回调。</para></param>
    /// <param name="onStderr">Callback for stderr chunks (unused in HDC — stdout/stderr are merged).
    /// <para>stderr 块回调（HDC 中未使用 — stdout/stderr 合并传输）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The exit code.<para>退出码。</para></returns>
    public int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_server is not null)
        {
            _server.ExecuteStreamingAsync(_connectKey!, "shell " + _command, bytes => onStdout?.Invoke(bytes), cancellationToken).GetAwaiter().GetResult();
            return -1;
        }
        using var stream = _connection!.OpenChannel(HdcCommand.UnityExecute, Encoding.UTF8.GetBytes(_command));

        while (!cancellationToken.IsCancellationRequested)
        {
            var chunk = stream.Read(250);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    continue;
                }

                break;
            }

            onStdout?.Invoke(chunk);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return -1;
    }
}