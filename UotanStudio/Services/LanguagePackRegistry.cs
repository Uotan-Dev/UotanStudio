using System.Collections.ObjectModel;
using System.Diagnostics;
using UotanStudio.Models;

namespace UotanStudio.Services;

/// <summary>
/// Language pack registry, managing registration, unregistration, querying of language packs, and version conflict resolution.
/// <para>语言包注册中心，管理语言包的注册、注销、查询，以及版本冲突解决。</para>
/// </summary>
public class LanguagePackRegistry
{
    private static readonly Lazy<LanguagePackRegistry> _instance = new(() => new LanguagePackRegistry());

    /// <summary>
    /// Gets the singleton instance of <see cref="LanguagePackRegistry"/>.
    /// <para>获取 <see cref="LanguagePackRegistry"/> 的单例实例。</para>
    /// </summary>
    /// <value>The shared registry instance. <para>共享的注册中心实例。</para></value>
    public static LanguagePackRegistry Instance => _instance.Value;

    private readonly Dictionary<string, LanguagePackDescriptor> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LanguageInfo> _availableLanguages = [];

    /// <summary>
    /// Gets the list of available languages, supporting dynamic change notifications.
    /// <para>获取当前可用的语言列表，支持动态变更通知。</para>
    /// </summary>
    /// <value>The system language plus one entry per registered pack.
    /// <para>系统语言与每个已注册语言包各一条的列表。</para></value>
    public ReadOnlyObservableCollection<LanguageInfo> AvailableLanguages { get; }

    /// <summary>
    /// Gets the minimum compatible version required by the application.
    /// <para>获取应用要求的最低语言包兼容版本。</para>
    /// </summary>
    /// <value>The lowest pack version accepted by registration. <para>注册可接受的最低语言包版本。</para></value>
    public LanguagePackVersion MinimumCompatibleVersion { get; set; } = new(1, 0, 0);

    /// <summary>
    /// Event raised when the language pack list has changed.
    /// <para>当语言包列表发生变更时触发的事件。</para>
    /// </summary>
    public event EventHandler<LanguagePackChangedEventArgs>? LanguagePackChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguagePackRegistry"/> class.
    /// <para>初始化 <see cref="LanguagePackRegistry"/> 类的新实例。</para>
    /// </summary>
    private LanguagePackRegistry()
    {
        AvailableLanguages = new ReadOnlyObservableCollection<LanguageInfo>(_availableLanguages);
    }

