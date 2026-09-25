using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>HDC device commands, using native OpenHarmony semantics.
/// <para>使用 OpenHarmony 原生命令语义的 HDC 设备服务。</para></summary>
public sealed class HdcDeviceServices : UotanStudio.Comm.Contracts.IHdcDeviceServices
{
    private readonly HdcConnection? _connection;
    private readonly HdcServerClient? _server;
    private readonly string? _connectKey;
    /// <summary>Creates direct device services.<para>创建直连设备服务。</para></summary>
    /// <param name="connection">An open native HDC connection used for every command on this instance.<para>已打开的原生 HDC 连接；此实例的每条命令都通过它执行。</para></param>
    public HdcDeviceServices(HdcConnection connection) => _connection = connection;
    internal HdcDeviceServices(HdcServerClient server, string connectKey)
    {
        _server = server;
        _connectKey = connectKey;
    }
    /// <summary>Quotes one device shell argument.<para>转义单个设备 Shell 参数。</para></summary>
    /// <param name="value">Raw argument text; it must be non-null and must not contain a NUL byte.<para>原始参数文本；不得为 null，且不得包含 NUL 字节。</para></param>
    /// <returns>The value wrapped in single quotes with embedded quotes escaped for the device shell.<para>用单引号包裹、并按设备 shell 规则转义内嵌引号后的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.<para><paramref name="value"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a NUL byte.<para><paramref name="value"/> 包含 NUL 字节。</para></exception>
    public static string QuoteShellArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("Argument contains NUL.", nameof(value));
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
    /// <summary>Executes a shell command.<para>执行 Shell 命令。</para></summary>
    /// <param name="command">Command text sent verbatim to the device shell.<para>原样发送到设备 shell 的命令文本。</para></param>
    /// <param name="timeoutMs">Overall response deadline in milliseconds.<para>总体响应时限（毫秒）。</para></param>
    /// <returns>The command's standard output decoded as UTF-8.<para>命令标准输出的 UTF-8 解码结果。</para></returns>
    public string RunCommand(string command, int timeoutMs = 30000)
    {
        var shell = _server is null ? new HdcShellClient(_connection!, command)
            : new HdcShellClient(_server, _connectKey!, command);
        return Encoding.UTF8.GetString(shell.Execute(timeoutMs).Stdout);
    }
    /// <summary>Reads an OpenHarmony system parameter.<para>读取 OpenHarmony 系统参数。</para></summary>
    /// <param name="key">Non-blank parameter name queried via <c>param get</c>.<para>通过 <c>param get</c> 查询的非空白参数名。</para></param>
    /// <returns>The trimmed parameter value.<para>去除首尾空白后的参数值。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null, empty or whitespace.<para><paramref name="key"/> 为 null、空或仅含空白。</para></exception>
    /// <exception cref="IOException">The device reported that the parameter could not be read.<para>设备报告无法读取该参数。</para></exception>
    public string GetProp(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var response = RunCommand("param get " + QuoteShellArgument(key)).Trim();
        if (response.StartsWith($"Get parameter \"{key}\" fail!", StringComparison.Ordinal))
            throw new IOException(response);
        return response;
    }
    /// <summary>Maps supported UI reboot modes to upstream target boot arguments.
    /// <para>将界面支持的重启模式映射为上游 target boot 参数。</para></summary>
    /// <param name="mode">UI reboot mode: <see langword="null"/>, empty, <c>"system"</c>, <c>"bootloader"</c> or <c>"recovery"</c>.<para>界面重启模式：<see langword="null"/>、空串、<c>"system"</c>、<c>"bootloader"</c> 或 <c>"recovery"</c>。</para></param>
    /// <returns>The upstream target boot argument; empty for a normal system reboot.<para>上游 target boot 参数；普通系统重启时为空串。</para></returns>
    /// <exception cref="NotSupportedException"><paramref name="mode"/> is not a supported reboot mode.<para><paramref name="mode"/> 不是受支持的重启模式。</para></exception>
    public static string RebootArgument(string? mode) => mode switch
    {
        null or "" or "system" => "",
        "bootloader" => "-bootloader",
        "recovery" => "-recovery",
        _ => throw new NotSupportedException($"Unsupported HDC reboot mode: {mode}")
    };
    /// <summary>Maps supported UI reboot modes to the device-shell reboot argument.
    /// <para>将界面支持的重启模式映射为设备 shell 的 reboot 参数。</para></summary>
    /// <param name="mode">UI reboot mode: <see langword="null"/>, empty, <c>"system"</c>, <c>"bootloader"</c> or <c>"recovery"</c>.<para>界面重启模式：<see langword="null"/>、空串、<c>"system"</c>、<c>"bootloader"</c> 或 <c>"recovery"</c>。</para></param>
    /// <returns>The device-shell <c>reboot</c> command for the mode.<para>对应模式的设备 shell <c>reboot</c> 命令。</para></returns>
    /// <exception cref="NotSupportedException"><paramref name="mode"/> is not a supported reboot mode.<para><paramref name="mode"/> 不是受支持的重启模式。</para></exception>
    public static string RebootShellArgument(string? mode) => mode switch
    {
        null or "" or "system" => "reboot",
        "bootloader" => "reboot bootloader",
        "recovery" => "reboot recovery",
        _ => throw new NotSupportedException($"Unsupported HDC reboot mode: {mode}")
    };

