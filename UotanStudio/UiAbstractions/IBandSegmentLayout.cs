namespace UotanStudio.UiAbstractions;

/// <summary>
/// Band segment geometry contract: the visual partition band lays children out sequentially by width
/// ratio, with gap children allowed to shrink smaller than real segments.
/// <para>分区段几何契约：可视化分区段布局按各段的宽度占比顺序排布，间隙段允许收缩到更小的宽度。</para>
/// </summary>
public interface IBandSegmentLayout
{
    /// <summary>
    /// The fraction of the available width this segment occupies (0..1).
    /// <para>段宽度占可用总宽的比例（0..1）。</para>
    /// </summary>
    double BandWidthRatio { get; }

    /// <summary>
    /// Whether this segment is an unallocated gap.
    /// <para>是否为未分配间隙段。</para>
    /// </summary>
    bool BandIsGap { get; }
}