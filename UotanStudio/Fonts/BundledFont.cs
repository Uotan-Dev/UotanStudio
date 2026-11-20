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
    /// <summary>The family name the bundled faces declare.<para>随包字形声明的字族名。</para></summary>
    public const string FamilyName = "MiSans";

    /// <summary>The <c>fonts:</c> key that <see cref="ZstdEmbeddedFontCollection"/> registers.<para><see cref="ZstdEmbeddedFontCollection"/> 注册的 <c>fonts:</c> 键。</para></summary>
    public const string CollectionKey = "fonts:UotanStudio/MiSans";

    /// <summary>
    /// The full family reference for XAML, <see cref="Avalonia.Media.FontManagerOptions"/> and code that builds a
    /// <see cref="Avalonia.Media.FontFamily"/> by hand.
    /// <para>供 XAML、<see cref="Avalonia.Media.FontManagerOptions"/> 与手写 <see cref="Avalonia.Media.FontFamily"/>
    /// 的代码使用的完整字族引用。</para>
    /// </summary>
    public const string Reference = CollectionKey + "#" + FamilyName;
}