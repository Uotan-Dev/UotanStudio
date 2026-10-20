using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private readonly List<string> _favoriteNodeTypes = [];
    private readonly List<string> _recentNodeTypes = [];
    private bool _toolboxPreferencesLoaded;

    /// <summary>Order in which the flat toolbox lists operations; it keeps related operations next to each other without grouping them.<para>平铺工具箱中操作的排列顺序，让同类操作相邻但不分组。</para></summary>
    private static readonly string[] ToolboxCategoryOrder =
        ["Inputs", "Workspace", "Data", "Validation", "Configuration", "Firmware", "Device", "Flow", "Interaction", "Composite", "Script"];

    private void InitializeToolbox()
    {
        if (!_toolboxPreferencesLoaded)
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            _favoriteNodeTypes.AddRange(settings.FavoriteNodeTypes.Take(256));
            _recentNodeTypes.AddRange(settings.RecentNodeTypes.Take(12));
            _toolboxPreferencesLoaded = true;
        }
        ToolboxItems.Clear();
        NodeFactoryRegistrar.RegisterAll();
        var language = LanguageService.Instance;
        var items = NodeFactoryRegistrar.Descriptors
            .Where(descriptor => descriptor.IsToolboxVisible && ToolboxCategoryOrder.Contains(descriptor.CategoryPath))
            .OrderBy(descriptor => Array.IndexOf(ToolboxCategoryOrder, descriptor.CategoryPath))
            .ThenBy(descriptor => FamilyName(descriptor.Family, language), StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.DisplayName ?? language.GetString(descriptor.NameResourceKey), StringComparer.OrdinalIgnoreCase)
            .Select(descriptor => new NodeToolboxItem(descriptor, language, _favoriteNodeTypes.Contains(descriptor.TypeKey),
                ToggleToolboxFavorite, AddToolboxPreset));
        foreach (var item in items)
        {
            ToolboxItems.Add(item);
        }
        RefreshVisibleToolbox();
    }

    /// <summary>Resolves the localized family name, falling back to the raw family identifier.<para>解析族名的本地化文本，缺少资源时回退到族标识。</para></summary>
    private static string FamilyName(string family, LanguageService language)
    {
        var key = "Node_Family_" + family;
        var name = language.GetString(key);
        return name == key ? family : name;
    }

    private void RefreshVisibleToolbox()
    {
        var keyword = (SearchText ?? string.Empty).Trim();
        VisibleToolboxItems.Clear();
        if (keyword.Length == 0)
        {
            AddShortcutItems(_favoriteNodeTypes);
            AddShortcutItems(_recentNodeTypes);
            foreach (var item in ToolboxItems)
            {
                VisibleToolboxItems.Add(item);
            }
            return;
        }
        var byKey = ToolboxItems.ToDictionary(item => item.TypeKey, StringComparer.Ordinal);
        foreach (var shortcut in ShortcutGroups)
        {
            var matches = shortcut.Keys.Where(byKey.ContainsKey).Select(key => byKey[key])
                .Where(item => item.Matches(keyword)).ToArray();
            if (matches.Length > 0)
            {
                VisibleToolboxItems.Add(NodeToolboxItem.CreateHeading(shortcut.Title));
                foreach (var item in matches)
                {
                    VisibleToolboxItems.Add(item);
                }
            }
        }
        foreach (var item in ToolboxItems.Where(item => item.Matches(keyword)))
        {
            VisibleToolboxItems.Add(item);
        }
    }

    private IEnumerable<(string Title, IReadOnlyList<string> Keys)> ShortcutGroups =>
        [("Favorites", _favoriteNodeTypes), ("Recent", _recentNodeTypes)];

    private void AddShortcutItems(IReadOnlyList<string> keys)
    {
        var byKey = ToolboxItems.ToDictionary(item => item.TypeKey, StringComparer.Ordinal);
        var items = keys.Distinct(StringComparer.Ordinal).Where(byKey.ContainsKey).Select(key => byKey[key]).ToArray();
        if (items.Length > 0)
        {
            var identity = ReferenceEquals(keys, _favoriteNodeTypes) ? "Favorites" : "Recent";
            VisibleToolboxItems.Add(NodeToolboxItem.CreateHeading(LanguageService.Instance.GetString("Node_Toolbox_" + identity)));
            foreach (var item in items)
            {
                VisibleToolboxItems.Add(item);
            }
        }
    }

    /// <summary>Records deliberate toolbox insertion; previews and deserialization do not alter recent items.<para>记录刻意的工具箱插入；预览与反序列化不会改变最近使用项。</para></summary>
    /// <param name="typeKey">Stable type key of the inserted node.<para>被插入节点的稳定类型键。</para></param>
    public void RecordToolboxUse(string typeKey)
    {
        if (!NodeFactoryRegistrar.Descriptors.Any(descriptor => descriptor.TypeKey == typeKey))
        {
            return;
        }
        _recentNodeTypes.Clear();
        _recentNodeTypes.AddRange(SettingsPersistenceService.Instance.LoadSettings().RecentNodeTypes.Take(12));
        _recentNodeTypes.Remove(typeKey);
        _recentNodeTypes.Insert(0, typeKey);
        if (_recentNodeTypes.Count > 12)
        {
            _recentNodeTypes.RemoveRange(12, _recentNodeTypes.Count - 12);
        }
        SaveToolboxPreferences(settings => settings.RecentNodeTypes = [.. _recentNodeTypes]);
        RefreshVisibleToolbox();
    }

    private void ToggleToolboxFavorite(NodeToolboxItem item)
    {
        _favoriteNodeTypes.Clear();
        _favoriteNodeTypes.AddRange(SettingsPersistenceService.Instance.LoadSettings().FavoriteNodeTypes.Take(256));
        if (!_favoriteNodeTypes.Remove(item.TypeKey))
        {
            if (_favoriteNodeTypes.Count >= 256)
            {
                _favoriteNodeTypes.RemoveAt(0);
            }
            _favoriteNodeTypes.Add(item.TypeKey);
        }
        item.IsFavorite = _favoriteNodeTypes.Contains(item.TypeKey);
        SaveToolboxPreferences(settings => settings.FavoriteNodeTypes = [.. _favoriteNodeTypes]);
        RefreshVisibleToolbox();
    }

    private void AddToolboxPreset(FlowNodeViewModel node)
    {
        AddNode(node);
        RecordToolboxUse(node.TypeKey);
    }

    private static void SaveToolboxPreferences(Action<AppSettings> update)
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        update(settings);
        SettingsPersistenceService.Instance.SaveSettings(settings);
    }
}

