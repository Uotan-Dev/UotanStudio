namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// One selectable resume point in the operator window: either "run every stage" or one declared stage.
/// <para>操作员窗口中的一个可选续跑点：运行全部阶段，或某个声明阶段。</para>
/// </summary>
/// <param name="StageId">The stage to resume from, or <see langword="null"/> to run every stage.
/// <para>续跑起始阶段；为 <see langword="null"/> 时运行全部阶段。</para></param>
/// <param name="DisplayName">Caption shown in the resume selector. <para>续跑选择器中显示的标题。</para></param>
public sealed record SchemeResumeOption(string? StageId, string DisplayName)
{
    /// <summary>
    /// Whether this option runs every stage instead of resuming at one stage.
    /// <para>本选项是否运行全部阶段而非从某一阶段续跑。</para>
    /// </summary>
    public bool IsRunAll => StageId is null;
}