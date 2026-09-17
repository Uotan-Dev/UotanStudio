using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Themes;

namespace UotanStudio.Services;

/// <summary>
/// <para>提供应用程序多语言切换与本地化字符串查询服务的单例类，支持动态语言包注册与模块化加载。</para>
/// A singleton service that provides language switching and localized string lookup for the application, supporting dynamic language pack registration and modular loading.
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
    /// <para>获取当前可用的语言列表，从 <see cref="LanguagePackRegistry"/> 动态获取。</para>
    /// Gets the list of available languages, dynamically retrieved from <see cref="LanguagePackRegistry"/>.
    /// </summary>
    /// <value>
    /// <para>包含所有可用语言信息的只读集合。</para>
    /// A read-only collection containing all available language information.
    /// </value>
    public ReadOnlyObservableCollection<LanguageInfo> AvailableLanguages =>
        LanguagePackRegistry.Instance.AvailableLanguages;

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

    /// <summary>
    /// <para>获取默认回退语言的文化名称。</para>
    /// Gets the culture name of the default fallback language.
    /// </summary>
    private const string FallbackCulture = "en-US";

    private LanguageService() { }

    /// <summary>
    /// <para>初始化语言服务，自动发现并注册内置语言包。</para>
    /// Initializes the language service, automatically discovering and registering built-in language packs.
    /// </summary>
    public void Initialize()
    {
        LanguagePackRegistry.Instance.DiscoverBuiltInPacks();
    }

    /// <summary>
    /// <para>应用指定语言的资源包，按模块加载语言资源文件，替换当前合并字典中的语言资源，并强制刷新所有 DynamicResource 绑定。</para>
    /// Applies the language pack for the specified culture, loading modular resource files, replacing the current locale resource in merged dictionaries, and forcing a refresh of all DynamicResource bindings.
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

        var previousLanguage = _currentLanguage;

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        // 找出所有旧的 Locale 资源（含早期 ResourceInclude 与当前类型实例）。
        // 先构造完整的新字典列表，再按原槽位替换，避免在切换期间让
        // DynamicResource 短暂解析到空值。逐个 Remove/Add 会触发控件树反复
        // 重新套用主题，部分控件的字体等样式值会因此丢失。
        var oldLocaleDicts = mergedDicts
            .Where(d => d is ResourceDictionary rd
                && (d is ResourceInclude ri && ri.Source?.OriginalString.Contains("Locale") == true
                    || rd.GetType().Namespace?.StartsWith("UotanStudio.Locale", StringComparison.Ordinal) == true))
            .ToList();

        // 获取该语言包的模块列表
        IReadOnlyList<string> modules;
        if (LanguagePackRegistry.Instance.TryGetPack(language, out var pack) && pack != null)
        {
            modules = pack.ModuleNames;
        }
        else
        {
            // 未注册的语言包，使用默认模块列表
            modules = ["Common", "Menu", "Dock", "Node", "Settings", "Device", "Basic"];
        }

        // 按模块加载语言资源。先全部创建完成，确保替换时不会暴露半套语言资源。
        var newLocaleDicts = new List<IResourceProvider>(modules.Count);
        foreach (var module in modules)
        {
            try
            {
                newLocaleDicts.Add(CreateLocaleDictionary(language, module));
            }
            catch (Exception ex) when (ex is ArgumentException)
            {
                // 模块文件缺失，回退到默认语言
                Debug.WriteLine($"[LanguageService] Module '{module}' not found for '{language}', falling back to '{FallbackCulture}'");
                var fallback = CreateFallbackDictionary(module);
                if (fallback is not null)
                    newLocaleDicts.Add(fallback);
            }
        }

        if (oldLocaleDicts.Count == newLocaleDicts.Count && oldLocaleDicts.Count > 0)
        {
            // 保持 MergedDictionaries 的数量、顺序和周边字典不变，只替换每个
            // Locale 槽位。这样 Avalonia 不会因空窗期重建应用级样式。
            for (var i = 0; i < oldLocaleDicts.Count; i++)
            {
                var index = mergedDicts.IndexOf(oldLocaleDicts[i]);
                if (index >= 0)
                    mergedDicts[index] = newLocaleDicts[i];
            }
        }
        else
        {
            // 首次加载或模块数量改变时，仍保留第一个 Locale 槽位的位置，
            // 并在一次变更中完成旧资源的移除和新资源的插入。
            var insertIndex = oldLocaleDicts.Count > 0
                ? mergedDicts.IndexOf(oldLocaleDicts[0])
                : mergedDicts.Count;
            foreach (var old in oldLocaleDicts)
                mergedDicts.Remove(old);
            if (insertIndex < 0 || insertIndex > mergedDicts.Count)
                insertIndex = mergedDicts.Count;
            for (var i = 0; i < newLocaleDicts.Count; i++)
                mergedDicts.Insert(insertIndex + i, newLocaleDicts[i]);
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);

        // 发送语言状态变更消息
        WeakReferenceMessenger.Default.Send(new LanguageStateChangedMessage(language, previousLanguage));
    }

    /// <summary>
    /// <para>按语言与模块名创建语言模块资源字典（AOT 安全：直接实例化带 <c>x:Class</c> 的
    /// <see cref="ResourceDictionary"/> 类型，避免运行时 <see cref="ResourceInclude"/> 反射加载）；
    /// 不支持的组合抛 <see cref="ArgumentException"/>。</para>
    /// Creates a locale module resource dictionary by culture and module name (AOT-safe: instantiates
    /// the <c>x:Class</c>'d <see cref="ResourceDictionary"/> type directly, avoiding runtime
    /// <see cref="ResourceInclude"/> reflection loading); throws <see cref="ArgumentException"/> for
    /// unsupported combinations.
    /// </summary>
    /// <param name="language">区域性名称（如 "en-US"、"zh-CN"）。<para>Culture name (e.g. "en-US", "zh-CN").</para></param>
    /// <param name="module">模块名。<para>Module name.</para></param>
    /// <returns><para>语言模块资源字典。</para>The locale module resource dictionary.</returns>
    private static IResourceProvider CreateLocaleDictionary(string language, string module)
    {
        return (language, module) switch
        {
            ("en-US", "Common") => new global::UotanStudio.Locale.EnUs.Common(),
            ("en-US", "Menu") => new global::UotanStudio.Locale.EnUs.Menu(),
            ("en-US", "Dock") => new global::UotanStudio.Locale.EnUs.Dock(),
            ("en-US", "Node") => new global::UotanStudio.Locale.EnUs.Node(),
            ("en-US", "Settings") => new global::UotanStudio.Locale.EnUs.Settings(),
            ("en-US", "Device") => new global::UotanStudio.Locale.EnUs.Device(),
            ("en-US", "Basic") => new global::UotanStudio.Locale.EnUs.Basic(),
            ("zh-CN", "Common") => new global::UotanStudio.Locale.ZhCn.Common(),
            ("zh-CN", "Menu") => new global::UotanStudio.Locale.ZhCn.Menu(),
            ("zh-CN", "Dock") => new global::UotanStudio.Locale.ZhCn.Dock(),
            ("zh-CN", "Node") => new global::UotanStudio.Locale.ZhCn.Node(),
            ("zh-CN", "Settings") => new global::UotanStudio.Locale.ZhCn.Settings(),
            ("zh-CN", "Device") => new global::UotanStudio.Locale.ZhCn.Device(),
            ("zh-CN", "Basic") => new global::UotanStudio.Locale.ZhCn.Basic(),
            _ => throw new ArgumentException($"Unsupported locale module: {language}/{module}", nameof(module)),
        };
    }

    /// <summary>
    /// <para>加载回退语言的指定模块资源。</para>
    /// Loads the specified module resource from the fallback language.
    /// </summary>
    /// <param name="mergedDicts">
    /// <para>合并字典集合。</para>
    /// The merged dictionaries collection.
    /// </param>
    /// <param name="module">
    /// <para>模块名称。</para>
    /// The module name.
    /// </param>
    private static IResourceProvider? CreateFallbackDictionary(string module)
    {
        try
        {
            return CreateLocaleDictionary(FallbackCulture, module);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LanguageService] Fallback module '{module}' also not found: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <para>注册新的语言包。注册成功后可用语言列表自动更新。</para>
    /// Registers a new language pack. The available languages list is automatically updated upon successful registration.
    /// </summary>
    /// <param name="descriptor">
    /// <para>要注册的语言包描述符。</para>
    /// The language pack descriptor to register.
    /// </param>
    /// <returns>
    /// <para>注册结果。</para>
    /// The registration result.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="descriptor"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="descriptor"/> is null.
    /// </exception>
    public LanguagePackRegistrationResult RegisterLanguagePack(LanguagePackDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var result = LanguagePackRegistry.Instance.Register(descriptor);

        // 根据注册结果发送消息
        switch (result)
        {
            case LanguagePackRegistrationResult.Replaced:
                WeakReferenceMessenger.Default.Send(new LanguagePackUpdatedMessage(descriptor.CultureName, descriptor.Version.ToString()));
                break;
            case LanguagePackRegistrationResult.RejectedVersionIncompatible:
                WeakReferenceMessenger.Default.Send(new LanguagePackRejectedMessage(
                    descriptor.CultureName,
                    $"Version {descriptor.Version} is below minimum compatible version {LanguagePackRegistry.Instance.MinimumCompatibleVersion}"));
                break;
        }

        return result;
    }

    /// <summary>
    /// <para>注销指定文化的语言包。</para>
    /// Unregisters the language pack for the specified culture.
    /// </summary>
    /// <param name="cultureName">
    /// <para>要注销的语言包区域性名称。</para>
    /// The culture name of the language pack to unregister.
    /// </param>
    /// <returns>
    /// <para>如果成功注销返回 true；否则返回 false。</para>
    /// True if successfully unregistered; otherwise, false.
    /// </returns>
    public bool UnregisterLanguagePack(string cultureName)
    {
        return LanguagePackRegistry.Instance.Unregister(cultureName);
    }

    /// <summary>
    /// <para>应用指定的主题样式，替换 MergedDictionaries 中的主题资源字典。</para>
    /// Applies the specified theme style, replacing the theme resource dictionary in MergedDictionaries.
    /// </summary>
    /// <param name="themeKey">
    /// <para>主题资源键名（如 "LightColors" 或 "DarkColors"）。</para>
    /// The theme resource key name (e.g., "LightColors", "DarkColors").
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

        var resourceInclude = themeKey == "DarkColors"
            ? (IResourceProvider)new DarkColors()
            : new LightColors();

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        int themeIndex = -1;
        for (int i = 0; i < mergedDicts.Count; i++)
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

        Debug.WriteLine($"[LanguageService] Resource key not found: {key}");
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
    /// <para>根据系统当前的区域设置自动初始化应用程序语言。优先匹配已注册的语言包，否则回退到默认语言。</para>
    /// Automatically initializes the application language based on the current system culture.
    /// Prefers matching a registered language pack; falls back to the default language otherwise.
    /// </summary>
    public void InitializeFromSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var cultureName = culture.Name;

        // 尝试精确匹配（如 "zh-CN"）
        if (LanguagePackRegistry.Instance.TryGetPack(cultureName, out _))
        {
            ApplyLanguage(cultureName);
            return;
        }

        // 尝试按两字母代码匹配（如 "zh" 匹配 "zh-CN"）
        var twoLetterCode = culture.TwoLetterISOLanguageName;
        var matchedPack = LanguagePackRegistry.Instance.GetAllPacks()
            .FirstOrDefault(p => p.CultureName.StartsWith(twoLetterCode, StringComparison.OrdinalIgnoreCase));

        if (matchedPack != null)
        {
            ApplyLanguage(matchedPack.CultureName);
            return;
        }

        // 回退到默认语言
        ApplyLanguage(FallbackCulture);
    }
}