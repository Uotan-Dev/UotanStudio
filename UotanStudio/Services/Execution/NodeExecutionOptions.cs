using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Execution;

/// <summary>Controls how the workflow engine handles a node failure.<para>控制工作流引擎如何处理节点失败。</para></summary>
public enum ExecutionFailurePolicy
{
    /// <summary>Record the failure and continue executing independent downstream nodes.<para>记录失败并继续执行独立的下游节点。</para></summary>
    Continue,

    /// <summary>Skip every downstream dependent of the failed node.<para>跳过失败节点的每个下游依赖。</para></summary>
    SkipDependents,

    /// <summary>Stop the workflow as soon as the first node fails.<para>首个节点失败即停止工作流。</para></summary>
    FailFast
}

/// <summary>Execution policy supplied to <see cref="NodeExecutionEngine.ExecuteAsync"/>.<para>供给执行引擎的执行策略。</para></summary>
public sealed record NodeExecutionOptions
{
    public Workspace.IWorkflowDocuments? Documents { get; init; }
    /// <summary>Composite entry node activation; inactive-only descendants are skipped before execution.<para>复合入口节点激活状态；仅未激活的后代在执行前被跳过。</para></summary>
    /// <value>Activation map keyed by node identifier.<para>按节点标识索引的激活映射。</para></value>
    internal IReadOnlyDictionary<string, bool>? EntryActivation { get; init; }
    /// <summary>Declarative interaction service available to this execution.<para>本次执行可用的声明式交互服务。</para></summary>
    /// <value>The interaction service instance.<para>交互服务实例。</para></value>
    public IWorkflowInteractionService Interactions { get; init; } = UnsupportedWorkflowInteractionService.Instance;

    /// <summary>Gets the failure policy. The default blocks dependents but preserves independent branches.<para>获取失败策略，默认阻断依赖但保留独立分支。</para></summary>
    /// <value>The configured failure policy.<para>已配置的失败策略。</para></value>
    public ExecutionFailurePolicy FailurePolicy { get; init; } = ExecutionFailurePolicy.SkipDependents;

    /// <summary>Executes only preview-safe nodes and discards patch effects after validation.<para>仅执行预览安全节点，并在校验后丢弃补丁效果。</para></summary>
    /// <value>Whether preview mode is enabled.<para>是否启用预览模式。</para></value>
    public bool Preview { get; init; }

    /// <summary>Keeps running later execution stages after a stage does not succeed; the default stops at the failed stage.
    /// <para>阶段未成功时是否继续运行后续执行阶段；默认在失败阶段停止。</para></summary>
    /// <value><see langword="true"/> to run the remaining stages anyway.<para>仍然运行剩余阶段时为 <see langword="true"/>。</para></value>
    public bool ContinueAfterStageFailure { get; init; }

    /// <summary>Package-local resources available to this graph, overridden by nested composites.<para>本图可用的包内资源，由嵌套复合节点覆盖。</para></summary>
    /// <value>The package-local resource set.<para>包内资源集合。</para></value>
    public VisualPluginPackage? Package { get; init; }

    /// <summary>Optional validation of the current dependency scope.<para>对当前依赖范围的可选校验。</para></summary>
    /// <value>Delegate returning the validated dependency names.<para>返回已校验依赖名称的委托。</para></value>
    public Func<IReadOnlyList<string>>? ValidatePackage { get; init; }

    /// <summary>Internal composite executions share their parent's staging and never publish independently.<para>内部复合执行共享父级暂存，从不独立发布。</para></summary>
    /// <value>The shared patch run, or null for an independent execution.<para>共享补丁运行，独立执行为空。</para></value>
    internal PatchRun? SharedPatches { get; init; }
}