using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Execution;

/// <summary>Controls how the workflow engine handles a node failure.</summary>
public enum ExecutionFailurePolicy
{
    /// <summary>Record the failure and continue executing independent downstream nodes.</summary>
    Continue,

    /// <summary>Skip every downstream dependent of the failed node.</summary>
    SkipDependents,

    /// <summary>Stop the workflow as soon as the first node fails.</summary>
    FailFast
}

/// <summary>Execution policy supplied to <see cref="NodeExecutionEngine.ExecuteAsync"/>.</summary>
public sealed record NodeExecutionOptions
{
    /// <summary>Gets the failure policy. The default blocks dependents but preserves independent branches.</summary>
    public ExecutionFailurePolicy FailurePolicy { get; init; } = ExecutionFailurePolicy.SkipDependents;

    /// <summary>Executes only preview-safe nodes and discards patch effects after validation.</summary>
    public bool Preview { get; init; }

    /// <summary>Package-local resources available to this graph, overridden by nested composites.</summary>
    public VisualPluginPackage? Package { get; init; }

    /// <summary>Optional host check for the package editor's current dependency scope.</summary>
    public Func<IReadOnlyList<string>>? ValidatePackage { get; init; }

    /// <summary>Internal composite executions share their parent's staging and never publish independently.</summary>
    internal PatchRun? SharedPatches { get; init; }
}