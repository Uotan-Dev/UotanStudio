using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Devices;

/// <summary>OpenHarmony device operations shared by UI and workflows.
/// <para>界面与工作流共用的 OpenHarmony 设备操作。</para></summary>
public sealed class HdcDeviceService(string executable = "hdc", string host = "127.0.0.1", int? port = null)
{
    private readonly int _port = port ?? HdcServerClient.DefaultServerPort;
    /// <summary>Creates a service using the user's configured HDC path. 使用用户配置路径创建服务。</summary>
    public static HdcDeviceService FromSettings() => new(ResolveExecutable());

    /// <summary>Resolves configured HDC, PATH or the standard DevEco installation.
    /// <para>解析配置路径、PATH 或 DevEco 标准安装位置。</para></summary>
    public static string ResolveExecutable()
    {
        var configured = SettingsPersistenceService.Instance.LoadSettings().HdcPath;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var name = OperatingSystem.IsWindows() ? "hdc.exe" : "hdc";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, name)))
                return Path.Combine(directory, name);
        }
        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Huawei", "DevEco Studio", "sdk", "default", "openharmony", "toolchains", name);
            if (File.Exists(bundled)) return bundled;
        }
        return name;
    }

    private static void ValidateDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc || string.IsNullOrWhiteSpace(device.Serial))
            throw new ArgumentException("An HDC target with a connect key is required.", nameof(device));
    }

    /// <summary>Executes a shell command and verifies its remote exit status.
    /// <para>执行 Shell 命令并验证远端退出码。</para></summary>
    public async Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken ct = default)
    {
        ValidateDevice(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var marker = "__UOTAN_HDC_" + Guid.NewGuid().ToString("N") + "__";
        var wrapped = "shell sh -c " + HdcDeviceServices.QuoteShellArgument(command)
            + "; printf '\\n" + marker + "%s\\n' \"$?\"";
        using var client = new HdcServerClient(host, _port);
        var text = Encoding.UTF8.GetString(await client.ExecuteAsync(device.Serial, wrapped, cancellationToken: ct).ConfigureAwait(false));
        HdcServerClient.ThrowOnFailure(text);
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || !int.TryParse(text.AsSpan(index + marker.Length).Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var status))
            throw new IOException("HDC shell ended without a remote exit status.");
        var output = text[..index].TrimEnd('\r', '\n');
        if (status != 0) throw new IOException($"HDC shell exited with code {status}: {output}");
        return output;
    }

    /// <summary>Reads a system parameter. 读取系统参数。</summary>
    public Task<string> GetPropertyAsync(DetectedDevice device, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ShellAsync(device, "param get " + HdcDeviceServices.QuoteShellArgument(name), ct);
    }

    /// <summary>Executes an official task with the exact selected target. 对固定目标执行官方任务。</summary>
    public Task<string> RunTaskAsync(DetectedDevice device, IEnumerable<string> arguments, CancellationToken ct = default)
    {
        ValidateDevice(device);
        return HdcCommandClient.RunAsync(executable, ["-s", $"{host}:{_port}", "-t", device.Serial, .. arguments], ct);
    }

    /// <summary>Sends a local file using official streaming transfer. 经官方流式传输发送文件。</summary>
    public Task<string> SendFileAsync(DetectedDevice device, string localPath, string remotePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var path = Path.GetFullPath(localPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Local file not found.", path);
        return RunTaskAsync(device, ["file", "send", path, remotePath], ct);
    }

    /// <summary>Receives to a temporary local file and atomically publishes on success.
    /// <para>接收到本地临时文件，成功后原子发布。</para></summary>
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
            var result = await RunTaskAsync(device, ["file", "recv", remotePath, temporary], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temporary)) throw new IOException("HDC did not produce a regular file.");
            File.Move(temporary, path, overwrite: true);
            return result;
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>Installs a HAP/HSP/APP package. 安装 HAP/HSP/APP 包。</summary>
    public async Task<string> InstallAsync(DetectedDevice device, string localPath, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(localPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Package not found.", path);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".hap" or ".hsp" or ".app"))
            throw new ArgumentException("HDC installation requires a HAP, HSP or APP package.", nameof(localPath));
        var result = await RunTaskAsync(device, ["install", path], ct).ConfigureAwait(false);
        RequireApplicationSuccess(result, uninstall: false);
        return result;
    }

    /// <summary>Uninstalls a bundle by name. 按包名卸载应用。</summary>
    public async Task<string> UninstallAsync(DetectedDevice device, string bundle, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
        var result = await RunTaskAsync(device, ["uninstall", bundle], ct).ConfigureAwait(false);
        RequireApplicationSuccess(result, uninstall: true);
        return result;
    }

    /// <summary>Checks the bundle-manager result carried in an HDC Info message.
    /// <para>检查 HDC Info 消息承载的包管理器结果；Info 不代表安装成功。</para></summary>
    public static void RequireApplicationSuccess(string output, bool uninstall)
    {
        // Upstream test_hdc_return_value_check.py checks these exact markers. A failed
        // uninstall is also emitted as [Info]App uninstall ... msg:error: ... .
        var marker = uninstall ? "msg:uninstall bundle successfully." : "msg:install bundle successfully.";
        if (!output.Contains(marker, StringComparison.Ordinal) || output.Contains("msg:error:", StringComparison.Ordinal))
            throw new IOException("HDC application operation did not report success: " + output.Trim());
    }

    /// <summary>Creates a forward/reverse TCP mapping. 创建正向或反向 TCP 映射。</summary>
    public Task<string> ForwardAsync(DetectedDevice device, int localPort, int remotePort, bool reverse = false, CancellationToken ct = default)
    {
        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(localPort), "TCP ports must be between 1 and 65535.");
        return RunTaskAsync(device, reverse ? ["rport", $"tcp:{remotePort}", $"tcp:{localPort}"]
            : ["fport", $"tcp:{localPort}", $"tcp:{remotePort}"], ct);
    }

    /// <summary>Disconnects a network target; USB transport is not an ADB connection.
    /// <para>断开网络目标；USB 目标不走 ADB 断开路径。</para></summary>
    public Task<string> DisconnectAsync(DetectedDevice device, CancellationToken ct = default)
    {
        if (!device.Serial.Contains(':')) throw new NotSupportedException("USB HDC targets disconnect when unplugged.");
        return RunTaskAsync(device, ["tconn", device.Serial, "-remove"], ct);
    }
}
