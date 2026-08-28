using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>CPIO 操作节点视图模型，提供 CPIO/TAR 归档的解包与打包功能。</para>
/// CPIO operation node view model, providing CPIO/TAR archive unpacking and packing capabilities.
/// </summary>
public class CpioNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的 CPIO 操作节点。</para>
    /// Creates a CPIO operation node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public CpioNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的 CPIO 解包节点。</para>
    /// Creates a default CPIO unpack node.
    /// </summary>
    public static CpioNodeViewModel CreateCpioUnpackNode()
    {
        var node = new CpioNodeViewModel("CPIO Unpack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.CpioUnpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Archive Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 CPIO 打包节点。</para>
    /// Creates a default CPIO pack node.
    /// </summary>
    public static CpioNodeViewModel CreateCpioPackNode()
    {
        var node = new CpioNodeViewModel("CPIO Pack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.CpioPack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Archive", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的 CPIO 逻辑：Firmware.CpioUnpack 解包 CPIO/TAR 归档、
    /// Firmware.CpioPack 将目录打包为 CPIO 归档。</para>
    /// Dispatches CPIO logic by the node type key (TypeKey): Firmware.CpioUnpack unpacks a CPIO/TAR archive,
    /// and Firmware.CpioPack packs a directory into a CPIO archive.
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
                case "Firmware.CpioUnpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.CpioPack":
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
            context.Log.Write(LogLevel.Error, Title, $"CPIO node failed: {ex.Message}  CPIO 节点执行失败。");
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var archivePath = PathText(ReadInput("Archive Path"));
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Archive not found: {archivePath ?? "<empty>"}  找不到归档文件。");
            return;
        }

        var outputDir = DeriveOutputDir(archivePath);
        var result = await FirmwareService.Instance.UnpackCpioAsync(archivePath, outputDir).ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Unpack failed: {result.Summary}  解包失败。");
            return;
        }

        WriteOutput("Output Dir", outputDir);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已解包。");
    }

    private async Task ExecutePackAsync(INodeExecutionContext context)
    {
        var inputDir = PathText(ReadInput("Input Dir"));
        var outputPath = PathText(ReadInput("Output Archive"));
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Input directory not found: {inputDir ?? "<empty>"}  找不到输入目录。");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputDir)) ?? ".", Path.GetFileName(inputDir) + ".cpio");
        }

        var result = await FirmwareService.Instance.PackCpioAsync(inputDir, outputPath, context.CancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Pack failed: {result.Summary}  打包失败。");
            return;
        }

        WriteOutput("Result", outputPath);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已打包。");
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string DeriveOutputDir(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, string.IsNullOrEmpty(name) ? "out" : name + "_out");
    }
}