using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>稀疏镜像转换节点视图模型，提供 raw 与 Android sparse 镜像格式互转功能。</para>
/// Sparse image conversion node view model, providing conversion between raw and Android sparse image formats.
/// </summary>
public class SparseNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的稀疏转换节点。</para>
    /// Creates a sparse conversion node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public SparseNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的稀疏格式转换节点。</para>
    /// Creates a default sparse-format conversion node.
    /// </summary>
    public static SparseNodeViewModel CreateSparseNode()
    {
        var node = new SparseNodeViewModel("Sparse Convert")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Sparse"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "To Sparse", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发稀疏转换逻辑：Firmware.Sparse 按 “To Sparse” 输入在 raw/sparse 之间转换。</para>
    /// Dispatches sparse conversion logic by the node type key (TypeKey): Firmware.Sparse converts between
    /// raw and sparse according to the “To Sparse” input.
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
            if (TypeKey != "Firmware.Sparse")
            {
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var input = PathText(ReadInput("Input"));
            var output = PathText(ReadInput("Output"));
            var toSparse = NodeDataUtil.ToBool(ReadInput("To Sparse"), true);
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                context.Log.Write(LogLevel.Warn, Title, $"Input image not found: {input ?? "<empty>"}  找不到输入镜像。");
                return;
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
                context.Log.Write(LogLevel.Error, Title, $"Sparse conversion failed: {result.Summary}  稀疏转换失败。");
                return;
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
            context.Log.Write(LogLevel.Error, Title, $"Sparse node failed: {ex.Message}  稀疏转换节点执行失败。");
        }
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}