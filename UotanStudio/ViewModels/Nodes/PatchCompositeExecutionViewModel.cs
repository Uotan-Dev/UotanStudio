using UotanStudio.Services;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Localized invocation evidence, separate from transaction publication.</summary>
public sealed record PatchCompositeExecutionViewModel(PatchCompositeExecution Execution)
{
    /// <summary>Package and invocation identity.</summary>
    public string Identity => $"#{Execution.Invocation} {Execution.Name} ({Execution.PackageId}, {Execution.Version})";

    /// <summary>Localized execution status.</summary>
    public string Status => LanguageService.Instance.GetString(Execution.SkipReason is { } reason
        ? "Node_CompositeSkip_" + reason : "Node_CompositeStatus_" + Execution.Status);

    /// <summary>Localized enclosing invocation identity.</summary>
    public string Parent => Execution.ParentInvocation is { } parent
        ? LanguageService.Instance.GetString("Node_Patch_CompositeParent", parent)
        : LanguageService.Instance.GetString("Node_Patch_CompositeRoot");

    /// <summary>Bounded failure detail, if any.</summary>
    public string Error => Execution.Error ?? string.Empty;

    /// <summary>Preflight observations including descendants; mixed states stay explicit.</summary>
    public string Applicability
    {
        get
        {
            var states = Enum.GetValues<CompositeApplicability>()
                .Where(state => state != CompositeApplicability.None && Execution.Applicability.HasFlag(state))
                .Select(state => LanguageService.Instance.GetString("Node_CompositeApplicability_" + state));
            return Execution.Applicability == CompositeApplicability.None
                ? LanguageService.Instance.GetString("Node_CompositeApplicability_None")
                : LanguageService.Instance.GetString("Node_CompositeApplicability_Observed", string.Join(" / ", states));
        }
    }
}