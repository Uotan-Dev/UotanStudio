using Avalonia;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;
using FirmwareOpResult = UotanStudio.Services.Firmware.FirmwareOpResult;

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
            TypeKey = "Firmware.Unpack"
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
            TypeKey = "Firmware.Pack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Config", PortType = NodePortType.Text });
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
            TypeKey = "Firmware.Verify"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
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
            TypeKey = "Firmware.Extract"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition Name", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Data });
        return node;
    }

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发对应的固件逻辑：Firmware.Unpack 解包（boot/oppo/dt）、Firmware.Pack 打包（boot/cpio/dtbo）、
    /// Firmware.Verify 校验 AVB 签名或计算哈希、Firmware.Extract 从 payload 提取分区。</para>
    /// Dispatches firmware logic by the node type key (TypeKey): Firmware.Unpack unpacks (boot/oppo/dt), Firmware.Pack packs
    /// (boot/cpio/dtbo), Firmware.Verify verifies the AVB signature or computes a hash, and Firmware.Extract extracts a
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
                case "Firmware.Unpack":
                    await ExecuteUnpackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Pack":
                    await ExecutePackAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Verify":
                    await ExecuteVerifyAsync(context).ConfigureAwait(false);
                    break;
                case "Firmware.Extract":
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
        }
    }

    private async Task ExecuteUnpackAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware Path"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Firmware path not found: {path ?? "<empty>"}  找不到固件路径。");
            return;
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
                context.Log.Write(LogLevel.Error, Title, result.Summary);
                return;
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
                context.Log.Write(LogLevel.Warn, Title, $"Unpack failed: {boot.Summary} | {oppo.Summary}  解包失败。");
                return;
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
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Input directory not found: {inputDir ?? "<empty>"}  找不到输入目录。");
            return;
        }

        var outputPath = string.IsNullOrWhiteSpace(config)
            ? DefaultOutputFor(inputDir)
            : config;

        var service = FirmwareService.Instance;
        FirmwareOpResult result;
        using var metadataScope = RepackMetadata.UseContext(
            UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(inputDir));
        var repack = RepackMetadata.Read(inputDir);
        if (repack is not null)
        {
            if (!repack.CanPackDirectory)
            {
                context.Log.Write(LogLevel.Error, Title, repack.PackingNotes);
                return;
            }
            var packed = await RegisteredFirmwareFormats.PackDirectoryAsync(repack.FormatName, inputDir,
                outputPath, context.CancellationToken, throwIfUnsupported: true).ConfigureAwait(false);
            if (!packed.Success)
            {
                context.Log.Write(LogLevel.Error, Title, packed.Summary);
                return;
            }
            WriteOutput("Firmware", outputPath);
            context.Log.Write(LogLevel.Info, Title, packed.Summary);
            return;
        }
        result = await service.PackCpioAsync(inputDir, outputPath, context.CancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Pack failed: {result.Summary}  打包失败。");
            return;
        }

        WriteOutput("Firmware", outputPath);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已打包。");
    }

    private async Task ExecuteVerifyAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Firmware path not found: {path ?? "<empty>"}  找不到固件路径。");
            return;
        }

        var service = FirmwareService.Instance;
        var isVbmeta = await HasMagicAsync(path, "AVB0").ConfigureAwait(false);
        FirmwareOpResult result;
        if (isVbmeta)
        {
            result = await service.VerifyAvbAsync(path, publicKeyPath: null, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            var checksum = await service.VerifyChecksumAsync(path, null, context.CancellationToken).ConfigureAwait(false);
            result = new FirmwareOpResult { Success = true, Summary = $"Computed {checksum.Summary}" };
        }

        var report = result.Success ? result.Summary : $"Verification failed: {result.Summary}  校验失败。";
        WriteOutput("Valid", result.Success);
        WriteOutput("Report", report);
        context.Log.Write(result.Success ? LogLevel.Info : LogLevel.Error, Title, report);
    }

    private async Task ExecuteExtractAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Firmware"));
        var partition = Convert.ToString(ReadInput("Partition Name"), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            context.Log.Write(LogLevel.Warn, Title, $"Firmware path not found: {path ?? "<empty>"}  找不到固件路径。");
            return;
        }

        var outputDir = DeriveOutputDir(path);
        var result = await FirmwareService.Instance
            .ExtractPayloadAsync(path, outputDir, partition)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            context.Log.Write(LogLevel.Error, Title, $"Extract failed: {result.Summary}  提取失败。");
            return;
        }

        var imagePath = string.IsNullOrWhiteSpace(partition)
            ? outputDir
            : Path.Combine(outputDir, partition);
        WriteOutput("Image", imagePath);
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

    private static async Task<bool> HasMagicAsync(string path, string magic)
    {
        await using var fs = File.OpenRead(path);
        var buffer = new byte[magic.Length];
        var read = await fs.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        return read >= magic.Length && System.Text.Encoding.ASCII.GetString(buffer, 0, magic.Length) == magic;
    }
}