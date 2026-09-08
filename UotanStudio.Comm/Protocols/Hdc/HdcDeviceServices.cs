using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>HDC device commands, using native OpenHarmony semantics.
/// <para>使用 OpenHarmony 原生命令语义的 HDC 设备服务。</para></summary>
public sealed class HdcDeviceServices
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
        return RunCommand("param get " + QuoteShellArgument(key)).Trim();
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
    /// <summary>Reboots to system, bootloader or recovery. 重启至系统、bootloader 或 recovery。</summary>
    public void Reboot(string? mode = null)
    {
        var argument = RebootArgument(mode);
        ExecuteService("target boot" + (argument.Length == 0 ? "" : " " + argument), HdcCommand.UnityReboot, argument, 10000);
    }
    /// <summary>Remounts system/vendor when permitted by the device. 在设备允许时重新挂载 system/vendor。</summary>
    public void Remount() => ExecuteService("target mount", HdcCommand.UnityRemount, "", 10000);
    private string ExecuteService(string command, HdcCommand nativeCommand, string argument, int timeoutMs)
    {
        if (_server is not null)
        {
            var response = Encoding.UTF8.GetString(_server.ExecuteOnDevice(_connectKey!, command, timeoutMs));
            HdcServerClient.ThrowOnFailure(response);
            return response;
        }
        using var channel = _connection!.OpenChannel(nativeCommand, Encoding.UTF8.GetBytes(argument));
        using var output = new MemoryStream();
        while (channel.Read(timeoutMs) is { } bytes)
        {
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
