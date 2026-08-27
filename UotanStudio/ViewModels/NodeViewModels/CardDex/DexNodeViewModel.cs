using UotanStudio.Services.Firmware;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>DEX 工具节点视图模型，提供 DEX 解析、反汇编（Smali）与重建写入功能。</para>
/// DEX tool node view model, providing DEX parse, disassembly (Smali) and rebuild-write capabilities.
/// </summary>
public class DexNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的 DEX 工具节点。</para>
    /// Creates a DEX tool node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public DexNodeViewModel(string title) : base(title, NodeCategory.Dex)
    {
    }

    /// <summary>
    /// <para>创建默认的 DEX 解析节点。</para>
    /// Creates a default DEX parse node.
    /// </summary>
    /// <returns>配置好输入输出端口的 DEX 解析节点。</returns>
    public static DexNodeViewModel CreateParseNode()
    {
        var node = new DexNodeViewModel("Parse DEX")
        {
            Location = new Point(100, 100),
            TypeKey = "Dex.Parse"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Dex Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Summary", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 DEX 反汇编节点。</para>
    /// Creates a default DEX disassemble node.
    /// </summary>
    public static DexNodeViewModel CreateDisassembleNode()
    {
        var node = new DexNodeViewModel("Disassemble DEX")
        {
            Location = new Point(100, 100),
            TypeKey = "Dex.Disassemble"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Dex Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Report", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 DEX 重建节点。</para>
    /// Creates a default DEX rebuild node.
    /// </summary>
    public static DexNodeViewModel CreateRebuildNode()
    {
        var node = new DexNodeViewModel("Rebuild DEX")
        {
            Location = new Point(100, 100),
            TypeKey = "Dex.Rebuild"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Dex Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>执行与节点 TypeKey 对应的 DEX 逻辑：Dex.Parse 解析描述、Dex.Disassemble 反汇编为 Smali、
    /// Dex.Rebuild 重建写入新 DEX 文件。</para>
    /// Executes the DEX logic matching the node TypeKey: Dex.Parse parses the description, Dex.Disassemble
    /// disassembles to Smali, and Dex.Rebuild rebuilds a new DEX file.
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
                case "Dex.Parse":
                    await ExecuteParseAsync(context).ConfigureAwait(false);
                    break;
                case "Dex.Disassemble":
                    await ExecuteDisassembleAsync(context).ConfigureAwait(false);
                    break;
                case "Dex.Rebuild":
                    await ExecuteRebuildAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Dex node failed: {ex.Message}  DEX 节点执行失败。");
        }
    }

    private async Task ExecuteParseAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Dex Path"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Dex path not found: {path ?? "<empty>"}  找不到 DEX 路径。");
            return;
        }

        var result = await DexService.Instance.ParseDexAsync(path, context.CancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Parse failed: {result.Summary}  解析失败。");
            return;
        }

        WriteOutput("Summary", result.Summary);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已解析。");
    }

    private async Task ExecuteDisassembleAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Dex Path"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Dex path not found: {path ?? "<empty>"}  找不到 DEX 路径。");
            return;
        }

        var outputDir = PathText(ReadInput("Output Dir"));
        var result = await DexService.Instance
            .DisassembleDexAsync(path, outputDir, context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Disassemble failed: {result.Summary}  反汇编失败。");
            return;
        }

        WriteOutput("Report", result.Summary);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已反汇编。");
    }

    private async Task ExecuteRebuildAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Dex Path"));
        var outputPath = PathText(ReadInput("Output Path"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Dex path not found: {path ?? "<empty>"}  找不到 DEX 路径。");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            context.Log.Write(LogLevel.Warn, Title, "Output path is empty.  输出路径为空。");
            return;
        }

        var result = await DexService.Instance
            .RebuildDexAsync(path, outputPath, context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Rebuild failed: {result.Summary}  重建失败。");
            return;
        }

        WriteOutput("Result", result.Summary);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已重建。");
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
}