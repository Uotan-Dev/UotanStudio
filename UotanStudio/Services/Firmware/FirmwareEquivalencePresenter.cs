using UotanStudio.Firmware.Application.Equivalence;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>把四维度等价报告呈现为“维度名 + 状态”的本地化行。每行都由资源字典模板与两个本地化标签
/// 格式化而来，绝不在界面里拼接中英文；维度未被评估时如实使用“未评估”文案，绝不伪造等价。</para>
/// Presents the four-dimension equivalence report as localized "dimension: state" lines. Every line comes from a
/// resource-dictionary template plus two localized labels, so no view concatenates Chinese with English, and an
/// unevaluated dimension keeps the "not evaluated" wording instead of a fabricated equivalence claim.
/// </summary>
internal static class FirmwareEquivalencePresenter
{
    /// <summary>Resource key of the "{0}: {1}" line template. <para>行模板 <c>{0}: {1}</c> 的资源键。</para></summary>
    internal const string LineKey = "Image_EquivalenceLine";

    /// <summary>Resource key of the report heading carrying the entry name. <para>带条目名的报告标题资源键。</para></summary>
    internal const string HeaderKey = "Image_EquivalenceReportHeader";

    /// <summary>
    /// <para>返回等价报告标题（含条目名）。</para>
    /// Returns the equivalence report heading for one image entry.
    /// </summary>
    /// <param name="getString"><para>本地化取值委托。</para>The localized string lookup.</param>
    /// <param name="entryName"><para>镜像条目显示名。</para>The display name of the image entry.</param>
    /// <returns><para>标题文本。</para>The heading text.</returns>
    internal static string FormatHeader(Func<string, string> getString, string entryName)
        => string.Format(getString(HeaderKey), entryName);

    /// <summary>
    /// <para>按字节、结构、内容、容量的固定顺序返回四行本地化文本；维度之间永不折叠。</para>
    /// Returns four localized lines in the fixed byte, structure, content and capacity order; dimensions are never collapsed.
    /// </summary>
    /// <param name="report"><para>待呈现的等价报告。</para>The equivalence report to present.</param>
    /// <param name="getString"><para>本地化取值委托。</para>The localized string lookup.</param>
    /// <returns><para>四行维度文本。</para>The four dimension lines.</returns>
    internal static IReadOnlyList<string> FormatLines(FilesystemEquivalenceReport report, Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(getString);
        return
        [
            FormatLine(getString, "Image_EquivalenceDimensionByte", report.ByteEquivalence),
            FormatLine(getString, "Image_EquivalenceDimensionStructure", report.StructureEquivalence),
            FormatLine(getString, "Image_EquivalenceDimensionContent", report.ContentEquivalence),
            FormatLine(getString, "Image_EquivalenceDimensionCapacity", report.CapacityEquivalence),
        ];
    }

    /// <summary>
    /// <para>返回某一状态对应的本地化资源键；未被评估的状态使用“未评估”文案。</para>
    /// Returns the resource key for one equivalence state; an unevaluated dimension gets the "not evaluated" wording.
    /// </summary>
    /// <param name="state"><para>待映射的状态。</para>The state to map.</param>
    /// <returns><para>状态文本的资源键。</para>The resource key of the state wording.</returns>
    internal static string StateResourceKey(FirmwareEquivalenceState state) => state switch
    {
        FirmwareEquivalenceState.Equivalent => "Image_EquivalenceStateEquivalent",
        FirmwareEquivalenceState.NotEquivalent => "Image_EquivalenceStateNotEquivalent",
        _ => "Image_EquivalenceStateNotEvaluated",
    };

    private static string FormatLine(Func<string, string> getString, string dimensionKey, FirmwareEquivalenceState state)
        => string.Format(getString(LineKey), getString(dimensionKey), getString(StateResourceKey(state)));
}