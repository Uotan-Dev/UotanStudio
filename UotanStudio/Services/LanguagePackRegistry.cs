using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using UotanStudio.Models;

namespace UotanStudio.Services;

/// <summary>
/// <para>语言包注册中心，管理语言包的注册、注销、查询，以及版本冲突解决。</para>
/// Language pack registry, managing registration, unregistration, querying of language packs, and version conflict resolution.
/// </summary>
public class LanguagePackRegistry
{
    private static readonly Lazy<LanguagePackRegistry> _instance = new(() => new LanguagePackRegistry());

    /// <summary>
    /// <para>获取 <see cref="LanguagePackRegistry"/> 的单例实例。</para>
    /// Gets the singleton instance of <see cref="LanguagePackRegistry"/>.
    /// </summary>
    public static LanguagePackRegistry Instance => _instance.Value;

    private readonly Dictionary<string, LanguagePackDescriptor> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LanguageInfo> _availableLanguages = [];

    /// <summary>
    /// <para>获取当前可用的语言列表，支持动态变更通知。</para>
    /// Gets the list of available languages, supporting dynamic change notifications.
    /// </summary>
    public ReadOnlyObservableCollection<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// <para>获取应用要求的最低语言包兼容版本。</para>
    /// Gets the minimum compatible version required by the application.
    /// </summary>
    public LanguagePackVersion MinimumCompatibleVersion { get; set; } = new(1, 0, 0);

    /// <summary>
    /// <para>当语言包列表发生变更时触发的事件。</para>
    /// Event raised when the language pack list has changed.
    /// </summary>
    public event EventHandler<LanguagePackChangedEventArgs>? LanguagePackChanged;

    /// <summary>
    /// <para>初始化 <see cref="LanguagePackRegistry"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguagePackRegistry"/> class.
    /// </summary>
    private LanguagePackRegistry()
    {
        AvailableLanguages = new ReadOnlyObservableCollection<LanguageInfo>(_availableLanguages);
    }

    /// <summary>
    /// <para>注册语言包。若已存在相同文化的语言包，则根据版本号决定是否替换。</para>
    /// Registers a language pack. If a pack with the same culture already exists, decides whether to replace based on version.
    /// </summary>
    /// <param name="pack">
    /// <para>要注册的语言包描述符。</para>
    /// The language pack descriptor to register.
    /// </param>
    /// <returns>
    /// <para>注册结果枚举，指示注册成功、替换或拒绝。</para>
    /// The registration result enum, indicating success, replacement, or rejection.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="pack"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="pack"/> is null.
    /// </exception>
    public LanguagePackRegistrationResult Register(LanguagePackDescriptor pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        // 版本兼容性校验
        if (pack.Version < MinimumCompatibleVersion)
        {
            Debug.WriteLine($"[LanguagePackRegistry] Rejected pack '{pack.CultureName}' v{pack.Version}: below minimum compatible version {MinimumCompatibleVersion}");
            return LanguagePackRegistrationResult.RejectedVersionIncompatible;
        }

        if (_packs.TryGetValue(pack.CultureName, out var existing))
        {
            if (pack.Version > existing.Version)
            {
                // 新版本更高，替换
                _packs[pack.CultureName] = pack;
                UpdateAvailableLanguagesList();
                OnLanguagePackChanged(new LanguagePackChangedEventArgs(pack.CultureName, LanguagePackChangeType.Updated));
                Debug.WriteLine($"[LanguagePackRegistry] Updated pack '{pack.CultureName}' from v{existing.Version} to v{pack.Version}");
                return LanguagePackRegistrationResult.Replaced;
            }
            else
            {
                // 新版本不高，保留现有版本
                Debug.WriteLine($"[LanguagePackRegistry] Conflict: pack '{pack.CultureName}' v{pack.Version} <= existing v{existing.Version}, keeping existing");
                return LanguagePackRegistrationResult.ConflictNotReplaced;
            }
        }

        // 新语言包
        _packs[pack.CultureName] = pack;
        UpdateAvailableLanguagesList();
        OnLanguagePackChanged(new LanguagePackChangedEventArgs(pack.CultureName, LanguagePackChangeType.Added));
        Debug.WriteLine($"[LanguagePackRegistry] Registered new pack '{pack.CultureName}' v{pack.Version}");
        return LanguagePackRegistrationResult.Registered;
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
    /// <para>如果成功注销返回 true；如果未找到该语言包返回 false。</para>
    /// True if successfully unregistered; false if the language pack was not found.
    /// </returns>
    public bool Unregister(string cultureName)
    {
        ArgumentNullException.ThrowIfNull(cultureName);

        if (_packs.Remove(cultureName))
        {
            UpdateAvailableLanguagesList();
            OnLanguagePackChanged(new LanguagePackChangedEventArgs(cultureName, LanguagePackChangeType.Removed));
            Debug.WriteLine($"[LanguagePackRegistry] Unregistered pack '{cultureName}'");
            return true;
        }

        return false;
    }

    /// <summary>
    /// <para>根据区域性名称获取已注册的语言包描述符。</para>
    /// Gets the registered language pack descriptor by culture name.
    /// </summary>
    /// <param name="cultureName">
    /// <para>区域性名称。</para>
    /// The culture name.
    /// </param>
    /// <param name="pack">
    /// <para>找到的语言包描述符；未找到时为 null。</para>
    /// The found language pack descriptor; null if not found.
    /// </param>
    /// <returns>
    /// <para>如果找到返回 true；否则返回 false。</para>
    /// True if found; otherwise, false.
    /// </returns>
    public bool TryGetPack(string cultureName, out LanguagePackDescriptor? pack)
    {
        return _packs.TryGetValue(cultureName, out pack);
    }

    /// <summary>
    /// <para>获取所有已注册语言包的描述符。</para>
    /// Gets all registered language pack descriptors.
    /// </summary>
    /// <returns>
    /// <para>所有已注册语言包描述符的只读集合。</para>
    /// A read-only collection of all registered language pack descriptors.
    /// </returns>
    public IReadOnlyCollection<LanguagePackDescriptor> GetAllPacks() => _packs.Values;

    /// <summary>
    /// <para>自动发现并注册内置语言包。扫描已知的内置语言文化，创建对应的描述符。</para>
    /// Automatically discovers and registers built-in language packs. Scans known built-in cultures and creates corresponding descriptors.
    /// </summary>
    /// <param name="baseUri">
    /// <para>语言包的基础 URI（如 "avares://UotanStudio/Locale"）。</para>
    /// The base URI for language packs (e.g., "avares://UotanStudio/Locale").
    /// </param>
    /// <param name="knownCultures">
    /// <para>已知的文化名称列表。若为 null，则使用默认的 en-US 和 zh-CN。</para>
    /// The list of known culture names. If null, uses default en-US and zh-CN.
    /// </param>
    public void DiscoverBuiltInPacks(string baseUri = "avares://UotanStudio/Locale", string[]? knownCultures = null)
    {
        var cultures = knownCultures ?? ["en-US", "zh-CN"];
        var defaultModules = new[] { "Common", "Menu", "Dock", "Node", "Settings", "Device" };

        foreach (var culture in cultures)
        {
            if (_packs.ContainsKey(culture))
                continue;

            var nativeName = culture switch
            {
                "en-US" => "English",
                "zh-CN" => "简体中文",
                _ => culture
            };
            var englishName = culture switch
            {
                "en-US" => "English",
                "zh-CN" => "Chinese (Simplified)",
                _ => culture
            };

            var descriptor = new LanguagePackDescriptor(
                culture,
                nativeName,
                englishName,
                LanguagePackVersion.Default,
                defaultModules,
                isBuiltIn: true);

            Register(descriptor);
        }
    }

    /// <summary>
    /// <para>更新可用语言列表，同步 ObservableCollection。</para>
    /// Updates the available languages list, syncing the ObservableCollection.
    /// </summary>
    private void UpdateAvailableLanguagesList()
    {
        _availableLanguages.Clear();

        // 始终在最前面添加"跟随系统"选项
        _availableLanguages.Add(LanguageInfo.SystemLanguage);

        // 按文化名称排序添加已注册语言
        foreach (var pack in _packs.Values.OrderBy(p => p.CultureName, StringComparer.OrdinalIgnoreCase))
        {
            _availableLanguages.Add(pack.ToLanguageInfo());
        }
    }

    /// <summary>
    /// <para>触发语言包变更事件。</para>
    /// Raises the language pack changed event.
    /// </summary>
    /// <param name="e">
    /// <para>事件参数。</para>
    /// The event arguments.
    /// </param>
    private void OnLanguagePackChanged(LanguagePackChangedEventArgs e)
    {
        LanguagePackChanged?.Invoke(this, e);
    }
}

/// <summary>
/// <para>语言包注册结果枚举。</para>
/// Language pack registration result enum.
/// </summary>
public enum LanguagePackRegistrationResult
{
    /// <summary>
    /// <para>注册成功（新语言包）。</para>
    /// Registered successfully (new language pack).
    /// </summary>
    Registered,

