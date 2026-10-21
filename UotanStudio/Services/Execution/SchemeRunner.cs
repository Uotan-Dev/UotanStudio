using UotanStudio.Services.Interactions;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Execution;

/// <summary>
/// Terminal state of one scheme stage inside a run.
/// <para>一次运行中单个 Scheme 阶段的最终状态。</para>
/// </summary>
public enum SchemeStageStatus
{
    /// <summary>The stage was not reached because an earlier stage stopped the run.<para>因先前阶段停止了运行而未到达该阶段。</para></summary>
    NotRun,

    /// <summary>The stage ran and succeeded.<para>阶段已运行并成功。</para></summary>
    Executed,

    /// <summary>The stage was skipped because an earlier run already completed it.<para>因先前运行已完成该阶段而被跳过。</para></summary>
    Reused,

    /// <summary>The stage ran but did not succeed.<para>阶段已运行但未成功。</para></summary>
    Failed,

    /// <summary>The stage was cancelled through the caller token.<para>阶段经调用方令牌取消。</para></summary>
    Cancelled,

    /// <summary>A node terminated the stage workflow.<para>某个节点终止了阶段工作流。</para></summary>
    Terminated
}

/// <summary>
/// Outcome of one scheme stage, including its declared outputs and patch artifacts.
/// <para>单个 Scheme 阶段的执行结果，包含其声明的输出与补丁产物。</para>
/// </summary>
public sealed class SchemeStageResult
{
    /// <summary>
    /// Identifier of the stage this result describes.
    /// <para>本结果所描述阶段的标识符。</para>
    /// </summary>
    /// <value>The declared <see cref="SchemeStage.Id"/>.<para>声明的 <see cref="SchemeStage.Id"/>。</para></value>
    public required string StageId { get; init; }

    /// <summary>
    /// Optional declared stage name.
    /// <para>可选声明的阶段名称。</para>
    /// </summary>
    /// <value>The declared name, or <see langword="null"/> when the stage has none.
    /// <para>声明的名称；阶段未声明时为 <see langword="null"/>。</para></value>
    public string? StageName { get; init; }

    /// <summary>
    /// Declared stage kind.
    /// <para>声明的阶段类型。</para>
    /// </summary>
    /// <value>The kind declared by the stage.<para>阶段声明的类型。</para></value>
    public SchemeStageKind Kind { get; init; }

    /// <summary>
    /// Terminal state of the stage.
    /// <para>阶段的最终状态。</para>
    /// </summary>
    /// <value>One of the <see cref="SchemeStageStatus"/> values.<para><see cref="SchemeStageStatus"/> 中的一个值。</para></value>
    public SchemeStageStatus Status { get; init; }

    /// <summary>
    /// Number of nodes the stage executed.
    /// <para>阶段执行的节点数量。</para>
    /// </summary>
    /// <value>Executed node count, <c>0</c> for reused or unreached stages.<para>已执行节点数；复用或未到达的阶段为 <c>0</c>。</para></value>
    public int ExecutedNodes { get; init; }

    /// <summary>
    /// Instance ids of the stage nodes that failed.
    /// <para>阶段中执行失败的节点实例 ID。</para>
    /// </summary>
    /// <value>One entry per failed node; empty when nothing failed.<para>每个失败节点一条记录；没有失败时为空列表。</para></value>
    public IReadOnlyList<string> FailedNodes { get; init; } = [];

    /// <summary>
    /// Publication outcome of the patch effects staged by this stage.
    /// <para>本阶段暂存的补丁效果的发布结果。</para>
    /// </summary>
    /// <value>The patch run outcome for this stage.<para>本阶段的补丁运行结果。</para></value>
    public PatchRunOutcome PatchOutcome { get; init; }

    /// <summary>
    /// Patch-owned file effects of this stage.
    /// <para>本阶段由补丁持有的文件影响。</para>
    /// </summary>
    /// <value>One entry per affected path.<para>每个受影响路径一条记录。</para></value>
    public IReadOnlyList<PatchChange> PatchChanges { get; init; } = [];

    /// <summary>
    /// Paths published by this stage, retained so a resumed run keeps earlier artifacts visible.
    /// <para>本阶段发布的路径，保留后使续跑仍能看到先前产物。</para>
    /// </summary>
    /// <value>Distinct affected paths of the stage.<para>阶段中不重复的受影响路径。</para></value>
    public IReadOnlyList<string> ArtifactPaths { get; init; } = [];