    /// <summary>
    /// Registers a language pack. If a pack with the same culture already exists, decides whether to replace based on version.
    /// <para>注册语言包。若已存在相同文化的语言包，则根据版本号决定是否替换。</para>
    /// </summary>
    /// <param name="pack">
    /// The language pack descriptor to register.
    /// <para>要注册的语言包描述符。</para>
    /// </param>
    /// <returns>
    /// The registration result enum, indicating success, replacement, or rejection.
    /// <para>注册结果枚举，指示注册成功、替换或拒绝。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="pack"/> is null.
    /// <para>当 <paramref name="pack"/> 为 null 时抛出。</para>
    /// </exception>
    public LanguagePackRegistrationResult Register(LanguagePackDescriptor pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        if (pack.Version < MinimumCompatibleVersion)
        {
            Debug.WriteLine($"[LanguagePackRegistry] Rejected pack '{pack.CultureName}' v{pack.Version}: below minimum compatible version {MinimumCompatibleVersion}");
            return LanguagePackRegistrationResult.RejectedVersionIncompatible;
        }

        if (_packs.TryGetValue(pack.CultureName, out var existing))
        {
            if (pack.Version > existing.Version)
            {
                _packs[pack.CultureName] = pack;
                UpdateAvailableLanguagesList();
                OnLanguagePackChanged(new LanguagePackChangedEventArgs(pack.CultureName, LanguagePackChangeType.Updated));
                Debug.WriteLine($"[LanguagePackRegistry] Updated pack '{pack.CultureName}' from v{existing.Version} to v{pack.Version}");
                return LanguagePackRegistrationResult.Replaced;
            }
            else
            {
                Debug.WriteLine($"[LanguagePackRegistry] Conflict: pack '{pack.CultureName}' v{pack.Version} <= existing v{existing.Version}, keeping existing");
                return LanguagePackRegistrationResult.ConflictNotReplaced;
            }
        }

        _packs[pack.CultureName] = pack;
        UpdateAvailableLanguagesList();
        OnLanguagePackChanged(new LanguagePackChangedEventArgs(pack.CultureName, LanguagePackChangeType.Added));
        Debug.WriteLine($"[LanguagePackRegistry] Registered new pack '{pack.CultureName}' v{pack.Version}");
        return LanguagePackRegistrationResult.Registered;
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
    /// True if successfully unregistered; false if the language pack was not found.
    /// <para>如果成功注销返回 true；如果未找到该语言包返回 false。</para>
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
    /// Gets the registered language pack descriptor by culture name.
    /// <para>根据区域性名称获取已注册的语言包描述符。</para>
    /// </summary>
    /// <param name="cultureName">
    /// The culture name.
    /// <para>区域性名称。</para>
    /// </param>
    /// <param name="pack">
    /// The found language pack descriptor; null if not found.
    /// <para>找到的语言包描述符；未找到时为 null。</para>
    /// </param>
    /// <returns>
    /// True if found; otherwise, false.
    /// <para>如果找到返回 true；否则返回 false。</para>
    /// </returns>
    public bool TryGetPack(string cultureName, out LanguagePackDescriptor? pack)
    {
        return _packs.TryGetValue(cultureName, out pack);
    }

    /// <summary>
    /// Gets all registered language pack descriptors.
    /// <para>获取所有已注册语言包的描述符。</para>
    /// </summary>
    /// <returns>
    /// A read-only collection of all registered language pack descriptors.
    /// <para>所有已注册语言包描述符的只读集合。</para>
    /// </returns>
    public IReadOnlyCollection<LanguagePackDescriptor> GetAllPacks() => _packs.Values;

    /// <summary>
    /// Registers built-in language pack descriptors for the known built-in cultures; the culture and module
    /// lists are enumerated here and no URI is read.
    /// <para>为已知内置语言文化注册内置语言包描述符，文化清单与模块清单在此固定列出，不读取任何 URI。</para>
    /// </summary>
    /// <param name="knownCultures">
    /// The culture names to register, defaulting to en-US and zh-CN when null.
    /// <para>要注册的区域性名称列表，为 null 时使用默认的 en-US 与 zh-CN。</para>
    /// </param>
    public void DiscoverBuiltInPacks(string[]? knownCultures = null)
    {
        var cultures = knownCultures ?? ["en-US", "zh-CN"];
        string[] defaultModules = ["Common", "Menu", "Dock", "Node", "Settings", "Device", "Interaction"];

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
    /// Updates the available languages list, syncing the ObservableCollection.
    /// <para>更新可用语言列表，同步 ObservableCollection。</para>
    /// </summary>
    private void UpdateAvailableLanguagesList()
    {
        _availableLanguages.Clear();

        _availableLanguages.Add(LanguageInfo.SystemLanguage);

        foreach (var pack in _packs.Values.OrderBy(p => p.CultureName, StringComparer.OrdinalIgnoreCase))
        {
            _availableLanguages.Add(pack.ToLanguageInfo());
        }
    }

    /// <summary>
    /// Raises the language pack changed event.
    /// <para>触发语言包变更事件。</para>
    /// </summary>
    /// <param name="e">
    /// The event arguments.
    /// <para>事件参数。</para>
    /// </param>
    private void OnLanguagePackChanged(LanguagePackChangedEventArgs e)
    {
        LanguagePackChanged?.Invoke(this, e);
    }
}

/// <summary>
/// Language pack registration result enum.
/// <para>语言包注册结果枚举。</para>
/// </summary>
public enum LanguagePackRegistrationResult
{
    /// <summary>
    /// Registered successfully (new language pack).
    /// <para>注册成功（新语言包）。</para>
    /// </summary>
    Registered,

    /// <summary>
    /// Replaced existing language pack (higher version).
    /// <para>替换了现有语言包（版本更高）。</para>
    /// </summary>
    Replaced,

    /// <summary>
    /// Version conflict, existing language pack not replaced.
    /// <para>版本冲突，未替换现有语言包。</para>
    /// </summary>
    ConflictNotReplaced,

    /// <summary>
    /// Rejected due to version incompatibility.
    /// <para>因版本不兼容被拒绝注册。</para>
    /// </summary>
    RejectedVersionIncompatible
}

/// <summary>
/// Language pack change type enum.
/// <para>语言包变更类型枚举。</para>
/// </summary>
public enum LanguagePackChangeType
{
    /// <summary>
    /// Language pack added.
    /// <para>新增语言包。</para>
    /// </summary>
    Added,

    /// <summary>
    /// Language pack updated (replaced).
    /// <para>语言包已更新（被替换）。</para>
    /// </summary>
    Updated,

    /// <summary>
    /// Language pack removed.
    /// <para>语言包被移除。</para>
    /// </summary>
    Removed
}

/// <summary>
/// Language pack changed event arguments.
/// <para>语言包变更事件参数。</para>
/// </summary>
public class LanguagePackChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the culture name of the changed language pack.
    /// <para>获取发生变更的语言包区域性名称。</para>
    /// </summary>
    /// <value>The culture name of the pack that changed. <para>发生变更的语言包区域性名称。</para></value>
    public string CultureName { get; }

    /// <summary>
    /// Gets the change type.
    /// <para>获取变更类型。</para>
    /// </summary>
    /// <value>The kind of change that occurred. <para>所发生的变更类型。</para></value>
    public LanguagePackChangeType ChangeType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguagePackChangedEventArgs"/> class.
    /// <para>初始化 <see cref="LanguagePackChangedEventArgs"/> 类的新实例。</para>
    /// </summary>
    /// <param name="cultureName">
    /// The culture name.
    /// <para>区域性名称。</para>
    /// </param>
    /// <param name="changeType">
    /// The change type.
    /// <para>变更类型。</para>
    /// </param>
    public LanguagePackChangedEventArgs(string cultureName, LanguagePackChangeType changeType)
    {
        CultureName = cultureName;
        ChangeType = changeType;
    }
}