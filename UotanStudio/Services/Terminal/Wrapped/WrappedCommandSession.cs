using System.Text;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// Input-line interception state machine for one local shell terminal session. Characters keep
/// flowing to the real shell so native line editing, history and tab completion behave unchanged;
/// when an Enter finalizes a line that names a wrapped tool with no native binary on the PATH, the
/// session executes the command inside the toolbox instead, prints official-style output into the
/// terminal, then sends Ctrl+C to the shell so it discards the pending line and re-issues its prompt.
/// Escape sequences the terminal generates itself (focus, cursor-position and device replies) stay
/// forwarded to the shell but never enter the mirrored line, so they cannot block interception.
/// <para>本地 shell 终端会话的输入行拦截状态机。字符照常流向真实 shell，原生行编辑、历史与 Tab 补全不受
/// 影响；当回车敲定一行以包装工具名开头且 PATH 上没有原生二进制时，本会话改为在工具箱内执行该命令，把
/// 官方风格输出写进终端，然后向 shell 发送 Ctrl+C 使其丢弃挂起行并重新给出提示符。终端自身产生的转义
/// 序列（焦点、光标位置与设备应答）仍转发给 shell，但不进入镜像行，因此不会阻断拦截。</para>
/// </summary>
/// <param name="runner">The in-toolbox command runner.<para>工具箱内命令执行器。</para></param>
/// <param name="write">Terminal output callback; the caller supplies a thread-safe write into the
/// terminal display. <para>终端输出回调；调用方提供线程安全的终端显示写入。</para></param>
/// <param name="sendToPty">Writes raw input to the real PTY, for forwarded text and the closing
/// Ctrl+C. <para>向真实 PTY 写原始输入，用于转发文本与收尾的 Ctrl+C。</para></param>
/// <param name="nativeBinaryProbe">Overrides the PATH probe; tests inject a fake.
/// <para>覆盖 PATH 探测；测试注入假实现。</para></param>
internal sealed class WrappedCommandSession(WrappedCommandRunner runner, Action<string> write,
    Func<string, Task> sendToPty, Func<WrappedTool, bool>? nativeBinaryProbe = null)
{
    /// <summary>Bounds the mirrored pending line so a runaway paste cannot grow memory unboundedly.
    /// <para>限制镜像中的待执行行长度，避免失控粘贴无界增长内存。</para></summary>
    private const int MaxPendingLength = 4 * 1024;

    /// <summary>Bounds the replay queue kept for the remainder of a paste that contained an
    /// intercepted Enter. <para>包含被拦截回车的粘贴余文的重放队列上限。</para></summary>
    private const int MaxReplayLength = 8 * 1024;

    /// <summary>Bounds a mirrored escape sequence so a runaway terminal reply cannot grow memory
    /// unboundedly. <para>限制镜像中的转义序列长度，避免失控的终端应答无界增长内存。</para></summary>
    private const int MaxEscapeLength = 64;

    private enum EscapeState
    {
        None,
        Escape,
        Csi,
        Osc,
        OscEscape,
        String,
    }

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _escape = new();
    private readonly Queue<string> _replay = new();
    private readonly Lock _replayLock = new();
    private EscapeState _escapeState;
    // Every byte this session sends to the PTY passes through one gate, so replayed text, the
    // closing Ctrl+C and live keystrokes cannot interleave at the stream level.
    // 本会话发往 PTY 的每个字节都经过同一个门，重放文本、收尾 Ctrl+C 与实时按键不会在流层面交错。
    private readonly SemaphoreSlim _ptyGate = new(1, 1);
    private readonly Func<WrappedTool, bool> _nativeBinaryProbe = nativeBinaryProbe ?? HasNativeBinary;
    private bool _desynced;
    private int _busy;
    private CancellationTokenSource? _runCancellation;

    /// <summary>Processes one input chunk, forwarding the surviving text to the real PTY.
    /// <para>处理一段输入，把幸存的文本转发给真实 PTY。</para></summary>
    /// <param name="data">The raw input chunk from the PTY input stream.<para>PTY 输入流给出的原始输入块。</para></param>
    /// <returns>A task completing when the chunk and any forwarded text have been handled.
    /// <para>输入块与转发文本处理完毕时完成的任务。</para></returns>
    internal async Task DispatchAsync(string data)
    {
        if (data.Length == 0) return;
        await _ptyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var forward = Handle(data);
            if (forward.Length > 0) await sendToPty(forward).ConfigureAwait(false);
        }
        finally
        {
            _ptyGate.Release();
        }
    }

    /// <summary>Writes raw input to the real PTY under the same serialization as dispatch.
    /// <para>在与派发相同的串行化之下向真实 PTY 写原始输入。</para></summary>
    /// <param name="data">Raw text such as the closing Ctrl+C.<para>原始文本，如收尾的 Ctrl+C。</para></param>
    internal async Task DispatchRawAsync(string data)
    {
        await _ptyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await sendToPty(data).ConfigureAwait(false);
        }
        finally
        {
            _ptyGate.Release();
        }
    }

    /// <summary>Reports whether a wrapped command is currently executing.<para>报告当前是否有包装命令在执行。</para></summary>
    internal bool IsBusy => Interlocked.CompareExchange(ref _busy, 1, 1) == 1;

    /// <summary>Splits one chunk into the text that must still reach the PTY and consumed input.
    /// Callers use <see cref="DispatchAsync"/>; tests use this directly.
    /// <para>把一段输入切分为仍需送达 PTY 的文本与已消费输入。调用方使用 <see cref="DispatchAsync"/>；测试直接使用本方法。</para></summary>
    /// <param name="data">The raw input chunk.<para>原始输入块。</para></param>
    /// <returns>The text to forward; empty when everything was consumed.
    /// <para>待转发文本；全部消费时为空。</para></returns>
    internal string Handle(string data)
    {
        if (data.Length == 0) return string.Empty;
        if (Interlocked.CompareExchange(ref _busy, 1, 1) == 1)
        {
            // While a wrapped command runs the terminal is ours: only Ctrl+C cancels it.
            // 包装命令执行期间终端归我们：只有 Ctrl+C 会取消它。
            if (data.Contains('\x03'))
            {
                _runCancellation?.Cancel();
                lock (_replayLock)
                {
                    _replay.Clear();
                }
            }
            else
            {
                EnqueueReplay(data);
            }
            return string.Empty;
        }
        var forward = new StringBuilder();
        for (var index = 0; index < data.Length; index++)
        {
            if (!ProcessChar(data[index], forward))
            {
                // Everything typed in this chunk belongs to the intercepted line, so none of it may
                // reach the shell: a pasted line would otherwise run twice, once wrapped and once for
                // real. The rest of the chunk belongs to the next prompt and queues for replay.
                // 本块中已敲入的字符属于被拦截的行，一个字节都不能到达 shell：否则粘贴的行会被执行两次。
                // 本块剩余部分属于下一个提示符，排队等执行结束后重放。
                if (index + 1 < data.Length) EnqueueReplay(data[(index + 1)..]);
                return string.Empty;
            }
        }
        return forward.ToString();
    }

    private bool ProcessChar(char c, StringBuilder forward)
    {
        // Protocol traffic is forwarded verbatim: the shell still needs focus reports, cursor
        // position replies and device attributes, while the command mirror must ignore them.
        // 协议流量原样转发：shell 仍需要焦点报告、光标位置应答与设备属性，命令镜像则必须忽略它们。
        forward.Append(c);
        if (_escapeState != EscapeState.None && c is '\r' or '\n' or '\x03') FinishEscape(userEditing: true);
        if (_escapeState != EscapeState.None)
        {
            ConsumeEscape(c);
            return true;
        }
        if (c == '\x1b')
        {
            RestartEscape();
            return true;
        }
        if (c is '\r' or '\n')
        {
            var line = _pending.ToString();
            _pending.Clear();
            var wasDesynced = _desynced;
            _desynced = false;
            if (!wasDesynced && TryTakeOver(line))
            {
                StartRun(line);
                return false;
            }
            return true;
        }
        if (c is '\b' or '\x7f')
        {
            if (_pending.Length > 0) _pending.Length--;
            return true;
        }
        if (c == '\x15')
        {
            _pending.Clear();
            return true;
        }
        if (c == '\x03')
        {
            _pending.Clear();
            _desynced = false;
            return true;
        }
        if (c == '\t' || (c < ' ' && c != '\r' && c != '\n'))
        {
            // Tab completion and other control bytes desynchronize the mirror; the line is left
            // entirely to the shell. Tab 补全与其它控制字节会使镜像失步；整行交还给 shell。
            Desync();
            return true;
        }
        if (_desynced) return true;
        if (_pending.Length < MaxPendingLength) _pending.Append(c);
        return true;
    }

    /// <summary>Consumes one byte of a terminal escape sequence and decides whether the completed
    /// sequence came from a line-editing key. <para>消费转义序列的一个字节，并判定完整序列是否来自行编辑按键。</para></summary>
    /// <param name="c">The byte just forwarded to the PTY.<para>刚转发到 PTY 的字节。</para></param>
    private void ConsumeEscape(char c)
    {
        if (_escape.Length < MaxEscapeLength) _escape.Append(c);
        switch (_escapeState)
        {
            case EscapeState.Escape:
                if (c == '[') _escapeState = EscapeState.Csi;
                else if (c == ']') _escapeState = EscapeState.Osc;
                else if (c == '\x1b') RestartEscape();
                else if (c is 'P' or 'X' or '^' or '_') _escapeState = EscapeState.String;
                else FinishEscape(IsEditingEscape(c));
                break;
            case EscapeState.Csi:
                if (c is >= '@' and <= '~') FinishEscape(IsEditingCsi(_escape.ToString()));
                else if (c == '\x1b') RestartEscape();
                break;
            case EscapeState.Osc:
                if (c == '\x07') FinishEscape(userEditing: false);
                else if (c == '\x1b') _escapeState = EscapeState.OscEscape;
                break;
            case EscapeState.OscEscape:
                if (c == '\\') FinishEscape(userEditing: false);
                else if (c == '\x1b') RestartEscape();
                else _escapeState = EscapeState.Osc;
                break;
            case EscapeState.String:
                if (c is '\x07' or '\x1b') FinishEscape(userEditing: false);
                break;
            default:
                FinishEscape(userEditing: true);
                break;
        }
        if (_escapeState != EscapeState.None && _escape.Length >= MaxEscapeLength) FinishEscape(userEditing: true);
    }

    /// <summary>Ends a mirrored escape sequence, desynchronizing only for line-editing keys.
    /// <para>结束镜像中的转义序列，仅对行编辑按键标记失步。</para></summary>
    /// <param name="userEditing">Whether the sequence came from a line-editing key.
    /// <para>序列是否来自行编辑按键。</para></param>
    private void FinishEscape(bool userEditing)
    {
        _escapeState = EscapeState.None;
        _escape.Clear();
        if (userEditing) Desync();
    }

    private void RestartEscape()
    {
        _escapeState = EscapeState.Escape;
        _escape.Clear();
        _escape.Append('\x1b');
    }

    private void Desync()
    {
        _desynced = true;
        _pending.Clear();
    }

    /// <summary>Reports whether a bare <c>ESC x</c> sequence is a line-editing key rather than a
    /// focus report. <para>报告 <c>ESC x</c> 序列是否为行编辑按键而非焦点报告。</para></summary>
    private static bool IsEditingEscape(char final) => final is not ('I' or 'O' or 'c' or 'n' or '\\');

    /// <summary>Reports whether a CSI sequence is a line-editing key; terminal replies, focus
    /// reports, mouse reports and mode reports are protocol noise for the command mirror.
    /// <para>报告 CSI 序列是否为行编辑按键；终端应答、焦点报告、鼠标报告与模式报告对命令镜像都是协议噪声。</para></summary>
    private static bool IsEditingCsi(string sequence)
    {
        if (sequence.Length < 3) return true;
        var parameters = sequence[2..^1];
        return sequence[^1] switch
        {
            'A' or 'B' or 'C' or 'D' or 'H' or 'F' or 'Z' => true,
            '~' => parameters is "1" or "2" or "3" or "4" or "5" or "6" or "200" or "201",
            _ => false,
        };
    }

    private bool TryTakeOver(string line)
    {
        var command = WrappedCommandLine.TryParse(line);
        if (command is null || !runner.CanHandle(command)) return false;
        return !_nativeBinaryProbe(command.Tool);
    }

    private void StartRun(string line)
    {
        // Parse again inside the run so the interception decision and the executed command cannot
        // disagree. 复刻一次解析，使拦截判定与实际执行的命令不可能不一致。
        var command = WrappedCommandLine.TryParse(line);
        if (command is null) return;
        Interlocked.Exchange(ref _busy, 1);
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        write("\r\n");
        _ = RunAsync(command, cancellation);
    }

    private async Task RunAsync(WrappedCommand command, CancellationTokenSource cancellation)
    {
        try
        {
            try
            {
                await runner.RunAsync(command, write, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The user pressed Ctrl+C; the runner already stopped writing.
                // 用户按下了 Ctrl+C；执行器已停止写出。
            }
            catch (Exception ex)
            {
                write($"adb: internal wrapped command failure: {ex.Message}\r\n");
            }
        }
        finally
        {
            cancellation.Dispose();
            _runCancellation = null;
        }
        // Send the closing Ctrl+C while still marked busy, so keystrokes racing with it queue for
        // replay instead of slipping ahead of the discard. The typed line is still pending in the
        // shell: Ctrl+C discards it and re-issues the prompt.
        // 仍保持 busy 状态发送收尾 Ctrl+C，让与它竞争的按键进入重放队列而不是插到丢弃之前。输入行
        // 仍挂在 shell 上：Ctrl+C 丢弃该行并重新给出提示符。
        await DispatchRawAsync("\x03").ConfigureAwait(false);
        Interlocked.Exchange(ref _busy, 0);
        _ = ReplayAsync();
    }

    private async Task ReplayAsync()
    {
        while (true)
        {
            string chunk;
            lock (_replayLock)
            {
                if (_replay.Count == 0 || IsBusy) return;
                chunk = _replay.Dequeue();
            }
            await DispatchAsync(chunk).ConfigureAwait(false);
        }
    }

    private void EnqueueReplay(string data)
    {
        if (data.Length > MaxReplayLength) return;
        lock (_replayLock)
        {
            _replay.Enqueue(data);
        }
    }

    /// <summary>Probes the PATH for a native tool binary; when one exists the wrapped executor
    /// declines the takeover so the real tool runs. <para>探测 PATH 上的原生工具二进制；存在时包装执行器
    /// 放弃接管，让真实工具运行。</para></summary>
    /// <param name="tool">The tool to probe.<para>要探测的工具。</para></param>
    /// <returns><c>true</c> when a native binary exists.<para>存在原生二进制时为 <c>true</c>。</para></returns>
    internal static bool HasNativeBinary(WrappedTool tool)
    {
        var name = tool switch
        {
            WrappedTool.Adb => "adb",
            WrappedTool.Fastboot => "fastboot",
            WrappedTool.Hdc => "hdc",
            _ => string.Empty,
        };
        if (name.Length == 0) return false;
        string[] candidates = OperatingSystem.IsWindows()
            ? [$"{name}.exe", $"{name}.cmd", $"{name}.bat", name]
            : [name];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), candidate))) return true;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entries are skipped like the shell does.
                    // 畸形 PATH 条目按 shell 的方式跳过。
                }
            }
        }
        return false;
    }
}