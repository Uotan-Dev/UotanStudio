using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Irihi.Lingua;
using UotanStudio.Locale;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Themes;

namespace UotanStudio.Services;

/// <summary>
/// Singleton facade over the Irihi.Lingua generated locale managers. Resolves bundled resource
/// keys, switches cultures across every module manager, and mirrors the active values into one
/// Avalonia <see cref="ResourceDictionary"/> so XAML <c>DynamicResource</c> lookups refresh on
/// language change without swapping merged dictionaries. Theme application stays here as well.
/// <para>基于 Irihi.Lingua 源生成语言管理器的单例门面：解析内置资源键、在所有模块管理器间切换区域性，并把当前值镜像进一个 Avalonia <see cref="ResourceDictionary"/>，使 XAML 的 <c>DynamicResource</c> 随语言切换刷新而无需替换合并字典。主题应用仍由本服务负责。</para>
/// </summary>
public sealed class LanguageService
{
    private static readonly Lazy<LanguageService> _instance =
        new(() => new LanguageService());

    /// <summary>
    /// Gets the singleton instance of <see cref="LanguageService"/>.
    /// <para>获取 <see cref="LanguageService"/> 的单例实例。</para>
    /// </summary>
    /// <value>The shared language service instance. <para>共享的语言服务实例。</para></value>
    public static LanguageService Instance => _instance.Value;

    private static readonly (ILinguaManager Manager, string[] Keys)[] ModuleKeys =
    [
        (CommonLocaleManager.Instance, LocaleKeySets.Common),
        (MenuLocaleManager.Instance, LocaleKeySets.Menu),
        (DockLocaleManager.Instance, LocaleKeySets.Dock),
        (NodeLocaleManager.Instance, LocaleKeySets.Node),
        (SettingsLocaleManager.Instance, LocaleKeySets.Settings),
        (DeviceLocaleManager.Instance, LocaleKeySets.Device),
        (InteractionLocaleManager.Instance, LocaleKeySets.Interaction),
    ];

    private readonly Dictionary<string, LinguaObservableString> _observables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ILinguaManager> _owners = new(StringComparer.Ordinal);
    private ResourceDictionary _bridge = new();
    private readonly ObservableCollection<LanguageInfo> _availableLanguages = [];

    private string _currentLanguage = FallbackCulture;

    /// <summary>
    /// Gets the culture name of the current language.
    /// <para>获取当前语言的区域性名称。</para>
    /// </summary>
    /// <value>
    /// The culture name of the current language, defaults to "en-US".
    /// <para>当前语言的区域性名称，默认为 "en-US"。</para>
    /// </value>
    public string CurrentLanguage => _currentLanguage;

    /// <summary>
    /// Gets the available languages: the system-follow option plus each bundled culture.
    /// <para>获取可用语言列表：跟随系统选项与每个内置语言。</para>
    /// </summary>
    /// <value>
    /// A read-only collection containing all available language information.
    /// <para>包含所有可用语言信息的只读集合。</para>
    /// </value>
    public ReadOnlyObservableCollection<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// Gets the currently applied theme key name.
    /// <para>获取当前应用的主题键名。</para>
    /// </summary>
    /// <value>
    /// The current theme key name, defaults to "LightColors".
    /// <para>当前主题键名，默认为 "LightColors"。</para>
    /// </value>
    public string CurrentTheme { get; private set; } = "LightColors";

    /// <summary>
    /// Event raised when the application language has changed.
    /// <para>当应用程序语言发生更改时触发的事件。</para>
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Static event raised when the application theme has changed.
    /// <para>当应用程序主题发生更改时触发的静态事件。</para>
    /// </summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Gets the culture name of the default fallback language, which matches the invariant
    /// (base) resource file the generator embeds.
    /// <para>获取默认回退语言的区域性名称，对应生成器内嵌的不变（基线）资源文件。</para>
    /// </summary>
    private const string FallbackCulture = "en-US";

    private LanguageService()
    {
        AvailableLanguages = new ReadOnlyObservableCollection<LanguageInfo>(_availableLanguages);
        _availableLanguages.Add(LanguageInfo.SystemLanguage);
        _availableLanguages.Add(new LanguageInfo("en-US", "English", "English", isBuiltIn: true));
        _availableLanguages.Add(new LanguageInfo("zh-CN", "简体中文", "Chinese (Simplified)", isBuiltIn: true));

        foreach (var (manager, keys) in ModuleKeys)
        {
            foreach (var key in keys)
            {
                // Keys are globally unique across modules; the module that owns the key answers.
                if (manager.GetObservable(key) is LinguaObservableString observable)
                {
                    _observables[key] = observable;
                    _owners[key] = manager;
                }
            }
        }
    }

    /// <summary>
    /// Initializes the language service by attaching the locale mirror dictionary to the
    /// application resources. Safe to call repeatedly and across headless application instances.
    /// <para>初始化语言服务：把语言镜像字典挂载到应用程序资源。重复调用与跨无头应用实例调用均安全。</para>
    /// </summary>
    public void Initialize() => RefreshBridge();

