using System.Collections.ObjectModel;
using System.Diagnostics;
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
/// A singleton service that provides language switching and localized string lookup for the application, supporting dynamic language pack registration and modular loading.
/// <para>提供应用程序多语言切换与本地化字符串查询服务的单例类，支持动态语言包注册与模块化加载。</para>
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

    private string _currentLanguage = "en-US";

    /// <summary>
    /// Gets or sets the culture name of the current language.
    /// <para>获取或设置当前语言的区域性名称。</para>
    /// </summary>
    /// <value>
    /// The culture name of the current language, defaults to "en-US".
    /// <para>当前语言的区域性名称，默认为 "en-US"。</para>
    /// </value>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set => _currentLanguage = value;
    }

    /// <summary>
    /// Gets the list of available languages, dynamically retrieved from <see cref="LanguagePackRegistry"/>.
    /// <para>获取当前可用的语言列表，从 <see cref="LanguagePackRegistry"/> 动态获取。</para>
    /// </summary>
    /// <value>
    /// A read-only collection containing all available language information.
    /// <para>包含所有可用语言信息的只读集合。</para>
    /// </value>
    public ReadOnlyObservableCollection<LanguageInfo> AvailableLanguages =>
        LanguagePackRegistry.Instance.AvailableLanguages;

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
    /// Gets the culture name of the default fallback language.
    /// <para>获取默认回退语言的文化名称。</para>
    /// </summary>
    private const string FallbackCulture = "en-US";

    private LanguageService() { }

    /// <summary>
    /// Initializes the language service, automatically discovering and registering built-in language packs.
    /// <para>初始化语言服务，自动发现并注册内置语言包。</para>
    /// </summary>
    public void Initialize()
    {
        LanguagePackRegistry.Instance.DiscoverBuiltInPacks();
    }

    /// <summary>
    /// Applies the language pack for the specified culture, loading modular resources, replacing the current
    /// locale dictionaries, and refreshing all <c>DynamicResource</c> bindings.
    /// <para>应用指定语言的资源包，按模块加载语言资源文件，替换当前合并字典中的语言资源，并强制刷新所有 DynamicResource 绑定。</para>
    /// </summary>
    /// <param name="language">
    /// The culture name to apply, such as "en-US" or "zh-CN".
    /// <para>要应用的区域性名称，如 "en-US" 或 "zh-CN"。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="language"/> is null.
    /// <para>当 <paramref name="language"/> 为 null 时抛出。</para>
    /// </exception>
    public void ApplyLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        var previousLanguage = _currentLanguage;

        var appResources = Application.Current!.Resources;
        var mergedDicts = appResources.MergedDictionaries;

        var oldLocaleDicts = mergedDicts
            .Where(d => d is ResourceDictionary rd
                && (d is ResourceInclude ri && ri.Source?.OriginalString.Contains("Locale") == true
                    || rd.GetType().Namespace?.StartsWith("UotanStudio.Locale", StringComparison.Ordinal) == true))
            .ToList();

        IReadOnlyList<string> modules;
        if (LanguagePackRegistry.Instance.TryGetPack(language, out var pack) && pack != null)
        {
            modules = pack.ModuleNames;
        }
        else
        {
            modules = ["Common", "Menu", "Dock", "Node", "Settings", "Device", "Interaction"];
        }

        var newLocaleDicts = new List<IResourceProvider>(modules.Count);
        foreach (var module in modules)
        {
            try
            {
                newLocaleDicts.Add(CreateLocaleDictionary(language, module));
            }
            catch (Exception ex) when (ex is ArgumentException)
            {
                Debug.WriteLine($"[LanguageService] Module '{module}' not found for '{language}', falling back to '{FallbackCulture}'");
                var fallback = CreateFallbackDictionary(module);
                if (fallback is not null)
                    newLocaleDicts.Add(fallback);
            }
        }

        if (oldLocaleDicts.Count == newLocaleDicts.Count && oldLocaleDicts.Count > 0)
        {
            for (var i = 0; i < oldLocaleDicts.Count; i++)
            {
                var index = mergedDicts.IndexOf(oldLocaleDicts[i]);
                if (index >= 0)
                    mergedDicts[index] = newLocaleDicts[i];
            }
        }
        else
        {
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

        WeakReferenceMessenger.Default.Send(new LanguageStateChangedMessage(language, previousLanguage));
    }

    /// <summary>
    /// Creates the locale resource dictionary for a culture and module name, throwing
    /// <see cref="ArgumentException"/> for unsupported combinations.
    /// <para>按区域性与模块名创建语言模块资源字典，组合不受支持时抛出 <see cref="ArgumentException"/>。</para>
    /// </summary>
    /// <param name="language">The culture name, such as "en-US" or "zh-CN".
    /// <para>区域性名称，如 "en-US" 或 "zh-CN"。</para></param>
    /// <param name="module">The module name. <para>模块名。</para></param>
    /// <returns>The locale module resource dictionary. <para>语言模块资源字典。</para></returns>
    private static IResourceProvider CreateLocaleDictionary(string language, string module)
    {
        return (language, module) switch
        {
            ("en-US", "Common") => new global::UotanStudio.Locale.EnUs.Common(),
            ("en-US", "Menu") => new global::UotanStudio.Locale.EnUs.Menu(),
            ("en-US", "Dock") => new global::UotanStudio.Locale.EnUs.Dock(),
            ("en-US", "Node") => new global::UotanStudio.Locale.EnUs.Node(),
            ("en-US", "Interaction") => new global::UotanStudio.Locale.EnUs.Interaction(),
            ("en-US", "Settings") => new global::UotanStudio.Locale.EnUs.Settings(),
            ("en-US", "Device") => new global::UotanStudio.Locale.EnUs.Device(),
            ("zh-CN", "Common") => new global::UotanStudio.Locale.ZhCn.Common(),
            ("zh-CN", "Menu") => new global::UotanStudio.Locale.ZhCn.Menu(),
            ("zh-CN", "Dock") => new global::UotanStudio.Locale.ZhCn.Dock(),
            ("zh-CN", "Node") => new global::UotanStudio.Locale.ZhCn.Node(),
            ("zh-CN", "Interaction") => new global::UotanStudio.Locale.ZhCn.Interaction(),
            ("zh-CN", "Settings") => new global::UotanStudio.Locale.ZhCn.Settings(),
            ("zh-CN", "Device") => new global::UotanStudio.Locale.ZhCn.Device(),
            _ => throw new ArgumentException($"Unsupported locale module: {language}/{module}", nameof(module)),
        };
    }

    /// <summary>
    /// Loads the specified module resource from the fallback language.
    /// <para>加载回退语言的指定模块资源。</para>
    /// </summary>
    /// <param name="module">
    /// The module name.
    /// <para>模块名称。</para>
    /// </param>
    /// <returns>The fallback dictionary, or <see langword="null"/> when the module is unavailable.
    /// <para>回退字典；模块不可用时为 <see langword="null"/>。</para></returns>
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
    /// Registers a new language pack. The available languages list is automatically updated upon successful registration.
    /// <para>注册新的语言包。注册成功后可用语言列表自动更新。</para>
    /// </summary>
    /// <param name="descriptor">
    /// The language pack descriptor to register.
    /// <para>要注册的语言包描述符。</para>
    /// </param>
    /// <returns>
    /// The registration result.
    /// <para>注册结果。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="descriptor"/> is null.
    /// <para>当 <paramref name="descriptor"/> 为 null 时抛出。</para>
    /// </exception>
    public LanguagePackRegistrationResult RegisterLanguagePack(LanguagePackDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var result = LanguagePackRegistry.Instance.Register(descriptor);

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
    /// Unregisters the language pack for the specified culture.
    /// <para>注销指定文化的语言包。</para>
    /// </summary>
    /// <param name="cultureName">
    /// The culture name of the language pack to unregister.
    /// <para>要注销的语言包区域性名称。</para>
    /// </param>
    /// <returns>
    /// True if successfully unregistered; otherwise, false.
    /// <para>如果成功注销返回 true；否则返回 false。</para>
    /// </returns>
    public bool UnregisterLanguagePack(string cultureName)
    {
        return LanguagePackRegistry.Instance.Unregister(cultureName);
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
    /// Gets the localized string for the specified resource key.
    /// Returns the key itself if the corresponding resource is not found.
    /// <para>根据资源键获取本地化字符串。如果未找到对应资源，则返回键名本身。</para>
    /// </summary>
    /// <param name="key">
    /// The resource key to look up.
    /// <para>要查找的资源键名。</para>
    /// </param>
    /// <returns>
    /// The localized string associated with the specified key, or the key itself if not found.
    /// <para>与指定键关联的本地化字符串；如果未找到则返回键名本身。</para>
    /// </returns>
    public string GetString(string key)
    {
        if (Application.Current?.TryFindResource(key, null, out var value) == true && value is string str)
            return str;

        Debug.WriteLine($"[LanguageService] Resource key not found: {key}");
        return key;
    }

    /// <summary>
    /// Gets the localized string for the specified resource key and formats it with the provided arguments.
    /// Returns the key itself if the corresponding resource is not found.
    /// <para>根据资源键获取本地化字符串，并使用指定参数进行格式化。如果未找到对应资源，则返回键名本身。</para>
    /// </summary>
    /// <param name="key">
    /// The resource key to look up.
    /// <para>要查找的资源键名。</para>
    /// </param>
    /// <param name="args">
    /// An array of arguments to format the string with.
    /// <para>用于格式化字符串的参数数组。</para>
    /// </param>
    /// <returns>
    /// The formatted localized string, the key itself if not found, or the unformatted string if formatting fails.
    /// <para>格式化后的本地化字符串；如果未找到资源则返回键名本身；如果格式化失败则返回未格式化的字符串。</para>
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
    /// Automatically initializes the application language based on the current system culture.
    /// Prefers matching a registered language pack; falls back to the default language otherwise.
    /// <para>根据系统当前的区域设置自动初始化应用程序语言。优先匹配已注册的语言包，否则回退到默认语言。</para>
    /// </summary>
    public void InitializeFromSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var cultureName = culture.Name;

        if (LanguagePackRegistry.Instance.TryGetPack(cultureName, out _))
        {
            ApplyLanguage(cultureName);
            return;
        }

        var twoLetterCode = culture.TwoLetterISOLanguageName;
        var matchedPack = LanguagePackRegistry.Instance.GetAllPacks()
            .FirstOrDefault(p => p.CultureName.StartsWith(twoLetterCode, StringComparison.OrdinalIgnoreCase));

        if (matchedPack != null)
        {
            ApplyLanguage(matchedPack.CultureName);
            return;
        }

        ApplyLanguage(FallbackCulture);
    }
}