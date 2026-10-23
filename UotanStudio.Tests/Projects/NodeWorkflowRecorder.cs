using System.Diagnostics.CodeAnalysis;
using UotanStudio.Projects;

namespace UotanStudio.Tests.Projects;

internal sealed class NodeWorkflowRecorder
{
    private readonly Dictionary<string, NodeProjectSnapshot> _workflows = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> OpenWorkflowNames => _workflows.Keys.ToArray();

    public string? ActiveWorkflowName { get; private set; }

    public IReadOnlyDictionary<string, NodeProjectSnapshot> Workflows => _workflows;

    public void ActivateWorkflow(string name)
    {
        if (_workflows.ContainsKey(name)) ActiveWorkflowName = name;
    }

    public void CloseWorkflow(string name)
    {
        _workflows.Remove(name);
        if (string.Equals(ActiveWorkflowName, name, StringComparison.OrdinalIgnoreCase))
            ActiveWorkflowName = OpenWorkflowNames.FirstOrDefault();
    }

    public void Clear()
    {
        _workflows.Clear();
        ActiveWorkflowName = null;
    }

    public void Restore(string name, NodeProjectSnapshot snapshot)
    {
        _workflows[name] = snapshot;
        ActiveWorkflowName ??= name;
    }

    public bool TryCapture(string name, [NotNullWhen(true)] out NodeProjectSnapshot? snapshot)
        => _workflows.TryGetValue(name, out snapshot);
}