using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Semi.Avalonia;

namespace UotanStudio.Themes;

/// <summary>
/// Mirrors accent keys into the Semi theme's own resource scope so its control themes resolve
/// them, keeping one source for re-skinning. Values are written per theme variant.
/// <para>把主题色键补进 Semi 主题自身的作用域，使其控件主题解析这些键，保持单一换色来源。
/// 写入按主题变体分别进行。</para>
/// </summary>
public static class AccentPalette
{
    /// <summary>
    /// Keys copied into the Semi theme scope: palette keys plus the hover and selection keys
    /// Semi aliases separately. Values come from the accent dictionary.
    /// <para>补进 Semi 主题作用域的键：调色板键，以及 Semi 另行引用的悬浮与选中状态键。取值来自主题色字典。</para>
    /// </summary>
    private static readonly string[] SemiAccentKeys =
    [
        "SemiColorPrimary",
        "SemiColorPrimaryPointerover",
        "SemiColorPrimaryActive",
        "SemiColorPrimaryDisabled",
        "SemiColorPrimaryLight",
        "SemiColorPrimaryLightActive",
        "SemiColorPrimaryLightPointerover",
        "SemiColorLink",
        "SemiColorFocusBorder",
        "HyperlinkButtonForeground",
        "HyperlinkButtonPointeroverForeground",
        "HyperlinkButtonPressedForeground",
        "HyperlinkButtonVisitedForeground",
        "HyperlinkButtonDisabledForeground",
        "ButtonDefaultPrimaryForeground",
        "ButtonDefaultBackground",
        "ButtonDefaultPointeroverBackground",
        "ButtonDefaultPressedBackground",
        "RadioButtonButtonCheckedBackground",
        "RadioButtonButtonCheckedForeground",
        "RadioButtonButtonUncheckedPointeroverBackground",
        "ListBoxItemPointeroverBackground",
        "ListBoxItemSelectedBackground",
        "ListBoxItemPressedBackground",
        "ListBoxItemSelectedPointeroverBackground",
        "DataGridRowPointeroverBackground",
        "DataGridRowSelectedBackground",
        "DataGridRowSelectedPointeroverBackground",
        "DataGridColumnHeaderPointeroverBackground"
    ];

    /// <summary>
    /// Writes the accent into the <see cref="SemiTheme"/> resources loaded by the application. Calling it once
    /// after the styles are loaded is enough; later theme or language switches keep the values.
    /// <para>把强调色写进应用当前加载的 <see cref="SemiTheme"/> 资源；在样式加载完成后调用一次即可，
    /// 之后切换浅色/深色主题或语言都不会丢失。</para>
    /// </summary>
    /// <param name="application">The application whose styles and resources are loaded. <para>已加载样式与资源的应用实例。</para></param>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.Styles.OfType<SemiTheme>().FirstOrDefault() is not { } semiTheme)
        {
            return;
        }

        var accent = new AccentColors();
        foreach (var entry in semiTheme.Resources.ThemeDictionaries.ToArray())
        {
            if (entry.Value is not ResourceDictionary target)
            {
                continue;
            }

            var source = IsDarkVariant(entry.Key) ? ThemeVariant.Dark : ThemeVariant.Light;
            foreach (var key in SemiAccentKeys)
            {
                if (accent.TryGetResource(key, source, out var value) && value is not null)
                {
                    target[key] = value;
                }
            }
        }
    }

    /// <summary>
    /// Whether a Semi theme variant is dark: Dark, Dusk and NightSky take the dark accent while Default,
    /// Light and Desert take the light one.
    /// <para>判断 Semi 的主题变体是否属于深色系：深色、黄昏与夜空使用深色强调色，默认、浅色与沙漠使用浅色强调色。</para>
    /// </summary>
    /// <param name="variant">The theme variant to classify. <para>待判定的主题变体。</para></param>
    /// <returns>True for the dark variants. <para>属于深色系时返回 true。</para></returns>
    private static bool IsDarkVariant(ThemeVariant variant)
    {
        var name = variant.ToString();
        return name is "Dark" or "Dusk" or "NightSky";
    }
}