    /// <summary>
    /// Values exposed by the stage through its declared output bindings.
    /// <para>阶段通过声明的输出绑定暴露的取值。</para>
    /// </summary>
    /// <value>Output id to observed value; empty for stages that expose nothing.<para>输出 ID 到观测取值；不暴露任何输出时为空字典。</para></value>
    public IReadOnlyDictionary<string, object?> Outputs { get; init; } = EmptyOutputs;

    /// <summary>
    /// Failure or contract error explaining a non-successful stage.
    /// <para>解释阶段未成功的失败或契约错误。</para>
    /// </summary>
    /// <value>The error text, or <see langword="null"/> when the stage succeeded.<para>错误文本；阶段成功时为 <see langword="null"/>。</para></value>
    public string? ErrorMessage { get; init; }

    internal static IReadOnlyDictionary<string, object?> EmptyOutputs { get; } = new Dictionary<string, object?>();
}

/// <summary>
/// Aggregate outcome of one scheme run, stage by stage.
/// <para>一次 Scheme 运行的逐阶段汇总结果。</para>
/// </summary>
public sealed class SchemeRunResult
{
    /// <summary>
    /// Overall run status.
    /// <para>整体运行状态。</para>
    /// </summary>
    /// <value><see cref="ExecutionStatus.Succeeded"/> only when every stage succeeded or was reused.
    /// <para>仅当所有阶段成功或被复用时为 <see cref="ExecutionStatus.Succeeded"/>。</para></value>
    public ExecutionStatus Status { get; init; }

    /// <summary>
    /// Per-stage results in scheme stage order, including stages that were not reached.
    /// <para>按 Scheme 阶段顺序排列的逐阶段结果，包含未到达的阶段。</para>
    /// </summary>
    /// <value>One entry per declared stage.<para>每个声明阶段一条记录。</para></value>
    public IReadOnlyList<SchemeStageResult> Stages { get; init; } = [];

    /// <summary>
    /// Identifier of the stage that stopped the run.
    /// <para>停止本次运行的阶段标识符。</para>
    /// </summary>
    /// <value>The failed stage id, or <see langword="null"/> when no stage stopped the run.
    /// <para>失败阶段 ID；没有阶段停止运行时为 <see langword="null"/>。</para></value>
    public string? FailedStageId { get; init; }

    /// <summary>
    /// Failure or contract error of the run.
    /// <para>本次运行的失败或契约错误。</para>
    /// </summary>
    /// <value>The first error text, or <see langword="null"/> when the run succeeded.
    /// <para>首个错误文本；运行成功时为 <see langword="null"/>。</para></value>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Device stages that ran; their effects are reported and never rolled back.
    /// <para>已运行的设备阶段；其影响只被报告，绝不会被回滚。</para>
    /// </summary>
    /// <value>Stage ids of device stages that executed or failed.<para>已执行或失败的设备阶段 ID。</para></value>
    public IReadOnlyList<string> DeviceStageIds { get; init; } = [];

    /// <summary>
    /// Every path published by the run, including artifacts reused from earlier runs.
    /// <para>本次运行发布的全部路径，包含从先前运行复用的产物。</para>
    /// </summary>
    /// <value>Distinct affected paths across all stages.<para>所有阶段中不重复的受影响路径。</para></value>
    public IReadOnlyList<string> ArtifactPaths { get; init; } = [];

    /// <summary>
    /// Number of stages that executed in this run.
    /// <para>本次运行实际执行的阶段数。</para>
    /// </summary>
    /// <value>Count of stages whose status is <see cref="SchemeStageStatus.Executed"/>.<para>状态为 <see cref="SchemeStageStatus.Executed"/> 的阶段数量。</para></value>
    public int ExecutedStageCount => Stages.Count(stage => stage.Status == SchemeStageStatus.Executed);
}

/// <summary>
/// Caller-supplied settings for one scheme run.
/// <para>一次 Scheme 运行的调用方设置。</para>
/// </summary>
public sealed record SchemeRunOptions
{
    /// <summary>
    /// Options with no inputs, no resume point and no project-workflow resolver.
    /// <para>不带输入、续跑点与项目工作流解析器的默认设置。</para>
    /// </summary>
    public static SchemeRunOptions Default { get; } = new();

