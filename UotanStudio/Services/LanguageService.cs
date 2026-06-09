using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace UotanStudio.Services;

/// <summary>
/// <para>提供应用程序多语言切换与本地化字符串查询服务的单例类。</para>
/// A singleton service that provides language switching and localized string lookup for the application.
/// </summary>
public sealed class LanguageService
{
    private static readonly Lazy<LanguageService> _instance =
        new(() => new LanguageService());

    /// <summary>
    /// <para>获取 <see cref="LanguageService"/> 的单例实例。</para>
    /// Gets the singleton instance of <see cref="LanguageService"/>.
    /// </summary>
    public static LanguageService Instance => _instance.Value;

    /// <summary>
    /// <para>存储当前语言的区域性名称（如 "en-US"、"zh-CN"）。</para>
    /// Stores the culture name of the current language (e.g., "en-US", "zh-CN").
    /// </summary>
    private string _currentLanguage = "en-US";

    /// <summary>
    /// <para>获取或设置当前语言的区域性名称。</para>
    /// Gets or sets the culture name of the current language.
    /// </summary>
    /// <value>
    /// <para>当前语言的区域性名称，默认为 "en-US"。</para>
    /// The culture name of the current language, defaults to "en-US".
    /// </value>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set => _currentLanguage = value;
    }

    /// <summary>
    /// <para>获取应用程序支持的语言列表。</para>
    /// Gets the list of supported language culture names.
    /// </summary>
    /// <value>
    /// <para>包含所有支持语言区域性名称的只读列表。</para>
    /// A read-only list containing all supported language culture names.
    /// </value>
    public IReadOnlyList<string> SupportedLanguages { get; } =
        Array.AsReadOnly(new[] { "en-US", "zh-CN" });

    /// <summary>
    /// <para>获取当前应用的主题键名。</para>
    /// Gets the currently applied theme key name.
    /// </summary>
    /// <value>
    /// <para>当前主题键名，默认为 "LightColors"。</para>
    /// The current theme key name, defaults to "LightColors".
    /// </value>
    public string CurrentTheme { get; private set; } = "LightColors";

    /// <summary>
    /// <para>当应用程序语言发生更改时触发的事件。</para>
    /// Event raised when the application language has changed.
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// <para>当应用程序主题发生更改时触发的静态事件。</para>
    /// Static event raised when the application theme has changed.
    /// </summary>
    public static event EventHandler? ThemeChanged;

    private LanguageService() { }

    /// <summary>
    /// <para>应用指定语言的资源包，替换当前合并字典中的语言资源，并强制刷新所有 DynamicResource 绑定。</para>
    /// Applies the language pack for the specified culture, replaces the current locale resource in merged dictionaries,
    /// and forces a refresh of all DynamicResource bindings.
    /// </summary>
    /// <param name="language">
    /// <para>要应用的语言区域性名称（如 "en-US"、"zh-CN"）。</para>
    /// The culture name of the language to apply (e.g., "en-US", "zh-CN").
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="language"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="language"/> is null.
    /// </exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "ResourceInclude loads XAML resources at runtime which requires reflection.")]
    public void ApplyLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var uri = new Uri($"avares://UotanStudio/Locale/{language}.axaml");
        var resourceInclude = new ResourceInclude(uri) { Source = uri };

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        IResourceProvider? oldLocale = null;
        foreach (var dict in mergedDicts)
        {
            if (dict is ResourceInclude ri && ri.Source?.OriginalString.Contains("Locale") == true)
            {
                oldLocale = dict;
                break;
            }
        }

        if (oldLocale != null)
            mergedDicts.Remove(oldLocale);

        mergedDicts.Add(resourceInclude);

        // Force refresh DynamicResource bindings by adding and removing a temporary dictionary
        var temp = new ResourceDictionary();
        mergedDicts.Add(temp);
        mergedDicts.Remove(temp);

        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <para>应用指定的主题样式，替换 MergedDictionaries 中的主题资源字典。</para>
    /// Applies the specified theme style, replacing the theme resource dictionary in MergedDictionaries.
    /// </summary>
    /// <param name="themeKey">
    /// <para>主题资源键名（如 "LightColors" 或 "DarkColors"）。</para>
    /// The theme resource key name (e.g., "LightColors" or "DarkColors").
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="themeKey"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="themeKey"/> is null.
    /// </exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "ResourceInclude loads XAML resources at runtime which requires reflection.")]
    public void ApplyTheme(string themeKey)
    {
        ArgumentNullException.ThrowIfNull(themeKey);

        var uri = new Uri($"avares://UotanStudio/Themes/{themeKey}.axaml");
        var resourceInclude = new ResourceInclude(uri) { Source = uri };

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        int themeIndex = -1;
        for (int i = 0; i < mergedDicts.Count; i++)
        {
            if (mergedDicts[i] is ResourceInclude ri && ri.Source?.OriginalString.Contains("Themes") == true)
            {
                themeIndex = i;
                break;
            }
        }

        if (themeIndex >= 0)
        {
            mergedDicts[themeIndex] = resourceInclude;
        }
        else
        {
            mergedDicts.Add(resourceInclude);
        }

        CurrentTheme = themeKey;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <para>根据资源键获取本地化字符串。如果未找到对应资源，则返回键名本身。</para>
    /// Gets the localized string for the specified resource key.
    /// Returns the key itself if the corresponding resource is not found.
    /// </summary>
    /// <param name="key">
    /// <para>要查找的资源键名。</para>
    /// The resource key to look up.
    /// </param>
    /// <returns>
    /// <para>与指定键关联的本地化字符串；如果未找到则返回键名本身。</para>
    /// The localized string associated with the specified key, or the key itself if not found.
    /// </returns>
    public string GetString(string key)
    {
        if (Application.Current?.TryFindResource(key, null, out var value) == true && value is string str)
            return str;

        System.Diagnostics.Debug.WriteLine($"[LanguageService] Resource key not found: {key}");
        return key;
    }

    /// <summary>
    /// <para>根据资源键获取本地化字符串，并使用指定参数进行格式化。如果未找到对应资源，则返回键名本身。</para>
    /// Gets the localized string for the specified resource key and formats it with the provided arguments.
    /// Returns the key itself if the corresponding resource is not found.
    /// </summary>
    /// <param name="key">
    /// <para>要查找的资源键名。</para>
    /// The resource key to look up.
    /// </param>
    /// <param name="args">
    /// <para>用于格式化字符串的参数数组。</para>
    /// An array of arguments to format the string with.
    /// </param>
    /// <returns>
    /// <para>格式化后的本地化字符串；如果未找到资源则返回键名本身；如果格式化失败则返回未格式化的字符串。</para>
    /// The formatted localized string, the key itself if not found, or the unformatted string if formatting fails.
    /// </returns>
    public string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    /// <summary>
    /// <para>根据系统当前的区域设置自动初始化应用程序语言。如果系统语言为中文则应用 zh-CN，否则应用 en-US。</para>
    /// Automatically initializes the application language based on the current system culture.
    /// Applies zh-CN for Chinese cultures, en-US otherwise.
    /// </summary>
    public void InitializeFromSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
        ApplyLanguage(language);
    }
}