    /// <summary>
    /// <para>替换了现有语言包（版本更高）。</para>
    /// Replaced existing language pack (higher version).
    /// </summary>
    Replaced,

    /// <summary>
    /// <para>版本冲突，未替换现有语言包。</para>
    /// Version conflict, existing language pack not replaced.
    /// </summary>
    ConflictNotReplaced,

    /// <summary>
    /// <para>因版本不兼容被拒绝注册。</para>
    /// Rejected due to version incompatibility.
    /// </summary>
    RejectedVersionIncompatible
}

/// <summary>
/// <para>语言包变更类型枚举。</para>
/// Language pack change type enum.
/// </summary>
public enum LanguagePackChangeType
{
    /// <summary>
    /// <para>新增语言包。</para>
    /// Language pack added.
    /// </summary>
    Added,

    /// <summary>
    /// <para>语言包已更新（被替换）。</para>
    /// Language pack updated (replaced).
    /// </summary>
    Updated,

    /// <summary>
    /// <para>语言包已移除。</para>
    /// Language pack removed.
    /// </summary>
    Removed
}

/// <summary>
/// <para>语言包变更事件参数。</para>
/// Language pack changed event arguments.
/// </summary>
public class LanguagePackChangedEventArgs : EventArgs
{
    /// <summary>
    /// <para>获取发生变更的语言包区域性名称。</para>
    /// Gets the culture name of the changed language pack.
    /// </summary>
    public string CultureName { get; }

    /// <summary>
    /// <para>获取变更类型。</para>
    /// Gets the change type.
    /// </summary>
    public LanguagePackChangeType ChangeType { get; }

    /// <summary>
    /// <para>初始化 <see cref="LanguagePackChangedEventArgs"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguagePackChangedEventArgs"/> class.
    /// </summary>
    /// <param name="cultureName">
    /// <para>区域性名称。</para>
    /// The culture name.
    /// </param>
    /// <param name="changeType">
    /// <para>变更类型。</para>
    /// The change type.
    /// </param>
    public LanguagePackChangedEventArgs(string cultureName, LanguagePackChangeType changeType)
    {
        CultureName = cultureName;
        ChangeType = changeType;
    }
}