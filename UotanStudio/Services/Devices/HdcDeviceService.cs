using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Devices;

/// <summary>OpenHarmony device operations routed through an HDC host channel.
/// <para>经由 HDC 主机通道转发的 OpenHarmony 设备操作。</para></summary>
/// <param name="host">HDC server host address.<para>HDC 服务器主机地址。</para></param>
/// <param name="port">HDC server port, or null for the default.<para>HDC 服务器端口，为空时使用默认值。</para></param>
internal sealed class HdcDeviceService(string host = "127.0.0.1", int? port = null)
{
    private readonly int _port = port ?? HdcServerClient.DefaultServerPort;
    /// <summary>Creates a service for the configured HDC host channel.<para>为已配置的 HDC 主机通道创建服务实例。</para></summary>
    /// <returns>A new service instance.<para>新的服务实例。</para></returns>
    public static HdcDeviceService FromSettings() => new();

    private static void ValidateDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc || string.IsNullOrWhiteSpace(device.Serial))
            throw new ArgumentException("An HDC target with a connect key is required.", nameof(device));
    }

    /// <summary>Executes a shell command and verifies its remote exit status.
    /// <para>执行 Shell 命令并验证远端退出码。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="command">Shell command text.<para>Shell 命令文本。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The command output.<para>命令输出。</para></returns>
    public async Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken ct = default)
    {
        ValidateDevice(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var marker = "__UOTAN_HDC_" + Guid.NewGuid().ToString("N") + "__";
        var wrapped = "shell sh -c " + HdcDeviceServices.QuoteShellArgument(command)
            + "; printf '\\n" + marker + "%s\\n' \"$?\"";
        string text;
        if (IsDirect(device))
        {
            text = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var opened = HdcDirectConnection.Open(device);
                using var lease = opened.Lease;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var output = new MemoryStream();
                opened.Session.CreateShellClient(wrapped[6..]).ExecuteStreaming(bytes =>
                {
                    if (output.Length + bytes.Length > HdcServerClient.MaximumOutputBytes)
                        throw new InvalidDataException("HDC shell output limit exceeded.");
                    output.Write(bytes);
                }, null, timeout.Token);
                return Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            }, ct).ConfigureAwait(false);
        }
        else
        {
            using var client = new HdcServerClient(host, _port);
            text = Encoding.UTF8.GetString(await client.ExecuteAsync(device.Serial, wrapped, cancellationToken: ct).ConfigureAwait(false));
        }
        HdcServerClient.ThrowOnFailure(text);
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || !int.TryParse(text.AsSpan(index + marker.Length).Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var status))
            throw new IOException("HDC shell ended without a remote exit status.");
        var output = text[..index].TrimEnd('\r', '\n');
        if (status != 0) throw new IOException($"HDC shell exited with code {status}: {output}");
        return output;
    }

    /// <summary>Reads a system parameter.<para>读取系统参数。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="name">Parameter name.<para>参数名称。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The parameter value.<para>参数值。</para></returns>
    public Task<string> GetPropertyAsync(DetectedDevice device, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ShellAsync(device, "param get " + HdcDeviceServices.QuoteShellArgument(name), ct);
    }

    private bool IsDirect(DetectedDevice device)
    {
        ValidateDevice(device);
        return !DeviceMonitorService.UseHdcServerFor(device, host, _port);
    }

    private static Task<string> DirectTaskAsync(DetectedDevice device, Func<HdcSession, string> operation, CancellationToken ct)
        => Task.Run(() =>
        {
            ValidateDevice(device);
            ct.ThrowIfCancellationRequested();
            var opened = HdcDirectConnection.Open(device);
            using var lease = opened.Lease;
            return operation(opened.Session);
        }, ct);

    /// <summary>Executes an HDC task with the exact selected target.<para>对精确选定的目标执行 HDC 任务。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="arguments">Single-token command arguments.<para>逐词命令参数。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The task response text.<para>任务响应文本。</para></returns>
    public async Task<string> RunTaskAsync(DetectedDevice device, IEnumerable<string> arguments, CancellationToken ct = default)
    {
        ValidateDevice(device);
        if (!DeviceMonitorService.UseHdcServerFor(device, host, _port))
            throw new NotSupportedException("This operation requires an official HDC server target; it cannot take over an active direct USB session.");
        var parts = arguments.ToArray();
        if (parts.Length == 0 || parts.Any(part => string.IsNullOrWhiteSpace(part) ||
            part.Any(c => char.IsWhiteSpace(c) || c == '\0')))
            throw new ArgumentException("HDC host command arguments must be nonempty single tokens.", nameof(arguments));
        using var server = new HdcServerClient(host, _port);
        var text = Encoding.UTF8.GetString(await server.ExecuteAsync(device.Serial,
            string.Join(' ', parts), cancellationToken: ct).ConfigureAwait(false)).TrimEnd('\0');
        HdcServerClient.ThrowOnFailure(text);
        return text;
    }

    private async Task<string> RunFileTaskAsync(DetectedDevice device, IEnumerable<string> arguments, CancellationToken ct)
    {
        ValidateDevice(device);
        if (!DeviceMonitorService.UseHdcServerFor(device, host, _port))
            throw new NotSupportedException("HDC server target is unavailable.");
        var parts = arguments.ToArray();
        if (parts.Length == 0 || parts[0] is not ("file" or "install"))
            throw new ArgumentException("A host file or install task is required.", nameof(arguments));
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) ||
            part.Any(c => c is '"' or '\0' || char.IsControl(c))))
            throw new ArgumentException("Invalid HDC file task argument.", nameof(arguments));
        var command = string.Join(' ', parts.Select(part =>
            part.Any(char.IsWhiteSpace) ? $"\"{part}\"" : part));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var server = new HdcServerClient(host, _port);
        using var output = new MemoryStream();
        try
        {
            await server.ExecuteStreamingAsync(device.Serial, command, bytes =>
            {
                if (output.Length + bytes.Length > HdcServerClient.MaximumOutputBytes)
                    throw new InvalidDataException("HDC file task output limit exceeded.");
                output.Write(bytes);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("HDC file task exceeded 300000 ms.");
        }
        var result = Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length))).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(result))
            throw new IOException("HDC file task ended without a completion response.");
        HdcServerClient.ThrowOnFailure(result);
        return result;
    }

    /// <summary>Sends a local file or directory through the selected HDC connection.<para>通过选定 HDC 连接发送本地文件或目录。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="localPath">Local file or directory path.<para>本地文件或目录路径。</para></param>
    /// <param name="remotePath">Remote destination path.<para>远端目标路径。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The transfer response text.<para>传输响应文本。</para></returns>
    public Task<string> SendFileAsync(DetectedDevice device, string localPath, string remotePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var path = Path.GetFullPath(localPath);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Local file or directory not found.", path);
        if (IsDirect(device)) return DirectTaskAsync(device, session =>
        {
            using var transfer = new HdcSyncClient(session.Connection);
            transfer.Send(path, remotePath, cancellationToken: ct);
            return "File sent successfully.";
        }, ct);
        return RunFileTaskAsync(device, ["file", "send", path, remotePath], ct);
    }

    /// <summary>Receives to a temporary local file and atomically publishes on success.
    /// <para>接收到本地临时文件，成功后原子发布。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="remotePath">Remote source path.<para>远端源路径。</para></param>
    /// <param name="localPath">Local destination path.<para>本地目标路径。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The transfer response text.<para>传输响应文本。</para></returns>
    public async Task<string> ReceiveFileAsync(DetectedDevice device, string remotePath, string localPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var path = Path.GetFullPath(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = Path.Combine(Path.GetDirectoryName(path)!, ".uotan-hdc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var temporary = Path.Combine(staging, "payload");
        try
        {
            var result = IsDirect(device)
                ? await DirectTaskAsync(device, session =>
                {
                    using var transfer = new HdcSyncClient(session.Connection);
                    transfer.Recv(remotePath, temporary, ct);
                    return "File received successfully.";
                }, ct).ConfigureAwait(false)
                : await RunFileTaskAsync(device, ["file", "recv", remotePath, temporary], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temporary)) throw new IOException("HDC did not produce a regular file.");
            File.Move(temporary, path, overwrite: true);
            return result;
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>Installs a HAP/HSP/APP package.<para>安装 HAP/HSP/APP 包。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="localPath">Local package path.<para>本地包路径。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The install response text.<para>安装响应文本。</para></returns>
    public async Task<string> InstallAsync(DetectedDevice device, string localPath, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(localPath);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Package not found.", path);
        if (!Directory.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is not (".hap" or ".hsp" or ".app"))
            throw new ArgumentException("HDC installation requires a HAP, HSP or APP package.", nameof(localPath));
        if (IsDirect(device)) return await DirectTaskAsync(device,
            session => new HdcAppClient(session.Connection).Install(path, ct), ct).ConfigureAwait(false);
        var result = await RunFileTaskAsync(device, ["install", path], ct).ConfigureAwait(false);
        RequireApplicationSuccess(result, uninstall: false);
        return result;
    }

    /// <summary>Uninstalls a bundle by name.<para>按包名卸载应用。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="bundle">Bundle name.<para>包名。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The uninstall response text.<para>卸载响应文本。</para></returns>
    public async Task<string> UninstallAsync(DetectedDevice device, string bundle, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
        if (IsDirect(device)) return await DirectTaskAsync(device,
            session => new HdcAppClient(session.Connection).Uninstall(bundle, ct), ct).ConfigureAwait(false);
        var result = await RunTaskAsync(device, ["uninstall", bundle], ct).ConfigureAwait(false);
        RequireApplicationSuccess(result, uninstall: true);
        return result;
    }

    /// <summary>Checks the bundle-manager result carried in an HDC Info message.
    /// <para>检查 HDC Info 消息承载的包管理器结果。</para></summary>
    /// <param name="output">Response text to inspect.<para>待检查的响应文本。</para></param>
    /// <param name="uninstall">Whether the result belongs to an uninstall.<para>结果是否属于卸载操作。</para></param>
    public static void RequireApplicationSuccess(string output, bool uninstall)
    {
        var marker = uninstall ? "msg:uninstall bundle successfully." : "msg:install bundle successfully.";
        if (!output.Contains(marker, StringComparison.Ordinal) || output.Contains("msg:error:", StringComparison.Ordinal))
            throw new IOException("HDC application operation did not report success: " + output.Trim());
    }

    /// <summary>Creates a forward or reverse TCP mapping.<para>创建正向或反向 TCP 映射。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="localPort">Local TCP port.<para>本地 TCP 端口。</para></param>
    /// <param name="remotePort">Remote TCP port.<para>远端 TCP 端口。</para></param>
    /// <param name="reverse">Whether the mapping is reverse.<para>是否为反向映射。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The mapping response text.<para>映射响应文本。</para></returns>
    public Task<string> ForwardAsync(DetectedDevice device, int localPort, int remotePort, bool reverse = false, CancellationToken ct = default)
    {
        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(localPort), "TCP ports must be between 1 and 65535.");
        if (IsDirect(device)) return DirectTaskAsync(device, session =>
        {
            session.Connection.Forwards.Add(localPort, remotePort, reverse, ct);
            return "Forwardport result:OK";
        }, ct);
        return RunTaskAsync(device, reverse ? ["rport", $"tcp:{remotePort}", $"tcp:{localPort}"]
            : ["fport", $"tcp:{localPort}", $"tcp:{remotePort}"], ct);
    }

    /// <summary>Lists the selected device's native or official forward mappings.<para>列出选定设备的原生或官方转发映射。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The forward listing text.<para>转发列表文本。</para></returns>
    public Task<string> ListForwardsAsync(DetectedDevice device, CancellationToken ct = default)
        => IsDirect(device) ? DirectTaskAsync(device, s => s.Connection.Forwards.List(), ct)
            : RunTaskAsync(device, ["fport", "ls"], ct);

    /// <summary>Lists debuggable processes through the selected HDC connection.<para>通过选定 HDC 连接列出可调试进程。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The process listing text.<para>进程列表文本。</para></returns>
    public Task<string> ListDebugProcessesAsync(DetectedDevice device, CancellationToken ct = default)
        => IsDirect(device) ? DirectTaskAsync(device, s => s.CreateDeviceServices().ListDebugProcesses(), ct)
            : RunTaskAsync(device, ["jpid"], ct);

    /// <summary>Removes one TCP mapping owned by the selected device connection.<para>移除选定设备连接持有的一条 TCP 映射。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="localPort">Local TCP port.<para>本地 TCP 端口。</para></param>
    /// <param name="remotePort">Remote TCP port.<para>远端 TCP 端口。</para></param>
    /// <param name="reverse">Whether the mapping is reverse.<para>是否为反向映射。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The removal response text.<para>移除响应文本。</para></returns>
    public Task<string> RemoveForwardAsync(DetectedDevice device, int localPort, int remotePort, bool reverse = false, CancellationToken ct = default)
    {
        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        return IsDirect(device) ? DirectTaskAsync(device, s => s.Connection.Forwards.Remove(reverse ? remotePort : localPort, reverse)
            ? "Forward removed." : "Forward not found.", ct)
            : RunTaskAsync(device, [reverse ? "rport" : "fport", "rm", $"tcp:{(reverse ? remotePort : localPort)}", $"tcp:{(reverse ? localPort : remotePort)}"], ct);
    }

    /// <summary>Requests the device's native remount service.<para>请求设备原生重新挂载服务。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The remount response text.<para>重新挂载响应文本。</para></returns>
    public Task<string> RemountAsync(DetectedDevice device, CancellationToken ct = default)
        => IsDirect(device) ? DirectTaskAsync(device, s => { s.CreateDeviceServices().Remount(); return "Remount completed."; }, ct)
            : RunTaskAsync(device, ["target", "mount"], ct);

    /// <summary>Disconnects a network target; USB transport is not an ADB connection.
    /// <para>断开网络目标；USB 目标不走 ADB 断开路径。</para></summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="ct">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The disconnect response text.<para>断开响应文本。</para></returns>
    public async Task<string> DisconnectAsync(DetectedDevice device, CancellationToken ct = default)
    {
        ValidateDevice(device);
        if (device.Transport != DeviceTransportKind.Tcp)
        {
            throw new NotSupportedException("USB HDC targets disconnect when unplugged.");
        }
        using var server = new HdcServerClient(host, _port);
        return await server.ConnectTargetAsync(device.Serial, disconnect: true, cancellationToken: ct).ConfigureAwait(false);
    }
}