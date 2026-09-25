using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>HDC device commands, using native OpenHarmony semantics.
/// <para>使用 OpenHarmony 原生命令语义的 HDC 设备服务。</para></summary>
public sealed class HdcDeviceServices : UotanStudio.Comm.Contracts.IHdcDeviceServices
{
    private readonly HdcConnection? _connection;
    private readonly HdcServerClient? _server;
    private readonly string? _connectKey;
    /// <summary>Creates direct device services. 创建直连设备服务。</summary>
    public HdcDeviceServices(HdcConnection connection) => _connection = connection;
    internal HdcDeviceServices(HdcServerClient server, string connectKey)
    {
        _server = server;
        _connectKey = connectKey;
    }
    /// <summary>Quotes one device shell argument. 转义单个设备 Shell 参数。</summary>
    public static string QuoteShellArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("Argument contains NUL.", nameof(value));
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
    /// <summary>Executes a shell command. 执行 Shell 命令。</summary>
    public string RunCommand(string command, int timeoutMs = 30000)
    {
        var shell = _server is null ? new HdcShellClient(_connection!, command)
            : new HdcShellClient(_server, _connectKey!, command);
        return Encoding.UTF8.GetString(shell.Execute(timeoutMs).Stdout);
    }
    /// <summary>Reads an OpenHarmony system parameter. 读取 OpenHarmony 系统参数。</summary>
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
    public static string RebootArgument(string? mode) => mode switch
    {
        null or "" or "system" => "",
        "bootloader" => "-bootloader",
        "recovery" => "-recovery",
        _ => throw new NotSupportedException($"Unsupported HDC reboot mode: {mode}")
    };
    /// <summary>Maps supported UI reboot modes to the device-shell reboot argument.
    /// <para>将界面支持的重启模式映射为设备 shell 的 reboot 参数。</para></summary>
    public static string RebootShellArgument(string? mode) => mode switch
    {
        null or "" or "system" => "reboot",
        "bootloader" => "reboot bootloader",
        "recovery" => "reboot recovery",
        _ => throw new NotSupportedException($"Unsupported HDC reboot mode: {mode}")
    };

    /// <summary>Reboots to system, bootloader or recovery. 重启至系统、bootloader 或 recovery。</summary>
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
            // The server text router carries device commands as "shell <command>".
            ExecuteService("shell " + RebootShellArgument(mode), HdcCommand.UnityReboot, "", 10000);
            return;
        }

        var argument = RebootArgument(mode);
        ExecuteService("target boot" + (argument.Length == 0 ? "" : " " + argument), HdcCommand.UnityReboot, argument, 10000);
    }
    /// <summary>Remounts system/vendor when permitted by the device. 在设备允许时重新挂载 system/vendor。</summary>
    public void Remount() => ExecuteService("target mount", HdcCommand.UnityRemount, "", 10000);

    /// <summary>Lists processes exposed by the device's debug service.</summary>
    public string ListDebugProcesses() => ListDebugProcesses(10000);

    /// <summary>Lists debug processes within an overall response deadline in milliseconds.</summary>
    public string ListDebugProcesses(int timeoutMs) => ExecuteService("jpid", HdcCommand.JdwpList, "", timeoutMs);

    /// <summary>Opens a native interactive shell; callers own its lifetime and terminal input.</summary>
    public HdcStream OpenInteractiveShell() => (_connection ?? throw new NotSupportedException("Interactive byte streams require a direct connection."))
        .OpenChannel(HdcCommand.ShellInit, []);

    /// <summary>Opens a native debug-process tracker with the upstream default, package or all-process mode.</summary>
    public HdcStream TrackDebugProcesses(string mode = "")
    {
        if (mode is not ("" or "p" or "a")) throw new ArgumentException("Invalid HDC process tracking mode.", nameof(mode));
        return (_connection ?? throw new NotSupportedException("Native process tracking requires a direct connection."))
            .OpenChannel(HdcCommand.JdwpTrack, Encoding.UTF8.GetBytes(mode));
    }

    /// <summary>Streams a device diagnostic report to a caller-owned destination without whole-report buffering.</summary>
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

    /// <summary>Requests root or normal daemon startup, subject to device authorization and build policy.</summary>
    public void SetRootMode(bool enabled) => ExecuteService(enabled ? "smode" : "smode -r", HdcCommand.UnityRootRun, enabled ? "" : "r", 10000);

    /// <summary>Switches the daemon to USB or TCP mode using upstream tmode arguments.</summary>
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
    /// <summary>Lists server targets. 列出服务端目标。</summary>
    public string ListTargets(int timeoutMs = 10000) => _server is null
        ? throw new NotSupportedException("Target enumeration is a host server command.")
        : string.Join('\n', _server.ListTargets(timeoutMs));
    /// <summary>Checks server availability. 检查服务端可用性。</summary>
    public string CheckServer()
    {
        if (_server is null) throw new NotSupportedException("Server checks require a host channel.");
        _server.Connect();
        return "OHOS HDC";
    }
    /// <summary>Waits for a selected target with a bounded deadline. 在有限时限内等待选定目标。</summary>
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