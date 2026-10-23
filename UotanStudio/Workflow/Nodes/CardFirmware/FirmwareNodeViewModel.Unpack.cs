using Avalonia;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public partial class FirmwareNodeViewModel
{
    /// <summary>Creates a default firmware unpack node.<para>创建默认的固件解包节点。</para></summary>
    /// <returns>The firmware unpack node with its input and output ports.<para>配置好输入输出端口的固件解包节点。</para></returns>
    public static FirmwareNodeViewModel CreateUnpackNode()
    {
        var node = new FirmwareNodeViewModel("Unpack Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Container.Unpack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Firmware Path", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Metadata Only",
            PortType = NodePortType.Boolean,
            InitialValue = "false",
            DisplayResourceKey = "Node_Conn_MetadataOnly"
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Partitions", PortType = NodePortType.Text });
        return node;
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
            Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(outputDir));
        if (IsMetadataOnlyRequested())
        {
            await ExecuteMetadataOnlyUnpackAsync(context, path, outputDir).ConfigureAwait(false);
            return;
        }

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
                // Neither boot nor OPPO layout matched, so the registered format handlers decide: this is how
                // cpio/tar, super, sparse, payload and filesystem images reach the generic unpack node.
                var detected = await service.UnpackDetectedAsync(path, outputDir, context.CancellationToken).ConfigureAwait(false);
                if (!detected.Success)
                {
                    throw new InvalidDataException($"Unpack failed: {boot.Summary} | {oppo.Summary} | {detected.Summary}");
                }

                WriteOutput("Output Dir", outputDir);
                WriteOutput("Partitions", detected.Entries);
                context.Log.Write(LogLevel.Info, Title, $"{detected.Summary}  已解包。");
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

    private bool IsMetadataOnlyRequested() => ReadInput("Metadata Only") switch
    {
        bool value => value,
        string text when bool.TryParse(text, out var value) => value,
        _ => false
    };

    private async Task ExecuteMetadataOnlyUnpackAsync(INodeExecutionContext context, string path, string outputDir)
    {
        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path)
            ?? throw new InvalidDataException("No format handler can read this container, so repack metadata cannot be exported alone.");
        var result = await handler.UnpackAsync(path, outputDir,
            new UnpackOptions { MetadataOnly = true }, context.CancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidDataException(result.Summary);
        }

        WriteOutput("Output Dir", outputDir);
        WriteOutput("Partitions", result.Entries);
        context.Log.Write(LogLevel.Info, Title, result.Summary);
    }
}