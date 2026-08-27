using UotanStudio.Services.Firmware;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>Android OTA v4 DAT 写出节点视图模型，从原始镜像写出 <c>.new.dat</c> 与 <c>.transfer.list</c> 镜像对。</para>
/// Android OTA v4 DAT writing node view model, writing the <c>.new.dat</c> and <c>.transfer.list</c> image
/// pair from a raw image.
/// </summary>
public class DatNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的 DAT 写出节点。</para>
    /// Creates a DAT writing node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public DatNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的 DAT 写出节点。</para>
    /// Creates a default DAT writing node.
    /// </summary>
    public static DatNodeViewModel CreateDatNode()
    {
        var node = new DatNodeViewModel("Write DAT")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Dat"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Transfer List", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发 DAT 写出逻辑：Firmware.Dat 从输入镜像写出 DAT 镜像对。</para>
    /// Dispatches DAT writing logic by the node type key (TypeKey): Firmware.Dat writes the DAT image pair
    /// from the input image.
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
            if (TypeKey != "Firmware.Dat")
            {
                await base.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var input = PathText(ReadInput("Transfer List"));
            var output = PathText(ReadInput("Output"));
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                context.Log.Write(LogLevel.Warn, Title, $"Input image not found: {input ?? "<empty>"}  找不到输入镜像。");
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                output = input + ".new.dat";
            }

            var result = await FirmwareService.Instance
                .WriteDatAsync(input, output, context.CancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                context.Log.Write(LogLevel.Error, Title, $"DAT write failed: {result.Summary}  写出 DAT 失败。");
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
            context.Log.Write(LogLevel.Error, Title, $"DAT node failed: {ex.Message}  DAT 节点执行失败。");
        }
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}