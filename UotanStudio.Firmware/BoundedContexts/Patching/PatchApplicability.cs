namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Preflight state, independent from whether any output has been published.</summary>
public enum PatchApplicability
{
    Applicable,
    AlreadyApplied,
    NotApplicable,
    Conflict
}

/// <summary>Classifies complete original/desired-state checks without treating partial matches as success.</summary>
public static class PatchApplicabilityCheck
{
    /// <summary>Incompatible targets are skipped; ambiguous or unknown compatible states are conflicts.</summary>
    public static PatchApplicability Evaluate(bool compatible, bool originalStateMatches, bool desiredStateMatches)
    {
        if (!compatible)
        {
            return PatchApplicability.NotApplicable;
        }
        if (originalStateMatches == desiredStateMatches)
        {
            return PatchApplicability.Conflict;
        }
        return desiredStateMatches ? PatchApplicability.AlreadyApplied : PatchApplicability.Applicable;
    }
}