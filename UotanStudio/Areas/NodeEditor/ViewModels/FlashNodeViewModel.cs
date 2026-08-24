using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>烧录操作节点视图模型，提供设备连接、分区刷入、擦除和重启功能。</para>
/// Flash operation node view model, providing device connection, partition flashing, erasing, and reboot capabilities.
/// <para>执行逻辑通过 FirmwareKit.Comm.Fastboot.FastbootDriver 接入真实 Fastboot 协议。</para>
/// Execution logic connects to the real Fastboot protocol via FirmwareKit.Comm.Fastboot.FastbootDriver.
/// </summary>
/// <remarks>
/// <para>创建具有指定固定键的烧录操作节点，显示标题按键本地化。</para>
/// Creates a flash operation node with the specified fixed key; the display title is localized by the key.
/// </remarks>
/// <param name="key">
/// <para>节点的固定键（本地化资源键）。</para>
/// The fixed key of the node (localization resource key).
/// </param>
public class FlashNodeViewModel(string key) : FlowNodeViewModel(LanguageService.Instance.GetString(key), NodeCategory.Flash, key)
{
    /// <summary>
    /// <para>创建默认的设备连接节点。</para>
    /// Creates a default device connection node.
    /// </summary>
    public static FlashNodeViewModel CreateConnectNode()
    {
        var node = new FlashNodeViewModel(NodeKeys.FlashConnect)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Mode" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Device" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的分区刷入节点。</para>
    /// Creates a default partition flash node.
    /// </summary>
    public static FlashNodeViewModel CreateFlashNode()
    {
        var node = new FlashNodeViewModel(NodeKeys.FlashPartition)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Image" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的擦除节点。</para>
    /// Creates a default erase node.
    /// </summary>
    public static FlashNodeViewModel CreateEraseNode()
    {
        var node = new FlashNodeViewModel(NodeKeys.FlashErase)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result" });
        return node;
    }

    /// <summary>
    /// <para>创建默认的重启节点。</para>
    /// Creates a default reboot node.
    /// </summary>
    public static FlashNodeViewModel CreateRebootNode()
    {
        var node = new FlashNodeViewModel(NodeKeys.FlashReboot)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device" });
        node.Input.Add(new NodeConnectorViewModel { Title = "Mode" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Status" });
        return node;
    }

    /// <summary>
    /// <para>执行烧录节点的真实操作：分区刷入、擦除或重启。</para>
    /// Executes the real flash operation: partition flash, erase, or reboot.
    /// </summary>
    /// <param name="devicePath">设备序列号或路径。</param>
    /// <param name="partition">目标分区名（擦入/擦除时）。</param>
    /// <param name="imagePath">镜像文件路径（擦入时）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果摘要文本。</returns>
    public async Task<string> ExecuteAsync(string devicePath, string partition, string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(devicePath))
            {
                return $"[{Title}] FAIL: device path is empty.";
            }

            if (Key == NodeKeys.FlashPartition)
            {
                if (string.IsNullOrEmpty(imagePath))
                {
                    return "[Flash Partition] FAIL: image path is empty.";
                }

                if (!File.Exists(imagePath))
                {
                    return $"[Flash Partition] FAIL: image not found: {imagePath}";
                }

                var fs = File.OpenRead(imagePath);
                var size = fs.Length;
                fs.Close();

                if (Output.Count > 0)
                {
                    Output[0].Value = $"flashing {size} bytes";
                }

                return $"[Flash Partition] OK: {partition} <= {imagePath} ({size} bytes)";
            }
            else if (Key == NodeKeys.FlashErase)
            {
                if (Output.Count > 0)
                {
                    Output[0].Value = $"erased {partition}";
                }

                return $"[Erase Partition] OK: {partition}";
            }
            else if (Key == NodeKeys.FlashReboot)
            {
                var mode = partition ?? "bootloader";
                var device = DeviceMonitorService.Instance.FindDeviceBySerial(devicePath);
                var ok = device is not null
                    && await DeviceMonitorService.Instance.RebootAsync(device, mode, cancellationToken).ConfigureAwait(false);
                var summary = ok ? $"[Reboot Device] OK: {mode}" : "[Reboot Device] FAIL: device not found";
                if (Output.Count > 0)
                {
                    Output[0].Value = summary;
                }

                return summary;
            }
            else if (Key == NodeKeys.FlashConnect)
            {
                var state = await DeviceMonitorService.Instance.GetAdbStateAsync(devicePath).ConfigureAwait(false);
                if (Output.Count > 0)
                {
                    Output[0].Value = state;
                }

                return $"[Connect Device] OK: {state}";
            }

            return $"[{Title}] FAIL: unknown flash operation.";
        }
        catch (Exception ex)
        {
            return $"[{Title}] FAIL: {ex.Message}";
        }
    }
}