    /// <summary>
    /// Scheme input values keyed by declared input id.
    /// <para>按声明输入 ID 索引的 Scheme 输入取值。</para>
    /// </summary>
    /// <value>Supplied values; missing keys fall back to the declared defaults.<para>已提供的取值；缺失的键回退到声明默认值。</para></value>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Discards patch artifacts instead of publishing them.
    /// <para>丢弃补丁产物而不发布。</para>
    /// </summary>
    /// <value><see langword="true"/> for a preview-only run.<para>仅预览的运行时为 <see langword="true"/>。</para></value>
    public bool Preview { get; init; }

    /// <summary>
    /// Stage id at which a resumed run starts; earlier stages are not executed again.
    /// <para>续跑开始的阶段 ID；更早的阶段不会再次执行。</para>
    /// </summary>
    /// <value>The resume stage id, or <see langword="null"/> to run every stage.<para>续跑阶段 ID；为 <see langword="null"/> 时运行全部阶段。</para></value>
    public string? ResumeFromStageId { get; init; }

    /// <summary>
    /// Results of stages completed by an earlier run, reused instead of executed again.
    /// <para>先前运行已完成的阶段结果，复用而不再次执行。</para>
    /// </summary>
    /// <value>Completed stage results; their artifacts and outputs are retained.<para>已完成的阶段结果；其产物与输出会被保留。</para></value>
    public IReadOnlyList<SchemeStageResult> CompletedStages { get; init; } = [];

    /// <summary>
    /// Resolves a <see cref="SchemeStage.ProjectWorkflowName"/> into an executable snapshot.
    /// <para>把 <see cref="SchemeStage.ProjectWorkflowName"/> 解析为可执行快照。</para>
    /// </summary>
    /// <value>The resolver, or <see langword="null"/> when the scheme only embeds graphs.<para>解析器；Scheme 仅内嵌图时为 <see langword="null"/>。</para></value>
    public Func<string, WorkflowSnapshot?>? ProjectWorkflowResolver { get; init; }

    /// <summary>
    /// Interaction service available to stage nodes.
    /// <para>阶段节点可用的交互服务。</para>
    /// </summary>
    /// <value>The interaction host; defaults to the unsupported host.<para>交互宿主；默认是不支持的宿主。</para></value>
    public IWorkflowInteractionService Interactions { get; init; } = UnsupportedWorkflowInteractionService.Instance;

    /// <summary>
    /// Workspace documents available to stage nodes.
    /// <para>阶段节点可用的工作区文档。</para>
    /// </summary>
    /// <value>The document host, or <see langword="null"/> when stage nodes cannot open documents.
    /// <para>文档宿主；阶段节点无法打开文档时为 <see langword="null"/>。</para></value>
    public IWorkflowDocuments? Documents { get; init; }
}

/// <summary>
/// Executes a scheme stage by stage, one independent engine per stage, and never rolls a device stage back.
/// <para>逐阶段执行 Scheme，每阶段使用独立引擎，并且从不对设备阶段执行回滚。</para>
/// </summary>
public sealed class SchemeRunner
{
    private readonly IUnifiedLog _log;

