namespace UotanStudio.Services.Interactions;

/// <summary>One desktop modal queue shared by all node editors and nested invocations.</summary>
public static class WorkflowInteractionHost
{
    public static IWorkflowInteractionService Desktop { get; } = new QueuedWorkflowInteractionService(new AvaloniaWorkflowInteractionService());
}