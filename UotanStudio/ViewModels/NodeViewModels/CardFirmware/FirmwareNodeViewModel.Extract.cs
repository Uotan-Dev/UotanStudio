using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.NodeViewModels;

public partial class FirmwareNodeViewModel
{
    /// <summary>
    /// Creates a default partition extraction node.
    /// <para>创建默认的分区提取节点。</para>
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
        node.Output.Add(new NodeConnectorViewModel { Title = "Artifact", PortType = NodePortType.Artifact });
        node.Output.Add(new NodeConnectorViewModel { Title = "Output Dir", PortType = NodePortType.Path });
        return node;
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
            WriteOutput("Artifact", new UotanStudio.Services.Projects.FileArtifact(imagePath));
        }
        WriteOutput("Output Dir", outputDir);
        context.Log.Write(LogLevel.Info, Title, $"{result.Summary}  已提取。");
    }
}