using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

/// <summary>
/// <para>ADB/Fastboot 设备信息。</para>
/// ADB/Fastboot device information.
/// </summary>
public sealed class AdbDevice
{
    /// <summary>设备序列号。</summary>
    public string Serial { get; init; } = string.Empty;
    /// <summary>设备状态（device/offline/unauthorized/fastboot 等）。</summary>
    public string State { get; init; } = string.Empty;
    /// <summary>连接模式（ADB/Fastboot）。</summary>
    public string Mode { get; init; } = "ADB";
}

/// <summary>
/// <para>ADB/Fastboot 命令执行结果。</para>
/// ADB/Fastboot command execution result.
/// </summary>
public sealed class AdbCommandResult
{
    /// <summary>是否成功（ExitCode == 0）。</summary>
    public bool Success => ExitCode == 0;
    /// <summary>退出码。</summary>
    public int ExitCode { get; init; }
    /// <summary>标准输出。</summary>
    public string Output { get; init; } = string.Empty;
    /// <summary>标准错误。</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// <para>ADB 与 Fastboot 命令封装服务，负责设备枚举、设备命令与进程输出捕获。</para>
/// ADB and Fastboot command service, responsible for device enumeration, device commands, and process output capture.
/// </summary>
public sealed class AdbService
{
    private static readonly Lazy<AdbService> LazyInstance = new(() => new AdbService());
    /// <summary>获取 AdbService 单例。</summary>
    public static AdbService Instance => LazyInstance.Value;

    private string _adbPath = string.Empty;
    private string _fastbootPath = string.Empty;
    private int _timeoutSeconds = 30;

    private AdbService() { }

    /// <summary>
    /// <para>使用应用设置更新 ADB/Fastboot 路径与超时。</para>
    /// Updates ADB/Fastboot paths and timeout from application settings.
    /// </summary>
    public void UpdateConfiguration(string adbPath, string fastbootPath, int timeoutSeconds)
    {
        _adbPath = adbPath ?? string.Empty;
        _fastbootPath = fastbootPath ?? string.Empty;
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);
    }

    /// <summary>
    /// <para>异步枚举当前连接的 ADB 与 Fastboot 设备。</para>
    /// Asynchronously enumerates currently connected ADB and Fastboot devices.
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>检测到的设备列表。</returns>
    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<AdbDevice>();

        AdbCommandResult adbResult = await RunAsync(ResolveAdb(), "devices", cancellationToken);
        if (adbResult.Success)
        {
            foreach (var line in adbResult.Output.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("List of devices")) continue;
                var parts = trimmed.Split('\t', ' ');
                if (parts.Length >= 2)
                    devices.Add(new AdbDevice { Serial = parts[0], State = parts[1], Mode = "ADB" });
            }
        }

        AdbCommandResult fbResult = await RunAsync(ResolveFastboot(), "devices", cancellationToken);
        if (fbResult.Success)
        {
            foreach (var line in fbResult.Output.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var parts = trimmed.Split('\t', ' ');
                if (parts.Length >= 2)
                    devices.Add(new AdbDevice { Serial = parts[0], State = "fastboot", Mode = "Fastboot" });
            }
        }

        return devices;
    }

    /// <summary>
    /// <para>异步执行针对指定设备的 ADB 命令。</para>
    /// Asynchronously executes an ADB command against the specified device.
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="arguments">ADB 参数（不含 -s）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令执行结果。</returns>
    public Task<AdbCommandResult> ExecuteAdbAsync(string serial, string arguments, CancellationToken cancellationToken = default)
        => RunAsync(ResolveAdb(), $"-s {serial} {arguments}", cancellationToken);

    /// <summary>
    /// <para>异步执行针对指定设备的 Fastboot 命令。</para>
    /// Asynchronously executes a Fastboot command against the specified device.
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="arguments">Fastboot 参数（不含 -s）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令执行结果。</returns>
    public Task<AdbCommandResult> ExecuteFastbootAsync(string serial, string arguments, CancellationToken cancellationToken = default)
        => RunAsync(ResolveFastboot(), $"-s {serial} {arguments}", cancellationToken);

    /// <summary>
    /// <para>异步重启设备到指定模式。</para>
    /// Asynchronously reboots the device to the specified mode.
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mode">重启模式（system/bootloader/recovery/edl 等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<AdbCommandResult> RebootAsync(string serial, string mode, CancellationToken cancellationToken = default)
    {
        var args = mode switch
        {
            "bootloader" or "fastboot" => "reboot bootloader",
            "recovery" => "reboot recovery",
            "edl" => "reboot edl",
            _ => "reboot"
        };
        return await ExecuteAdbAsync(serial, args, cancellationToken);
    }

    /// <summary>
    /// <para>异步获取设备属性（getprop）。</para>
    /// Asynchronously retrieves device properties (getprop).
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>属性键值对字典。</returns>
    public async Task<IReadOnlyDictionary<string, string>> GetPropertiesAsync(string serial, CancellationToken cancellationToken = default)
    {
        AdbCommandResult result = await ExecuteAdbAsync(serial, "shell getprop", cancellationToken);
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!result.Success) return props;
        foreach (var line in result.Output.Split('\n', '\r'))
        {
            var trimmed = line.Trim('[', ']', ' ', '\t');
            var idx = trimmed.IndexOf("]: [", StringComparison.Ordinal);
            if (idx <= 0) continue;
            var key = trimmed.Substring(0, idx).TrimStart('[');
            var valueStart = idx + 4;
            var valueEnd = trimmed.LastIndexOf(']');
            if (valueEnd <= valueStart) continue;
            var value = trimmed.Substring(valueStart, valueEnd - valueStart);
            props[key] = value;
        }
        return props;
    }

    /// <summary>
    /// <para>异步启动 ADB logcat 流式捕获，逐行回调。</para>
    /// Asynchronously starts ADB logcat streaming capture, invoking the callback per line.
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="onLine">每行日志的回调（在后台线程触发，调用方需自行处理 UI 线程调度）。</param>
    /// <param name="cancellationToken">取消令牌；取消时会终止 logcat 进程。</param>
    /// <returns>任务在 logcat 进程退出或被取消时完成。</returns>
    public async Task StartLogcatAsync(string serial, Action<string> onLine, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveAdb(),
            Arguments = $"-s {serial} logcat",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();

            using StreamReader reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (cancellationToken.IsCancellationRequested) break;
                onLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            onLine($"[ADB ERROR] {ex.Message}");
        }
        finally
        {
            try
            {
                if (process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* 忽略 kill 异常 */ }
            process?.Dispose();
        }
    }

    private string ResolveAdb() => ResolveBinary(_adbPath, "adb");
    private string ResolveFastboot() => ResolveBinary(_fastbootPath, "fastboot");

    private static string ResolveBinary(string configuredPath, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        // 退回到 PATH 中的裸名（Windows 下 ProcessStartInfo 会自动追加 .exe）
        return fallbackName;
    }

    private async Task<AdbCommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new AdbCommandResult { ExitCode = -1, Error = "Command timed out." };
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new AdbCommandResult { ExitCode = process.ExitCode, Output = stdout, Error = stderr };
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            return new AdbCommandResult { ExitCode = -1, Error = ex.Message };
        }
    }
}
