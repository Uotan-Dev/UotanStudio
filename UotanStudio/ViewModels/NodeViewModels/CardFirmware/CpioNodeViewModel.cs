using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// CPIO operation node view model, providing CPIO/TAR archive unpacking and packing capabilities.
/// <para>CPIO 操作节点视图模型，提供 CPIO/TAR 归档的解包与打包功能。</para>
/// </summary>
public class CpioNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a CPIO operation node with the specified title.
    /// <para>创建具有指定标题的 CPIO 操作节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public CpioNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// Creates a default CPIO unpack node.
    /// <para>创建默认的 CPIO 解包节点。</para>
    /// </summary>
    public static CpioNodeViewModel CreateCpioUnpackNode()
    {
        var node = new CpioNodeViewModel("CPIO Unpack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Cpio.Unpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Archive Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// Creates a default CPIO pack node.
    /// <para>创建默认的 CPIO 打包节点。</para>
    /// </summary>
    public static CpioNodeViewModel CreateCpioPackNode()
    {
        var node = new CpioNodeViewModel("CPIO Pack")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Cpio.Pack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Archive", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>Dispatches CPIO logic by the node type key (TypeKey): Firmware.Cpio.Unpack unpacks a CPIO/TAR archive, and Firmware.Cpio.Pack packs a directory into a CPIO archive.
    /// <para>按节点类型键（TypeKey）分发对应的 CPIO 逻辑：Firmware.Cpio.Unpack 解包 CPIO/TAR 归档、 Firmware.Cpio.Pack 将目录打包为 CPIO 归档。</para></summary>
    /// <param name="context">The execution context.<para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution.<para>表示异步执行的任务。</para></returns>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            switch (TypeKey)
            {
                case "Firmware.Cpio.Unpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Cpio.Pack":
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
            throw;
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var archivePath = PathText(ReadInput("Archive Path"));
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException($"Archive not found: {archivePath ?? "<empty>"}  找不到归档文件。");
        }

        var outputDir = DeriveOutputDir(archivePath);
        var result = await FirmwareService.Instance.UnpackCpioAsync(archivePath, outputDir,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Unpack failed: {result.Summary}  解包失败。");
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
            throw new InvalidOperationException($"Input directory not found: {inputDir ?? "<empty>"}  找不到输入目录。");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputDir)) ?? ".", Path.GetFileName(inputDir) + ".cpio");
        }

        var result = await FirmwareService.Instance.PackCpioAsync(inputDir, outputPath, context.CancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Pack failed: {result.Summary}  打包失败。");
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