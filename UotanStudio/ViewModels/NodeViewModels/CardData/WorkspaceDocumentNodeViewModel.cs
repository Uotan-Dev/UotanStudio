using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Workspace;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed class WorkspaceDocumentNodeViewModel : FlowNodeViewModel
{
    private WorkspaceDocumentNodeViewModel(string typeKey) : base(typeKey, NodeCategory.Data)
    {
        TypeKey = typeKey;
        Input.Add(new() { Title = "Flow", PortType = NodePortType.Flow });
        if (typeKey == "Workspace.Document.Resolve")
        {
            Input.Add(new() { Title = "Selector", PortType = NodePortType.Text });
        }
        else
        {
            Input.Add(new() { Title = "Document", PortType = NodePortType.Document, IsRequired = true });
        }
        if (typeKey == "Workspace.Document.ReplaceText")
        {
            Input.Add(new() { Title = "Text", PortType = NodePortType.Text, InitialValue = string.Empty, IsMultiline = true });
        }
        Output.Add(new() { Title = "Document", PortType = NodePortType.Document });
        Output.Add(new() { Title = "Text", PortType = NodePortType.Text });
        Output.Add(new() { Title = "Path", PortType = NodePortType.Path });
        Output.Add(new() { Title = "Kind", PortType = NodePortType.Text });
        Output.Add(new() { Title = "Can Edit", PortType = NodePortType.Boolean });
        Output.Add(new() { Title = "Completed", PortType = NodePortType.Flow });
    }

    public static WorkspaceDocumentNodeViewModel Create(string typeKey) => new(typeKey);

    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        var documents = context.Documents
            ?? throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Document_Unsupported"));
        var snapshot = TypeKey == "Workspace.Document.Resolve"
            ? await documents.ResolveAsync(Convert.ToString(ReadInput("Selector")) ?? string.Empty,
                context.CancellationToken).ConfigureAwait(false)
            : ReadInput("Document") as WorkflowDocumentSnapshot
                ?? throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Document_NotFound"));
        if (TypeKey == "Workspace.Document.ReplaceText")
        {
            var text = ReadInput("Text") as string
                ?? throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Document_TextRequired"));
            snapshot = await documents.ReplaceAsync(snapshot, text, context.CancellationToken).ConfigureAwait(false);
        }
        else if (TypeKey == "Workspace.Document.ReadText")
        {
            snapshot = await documents.ReadAsync(snapshot, context.CancellationToken).ConfigureAwait(false);
            if (snapshot.Text is null)
            {
                throw new InvalidOperationException(LanguageService.Instance.GetString("Node_Document_ReadOnly"));
            }
        }
        WriteOutput("Document", snapshot);
        WriteOutput("Text", snapshot.Text);
        WriteOutput("Path", snapshot.Path);
        WriteOutput("Kind", snapshot.Kind);
        WriteOutput("Can Edit", snapshot.CanEdit);
        WriteOutput("Completed", true);
    }
}