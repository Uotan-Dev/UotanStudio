using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Sparse image conversion node view model, providing conversion between raw and Android sparse image formats.
/// <para>稀疏镜像转换节点视图模型，提供 raw 与 Android sparse 镜像格式互转功能。</para>
/// </summary>
public class SparseNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a sparse conversion node with the specified title.
    /// <para>创建具有指定标题的稀疏转换节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public SparseNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// Creates a default sparse-format conversion node.
    /// <para>创建默认的稀疏格式转换节点。</para>
    /// </summary>
    public static SparseNodeViewModel CreateSparseNode()
    {
        var node = new SparseNodeViewModel("Sparse Convert")
        {
            Location = new Point(100, 100),
            TypeKey = "Image.Sparse.Convert"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "To Sparse", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>Dispatches sparse conversion logic by the node type key (TypeKey): Image.Sparse.Convert converts between raw and sparse according to the “To Sparse” input.
    /// <para>按节点类型键（TypeKey）分发稀疏转换逻辑：Image.Sparse.Convert 按 “To Sparse” 输入在 raw/sparse 之间转换。</para></summary>
    /// <param name="context">The execution context.<para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution.<para>表示异步执行的任务。</para></returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            if (TypeKey != "Image.Sparse.Convert")
            {
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var input = PathText(ReadInput("Input"));
            var output = PathText(ReadInput("Output"));
            var toSparse = NodeDataUtil.ToBool(ReadInput("To Sparse"), true);
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                throw new InvalidOperationException($"Input image not found: {input ?? "<empty>"}");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                output = input + (toSparse ? ".sparse" : ".raw");
            }

            var result = await FirmwareService.Instance
                .ConvertSparseAsync(input, output, toSparse, context.CancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Sparse conversion failed: {result.Summary}");
            }

            WriteOutput("Result", result.Summary);
            context.Log.Write(LogLevel.Info, Title, result.Summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, $"Sparse node failed: {ex.Message}");
            throw;
        }
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}