    /// <summary>
    /// Creates a runner writing to the supplied log.
    /// <para>创建写入给定日志的运行器。</para>
    /// </summary>
    /// <param name="log">Log service receiving stage diagnostics.<para>接收阶段诊断的日志服务。</para></param>
    public SchemeRunner(IUnifiedLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Runs the scheme stages in declared order and stops at the first failure unless the stage opts in to continuing.
    /// <para>按声明顺序运行 Scheme 各阶段；除非阶段声明继续，否则在首次失败处停止。</para>
    /// </summary>
    /// <param name="package">Scheme to run; its format version and structure must be valid.
    /// <para>要运行的 Scheme；其格式版本与结构必须有效。</para></param>
    /// <param name="options">Inputs, resume point, preview flag and stage dependencies.
    /// <para>输入、续跑点、预览标志与阶段依赖。</para></param>
    /// <param name="cancellationToken">Cancels the current stage and stops the run.<para>取消当前阶段并停止运行。</para></param>
    /// <returns>The aggregate result carrying one entry per declared stage.
    /// <para>携带每个声明阶段一条记录的总结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="package"/> carries a format version this implementation cannot read.
    /// <para><paramref name="package"/> 携带的格式版本无法被本实现读取。</para></exception>
    public async Task<SchemeRunResult> RunAsync(SchemePackage package, SchemeRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        options ??= SchemeRunOptions.Default;
        package.EnsureSupportedVersion();

        var contractErrors = package.Validate();
        if (contractErrors.Count > 0)
        {
            return Failure(string.Join(Environment.NewLine, contractErrors));
        }

        var inputError = ValidateInputs(package, options);
        if (inputError is not null)
        {
            return Failure(inputError);
        }

        var resumeIndex = 0;
        if (!string.IsNullOrWhiteSpace(options.ResumeFromStageId))
        {
            resumeIndex = package.Stages.FindIndex(stage =>
                string.Equals(stage.Id, options.ResumeFromStageId, StringComparison.Ordinal));
            if (resumeIndex < 0)
            {
                return Failure($"Resume stage '{options.ResumeFromStageId}' does not exist in scheme '{package.Id}'.");
            }
        }

        Dictionary<string, SchemeStageResult> completedById = new(StringComparer.Ordinal);
        foreach (var completed in options.CompletedStages)
        {
            if (completed.Status is SchemeStageStatus.Executed or SchemeStageStatus.Reused)
            {
                completedById[completed.StageId] = completed;
            }
        }

        Dictionary<string, object?> stageOutputs = new(StringComparer.Ordinal);
        List<SchemeStageResult> results = new(package.Stages.Count);
        string? failedStageId = null;
        var status = ExecutionStatus.Succeeded;
        var stopped = false;
        for (var index = 0; index < package.Stages.Count; index++)
        {
            var stage = package.Stages[index];
            var completed = completedById.GetValueOrDefault(stage.Id);
            if (completed is not null || index < resumeIndex)
            {
                if (completed is not null)
                {
                    results.Add(Reused(completed));
                    SeedOutputs(stageOutputs, stage.Id, completed.Outputs);
                    _log.Write(LogLevel.Info, "Scheme", $"Stage '{stage.Id}' reused from an earlier run");
                }
                else
                {
                    results.Add(SkippedByResume(stage));
                    _log.Write(LogLevel.Info, "Scheme", $"Stage '{stage.Id}' skipped by the resume point");
                }
                continue;
            }

            var stageResult = await RunStageAsync(package, stage, options, stageOutputs, cancellationToken)
                .ConfigureAwait(false);
            results.Add(stageResult);
            if (stageResult.Status == SchemeStageStatus.Executed)
            {
                SeedOutputs(stageOutputs, stage.Id, stageResult.Outputs);
                continue;
            }

            failedStageId ??= stage.Id;
            if (stageResult.Status is SchemeStageStatus.Cancelled or SchemeStageStatus.Terminated)
            {
                status = stageResult.Status == SchemeStageStatus.Cancelled
                    ? ExecutionStatus.Cancelled
                    : ExecutionStatus.Terminated;
                stopped = true;
                break;
            }

            if (stage.FailurePolicy == SchemeStageFailurePolicy.Stop)
            {
                status = ExecutionStatus.Failed;
                stopped = true;
                break;
            }

            status = ExecutionStatus.PartiallySucceeded;
            _log.Write(LogLevel.Warn, "Scheme", $"Stage '{stage.Id}' failed; continuing with the next stage");
        }

        if (stopped)
        {
            for (var index = results.Count; index < package.Stages.Count; index++)
            {
                results.Add(NotRun(package.Stages[index]));
            }
        }

        return new SchemeRunResult
        {
            Status = status,
            Stages = results,
            FailedStageId = failedStageId,
            ErrorMessage = results.Select(result => result.ErrorMessage)
                .FirstOrDefault(message => !string.IsNullOrEmpty(message)),
            DeviceStageIds = [.. results
                .Where(result => result.Kind == SchemeStageKind.Device
                    && result.Status is SchemeStageStatus.Executed or SchemeStageStatus.Failed)
                .Select(result => result.StageId)],
            ArtifactPaths = [.. results.SelectMany(result => result.ArtifactPaths).Distinct(StringComparer.Ordinal)]
        };
    }

    private async Task<SchemeStageResult> RunStageAsync(SchemePackage package, SchemeStage stage,
        SchemeRunOptions options, IReadOnlyDictionary<string, object?> previousOutputs, CancellationToken cancellationToken)
    {
        var snapshot = ResolveSnapshot(stage, options);
        if (snapshot is null)
        {
            return FailedStage(stage, $"Stage '{stage.Id}' has no embedded graph and project workflow " +
                $"'{stage.ProjectWorkflowName}' was not resolved.");
        }
        try
        {
            snapshot.EnsureSupportedVersion();
        }
        catch (InvalidDataException error)
        {
            return FailedStage(stage, error.Message);
        }

        var runtime = WorkflowRuntimeBuilder.Build(snapshot, preserveUnknown: false, restoreInstanceIds: true);
        if (runtime.Errors.Count > 0)
        {
            return FailedStage(stage, string.Join(Environment.NewLine, runtime.Errors));
        }

        var bindingError = ApplyInputs(package, stage, runtime, options.Inputs, previousOutputs);
        if (bindingError is not null)
        {
            return FailedStage(stage, bindingError);
        }

        _log.Write(LogLevel.Info, "Scheme",
            $"Stage '{stage.Id}' ({stage.Kind}) starting with {runtime.Nodes.Count} node(s)");
        var engine = new NodeExecutionEngine(_log);
        var engineResult = await engine.ExecuteAsync(runtime.Nodes, runtime.Connections, cancellationToken,
            new NodeExecutionOptions
            {
                Preview = options.Preview,
                Interactions = options.Interactions,
                Documents = options.Documents,
                FailurePolicy = ExecutionFailurePolicy.SkipDependents
            }).ConfigureAwait(false);

        var stageStatus = engineResult.Status switch
        {
            ExecutionStatus.Succeeded => SchemeStageStatus.Executed,
            ExecutionStatus.Cancelled => SchemeStageStatus.Cancelled,
            ExecutionStatus.Terminated => SchemeStageStatus.Terminated,
            _ => SchemeStageStatus.Failed
        };
        Dictionary<string, object?> outputs = new(StringComparer.Ordinal);
        string? outputError = null;
        if (stageStatus == SchemeStageStatus.Executed)
        {
            outputError = CaptureOutputs(stage, runtime, outputs);
        }
        var artifacts = engineResult.PatchChanges
            .Where(change => change.Kind != PatchChangeKind.Unchanged)
            .Select(change => change.Path)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _log.Write(stageStatus == SchemeStageStatus.Executed ? LogLevel.Info : LogLevel.Warn,
            "Scheme", $"Stage '{stage.Id}' finished with {stageStatus}");

        return new SchemeStageResult
        {
            StageId = stage.Id,
            StageName = stage.Name,
            Kind = stage.Kind,
            Status = outputError is null ? stageStatus : SchemeStageStatus.Failed,
            ExecutedNodes = engineResult.ExecutedCount,
            FailedNodes = engineResult.FailedNodes,
            PatchOutcome = engineResult.PatchOutcome,
            PatchChanges = engineResult.PatchChanges,
            ArtifactPaths = artifacts,
            Outputs = outputError is null ? outputs : SchemeStageResult.EmptyOutputs,
            ErrorMessage = outputError ?? engineResult.ErrorMessage
        };
    }

    private static WorkflowSnapshot? ResolveSnapshot(SchemeStage stage, SchemeRunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(stage.ProjectWorkflowName))
        {
            return options.ProjectWorkflowResolver?.Invoke(stage.ProjectWorkflowName);
        }
        return stage.Graph;
    }

