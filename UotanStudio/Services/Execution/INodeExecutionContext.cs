using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Execution;

/// <summary>
/// Node execution context, providing logging, cancellation token, and progress reporting.
/// <para>节点执行上下文，提供日志、取消令牌与进度报告。</para>
/// </summary>
public interface INodeExecutionContext
{
    Workspace.IWorkflowDocuments? Documents => null;
    /// <summary>
    /// Unified log service; all node execution logs are pushed through this.
    /// <para>统一日志服务，所有节点执行日志通过此推送。</para>
    /// </summary>
    /// <value>The log service receiving node execution output.<para>接收节点执行输出的日志服务。</para></value>
    IUnifiedLog Log { get; }

    /// <summary>
    /// Cancellation token.
    /// <para>取消令牌。</para>
    /// </summary>
    /// <value>The token signalling cancellation of the current run.<para>标志着当前运行被取消的令牌。</para></value>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Shared staging owned by the outermost execution, including nested composites.
    /// <para>由最外层执行持有的共享暂存区，包含嵌套的组合节点。</para>
    /// </summary>
    /// <value>The shared patch run, or <c>null</c> when staging is unavailable.<para>共享的补丁运行；无暂存区时为 <c>null</c>。</para></value>
    PatchRun? Patches => null;

    /// <summary>
    /// Resource scope of the currently executing composite or package.
    /// <para>当前执行的组合节点或包所占用的资源作用域。</para>
    /// </summary>
    /// <value>The package holding the resource scope, or <c>null</c> when none applies.<para>持有资源作用域的包；不适用时为 <c>null</c>。</para></value>
    VisualPluginPackage? Package => null;

    /// <summary>
    /// Interaction boundary of the running execution; noninteractive contexts reject requests explicitly.
    /// <para>当前执行的交互边界；非交互上下文会显式拒绝请求。</para>
    /// </summary>
    /// <value>The interaction service the execution relies on.<para>执行过程所依赖的交互服务。</para></value>
    IWorkflowInteractionService Interactions => UnsupportedWorkflowInteractionService.Instance;

    /// <summary>
    /// Reports a user wait without blocking the thread running the execution.
    /// <para>上报一次等待用户操作，且不阻塞执行所在线程。</para>
    /// </summary>
    /// <returns>A scope that must be disposed once the user interaction ends.<para>用户交互结束后必须释放的作用域。</para></returns>
    IDisposable BeginUserInteractionWait() => new InteractionWaitScope(null);

    /// <summary>
    /// Transfers a runtime handle to the current invocation for unconditional cleanup.
    /// <para>把运行时句柄交给当前调用，以便无条件清理。</para>
    /// </summary>
    /// <param name="resource">The disposable handle to clean up when the invocation ends.<para>调用结束时需清理的可释放句柄。</para></param>
    void RegisterResource(IAsyncDisposable resource) => throw new NotSupportedException("The execution context does not own runtime resources.");

    /// <summary>
    /// Reports the node execution progress as a fraction of the whole run.
    /// <para>按整次运行的占比上报节点执行进度。</para>
    /// </summary>
    /// <param name="progress">Fraction of the run completed by the node.<para>该节点已完成的整次运行占比。</para></param>
    void ReportProgress(double progress);
}