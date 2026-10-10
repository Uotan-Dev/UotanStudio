using UotanStudio.Firmware.Application.Equivalence;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>把四维度等价报告渲染成一行固定英文的统一日志文本。统一日志是诊断产物，不随界面语言切换，因此标题与
/// 四个维度用 <c> | </c> 连成单行，既不出现换行也不出现中文；库未评估的维度如实写作 not evaluated，
/// 绝不伪造等价。</para>
/// Renders the four-dimension equivalence report as a single fixed-English unified-log line. The unified log is a
/// diagnostic artifact that does not follow the interface language, so the heading and the four dimensions are
/// joined by <c> | </c> on one line with no line break and no Chinese wording, and a dimension the library never
/// evaluated reads "not evaluated" instead of a fabricated equivalence claim.
/// </summary>
internal static class FirmwareEquivalencePresenter
{
    private const string Heading = "Equivalence of the built image";
    private const string Separator = " | ";

    /// <summary>
    /// <para>返回一行英文等价报告：标题、条目名，以及按字节、结构、内容、容量固定顺序排列的四个维度。</para>
    /// Returns the one-line English equivalence report: the heading, the entry name and the four dimensions in the
    /// fixed byte, structure, content and capacity order.
    /// </summary>
    /// <param name="entryName"><para>镜像条目显示名。</para>The display name of the image entry.</param>
    /// <param name="report"><para>待呈现的等价报告。</para>The equivalence report to present.</param>
    /// <returns><para>不含换行的单行日志文本。</para>The single log line, which carries no line break.</returns>
    /// <exception cref="ArgumentNullException"><para><paramref name="entryName"/> 或 <paramref name="report"/> 为
    /// <see langword="null"/>。</para><paramref name="entryName"/> or <paramref name="report"/> is
    /// <see langword="null"/>.</exception>
    internal static string FormatLogLine(string entryName, FilesystemEquivalenceReport report)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        ArgumentNullException.ThrowIfNull(report);
        return string.Join(Separator,
            $"{Heading}: {entryName}",
            FormatDimension("Byte identity", report.ByteEquivalence),
            FormatDimension("Inode structure", report.StructureEquivalence),
            FormatDimension("File content", report.ContentEquivalence),
            FormatDimension("Capacity", report.CapacityEquivalence));
    }

    /// <summary>
    /// <para>返回某一状态的英文文案；未被评估的状态写作 not evaluated by the library，绝不写成等价。</para>
    /// Returns the English wording of one equivalence state; an unevaluated dimension reads
    /// "not evaluated by the library" and is never written as equivalent.
    /// </summary>
    /// <param name="state"><para>待映射的状态。</para>The state to map.</param>
    /// <returns><para>状态文案。</para>The state wording.</returns>
    internal static string StateWording(FirmwareEquivalenceState state) => state switch
    {
        FirmwareEquivalenceState.Equivalent => "Equivalent",
        FirmwareEquivalenceState.NotEquivalent => "Not equivalent",
        _ => "Not evaluated by the library",
    };

    private static string FormatDimension(string dimension, FirmwareEquivalenceState state)
        => $"{dimension}: {StateWording(state)}";
}