    /// <summary>
    /// Applies the language for the specified culture: updates every module manager, refreshes the
    /// locale mirror dictionary, raises <see cref="LanguageChanged"/> and sends a
    /// <see cref="LanguageStateChangedMessage"/>.
    /// <para>应用指定语言的区域性：更新所有模块管理器、刷新语言镜像字典、触发 <see cref="LanguageChanged"/> 并发送 <see cref="LanguageStateChangedMessage"/>。</para>
    /// </summary>
    /// <param name="language">
    /// The culture name to apply, such as "en-US" or "zh-CN". Unknown cultures fall back through
    /// the parent-culture chain to the bundled English resources.
    /// <para>要应用的区域性名称，如 "en-US" 或 "zh-CN"。未知区域性按父链回退到内置英文资源。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="language"/> is null.
    /// <para>当 <paramref name="language"/> 为 null 时抛出。</para>
    /// </exception>
    public void ApplyLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var previousLanguage = _currentLanguage;
        var culture = ResolveCulture(language);

        foreach (var (manager, _) in ModuleKeys)
            manager.UpdateCulture(culture);

        _currentLanguage = language;
        RefreshBridge();

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        WeakReferenceMessenger.Default.Send(new LanguageStateChangedMessage(language, previousLanguage));
    }

    /// <summary>
    /// Gets the localized string for the specified resource key.
    /// Returns the key itself if the corresponding resource is not found.
    /// <para>根据资源键获取本地化字符串。如果未找到对应资源，则返回键名本身。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键名。</para></param>
    /// <returns>
    /// The localized string associated with the specified key, or the key itself if not found.
    /// <para>与指定键关联的本地化字符串；如果未找到则返回键名本身。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> is null.
    /// <para>当 <paramref name="key"/> 为 null 时抛出。</para>
    /// </exception>
    public string GetString(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_observables.TryGetValue(key, out var observable))
            return observable.CurrentValue ?? key;

        Debug.WriteLine($"[LanguageService] Resource key not found: {key}");
        return key;
    }

    /// <summary>
    /// Gets the bundled English (invariant base) value for a key, independent of the active
    /// language; used as the stable fallback label for property rows and node metadata.
    /// <para>获取键对应的内置英文（不变基线）值，不受当前语言影响；用作属性行与节点元数据的稳定回退标签。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键名。</para></param>
    /// <returns>The English value, or <paramref name="key"/> itself when unknown.
    /// <para>英文值；键未知时返回键名本身。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> is null.
    /// <para>当 <paramref name="key"/> 为 null 时抛出。</para>
    /// </exception>
    public string GetEnglish(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_observables.TryGetValue(key, out var observable) &&
            _owners.TryGetValue(key, out var owner) &&
            owner.GetTranslations(observable).TryGetValue(CultureInfo.InvariantCulture, out var english) &&
            !string.IsNullOrEmpty(english))
        {
            return english;
        }

        return key;
    }

    /// <summary>
    /// Gets every bundled translation of a key across the available cultures, used to build
    /// bilingual search indexes without changing the active application language.
    /// <para>获取键在所有内置语言下的翻译值，用于在不改变当前应用语言的情况下构建双语搜索索引。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键名。</para></param>
    /// <returns>The distinct translation values; empty when the key is unknown.
    /// <para>去重后的各语言值序列；键未知时为空。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> is null.
    /// <para>当 <paramref name="key"/> 为 null 时抛出。</para>
    /// </exception>
    public IReadOnlyList<string> GetAllTranslations(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_observables.TryGetValue(key, out var observable) &&
            _owners.TryGetValue(key, out var owner))
        {
            var values = new List<string>();
            foreach (var value in owner.GetTranslations(observable).Values)
            {
                if (!string.IsNullOrEmpty(value) && !values.Contains(value, StringComparer.Ordinal))
                    values.Add(value);
            }

            return values;
        }

        return [];
    }

    /// <summary>
    /// Gets the value of a key in one explicit culture, following the same parent-culture chain
    /// the generator uses (exact culture, then parents, then the invariant base) without changing
    /// the active application language.
    /// <para>按显式区域性获取键值，遵循生成器相同的父区域性链（精确匹配、逐级父级、最后回退到不变基线），不改变当前应用语言。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键名。</para></param>
    /// <param name="culture">The culture to read from. <para>要读取的区域性。</para></param>
    /// <returns>The culture value, or <paramref name="key"/> itself when the key is unknown.
    /// <para>该区域性下的值；键未知时返回键名本身。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> or <paramref name="culture"/> is null.
    /// <para>当 <paramref name="key"/> 或 <paramref name="culture"/> 为 null 时抛出。</para>
    /// </exception>
    public string GetTranslation(string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(culture);

        if (_observables.TryGetValue(key, out var observable) &&
            _owners.TryGetValue(key, out var owner))
        {
            var translations = owner.GetTranslations(observable);
            var candidate = culture;
            for (var depth = 0; depth < 10; depth++)
            {
                if (translations.TryGetValue(candidate, out var exact) && !string.IsNullOrEmpty(exact))
                    return exact;

                var parent = candidate.Parent;
                if (parent.Equals(candidate) || string.IsNullOrEmpty(parent.Name))
                    break;

                candidate = parent;
            }

            if (translations.TryGetValue(CultureInfo.InvariantCulture, out var invariant) && !string.IsNullOrEmpty(invariant))
                return invariant;
        }

        return key;
    }

    /// <summary>
    /// Gets the localized string for the specified resource key and formats it with the provided
    /// arguments using the template of the active language.
    /// <para>根据资源键获取本地化字符串，并用当前语言的模板与给定参数格式化。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键名。</para></param>
    /// <param name="args">An array of arguments to format the string with. <para>用于格式化字符串的参数数组。</para></param>
    /// <returns>
    /// The formatted localized string, the key itself if not found, or the unformatted string if
    /// formatting fails.
    /// <para>格式化后的本地化字符串；未找到资源时返回键名本身；格式化失败时返回未格式化的字符串。</para>
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
        catch (IndexOutOfRangeException)
        {
            return format;
        }
    }

    /// <summary>
    /// Determines whether the key exists in a bundled locale module.
    /// <para>判断键是否存在于内置语言模块中。</para>
    /// </summary>
    /// <param name="key">The resource key to probe. <para>要探测的资源键名。</para></param>
    /// <returns>True when the key resolves to a generated observable. <para>键可解析到生成器可观测值时为 true。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> is null.
    /// <para>当 <paramref name="key"/> 为 null 时抛出。</para>
    /// </exception>
    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _observables.ContainsKey(key);
    }

    /// <summary>
    /// Initializes the application language from the current system UI culture. Chinese systems
    /// select zh-CN; every other culture falls back to the bundled English resources.
    /// <para>按系统当前 UI 区域性初始化应用语言。中文系统选择 zh-CN，其余区域性回退到内置英文资源。</para>
    /// </summary>
    public void InitializeFromSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : FallbackCulture;
        ApplyLanguage(language);
    }

    /// <summary>
    /// Applies the specified theme style, replacing the theme resource dictionary in MergedDictionaries.
    /// <para>应用指定的主题样式，替换 MergedDictionaries 中的主题资源字典。</para>
    /// </summary>
    /// <param name="themeKey">
    /// The theme resource key name, such as "LightColors" or "DarkColors".
    /// <para>主题资源键名，如 "LightColors" 或 "DarkColors"。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="themeKey"/> is null.
    /// <para>当 <paramref name="themeKey"/> 为 null 时抛出。</para>
    /// </exception>
    public void ApplyTheme(string themeKey)
    {
        ArgumentNullException.ThrowIfNull(themeKey);

        IResourceProvider resourceInclude = themeKey == "DarkColors"
            ? new DarkColors()
            : new LightColors();

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        var themeIndex = -1;
        for (var i = 0; i < mergedDicts.Count; i++)
        {
            if (mergedDicts[i] is LightColors or DarkColors
                || (mergedDicts[i] is ResourceInclude ri && ri.Source?.OriginalString.Contains("Themes") == true))
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

    private static CultureInfo ResolveCulture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Mirrors every active value into the locale dictionary and keeps it attached to the current
    /// application. A headless test session replaces <see cref="Application.Current"/> while the
    /// abandoned instance still owns the previous dictionary, and Avalonia enforces one parent per
    /// <see cref="ResourceDictionary"/>, so the bridge is replaced with a fresh instance whenever
    /// its owner is no longer the current application. Within one application the dictionary is
    /// detached only for the bulk value update and re-attached at its recorded index, so one
    /// culture switch fires exactly one resource-changed notification instead of one per key.
    /// <para>把所有当前语言的值镜像进语言字典，并保持其挂在当前应用上。无头测试会话会替换 <see cref="Application.Current"/>，而被弃用的实例仍持有旧字典的所有权，且 Avalonia 强制每个 <see cref="ResourceDictionary"/> 只有一个父级，因此当字典的宿主不再是当前应用时就更换一个新的桥实例。同一应用内批量赋值时先摘除、按原下标挂回，使每次语言切换只触发一次资源变更通知，而不是每键一次。</para>
    /// </summary>
    private void RefreshBridge()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;

        if (_bridge.Owner is not null && !ReferenceEquals(_bridge.Owner, app))
        {
            _bridge = new ResourceDictionary();
        }

        var index = merged.IndexOf(_bridge);
        if (index >= 0)
        {
            merged.RemoveAt(index);
        }
        else
        {
            index = merged.Count;
        }

        foreach (var (key, observable) in _observables)
            _bridge[key] = observable.CurrentValue ?? key;

        merged.Insert(index, _bridge);
    }
}