using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace UotanStudio.Localization;

/// <summary>
/// <para>提供运行时语言切换和本地化资源访问的单例服务。</para>
/// Provides a singleton service for runtime language switching and localized resource access.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());

    /// <summary>
    /// <para>获取 LocalizationService 的单例实例。</para>
    /// Gets the singleton instance of LocalizationService.
    /// </summary>
    public static LocalizationService Instance => _instance.Value;

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    /// <summary>
    /// <para>获取或设置当前语言文化。</para>
    /// Gets or sets the current language culture.
    /// </summary>
    /// <para>设置此属性时将触发 PropertyChanged 事件，通知所有绑定更新。</para>
    /// <exception cref="ArgumentNullException">
    /// <para>当传入的 CultureInfo 为 null 时抛出。</para>
    /// Thrown when the provided CultureInfo is null.
    /// </exception>
    public CultureInfo Culture
    {
        get => _currentCulture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (_currentCulture.Name == value.Name)
                return;

            _currentCulture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            Resources.Culture = value;
            OnPropertyChanged(nameof(Culture));
            OnPropertyChanged("Item[]");
        }
    }

    /// <summary>
    /// <para>获取应用支持的所有语言文化列表。</para>
    /// Gets the list of all supported language cultures.
    /// </summary>
    public IReadOnlyList<CultureInfo> SupportedCultures { get; }

    /// <summary>
    /// <para>获取默认（回退）语言文化。</para>
    /// Gets the default (fallback) language culture.
    /// </summary>
    public CultureInfo DefaultCulture { get; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// <para>通过资源键获取当前语言的本地化字符串。</para>
    /// Gets the localized string for the specified resource key in the current language.
    /// </summary>
    /// <param name="key">
    /// <para>资源键名。</para>
    /// The resource key name.
    /// </param>
    /// <returns>
    /// <para>当前语言下对应键的翻译字符串。如果当前语言中不存在，则回退到默认语言；如果默认语言中也不存在，则返回键名本身。</para>
    /// The translated string for the key in the current language. Falls back to the default language if not found, or returns the key itself if not found in any language.
    /// </returns>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
                return key ?? string.Empty;

            // Try current culture
            var value = Resources.ResourceManager.GetString(key, _currentCulture);
            if (!string.IsNullOrEmpty(value))
                return value;

            // Fallback to default culture
            if (_currentCulture.Name != DefaultCulture.Name)
            {
                value = Resources.ResourceManager.GetString(key, DefaultCulture);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            // Return key itself as last resort
            return key;
        }
    }

    /// <summary>
    /// <para>切换当前语言到指定的语言文化。</para>
    /// Switches the current language to the specified culture.
    /// </summary>
    /// <param name="culture">
    /// <para>目标语言文化。</para>
    /// The target language culture.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <para>当传入的 CultureInfo 为 null 时抛出。</para>
    /// Thrown when the provided CultureInfo is null.
    /// </exception>
    public void SwitchLanguage(CultureInfo culture)
    {
        Culture = culture;
    }

    /// <summary>
    /// <para>根据系统语言偏好自动检测并设置初始语言。</para>
    /// Detects and sets the initial language based on the system language preference.
    /// </summary>
    public void InitializeFromSystemLanguage()
    {
        var systemCulture = CultureInfo.CurrentUICulture;
        var matchedCulture = FindBestMatch(systemCulture);
        Culture = matchedCulture;
    }

    /// <summary>
    /// <para>在支持的语言列表中查找与指定文化最佳匹配的语言。</para>
    /// Finds the best matching supported culture for the specified culture.
    /// </summary>
    /// <param name="culture">
    /// <para>要匹配的语言文化。</para>
    /// The culture to match.
    /// </param>
    /// <returns>
    /// <para>最佳匹配的支持语言文化；如果没有匹配则返回默认语言文化。</para>
    /// The best matching supported culture, or the default culture if no match is found.
    /// </returns>
    private CultureInfo FindBestMatch(CultureInfo culture)
    {
        // Exact match
        if (SupportedCultures.Any(c => c.Name == culture.Name))
            return culture;

        // Language-only match (e.g., "zh" matches "zh-CN")
        var twoLetterName = culture.TwoLetterISOLanguageName;
        var languageMatch = SupportedCultures.FirstOrDefault(c => c.TwoLetterISOLanguageName == twoLetterName);
        if (languageMatch != null)
            return languageMatch;

        // No match, use default
        return DefaultCulture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private LocalizationService()
    {
        var supported = new List<CultureInfo>
        {
            DefaultCulture, // en-US
            CultureInfo.GetCultureInfo("zh-CN"),
        };

        SupportedCultures = supported.AsReadOnly();
        Resources.Culture = DefaultCulture;
    }
}
