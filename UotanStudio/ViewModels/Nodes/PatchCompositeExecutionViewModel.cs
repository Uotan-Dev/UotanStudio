using UotanStudio.Services;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Localized invocation evidence, separate from transaction publication.<para>本地化的调用证据，与事务发布相分离。</para></summary>
/// <param name="Execution">The immutable composite execution supplying the evidence.<para>提供证据的不可变组合执行记录。</para></param>
public sealed record PatchCompositeExecutionViewModel(PatchCompositeExecution Execution)
{
    /// <summary>Package and invocation identity.<para>软件包与调用标识。</para></summary>
    /// <value>The formatted identity line.<para>格式化后的标识行。</para></value>
    public string Identity => $"#{Execution.Invocation} {Execution.Name} ({Execution.PackageId}, {Execution.Version})";

    /// <summary>Localized execution status.<para>本地化的执行状态。</para></summary>
    /// <value>The localized status label or skip reason.<para>本地化的状态标签或跳过原因。</para></value>
    public string Status => LanguageService.Instance.GetString(Execution.SkipReason is { } reason
        ? "Node_CompositeSkip_" + reason : "Node_CompositeStatus_" + Execution.Status);

    /// <summary>Localized enclosing invocation identity.<para>本地化的外层调用标识。</para></summary>
    /// <value>The localized parent invocation or root marker.<para>本地化的父调用或根标记。</para></value>
    public string Parent => Execution.ParentInvocation is { } parent
        ? LanguageService.Instance.GetString("Node_Patch_CompositeParent", parent)
        : LanguageService.Instance.GetString("Node_Patch_CompositeRoot");

    /// <summary>Bounded failure detail, if any.<para>有界的失败详情（如有）。</para></summary>
    /// <value>The bounded error text, or empty.<para>有界的错误文本，或为空。</para></value>
    public string Error => Execution.Error ?? string.Empty;

    /// <summary>Preflight observations including descendants; mixed states stay explicit.<para>包含后代在内的预检观察结果；混合状态保持显式。</para></summary>
    /// <value>The localized applicability observation text.<para>本地化的适用性观察文本。</para></value>
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