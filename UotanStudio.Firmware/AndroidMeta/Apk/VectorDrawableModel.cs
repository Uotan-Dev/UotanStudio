namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>
/// Decoded Android vector drawable: intrinsic size in dp, viewport in raw path units, and the flattened path list.
/// <para>解析后的 Android vector drawable：以 dp 计的固有尺寸、以原始路径坐标单位计的视口，以及展平后的路径列表。</para>
/// </summary>
public sealed class VectorDrawable
{
    /// <summary>
    /// Intrinsic width in dp, decoded from a float or a complex dimension attribute.
    /// <para>固有宽度（dp），由 float 或 complex 尺寸属性解码得到。</para>
    /// </summary>
    /// <value>The dp width; the parser substitutes 24f when the attribute is absent or has an unsupported value type.
    /// <para>以 dp 计的值；属性缺失或值类型不受支持时解析器回退为 24f。</para></value>
    public float Width { get; init; } = 24f;

    /// <summary>
    /// Intrinsic height in dp, decoded from a float or a complex dimension attribute.
    /// <para>固有高度（dp），由 float 或 complex 尺寸属性解码得到。</para>
    /// </summary>
    /// <value>The dp height; the parser substitutes 24f when the attribute is absent or has an unsupported value type.
    /// <para>以 dp 计的值；属性缺失或值类型不受支持时解析器回退为 24f。</para></value>
    public float Height { get; init; } = 24f;

    /// <summary>
    /// Viewport width in raw path units that path coordinates map onto.
    /// <para>视口宽度，以 pathData 坐标所映射的原始单位计。</para>
    /// </summary>
    /// <value>The viewport width in path units; the parser substitutes 24f when the float attribute is absent.
    /// <para>以路径单位计的值；float 属性缺失时解析器回退为 24f。</para></value>
    public float ViewportWidth { get; init; } = 24f;

    /// <summary>
    /// Viewport height in raw path units that path coordinates map onto.
    /// <para>视口高度，以 pathData 坐标所映射的原始单位计。</para>
    /// </summary>
    /// <value>The viewport height in path units; the parser substitutes 24f when the float attribute is absent.
    /// <para>以路径单位计的值；float 属性缺失时解析器回退为 24f。</para></value>
    public float ViewportHeight { get; init; } = 24f;

    /// <summary>
    /// Root opacity of the whole drawable.
    /// <para>整个 drawable 的根不透明度。</para>
    /// </summary>
    /// <value>A 0-1 alpha factor; the parser substitutes 1f (fully opaque) when the float attribute is absent.
    /// <para>取值 0-1 的 alpha 系数；float 属性缺失时解析器回退为 1f（完全不透明）。</para></value>
    public float Alpha { get; init; } = 1f;

    /// <summary>
    /// Collected &lt;path&gt; elements in document order, flattened through any &lt;group&gt; nesting.
    /// <para>按文档顺序收集的 &lt;path&gt; 元素，已展平 &lt;group&gt; 嵌套。</para>
    /// </summary>
    /// <value>The path list; empty when the vector declares no path with non-blank path data.
    /// <para>路径列表；当 vector 中没有带非空白路径数据的 path 元素时为空。</para></value>
    public IReadOnlyList<VectorPath> Paths { get; init; } = [];
}

/// <summary>
/// Single vector path: an SVG-style command string plus optional fill and stroke paint.
/// <para>单条 vector 路径：SVG 风格的命令字符串，加上可选的填充与描边绘制属性。</para>
/// </summary>
public sealed class VectorPath
{
    /// <summary>
    /// SVG-style path command string (M/L/C/... commands).
    /// <para>SVG 风格的路径命令字符串（M/L/C/… 命令）。</para>
    /// </summary>
    /// <value>Non-blank path data; the parser skips paths whose attribute is missing or whitespace-only.
    /// <para>非空白路径数据；属性缺失或仅含空白字符的路径会被解析器跳过。</para></value>
    public string PathData { get; init; } = string.Empty;

    /// <summary>
    /// Fill color packed as 0xAARRGGBB; an RGB8 attribute is stored with fully opaque 0xFF alpha.
    /// <para>以 0xAARRGGBB 打包的填充色；RGB8 属性按完全不透明的 0xFF alpha 存储。</para>
    /// </summary>
    /// <value>The ARGB color, or <see langword="null"/> when the attribute is absent or not a color type (no fill).
    /// <para>ARGB 颜色；属性缺失或不是颜色类型（即不填充）时为 <see langword="null"/>。</para></value>
    public uint? FillColor { get; init; }

    /// <summary>
    /// Fill opacity factor.
    /// <para>填充不透明度系数。</para>
    /// </summary>
    /// <value>A 0-1 alpha factor; the parser substitutes 1f when the float attribute is absent.
    /// <para>取值 0-1；float 属性缺失时解析器回退为 1f。</para></value>
    public float FillAlpha { get; init; } = 1f;

    /// <summary>
    /// Stroke color packed as 0xAARRGGBB; an RGB8 attribute is stored with fully opaque 0xFF alpha.
    /// <para>以 0xAARRGGBB 打包的描边色；RGB8 属性按完全不透明的 0xFF alpha 存储。</para>
    /// </summary>
    /// <value>The ARGB color, or <see langword="null"/> when the attribute is absent or not a color type (no stroke).
    /// <para>ARGB 颜色；属性缺失或不是颜色类型（即不描边）时为 <see langword="null"/>。</para></value>
    public uint? StrokeColor { get; init; }

    /// <summary>
    /// Stroke width in dp, decoded from a float or a complex dimension attribute.
    /// <para>描边宽度（dp），由 float 或 complex 尺寸属性解码得到。</para>
    /// </summary>
    /// <value>The dp width; the parser substitutes 0f when the attribute is absent or has an unsupported value type.
    /// <para>以 dp 计的值；属性缺失或值类型不受支持时解析器回退为 0f。</para></value>
    public float StrokeWidth { get; init; }

    /// <summary>
    /// Stroke opacity factor.
    /// <para>描边不透明度系数。</para>
    /// </summary>
    /// <value>A 0-1 alpha factor; the parser substitutes 1f when the float attribute is absent.
    /// <para>取值 0-1；float 属性缺失时解析器回退为 1f。</para></value>
    public float StrokeAlpha { get; init; } = 1f;
}