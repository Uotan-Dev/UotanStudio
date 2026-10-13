using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Creates a typed file reference without reading its contents into memory.<para>创建类型化文件引用，不将内容读入内存。</para></summary>
public sealed class ArtifactNodeViewModel : FlowNodeViewModel
{
    public ArtifactNodeViewModel() : base("File artifact", NodeCategory.Data)
    {
        TypeKey = "File.Artifact.Open";
        Input.Add(new NodeConnectorViewModel { Title = "Path", PortId = "path", PortType = NodePortType.Path, IsRequired = true });
        Output.Add(new NodeConnectorViewModel { Title = "Artifact", PortId = "artifact", PortType = NodePortType.Artifact });
    }

    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var path = Convert.ToString(ReadInput("Path"));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The artifact source file does not exist.", fullPath);
        }
        WriteOutput("Artifact", new FileArtifact(fullPath));
        return Task.CompletedTask;
    }
}