    private static string? ValidateInputs(SchemePackage package, SchemeRunOptions options)
    {
        foreach (var definition in package.Inputs)
        {
            if (!definition.Required)
            {
                continue;
            }
            if (options.Inputs.TryGetValue(definition.Id, out var supplied) && !string.IsNullOrWhiteSpace(supplied))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(definition.Default))
            {
                continue;
            }
            return $"Scheme input '{definition.Id}' is required but no value was supplied.";
        }
        return null;
    }

    private static string? ApplyInputs(SchemePackage package, SchemeStage stage, WorkflowRuntime runtime,
        IReadOnlyDictionary<string, string> suppliedInputs, IReadOnlyDictionary<string, object?> previousOutputs)
    {
        foreach (var binding in stage.Inputs)
        {
            var value = ResolveBindingValue(package, binding, suppliedInputs, previousOutputs);
            if (value is null)
            {
                var definition = package.Inputs.FirstOrDefault(input =>
                    string.Equals(input.Id, binding.InputId, StringComparison.Ordinal));
                if (definition?.Required == true)
                {
                    return $"Stage '{stage.Id}' requires input '{binding.InputId}' but no value was supplied.";
                }
                continue;
            }
            var node = FindNode(runtime, binding.NodeInstanceId);
            if (node is null)
            {
                return $"Stage '{stage.Id}' binds a value to unknown node '{binding.NodeInstanceId}'.";
            }
            var port = WorkflowGraphValidation.ResolvePort(node.Input, binding.PortId,
                binding.PortTitle ?? binding.InputId);
            if (port is null)
            {
                return $"Stage '{stage.Id}' binds a value to an unknown input port on node '{binding.NodeInstanceId}'.";
            }
            port.InitialValue = value is string text
                ? WorkflowReconstruction.ConvertInputValue(port.PortType, text)
                : value;
        }
        return null;
    }

    private static object? ResolveBindingValue(SchemePackage package, SchemeStageInputBinding binding,
        IReadOnlyDictionary<string, string> suppliedInputs, IReadOnlyDictionary<string, object?> previousOutputs)
    {
        if (binding.Literal is { } literal)
        {
            return literal;
        }
        if (!string.IsNullOrWhiteSpace(binding.SourceStageId))
        {
            var key = OutputKey(binding.SourceStageId, binding.SourceOutput ?? binding.InputId);
            return previousOutputs.TryGetValue(key, out var sourced) ? sourced : null;
        }
        if (suppliedInputs.TryGetValue(binding.InputId, out var supplied))
        {
            return supplied;
        }
        var definition = package.Inputs.FirstOrDefault(input =>
            string.Equals(input.Id, binding.InputId, StringComparison.Ordinal));
        return definition?.Default;
    }

    private static string? CaptureOutputs(SchemeStage stage, WorkflowRuntime runtime,
        Dictionary<string, object?> outputs)
    {
        foreach (var binding in stage.Outputs)
        {
            var node = FindNode(runtime, binding.NodeInstanceId);
            if (node is null)
            {
                return $"Stage '{stage.Id}' exposes an output of unknown node '{binding.NodeInstanceId}'.";
            }
            var port = WorkflowGraphValidation.ResolvePort(node.Output, binding.PortId,
                binding.PortTitle ?? binding.OutputId);
            if (port is null)
            {
                return $"Stage '{stage.Id}' exposes an unknown output port on node '{binding.NodeInstanceId}'.";
            }
            outputs[binding.OutputId] = port.Data;
        }
        return null;
    }

    private static FlowNodeViewModel? FindNode(WorkflowRuntime runtime, string instanceId)
    {
        if (runtime.NodesBySerializedId.TryGetValue(instanceId, out var node))
        {
            return node;
        }
        return runtime.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal));
    }

    private static void SeedOutputs(Dictionary<string, object?> outputs, string stageId,
        IReadOnlyDictionary<string, object?> stageOutputs)
    {
        foreach (var (outputId, value) in stageOutputs)
        {
            outputs[OutputKey(stageId, outputId)] = value;
        }
    }

    private static string OutputKey(string stageId, string outputId) => stageId + "/" + outputId;

    private static SchemeStageResult Reused(SchemeStageResult completed) => new()
    {
        StageId = completed.StageId,
        StageName = completed.StageName,
        Kind = completed.Kind,
        Status = SchemeStageStatus.Reused,
        ExecutedNodes = completed.ExecutedNodes,
        FailedNodes = completed.FailedNodes,
        PatchOutcome = completed.PatchOutcome,
        PatchChanges = completed.PatchChanges,
        ArtifactPaths = completed.ArtifactPaths,
        Outputs = completed.Outputs
    };

    private static SchemeStageResult NotRun(SchemeStage stage) => new()
    {
        StageId = stage.Id,
        StageName = stage.Name,
        Kind = stage.Kind,
        Status = SchemeStageStatus.NotRun
    };

    private static SchemeStageResult SkippedByResume(SchemeStage stage) => new()
    {
        StageId = stage.Id,
        StageName = stage.Name,
        Kind = stage.Kind,
        Status = SchemeStageStatus.Reused
    };

    private static SchemeStageResult FailedStage(SchemeStage stage, string error) => new()
    {
        StageId = stage.Id,
        StageName = stage.Name,
        Kind = stage.Kind,
        Status = SchemeStageStatus.Failed,
        ErrorMessage = error
    };

    private static SchemeRunResult Failure(string error) => new()
    {
        Status = ExecutionStatus.Failed,
        ErrorMessage = error
    };
}