/// <summary>One concrete operation in the flat toolbox list, with searchable contract identity and a persistent favorite action.<para>平铺工具箱列表中的一个具体操作，带有可搜索的契约标识与可持久化的收藏动作。</para></summary>
public sealed class NodeToolboxItem : ViewModelBase
{
    private readonly IReadOnlyList<string> _keywords = [];

    public string Name { get; }
    public Func<FlowNodeViewModel> CreateNode { get; }
    public string TypeKey { get; }
    public string FamilyId { get; } = string.Empty;
    public string FamilyName { get; } = string.Empty;
    public bool RequiresConfirmation { get; }
    public NodeCapability Capabilities { get; }
    public IRelayCommand ToggleFavoriteCommand { get; }
    public IReadOnlyList<NodeToolboxPreset> Presets { get; } = [];
    public bool HasPresets => Presets.Count > 0;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteHint => LanguageService.Instance.GetString(IsFavorite ? "Node_Toolbox_Unfavorite" : "Node_Toolbox_Favorite");
    public bool IsHeading => CreateNode is null;
    public bool IsOperation => CreateNode is not null;

    public bool IsFavorite
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
                OnPropertyChanged(nameof(FavoriteHint));
            }
        }
    }

    private NodeToolboxItem(string name, Func<FlowNodeViewModel>? createNode, string typeKey)
    {
        Name = name;
        CreateNode = createNode!;
        TypeKey = typeKey;
        ToggleFavoriteCommand = new RelayCommand(() => { });
    }

    /// <summary>Creates a non-interactive heading that separates the favorites and recently used operations.<para>创建一个不可交互的标题，用于分隔收藏与最近使用的操作。</para></summary>
    /// <param name="title">Localized heading text.<para>本地化的标题文本。</para></param>
    /// <returns>A heading entry whose operation members are unused.<para>一个标题条目，其操作成员不使用。</para></returns>
    public static NodeToolboxItem CreateHeading(string title) => new(title, null, "heading:" + title);

    public NodeToolboxItem(NodeDescriptor descriptor, LanguageService language, bool isFavorite = false,
        Action<NodeToolboxItem>? toggleFavorite = null, Action<FlowNodeViewModel>? addPreset = null)
    {
        Name = descriptor.DisplayName ?? language.GetString(descriptor.NameResourceKey);
        CreateNode = descriptor.Factory;
        TypeKey = descriptor.TypeKey;
        FamilyId = descriptor.Family;
        var familyKey = "Node_Family_" + FamilyId;
        var family = language.GetString(familyKey);
        FamilyName = family == familyKey ? FamilyId : family;
        RequiresConfirmation = descriptor.RequiresConfirmation;
        Capabilities = descriptor.Capabilities;
        IsFavorite = isFavorite;
        _keywords = [.. descriptor.SearchKeywords, .. NodeSearchIndex.GetLabels(descriptor.NameResourceKey)];
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite?.Invoke(this));
        if (descriptor.TypeKey == "Device.Partition.Flash")
        {
            Presets = [.. new[] { "boot", "bootloader", "super" }.Select(partition => new NodeToolboxPreset(
                language.GetString("Node_Preset_Flash_" + partition), new RelayCommand(() =>
                {
                    var node = descriptor.Factory();
                    node.Input.Single(input => input.PortId == "partition").InitialValue = partition;
                    addPreset?.Invoke(node);
                })))];
        }
    }

    /// <summary>Finds localized names, technical identities and aliases without depending on the active category.<para>在不依赖当前分类的情况下匹配本地化名称、技术标识与别名。</para></summary>
    /// <param name="keyword">Search text compared case-insensitively.<para>按不区分大小写方式比较的搜索文本。</para></param>
    /// <returns>True when a name, family or alias contains the keyword.<para>名称、族名或别名包含该关键字时为 True。</para></returns>
    public bool Matches(string keyword) => IsOperation && (Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || FamilyName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || _keywords.Any(value => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>A named creation preset that changes data defaults without introducing another operation identity.<para>命名创建预设，只改变数据默认值，不引入新的操作标识。</para></summary>
/// <param name="Name">Display name of the preset.<para>预设的显示名称。</para></param>
/// <param name="CreateCommand">Command that creates the preset node.<para>创建预设节点的命令。</para></param>
public sealed record NodeToolboxPreset(string Name, IRelayCommand CreateCommand);