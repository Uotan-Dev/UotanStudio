using Avalonia;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>固件操作节点视图模型，提供固件解包、打包、签名验证和分区提取功能。</para>
/// Firmware operation node view model, providing firmware unpack, pack, signature verification, and partition extraction capabilities.
/// </summary>
public class FirmwareNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的固件操作节点。</para>
    /// Creates a firmware operation node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public FirmwareNodeViewModel(string title) : base(title, NodeCategory.Firmware)
    {
    }

    /// <summary>
    /// <para>创建默认的固件解包节点。</para>
    /// Creates a default firmware unpack node.
    /// </summary>
    /// <returns>配置好输入输出端口的固件解包节点。</returns>
    public static FirmwareNodeViewModel CreateUnpackNode()
    {
        var node = new FirmwareNodeViewModel("Unpack Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Container.Unpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Partitions", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的固件打包节点。</para>
    /// Creates a default firmware pack node.
    /// </summary>
    public static FirmwareNodeViewModel CreatePackNode()
    {
        var node = new FirmwareNodeViewModel("Pack Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Container.Pack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Config",
            PortType = NodePortType.Text,
            DisplayResourceKey = "Node_Conn_LegacyOutputPath"
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>创建默认的签名验证节点。</para>
    /// Creates a default signature verification node.
    /// </summary>
    public static FirmwareNodeViewModel CreateVerifyNode()
    {
        var node = new FirmwareNodeViewModel("Verify Signature")
        {
            Location = new Point(100, 100),
            TypeKey = "Validation.Signature.Verify"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Public Key", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Valid", PortType = NodePortType.Boolean });
        node.Output.Add(new NodeConnectorViewModel { Title = "Report", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的分区提取节点。</para>
    /// Creates a default partition extraction node.
    /// </summary>
    public static FirmwareNodeViewModel CreateExtractNode()
    {
        var node = new FirmwareNodeViewModel("Extract Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Container.Extract"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition Name", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Data });
        node.Output.Add(new NodeConnectorViewModel { Title = "Image File", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的固件逻辑：Firmware.Container.Unpack 解包（boot/oppo/dt）、Firmware.Container.Pack 打包（boot/cpio/dtbo）、
    /// Validation.Signature.Verify 校验 AVB 签名、Firmware.Container.Extract 从 payload 提取分区。</para>
    /// Dispatches firmware logic by the node type key (TypeKey): Firmware.Container.Unpack unpacks (boot/oppo/dt), Firmware.Container.Pack packs
    /// (boot/cpio/dtbo), Validation.Signature.Verify verifies an AVB signature, and Firmware.Container.Extract extracts a
    /// partition from a payload.
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
                case "Firmware.Container.Unpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Container.Pack":
                    await ExecutePackAsync(context).ConfigureAwait(false);
                    break;
                case "Validation.Signature.Verify":
                    await ExecuteVerifyAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Container.Extract":
                    await ExecuteExtractAsync(context).ConfigureAwait(false);
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
            context.Log.Write(LogLevel.Error, Title, $"Firmware node failed: {ex.Message}  固件节点执行失败。");
            throw;
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware Path"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Firmware input was not found.", path);
        }

        var outputDir = DeriveOutputDir(path);
        using var metadataScope = RepackMetadata.UseContext(
            UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(outputDir));
        var dt = new DtFormatHandler();
        if (dt.CanHandle(path))
        {
            var result = await dt.UnpackAsync(path, outputDir, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidDataException(result.Summary);
            }
            WriteOutput("Output Dir", outputDir);
            WriteOutput("Partitions", result.Entries);
            context.Log.Write(LogLevel.Info, Title, result.Summary);
            return;
        }
        var service = FirmwareService.Instance;
        var boot = await service.UnpackBootAsync(path, outputDir, context.CancellationToken).ConfigureAwait(false);
        if (!boot.Success)
        {
            var oppo = await service.ExtractOppoAsync(path, outputDir, true, context.CancellationToken).ConfigureAwait(false);
            if (!oppo.Success)
            {
                throw new InvalidDataException($"Unpack failed: {boot.Summary} | {oppo.Summary}");
            }

            WriteOutput("Output Dir", outputDir);
            WriteOutput("Partitions", oppo.Entries);
            context.Log.Write(LogLevel.Info, Title, $"{oppo.Summary}  已解包。");
            return;
        }

        WriteOutput("Output Dir", outputDir);
        WriteOutput("Partitions", boot.Entries);
        context.Log.Write(LogLevel.Info, Title, $"{boot.Summary}  已解包。");
    }

    private async Task ExecutePackAsync(INodeExecutionContext context)
    {
        var inputDir = PathText(ReadInput("Input Dir"));
        var config = PathText(ReadInput("Config"));
        var configuredOutput = PathText(ReadInput("Output Path"));
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            throw new DirectoryNotFoundException($"Input directory was not found: {inputDir}");
        }

        var outputPath = !string.IsNullOrWhiteSpace(configuredOutput)
            ? configuredOutput
            : string.IsNullOrWhiteSpace(config) ? DefaultOutputFor(inputDir) : config;

        using var metadataScope = RepackMetadata.UseContext(
            UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(inputDir));
        var repack = RepackMetadata.Read(inputDir)
            ?? throw new InvalidDataException(LanguageService.Instance.GetString("Node_Pack_RequiresMetadata"));
        if (!repack.CanPackDirectory)
        {
            throw new InvalidDataException(repack.PackingNotes);
        }
        var packed = await RegisteredFirmwareFormats.PackDirectoryAsync(repack.FormatName, inputDir,
            outputPath, context.CancellationToken, throwIfUnsupported: true).ConfigureAwait(false);
        if (!packed.Success)
        {
            throw new InvalidDataException(packed.Summary);
        }
        WriteOutput("Firmware", outputPath);
        context.Log.Write(LogLevel.Info, Title, packed.Summary);
    }

    private async Task ExecuteVerifyAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Firmware input was not found.", path);
        }

        if (!await HasMagicAsync(path, "AVB0", context.CancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Signature verification requires an AVB vbmeta image.");
        }

        var publicKey = PathText(ReadInput("Public Key"));
        var result = await FirmwareService.Instance.VerifyAvbAsync(path,
            publicKeyPath: string.IsNullOrWhiteSpace(publicKey) ? null : publicKey,
            context.CancellationToken).ConfigureAwait(false);
        var report = result.Success ? result.Summary : $"Verification failed: {result.Summary}  校验失败。";
        WriteOutput("Valid", result.Success);
        WriteOutput("Report", report);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title, report);
        if (!result.Success)
        {
            throw new InvalidDataException(report);
        }
    }

    private async Task ExecuteExtractAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware"));
        var partition = Convert.ToString(ReadInput("Partition Name"), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Firmware input was not found.", path);
        }

        var outputDir = DeriveOutputDir(path);
        var result = await FirmwareService.Instance
            .ExtractPayloadAsync(path, outputDir, partition, context.CancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidDataException(result.Summary);
        }

        if (!string.IsNullOrWhiteSpace(partition))
        {
            var imagePath = Path.Combine(outputDir, partition + ".img");
            WriteOutput("Image", new UotanStudio.Services.Projects.FileArtifact(imagePath));
            WriteOutput("Image File", imagePath);
        }
        WriteOutput("Output Dir", outputDir);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已提取。");
    }

    private static string? PathText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string DeriveOutputDir(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, string.IsNullOrEmpty(name) ? "out" : name + "_out");
    }

    private static string DefaultOutputFor(string inputDir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputDir)) ?? ".";
        return Path.Combine(parent, Path.GetFileName(inputDir) + ".img");
    }

    private static async Task<bool> HasMagicAsync(string path, string magic, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(path);
        var buffer = new byte[magic.Length];
        var read = await fs.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read >= magic.Length && System.Text.Encoding.ASCII.GetString(buffer, 0, magic.Length) == magic;
    }
}
