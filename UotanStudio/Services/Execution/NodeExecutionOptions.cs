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
}