    /// <summary>Reboots to system, bootloader or recovery.<para>重启至系统、bootloader 或 recovery。</para></summary>
    /// <param name="mode">UI reboot mode: <see langword="null"/>, empty, <c>"system"</c>, <c>"bootloader"</c> or <c>"recovery"</c>.<para>界面重启模式：<see langword="null"/>、空串、<c>"system"</c>、<c>"bootloader"</c> 或 <c>"recovery"</c>。</para></param>
    /// <remarks>
    /// Server routes execute the device-shell <c>reboot</c>: "target boot" is a Unity binary
    /// command (CMD_UNITY_REBOOT) the server's text router does not carry, so a literal string
    /// there fails with "Device not founded" (verified against a real 3.0.0e server). Direct
    /// routes keep the native Unity channel.
    /// <para>server 路由改走设备 shell 的 <c>reboot</c>："target boot" 是 Unity 二进制命令
    /// （CMD_UNITY_REBOOT），server 文本路由不承载，直接发字符串会报
    /// "Device not founded"（真机 3.0.0e 验证）。直连路由保留原生 Unity 通道。</para>
    /// </remarks>
    public void Reboot(string? mode = null)
    {
        if (_server is not null)
        {
            ExecuteService("shell " + RebootShellArgument(mode), HdcCommand.UnityReboot, "", 10000);
            return;
        }

        var argument = RebootArgument(mode);
        ExecuteService("target boot" + (argument.Length == 0 ? "" : " " + argument), HdcCommand.UnityReboot, argument, 10000);
    }
    /// <summary>Remounts system/vendor when permitted by the device.<para>在设备允许时重新挂载 system/vendor。</para></summary>
    public void Remount() => ExecuteService("target mount", HdcCommand.UnityRemount, "", 10000);

    /// <summary>Lists processes exposed by the device's debug service.<para>列出设备调试服务暴露的进程。</para></summary>
    /// <returns>The debug-process list as text, using the default 10000 ms deadline.<para>调试进程列表文本，使用默认 10000 毫秒时限。</para></returns>
    public string ListDebugProcesses() => ListDebugProcesses(10000);

    /// <summary>Lists debug processes within an overall response deadline in milliseconds.<para>在总体响应时限（毫秒）内列出调试进程。</para></summary>
    /// <param name="timeoutMs">Overall response deadline in milliseconds.<para>总体响应时限（毫秒）。</para></param>
    /// <returns>The debug-process list as text.<para>调试进程列表的文本形式。</para></returns>
    public string ListDebugProcesses(int timeoutMs) => ExecuteService("jpid", HdcCommand.JdwpList, "", timeoutMs);

