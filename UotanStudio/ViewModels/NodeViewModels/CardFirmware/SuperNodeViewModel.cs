using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

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
            TypeKey = "Image.Super.Unpack"
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
            TypeKey = "Image.Super.Pack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Device Size", PortType = NodePortType.Number });
        node.Input.Add(new NodeConnectorViewModel { Title = "Group Name", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partitions", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Metadata Size", PortType = NodePortType.Number, InitialValue = 65536d });
        node.Input.Add(new NodeConnectorViewModel { Title = "Slots", PortType = NodePortType.Number, InitialValue = 2d });
        node.Input.Add(new NodeConnectorViewModel { Title = "Block Size", PortType = NodePortType.Number, InitialValue = 4096d });
        node.Input.Add(new NodeConnectorViewModel { Title = "Sparse", PortType = NodePortType.Boolean, InitialValue = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Virtual AB", PortType = NodePortType.Boolean, InitialValue = false });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的 super 逻辑：Image.Super.Unpack 从 super 镜像解出逻辑分区、
    /// Image.Super.Pack 将分区镜像合成为 super 镜像。</para>
    /// Dispatches super logic by the node type key (TypeKey): Image.Super.Unpack extracts logical partitions
    /// from a super image, and Image.Super.Pack synthesizes a super image from partition images.
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
                case "Image.Super.Unpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Image.Super.Pack":
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
            throw;
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var superPath = PathText(ReadInput("Super Path"));
        if (string.IsNullOrWhiteSpace(superPath) || !File.Exists(superPath))
        {
            throw new InvalidOperationException($"Super image not found: {superPath ?? "<empty>"}  找不到 super 镜像。");
        }

        var outputDir = DeriveOutputDir(superPath);
        var result = await FirmwareService.Instance
            .UnpackSuperAsync(superPath, outputDir, context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Super unpack failed: {result.Summary}  解包 super 失败。");
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
            throw new InvalidOperationException("Output Path is empty.  输出路径为空。");
        }

        if (deviceSizeValue <= 0)
        {
            throw new InvalidOperationException("Device Size must be a positive number (bytes).  Device Size 必须为正数（字节）。");
        }

        var partitions = new List<SuperPartitionInput>();
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
                throw new InvalidDataException($"Invalid partition entry '{pair}' (expected name=path).");
            }

            var name = pair[..idx].Trim();
            var path = pair[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || !File.Exists(path))
            {
                throw new FileNotFoundException($"Partition image for '{name}' was not found.", path);
            }

            partitions.Add(new SuperPartitionInput(name, path));
        }

        if (partitions.Count == 0)
        {
            throw new InvalidOperationException("No valid partitions supplied.  未提供有效分区。");
        }

        var result = await FirmwareService.Instance
            .SynthesizeSuperAsync(partitions, outputPath, (ulong)deviceSizeValue, groupName ?? "default", context.CancellationToken,
                metadataSize: ToUInt(ReadInput("Metadata Size"), 65536),
                slots: ToUInt(ReadInput("Slots"), 2),
                blockSize: ToUInt(ReadInput("Block Size"), 4096),
                sparse: NodeDataUtil.ToBool(ReadInput("Sparse"), true),
                virtualAb: NodeDataUtil.ToBool(ReadInput("Virtual AB"), false))
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Super pack failed: {result.Summary}  合成 super 失败。");
        }

        WriteOutput("Result", result.Summary);
        context.Log.Write(LogLevel.Info, Title, result.Summary);
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static uint ToUInt(object? value, uint fallback)
    {
        var number = NodeDataUtil.ToDouble(value, fallback);
        return number <= 0 ? fallback : Convert.ToUInt32(number, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DeriveOutputDir(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, string.IsNullOrEmpty(name) ? "out" : name + "_out");
    }
}
