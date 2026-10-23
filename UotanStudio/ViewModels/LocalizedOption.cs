using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;

namespace UotanStudio.ViewModels;

/// <summary>
/// One entry of a localized item list: its display text is either resolved from a locale resource
/// key in the active language or a fixed, non-translatable label. The entry raises a change
/// notification after a language switch so item containers and combo-box selection boxes re-read it.
/// <para>本地化列表项：显示文本要么按当前语言从语言资源键解析，要么是固定的不翻译标签。
/// 语言切换后条目触发变更通知，使条目容器与下拉框选中框重新读取文本。</para>
/// </summary>
public sealed class LocalizedOption : ViewModelBase
{
    private readonly string? _resourceKey;
    private readonly string _fixedText;
    private readonly Func<string>? _textFactory;

    private LocalizedOption(string? resourceKey, string fixedText, Func<string>? textFactory = null)
    {
        _resourceKey = resourceKey;
        _fixedText = fixedText;
        _textFactory = textFactory;
    }

    /// <summary>
    /// Gets the display text in the active language.
    /// <para>获取当前语言下的显示文本。</para>
    /// </summary>
    /// <value>
    /// The factory result, the localized value, or the fixed label when the entry carries neither a
    /// factory nor a resource key.
    /// <para>工厂结果、本地化值；条目既没有工厂也没有资源键时为固定标签。</para>
    /// </value>
    public string DisplayName => _textFactory is not null
        ? _textFactory()
        : _resourceKey is null ? _fixedText : LanguageService.Instance.GetString(_resourceKey);


    /// <summary>
    /// Creates an entry whose text is resolved from the locale resources.
    /// <para>创建文本来自语言资源的条目。</para>
    /// </summary>
    /// <param name="resourceKey">The locale resource key. <para>语言资源键。</para></param>
    /// <returns>The localized option. <para>本地化选项。</para></returns>
    /// <exception cref="ArgumentException">When <paramref name="resourceKey"/> is null or empty.
    /// <para>当 <paramref name="resourceKey"/> 为 null 或空时抛出。</para></exception>
    public static LocalizedOption FromResource(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceKey);
        return new LocalizedOption(resourceKey, string.Empty);
    }

    /// <summary>
    /// Creates an entry whose text is a fixed label that is not translated, such as a protocol name.
    /// <para>创建文本为固定不翻译标签的条目，例如协议名称。</para>
    /// </summary>
    /// <param name="text">The fixed display text. <para>固定的显示文本。</para></param>
    /// <returns>The fixed option. <para>固定文本选项。</para></returns>
    public static LocalizedOption FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LocalizedOption(null, text);
    }

    /// <summary>
    /// Creates an entry whose text is rebuilt on every read, for labels that combine localized text with
    /// runtime state such as the backend currently in use.
    /// <para>创建每次读取都重新构建文本的条目，用于把本地化文本与运行时状态（例如当前使用的后端）组合起来的标签。</para>
    /// </summary>
    /// <param name="textFactory">The factory producing the display text.<para>生成显示文本的工厂。</para></param>
    /// <returns>The dynamic option.<para>动态文本选项。</para></returns>
    /// <exception cref="ArgumentNullException">When <paramref name="textFactory"/> is null.
    /// <para>当 <paramref name="textFactory"/> 为 null 时抛出。</para></exception>
    public static LocalizedOption FromFactory(Func<string> textFactory)
    {
        ArgumentNullException.ThrowIfNull(textFactory);
        return new LocalizedOption(null, string.Empty, textFactory);
    }

    internal void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>
/// A stable list of <see cref="LocalizedOption"/> entries that refreshes every entry after the
/// application language changes. Keep one instance per view model or view so the weak messenger
/// subscription stays alive for the lifetime of the owner.
/// <para>由 <see cref="LocalizedOption"/> 组成的稳定列表，在应用语言切换后刷新每个条目。
/// 每个视图模型或视图保留一个实例，使弱引用消息订阅与宿主同生命周期。</para>
/// </summary>
public sealed class LocalizedOptionSet
{
    private readonly LocalizedOption[] _options;

    /// <summary>
    /// Initializes the set from locale resource keys, preserving the given order.
    /// <para>按给定顺序用语言资源键初始化集合。</para>
    /// </summary>
    /// <param name="resourceKeys">The locale resource keys. <para>语言资源键。</para></param>
    public LocalizedOptionSet(params string[] resourceKeys)
        : this((resourceKeys ?? throw new ArgumentNullException(nameof(resourceKeys)))
            .Select(LocalizedOption.FromResource).ToArray())
    {
    }

    private LocalizedOptionSet(LocalizedOption[] options)
    {
        _options = options;
        WeakReferenceMessenger.Default.Register<LocalizedOptionSet, LanguageStateChangedMessage>(
            this, static (set, _) => set.Refresh());
    }

    /// <summary>
    /// Creates a set from explicit entries, allowing fixed technical labels next to localized ones.
    /// <para>用显式条目创建集合，可在本地化条目旁混用固定技术标签。</para>
    /// </summary>
    /// <param name="options">The entries in display order. <para>按显示顺序排列的条目。</para></param>
    /// <returns>The option set. <para>选项集合。</para></returns>
    public static LocalizedOptionSet FromOptions(params LocalizedOption[] options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LocalizedOptionSet(options);
    }

    /// <summary>
    /// Gets the entries in display order.
    /// <para>按显示顺序获取条目。</para>
    /// </summary>
    /// <value>The read-only entry list. <para>只读条目列表。</para></value>
    public IReadOnlyList<LocalizedOption> Options => _options;

    internal void Refresh()
    {
        foreach (var option in _options)
        {
            option.RefreshDisplayName();
        }
    }
}