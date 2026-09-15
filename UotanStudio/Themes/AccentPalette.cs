using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Semi.Avalonia;

namespace UotanStudio.Themes;

/// <summary>
/// <para>把 <c>Themes/AccentColors.axaml</c> 里定义的强调色补进 Semi 主题自身的作用域。
/// Semi 的控件主题在主题样式作用域内用 DynamicResource 解析调色板，只在应用资源字典里覆盖同名键
/// 不会影响按钮、复选框等控件；这里把同一组键写进 <see cref="SemiTheme"/> 的资源，
/// 让「改一处颜色即整体换色」对半主题控件同样成立。写入按主题变体分别进行，
/// 因此浅色与深色可以使用各自的强调色。</para>
/// Copies the accent defined in <c>Themes/AccentColors.axaml</c> into the Semi theme's own resource scope.
/// Semi's control themes resolve their palette through DynamicResource inside the theme's scope, so overriding
/// the same keys in the application dictionary does not reach buttons and other controls. Writing the same keys
/// into <see cref="SemiTheme"/>'s resources keeps a single source of truth for re-skinning. The values are
/// written per theme variant, so light and dark can use their own accent.
/// </summary>
public static class AccentPalette
{
    /// <summary>
    /// <para>需要补进 Semi 主题作用域的键：Semi 调色板键，以及按钮/分段单选的悬浮、选中状态键
    /// （Semi 用自己的按键名再引用调色板，只改调色板不足以覆盖这些状态）。值都在 <c>Themes/AccentColors.axaml</c> 中定义。</para>
    /// Keys copied into the Semi theme scope: the palette keys plus the button / segmented-radio hover and
    /// selection keys (Semi aliases those from its own key names, so the palette alone does not cover them).
    /// Their values live in <c>Themes/AccentColors.axaml</c>.
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
    /// <para>把强调色写进应用当前加载的 <see cref="SemiTheme"/> 资源；在样式加载完成后调用一次即可，
    /// 之后切换浅色/深色主题或语言都不会丢失。</para>
    /// Writes the accent into the <see cref="SemiTheme"/> resources loaded by the application. Calling it once
    /// after the styles are loaded is enough; later theme or language switches keep the values.
    /// </summary>
    /// <param name="application"><para>已加载样式与资源的应用实例。</para>The application whose styles and resources are loaded.</param>
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
    /// <para>判断 Semi 的主题变体是否属于深色系：深色、黄昏与夜空使用深色强调色，
    /// 默认、浅色与沙漠使用浅色强调色。</para>
    /// Whether a Semi theme variant is dark: Dark, Dusk and NightSky take the dark accent while Default,
    /// Light and Desert take the light one.
    /// </summary>
    private static bool IsDarkVariant(ThemeVariant variant)
    {
        var name = variant.ToString();
        return name is "Dark" or "Dusk" or "NightSky";
    }
}