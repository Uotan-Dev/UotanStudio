using UotanStudio.Firmware.Application.Equivalence;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Renders the four-dimension equivalence report as one fixed-English log line joined by a separator.
/// <para>把四维度等价报告渲染成以分隔符连接的单行固定英文日志。</para>
/// </summary>
internal static class FirmwareEquivalencePresenter
{
    private const string Heading = "Equivalence of the built image";
    private const string Separator = " | ";

    /// <summary>
    /// Returns the one-line English equivalence report: the heading, the entry name and the four dimensions in the
    /// fixed byte, structure, content and capacity order.
    /// <para>返回一行英文等价报告：标题、条目名，以及按字节、结构、内容、容量固定顺序排列的四个维度。</para>
    /// </summary>
    /// <param name="entryName">The display name of the image entry. <para>镜像条目显示名。</para></param>
    /// <param name="report">The equivalence report to present. <para>待呈现的等价报告。</para></param>
    /// <returns>The single log line, which carries no line break. <para>不含换行的单行日志文本。</para></returns>
    /// <exception cref="ArgumentNullException">Raised when either argument is <see langword="null"/>.
    /// <para>任一参数为空值时抛出。</para></exception>
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
    /// Returns the English wording of one equivalence state; an unevaluated dimension reads
    /// "not evaluated by the library" and is never written as equivalent.
    /// <para>返回某一状态的英文文案；未被评估的状态写作 not evaluated by the library，绝不写成等价。</para>
    /// </summary>
    /// <param name="state">The state to map. <para>待映射的状态。</para></param>
    /// <returns>The state wording. <para>状态文案。</para></returns>
    internal static string StateWording(FirmwareEquivalenceState state) => state switch
    {
        FirmwareEquivalenceState.Equivalent => "Equivalent",
        FirmwareEquivalenceState.NotEquivalent => "Not equivalent",
        _ => "Not evaluated by the library",
    };

    private static string FormatDimension(string dimension, FirmwareEquivalenceState state)
        => $"{dimension}: {StateWording(state)}";
}