    /// <summary>Opens a native interactive shell; callers own its lifetime and terminal input.<para>打开原生交互式 shell；调用方负责其生命周期与终端输入。</para></summary>
    /// <returns>An open shell channel bound to the direct connection.<para>绑定直连会话的已打开 shell 通道。</para></returns>
    /// <exception cref="NotSupportedException">No direct connection is available for byte-stream channels.<para>没有可用于字节流通道的直连会话。</para></exception>
    public HdcStream OpenInteractiveShell() => (_connection ?? throw new NotSupportedException("Interactive byte streams require a direct connection."))
        .OpenChannel(HdcCommand.ShellInit, []);

    /// <summary>Opens a native debug-process tracker with the upstream default, package or all-process mode.<para>按上游默认、按包或全部进程模式打开原生调试进程跟踪通道。</para></summary>
    /// <param name="mode">Tracking mode: empty (default), <c>"p"</c> (package) or <c>"a"</c> (all processes).<para>跟踪模式：空串（默认）、<c>"p"</c>（按包）或 <c>"a"</c>（全部进程）。</para></param>
    /// <returns>An open tracker channel delivering process-list updates.<para>已打开的跟踪通道，持续投递进程列表更新。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not one of the supported values.<para><paramref name="mode"/> 不属于受支持的取值。</para></exception>
    /// <exception cref="NotSupportedException">No direct connection is available for native tracking.<para>没有可用于原生跟踪的直连会话。</para></exception>
    public HdcStream TrackDebugProcesses(string mode = "")
    {
        if (mode is not ("" or "p" or "a")) throw new ArgumentException("Invalid HDC process tracking mode.", nameof(mode));
        return (_connection ?? throw new NotSupportedException("Native process tracking requires a direct connection."))
            .OpenChannel(HdcCommand.JdwpTrack, Encoding.UTF8.GetBytes(mode));
    }

