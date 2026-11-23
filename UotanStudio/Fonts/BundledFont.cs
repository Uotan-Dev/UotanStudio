namespace UotanStudio.Fonts;

/// <summary>
/// Identifiers of the MiSans faces that ship in the <c>fonts</c> asset bundle. The whole UI resolves its text
/// through <see cref="Reference"/>: the Linux build links a Skia without fontconfig, so the system font
/// collection is empty and every family name has to end at these bundled faces.
/// <para>随包 <c>fonts</c> 资源包中 MiSans 字形的标识。整个界面都经 <see cref="Reference"/> 解析文本：Linux 构建链接的
/// Skia 不带 fontconfig，系统字体集合为空，任何字族名最终都必须落到这些随包字形上。</para>
/// </summary>
public static class BundledFont
{
    /// <summary>
    /// The family name the bundled variable face declares in its own <c>name</c> table. It is used verbatim so
    /// family resolution never depends on a registered alias.
    /// <para>随包可变字形在其 <c>name</c> 表中声明的字族名。这里原样使用，使字族解析不依赖任何注册别名。</para>
    /// </summary>
    public const string FamilyName = "MiSans VF";

    /// <summary>The <c>fonts:</c> key that <see cref="ZstdEmbeddedFontCollection"/> registers.<para><see cref="ZstdEmbeddedFontCollection"/> 注册的 <c>fonts:</c> 键。</para></summary>
    public const string CollectionKey = "fonts:UotanStudio/MiSansVF";

    /// <summary>
    /// The full family reference for XAML, <see cref="Avalonia.Media.FontManagerOptions"/> and code that builds a
    /// <see cref="Avalonia.Media.FontFamily"/> by hand.
    /// <para>供 XAML、<see cref="Avalonia.Media.FontManagerOptions"/> 与手写 <see cref="Avalonia.Media.FontFamily"/>
    /// 的代码使用的完整字族引用。</para>
    /// </summary>
    public const string Reference = CollectionKey + "#" + FamilyName;

    /// <summary>
    /// The lightest coordinate of the bundled variable face's <c>wght</c> axis; requests below it clamp here.
    /// <para>随包可变字形 <c>wght</c> 轴的最轻坐标；更轻的请求会被夹取到该值。</para>
    /// </summary>
    public const int MinimumWeight = 150;

    /// <summary>
    /// The heaviest coordinate of the bundled variable face's <c>wght</c> axis; requests above it clamp here.
    /// <para>随包可变字形 <c>wght</c> 轴的最重坐标；更重的请求会被夹取到该值。</para>
    /// </summary>
    public const int MaximumWeight = 700;
}