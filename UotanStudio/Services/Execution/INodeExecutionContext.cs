using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Execution;

/// <summary>
/// <para>节点执行上下文，提供日志、取消令牌与进度报告。</para>
/// Node execution context, providing logging, cancellation token, and progress reporting.
/// </summary>
public interface INodeExecutionContext
{
    /// <summary>
    /// <para>统一日志服务，所有节点执行日志通过此推送。</para>
    /// Unified log service; all node execution logs are pushed through this.
    /// </summary>
    IUnifiedLog Log { get; }

    /// <summary>
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Shared staging owned by the outermost execution, including nested composites.</summary>
    PatchRun? Patches => null;

    /// <summary>Resource scope of the currently executing composite or package editor.</summary>
    VisualPluginPackage? Package => null;

    /// <summary>Host interaction boundary; noninteractive hosts explicitly reject requests.</summary>
    IWorkflowInteractionService Interactions => UnsupportedWorkflowInteractionService.Instance;

    /// <summary>Reports a user wait without blocking the UI thread.</summary>
    IDisposable BeginUserInteractionWait() => new InteractionWaitScope(null);

    /// <summary>Transfers a runtime handle to the current invocation for unconditional cleanup.</summary>
    void RegisterResource(IAsyncDisposable resource) => throw new NotSupportedException("The execution context does not own runtime resources.");

    /// <summary>
    /// <para>报告节点执行进度（0.0 ~ 1.0）。</para>
    /// Reports the node execution progress (0.0 ~ 1.0).
    /// </summary>
    void ReportProgress(double progress);
}