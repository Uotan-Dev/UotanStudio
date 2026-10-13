using Avalonia;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

public partial class FirmwareNodeViewModel
{
    /// <summary>
    /// Creates a default firmware pack node.
    /// <para>创建默认的固件打包节点。</para>
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

    private static string DefaultOutputFor(string inputDir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputDir)) ?? ".";
        return Path.Combine(parent, Path.GetFileName(inputDir) + ".img");
    }
}