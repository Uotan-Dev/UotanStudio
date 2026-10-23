using UotanStudio.Services.Ui.Interactions;

namespace UotanStudio.Services.Interactions;

/// <summary>One desktop modal queue shared by all node editors and nested invocations.
/// <para>一个桌面模态队列，由全部节点编辑会话与嵌套调用共同使用。</para></summary>
public static class WorkflowInteractionHost
{
    public static IWorkflowInteractionService Desktop { get; } = new QueuedWorkflowInteractionService(new AvaloniaWorkflowInteractionService());
}