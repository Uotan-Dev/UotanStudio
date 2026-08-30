namespace UotanStudio.Firmware.Apk;

/// <summary>
/// <para>Android vector drawable 的结构化模型（从 AXML <c>&lt;vector&gt;</c> 解析），
/// 供应用层渲染为位图。仅承载渲染所需的最小集合：视口尺寸与路径列表。</para>
/// Structured model of an Android vector drawable (parsed from the AXML
/// <c>&lt;vector&gt;</c> root), consumed by the UI layer to render a bitmap. Carries
/// only the minimal set needed for rendering: viewport size and path list.
/// </summary>
public sealed class VectorDrawable
{
    /// <summary>
    /// <para>逻辑宽度（dp，来自 <c>android:width</c>）。</para>
    /// Logical width in dp (from <c>android:width</c>).
    /// </summary>
    public float Width { get; init; } = 24f;

    /// <summary>
    /// <para>逻辑高度（dp，来自 <c>android:height</c>）。</para>
    /// Logical height in dp (from <c>android:height</c>).
    /// </summary>
    public float Height { get; init; } = 24f;

    /// <summary>
    /// <para>视口宽度（来自 <c>android:viewportWidth</c>）。</para>
    /// Viewport width (from <c>android:viewportWidth</c>).
    /// </summary>
    public float ViewportWidth { get; init; } = 24f;

    /// <summary>
    /// <para>视口高度（来自 <c>android:viewportHeight</c>）。</para>
    /// Viewport height (from <c>android:viewportHeight</c>).
    /// </summary>
    public float ViewportHeight { get; init; } = 24f;

    /// <summary>
    /// <para>整体透明度（来自 <c>android:alpha</c>，0.0~1.0，默认 1）。</para>
    /// Overall alpha (from <c>android:alpha</c>, 0.0~1.0, default 1).
    /// </summary>
    public float Alpha { get; init; } = 1f;

    /// <summary>
    /// <para>路径列表（<c>&lt;path&gt;</c> 子元素）。</para>
    /// The path list (<c>&lt;path&gt;</c> children).
    /// </summary>
    public IReadOnlyList<VectorPath> Paths { get; init; } = [];
}

/// <summary>
/// <para>Vector drawable 中的一条路径（<c>&lt;path&gt;</c> 元素）。</para>
/// A single path (<c>&lt;path&gt;</c> element) inside a vector drawable.
/// </summary>
public sealed class VectorPath
{
    /// <summary>
    /// <para>SVG 路径数据（<c>android:pathData</c>）。</para>
    /// SVG path data (<c>android:pathData</c>).
    /// </summary>
    public string PathData { get; init; } = string.Empty;

    /// <summary>
    /// <para>填充颜色（ARGB）；无填充为 <c>null</c>。</para>
    /// Fill color (ARGB); <c>null</c> when no fill.
    /// </summary>
    public uint? FillColor { get; init; }

    /// <summary>
    /// <para>填充透明度（0.0~1.0，默认 1）。</para>
    /// Fill alpha (0.0~1.0, default 1).
    /// </summary>
    public float FillAlpha { get; init; } = 1f;

    /// <summary>
    /// <para>描边颜色（ARGB）；无描边为 <c>null</c>。</para>
    /// Stroke color (ARGB); <c>null</c> when no stroke.
    /// </summary>
    public uint? StrokeColor { get; init; }

    /// <summary>
    /// <para>描边宽度（视口单位，来自 <c>android:strokeWidth</c>）。</para>
    /// Stroke width in viewport units (from <c>android:strokeWidth</c>).
    /// </summary>
    public float StrokeWidth { get; init; }

    /// <summary>
    /// <para>描边透明度（0.0~1.0，默认 1）。</para>
    /// Stroke alpha (0.0~1.0, default 1).
    /// </summary>
    public float StrokeAlpha { get; init; } = 1f;
}
