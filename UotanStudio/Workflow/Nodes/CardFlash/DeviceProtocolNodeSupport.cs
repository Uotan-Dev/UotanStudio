using System.Diagnostics;
using System.Globalization;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Shared input validation, progress adaptation and device polling for the protocol nodes in this folder.
/// <para>本目录协议节点共用的输入校验、进度适配与设备轮询。</para>
/// </summary>
internal static class DeviceProtocolNodeSupport
{
    /// <summary>
    /// Reads the Device input and requires the exact protocol; missing or mismatched devices fail before any
    /// session is opened.
    /// <para>读取 Device 输入并要求精确协议；设备缺失或协议不符时在打开任何会话之前失败。</para>
    /// </summary>
    internal static DetectedDevice RequireDevice(FlowNodeViewModel node, INodeExecutionContext context,
        DeviceProtocolKind protocol, string protocolName)
    {
        var device = NodeDataUtil.ToDevice(node.ReadInput("Device"));
        if (device is null)
        {
            WriteFailure(node, "no-device");
            context.Log.Write(LogLevel.Warn, node.Title, "No device connected. 未连接设备。");
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }

        if (device.Protocol != protocol)
        {
            WriteFailure(node, "wrong-protocol");
            var message = $"The node requires a {protocolName} device but received {device.Protocol}. " +
                $"该节点需要 {protocolName} 设备，实际协议为 {device.Protocol}。";
            context.Log.Write(LogLevel.Warn, node.Title, message);
            throw new InvalidOperationException(message);
        }

        return device;
    }

    /// <summary>
    /// Reads a required text input and fails with <paramref name="statusCode"/> when it is empty.
    /// <para>读取必填文本输入，为空时以 <paramref name="statusCode"/> 失败。</para>
    /// </summary>
    internal static string RequireText(FlowNodeViewModel node, string port, string statusCode)
    {
        var value = Text(node.ReadInput(port));
        if (string.IsNullOrWhiteSpace(value))
        {
            node.WriteOutput("Status", statusCode);
            throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Device_InvalidInput"));
        }
        return value;
    }

    /// <summary>
    /// Reads a required local file path and expands it; a missing file fails before any device access.
    /// <para>读取必填本地文件路径并展开；文件缺失时在访问设备之前失败。</para>
    /// </summary>
    internal static string RequireFile(FlowNodeViewModel node, string port, string statusCode)
    {
        var path = RequireText(node, port, statusCode);
        if (!File.Exists(path))
        {
            node.WriteOutput("Status", statusCode);
            throw new InvalidOperationException($"The file does not exist: {path}  文件不存在：{path}。");
        }
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Reads an optional local file path; an empty input yields <see langword="null"/> and a missing file fails.
    /// <para>读取可选本地文件路径；输入为空返回 <see langword="null"/>，文件缺失时失败。</para>
    /// </summary>
    internal static string? OptionalFile(FlowNodeViewModel node, string port, string statusCode)
    {
        var path = Text(node.ReadInput(port));
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (!File.Exists(path))
        {
            node.WriteOutput("Status", statusCode);
            throw new InvalidOperationException($"The file does not exist: {path}  文件不存在：{path}。");
        }
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Reads a required output path and expands it; the directory is not created here.
    /// <para>读取必填输出路径并展开；此处不创建目录。</para>
    /// </summary>
    internal static string RequireOutputPath(FlowNodeViewModel node, string port, string statusCode)
    {
        var path = RequireText(node, port, statusCode);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Reads the optional LUN input; empty or <c>-1</c> means automatic, a negative or malformed value fails.
    /// <para>读取可选 LUN 输入；为空或 <c>-1</c> 表示自动，负数或格式错误时失败。</para>
    /// </summary>
    internal static int? OptionalLun(FlowNodeViewModel node)
    {
        var text = Text(node.ReadInput("LUN"));
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "-1", StringComparison.Ordinal))
        {
            return null;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lun) || lun < 0)
        {
            node.WriteOutput("Status", "invalid-lun");
            throw new InvalidOperationException($"The LUN must be a non-negative integer: {text}  LUN 必须是非负整数。");
        }
        return lun;
    }

    /// <summary>Adapts flash progress percentages to the node progress fraction.<para>把刷写进度百分比适配为节点进度占比。</para></summary>
    internal static Progress<FlashProgress> CreateFlashProgress(INodeExecutionContext context) =>
        new(update => context.ReportProgress(Math.Clamp(update.Percent / 100.0, 0.0, 1.0)));

    /// <summary>Adapts backup progress percentages to the node progress fraction.<para>把备份进度百分比适配为节点进度占比。</para></summary>
    internal static Progress<BackupProgress> CreateBackupProgress(INodeExecutionContext context) =>
        new(update => context.ReportProgress(Math.Clamp(update.Percent / 100.0, 0.0, 1.0)));

    /// <summary>
    /// Writes a failure result and fills the status only when the node has not already reported a specific one.
    /// <para>写入失败结果，仅在节点尚未上报具体状态时补充状态。</para>
    /// </summary>
    internal static void WriteFailure(FlowNodeViewModel node, string status)
    {
        node.WriteOutput("Result", false);
        var port = node.Output.FirstOrDefault(static candidate => string.Equals(candidate.Title, "Status", StringComparison.Ordinal));
        if (port is not null && port.Data is null)
        {
            port.Data = status;
        }
    }

    /// <summary>
    /// Polls the device monitor until a matching endpoint appears; a timeout or invalid wait window fails.
    /// <para>轮询设备监视服务直到出现匹配端点；超时或等待参数无效时失败。</para>
    /// </summary>
    internal static async Task<(DetectedDevice Device, TimeSpan Elapsed)> WaitForDeviceAsync(
        FlowNodeViewModel node, INodeExecutionContext context, DeviceProtocolKind? protocol, string? serial,
        double timeoutSeconds, double intervalSeconds)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0)
        {
            node.WriteOutput("Status", "invalid-timeout");
            throw new InvalidOperationException("The timeout must be a positive number of seconds. 超时时间必须为正数秒。");
        }
        if (!double.IsFinite(intervalSeconds) || intervalSeconds < 0.05)
        {
            node.WriteOutput("Status", "invalid-interval");
            throw new InvalidOperationException("The polling interval must be at least 0.05 seconds. 轮询间隔至少为 0.05 秒。");
        }

        var timeout = TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 86_400));
        var interval = TimeSpan.FromSeconds(Math.Min(intervalSeconds, 60));
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DetectedDevice> devices;
            try
            {
                devices = await Task.Run(static () => App.DeviceMonitor.Scan(), context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Log.Write(LogLevel.Warn, node.Title,
                    $"Device scan failed: {ex.Message}  设备扫描失败：{ex.Message}。");
                devices = [];
            }

            var match = devices.FirstOrDefault(candidate =>
                (protocol is null || candidate.Protocol == protocol)
                && (string.IsNullOrWhiteSpace(serial)
                    || string.Equals(candidate.Serial, serial, StringComparison.Ordinal)));
            if (match is not null)
            {
                return (match, Stopwatch.GetElapsedTime(started));
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            context.ReportProgress(Math.Clamp(elapsed / timeout, 0.0, 0.99));
            if (elapsed >= timeout)
            {
                node.WriteOutput("Status", "timeout");
                throw new InvalidOperationException(
                    $"No matching device appeared within {timeout.TotalSeconds:0.###} seconds. 等待设备超时。");
            }

            var remaining = timeout - elapsed;
            await Task.Delay(remaining < interval ? remaining : interval, context.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Converts a port value to invariant text.<para>把端口值转换为固定区域文本。</para></summary>
    internal static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}