    /// <summary>Streams a device diagnostic report to a caller-owned destination without whole-report buffering.<para>将设备诊断报告流式写入调用方持有的目标，不对整份报告缓冲。</para></summary>
    /// <param name="destination">Writable stream that receives report bytes as they arrive; the caller owns and closes it.<para>接收报告字节的可写流，随到达即写入；由调用方持有并关闭。</para></param>
    /// <param name="cancellationToken">Cancels a blocked report read.<para>取消被阻塞的报告读取。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.<para><paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.<para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="NotSupportedException">No direct connection is available for native report streaming.<para>没有可用于原生报告流式传输的直连会话。</para></exception>
    /// <exception cref="TimeoutException">The report did not finish within the 300000 ms deadline.<para>报告未在 300000 毫秒时限内完成。</para></exception>
    /// <exception cref="InvalidDataException">The device sent an unexpected report command.<para>设备发送了非预期的报告命令。</para></exception>
    public void Bugreport(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        using var channel = (_connection ?? throw new NotSupportedException("Native report streaming requires a direct connection."))
            .OpenChannel(HdcCommand.UnityBugreportInit, []);
        var deadline = Environment.TickCount64 + 300000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("HDC diagnostic report timed out.");
            var message = channel.ReadMessage(250);
            if (message is null) { if (channel.IsClosed) return; continue; }
            if (message.Command == HdcCommand.UnityBugreportData) destination.Write(message.Payload ?? []);
            else if (message.Command is not (HdcCommand.Echo or HdcCommand.EchoRaw))
                throw new InvalidDataException("Unexpected HDC diagnostic report command.");
        }
    }

    /// <summary>Requests root or normal daemon startup, subject to device authorization and build policy.<para>请求以 root 或普通模式启动守护进程，受设备授权与构建策略约束。</para></summary>
    /// <param name="enabled"><see langword="true"/> to restart the daemon in root mode; <see langword="false"/> for normal mode.<para><see langword="true"/> 以 root 模式重启守护进程；<see langword="false"/> 为普通模式。</para></param>
    public void SetRootMode(bool enabled) => ExecuteService(enabled ? "smode" : "smode -r", HdcCommand.UnityRootRun, enabled ? "" : "r", 10000);

    /// <summary>Switches the daemon to USB or TCP mode using upstream tmode arguments.<para>使用上游 tmode 参数将守护进程切换为 USB 或 TCP 模式。</para></summary>
    /// <param name="tcpPort">TCP listen port (1..65535) for network mode, or <see langword="null"/> to switch back to USB.<para>网络模式的 TCP 监听端口（1..65535），为 <see langword="null"/> 时切回 USB。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tcpPort"/> is set but outside 1..65535.<para><paramref name="tcpPort"/> 已设置且不在 1..65535 范围内。</para></exception>
    public void SetRunMode(int? tcpPort)
    {
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        var argument = tcpPort is { } port ? $"port {port}" : "usb";
        ExecuteService("tmode " + argument, HdcCommand.UnityRunmode, argument, 10000);
    }
    private string ExecuteService(string command, HdcCommand nativeCommand, string argument, int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        if (_server is not null)
        {
            var response = Encoding.UTF8.GetString(_server.ExecuteOnDevice(_connectKey!, command, timeoutMs));
            HdcServerClient.ThrowOnFailure(response);
            return response;
        }
        using var channel = _connection!.OpenChannel(nativeCommand, Encoding.UTF8.GetBytes(argument));
        using var output = new MemoryStream();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new TimeoutException("HDC service did not complete.");
            var bytes = channel.Read((int)remaining);
            if (bytes is null) break;
            if (output.Length + bytes.Length > HdcServerClient.MaximumOutputBytes)
                throw new InvalidDataException("HDC service output limit exceeded.");
            output.Write(bytes);
        }
        if (!channel.IsClosed) throw new TimeoutException("HDC service did not complete.");
        var text = Encoding.UTF8.GetString(output.ToArray());
        HdcServerClient.ThrowOnFailure(text);
        return text;
    }
    /// <summary>Lists server targets.<para>列出服务端目标。</para></summary>
    /// <param name="timeoutMs">Overall response deadline in milliseconds.<para>总体响应时限（毫秒）。</para></param>
    /// <returns>Newline-joined connect keys reported by the host server.<para>由宿主服务端报告的、以换行拼接的连接键。</para></returns>
    /// <exception cref="NotSupportedException">Instance is bound to a direct connection rather than a host server.<para>此实例绑定的是直连会话，而非宿主服务端。</para></exception>
    public string ListTargets(int timeoutMs = 10000) => _server is null
        ? throw new NotSupportedException("Target enumeration is a host server command.")
        : string.Join('\n', _server.ListTargets(timeoutMs));
    /// <summary>Checks server availability.<para>检查服务端可用性。</para></summary>
    /// <returns><c>"OHOS HDC"</c> once the host channel handshake succeeds.<para>宿主通道握手成功后返回 <c>"OHOS HDC"</c>。</para></returns>
    /// <exception cref="NotSupportedException">No host channel is available to check.<para>没有可用于检查的宿主通道。</para></exception>
    public string CheckServer()
    {
        if (_server is null) throw new NotSupportedException("Server checks require a host channel.");
        _server.Connect();
        return "OHOS HDC";
    }
    /// <summary>Waits for a selected target with a bounded deadline.<para>在有限时限内等待选定目标。</para></summary>
    /// <param name="connectKey">Target identifier to wait for.<para>要等待的目标标识。</para></param>
    /// <param name="timeoutMs">Overall wait deadline in milliseconds.<para>总体等待时限（毫秒）。</para></param>
    /// <returns>The <paramref name="connectKey"/> once it appears in the target list.<para>目标出现在列表中后返回的 <paramref name="connectKey"/>。</para></returns>
    /// <exception cref="NotSupportedException">No host channel is available to poll targets.<para>没有可用于轮询目标的宿主通道。</para></exception>
    /// <exception cref="TimeoutException">The target did not appear before the deadline.<para>目标在时限前未出现。</para></exception>
    public string WaitFor(string connectKey, int timeoutMs = 30000)
    {
        if (_server is null) throw new NotSupportedException("Waiting for targets requires a host channel.");
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (_server.ListTargets((int)Math.Max(1, timeoutMs - (Environment.TickCount64 - start))).Contains(connectKey)) return connectKey;
            Thread.Sleep(100);
        }
        throw new TimeoutException("HDC target did not become available.");
    }
}