using UotanStudio.Services.Firmware;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>super（逻辑分区）操作节点视图模型，提供 super 镜像解包与合成功能。</para>
/// Super (logical partition) operation node view model, providing super image unpacking and synthesis.
/// </summary>
public class SuperNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的 super 操作节点。</para>
    /// Creates a super operation node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public SuperNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的 super 解包节点。</para>
    /// Creates a default super unpack node.
    /// </summary>
    public static SuperNodeViewModel CreateSuperUnpackNode()
    {
        var node = new SuperNodeViewModel("Super Unpack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.SuperUnpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Super Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 super 合成节点。</para>
    /// Creates a default super pack (synthesis) node.
    /// </summary>
    public static SuperNodeViewModel CreateSuperPackNode()
    {
        var node = new SuperNodeViewModel("Super Pack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.SuperPack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Device Size", PortType = NodePortType.Number });
        node.Input.Add(new NodeConnectorViewModel { Title = "Group Name", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partitions", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的 super 逻辑：Firmware.SuperUnpack 从 super 镜像解出逻辑分区、
    /// Firmware.SuperPack 将分区镜像合成为 super 镜像。</para>
    /// Dispatches super logic by the node type key (TypeKey): Firmware.SuperUnpack extracts logical partitions
    /// from a super image, and Firmware.SuperPack synthesizes a super image from partition images.
    /// </summary>
    /// <param name="context">
    /// <para>执行上下文。</para>
    /// The execution context.
    /// </param>
    /// <returns>
    /// <para>表示异步执行的任务。</para>
    /// A task representing the asynchronous execution.
    /// </returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            switch (TypeKey)
            {
                case "Firmware.SuperUnpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.SuperPack":
                    await ExecutePackAsync(context).ConfigureAwait(false);
                    break;
                default:
                    await base.ExecuteAsync(context).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, $"Super node failed: {ex.Message}  super 节点执行失败。");
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var superPath = PathText(ReadInput("Super Path"));
        if (string.IsNullOrWhiteSpace(superPath) || !File.Exists(superPath))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Super image not found: {superPath ?? "<empty>"}  找不到 super 镜像。");
            return;
        }

        var outputDir = DeriveOutputDir(superPath);
        var result = await FirmwareService.Instance
            .UnpackSuperAsync(superPath, outputDir, context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Super unpack failed: {result.Summary}  解包 super 失败。");
            return;
        }

        WriteOutput("Output Dir", outputDir);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已解包。");
    }

    private async Task ExecutePackAsync(INodeExecutionContext context)
    {
        var outputPath = PathText(ReadInput("Output Path"));
        var deviceSizeValue = NodeDataUtil.ToDouble(ReadInput("Device Size"));
        var groupName = PathText(ReadInput("Group Name"));
        var partitionsText = PathText(ReadInput("Partitions")) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            context.Log.Write(LogLevel.Warn, Title, "Output Path is empty.  输出路径为空。");
            return;
        }

        if (deviceSizeValue <= 0)
        {
            context.Log.Write(LogLevel.Warn, Title, "Device Size must be a positive number (bytes).  Device Size 必须为正数（字节）。");
            return;
        }

        var partitions = new List<SuperPartitionInput>();
        var skipped = false;
        foreach (var item in partitionsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx == pair.Length - 1)
            {
                context.Log.Write(LogLevel.Warn, Title, $"Invalid partition entry: '{pair}' (expected name=path)  无效分区条目：'{pair}'（应为 name=path）。");
                skipped = true;
                continue;
            }

            var name = pair[..idx].Trim();
            var path = pair[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || !File.Exists(path))
            {
                context.Log.Write(LogLevel.Warn, Title, $"Partition image not found: {path ?? "<empty>"}  找不到分区镜像。");
                skipped = true;
                continue;
            }

            partitions.Add(new SuperPartitionInput(name, path));
        }

        if (partitions.Count == 0)
        {
            context.Log.Write(LogLevel.Warn, Title, "No valid partitions supplied.  未提供有效分区。");
            return;
        }

        if (skipped)
        {
            context.Log.Write(LogLevel.Warn, Title, "Some partition entries were skipped.  部分分区条目已被跳过。");
        }

        var result = await FirmwareService.Instance
            .SynthesizeSuperAsync(partitions, outputPath, (ulong)deviceSizeValue, groupName ?? "default", context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Super pack failed: {result.Summary}  合成 super 失败。");
            return;
        }

        WriteOutput("Result", result.Summary);
        context.Log.Write(LogLevel.Info, Title, result.Summary);
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string DeriveOutputDir(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, string.IsNullOrEmpty(name) ? "out" : name + "_out");
    }
}