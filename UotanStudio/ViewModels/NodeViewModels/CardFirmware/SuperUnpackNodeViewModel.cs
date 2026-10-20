using System.Globalization;
using Avalonia;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Logical-partition (super) unpack node. Reads the LP metadata, reports the partition inventory, and crops the
/// selected partitions into standalone images so a super can be rebuilt from its parts.
/// <para>逻辑分区（super）解包节点。读取 LP 元数据、报告分区清单，并把选定分区裁剪为独立镜像，使 super 可由各部件重建。</para>
/// </summary>
public sealed class SuperUnpackNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a super unpack node with the specified display title.
    /// <para>创建具有指定显示标题的 super 解包节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public SuperUnpackNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// Creates the logical-partition unpack node.
    /// <para>创建逻辑分区解包节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static SuperUnpackNodeViewModel CreateUnpackNode()
    {
        var node = new SuperUnpackNodeViewModel("Unpack Super")
        {
            Location = new Point(100, 100),
            TypeKey = "Image.Super.Unpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Super", PortType = NodePortType.Path, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Partitions",
            PortType = NodePortType.Text,
            IsRequired = true
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Available", PortType = NodePortType.List });
        node.Output.Add(new NodeConnectorViewModel { Title = "Extracted", PortType = NodePortType.List });
        node.Output.Add(new NodeConnectorViewModel { Title = "Count", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Reads the LP metadata and extracts the requested partitions into the output directory.
    /// <para>读取 LP 元数据并把请求的分区提取到输出目录。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">An input is missing. <para>输入缺失。</para></exception>
    /// <exception cref="InvalidDataException">The image is not a logical-partition container or a partition is missing. <para>镜像不是逻辑分区容器或分区缺失。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "Image.Super.Unpack", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var superPath = Text(ReadInput("Super"));
        var requested = Text(ReadInput("Partitions"));
        var outputDir = Text(ReadInput("Output Dir"));
        if (string.IsNullOrWhiteSpace(superPath) || !File.Exists(superPath))
        {
            throw new InvalidOperationException($"The Super input was not found: {superPath}.  super 镜像不存在。");
        }
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new InvalidOperationException("The Partitions input is required before unpacking a super image.");
        }
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new InvalidOperationException("The Output Dir input is required before unpacking a super image.");
        }

        var names = requested
            .Split([',', '\n', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var metadata = await Task.Run(() => MetadataReader.ReadFromImageFile(superPath), context.CancellationToken)
            .ConfigureAwait(false);
        var available = (IReadOnlyList<object?>)[.. metadata.Partitions.Select(partition => (object?)partition.NameString)];

        var missing = names.Where(name => !metadata.Partitions.Any(partition =>
            string.Equals(partition.NameString, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"The super image has no partition(s): {string.Join(", ", missing)}.  super 中不存在请求的分区。");
        }

        Directory.CreateDirectory(outputDir);
        var result = await ContainerEntryExtractor.ExtractAsync(superPath, names, cancellationToken: context.CancellationToken,
            outputDirectory: outputDir).ConfigureAwait(false);
        if (result.OutputPaths.Count == 0)
        {
            throw new InvalidDataException("No logical partition was extracted from the super image.  未从 super 提取到任何分区。");
        }

        WriteOutput("Available", available);
        WriteOutput("Extracted", (IReadOnlyList<object?>)[.. result.OutputPaths.Select(path => (object?)path)]);
        WriteOutput("Count", (double)result.OutputPaths.Count);
        context.Log.Write(LogLevel.Info, Title,
            $"Extracted {result.OutputPaths.Count} partition(s) into '{outputDir}'.  已提取逻辑分区。");
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}