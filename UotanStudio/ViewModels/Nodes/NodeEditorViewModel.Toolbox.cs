using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

public partial class NodeEditorViewModel
{
    private readonly List<string> _favoriteNodeTypes = [];
    private readonly List<string> _recentNodeTypes = [];
    private readonly HashSet<string> _expandedNodeCategories = new(StringComparer.Ordinal);
    private bool _toolboxPreferencesLoaded;

    private void InitializeToolbox()
    {
        if (!_toolboxPreferencesLoaded)
        {
            var settings = SettingsPersistenceService.Instance.LoadSettings();
            _favoriteNodeTypes.AddRange(settings.FavoriteNodeTypes.Take(256));
            _recentNodeTypes.AddRange(settings.RecentNodeTypes.Take(12));
            _expandedNodeCategories.UnionWith(settings.ExpandedNodeCategories.Take(256));
            _toolboxPreferencesLoaded = true;
        }
        ToolboxCategories.Clear();
        NodeFactoryRegistrar.RegisterAll();
        var language = LanguageService.Instance;
        string[] categories = ["Inputs", "Workspace", "Data", "Validation", "Configuration", "Firmware", "Device", "Flow", "Interaction", "Composite", "Script"];
        foreach (var category in categories)
        {
            var descriptors = NodeFactoryRegistrar.Descriptors.Where(descriptor =>
                descriptor.IsToolboxVisible && descriptor.CategoryPath == category).ToArray();
            if (descriptors.Length == 0)
            {
                continue;
            }
            var items = new ObservableCollection<NodeToolboxItem>(descriptors.Select(descriptor =>
                new NodeToolboxItem(descriptor, language, _favoriteNodeTypes.Contains(descriptor.TypeKey), ToggleToolboxFavorite, AddToolboxPreset)));
            var families = items.GroupBy(item => item.FamilyId).OrderBy(group => group.First().FamilyName)
                .Select(group => CreateToolboxCategory(category + "/" + group.Key, group.First().FamilyName,
                    new ObservableCollection<NodeToolboxItem>(group.OrderBy(item => item.Name)), [])).ToArray();
            ToolboxCategories.Add(CreateToolboxCategory(category, language.GetString("Node_Category_" + category), items, families));
        }
    }

    private NodeToolboxCategory CreateToolboxCategory(string identity, string name, ObservableCollection<NodeToolboxItem> items,
        IReadOnlyList<NodeToolboxCategory> families, bool? expanded = null)
    {
        return new NodeToolboxCategory(name, GetToolboxNodeCategory(identity.Split('/')[0]), items)
        {
            Identity = identity,
            Families = families,
            IsExpanded = expanded ?? _expandedNodeCategories.Contains(identity),
            ExpansionChanged = OnToolboxExpansionChanged
        };
    }

    private static NodeCategory GetToolboxNodeCategory(string category) => category switch
    {
        "Device" => NodeCategory.Flash,
        "Flow" => NodeCategory.Flow,
        "Interaction" => NodeCategory.Output,
        "Firmware" or "Build" => NodeCategory.Firmware,
        "Composite" => NodeCategory.Plugin,
        "Script" => NodeCategory.Plugin,
        _ => NodeCategory.Data
    };

    private void RefreshVisibleToolbox()
    {
        var keyword = (SearchText ?? string.Empty).Trim();
        VisibleToolboxCategories.Clear();
        if (keyword.Length == 0)
        {
            AddShortcutCategory("Favorites", _favoriteNodeTypes);
            AddShortcutCategory("Recent", _recentNodeTypes);
            foreach (var category in ToolboxCategories)
            {
                VisibleToolboxCategories.Add(category);
            }
            return;
        }
        foreach (var category in ToolboxCategories)
        {
            var matches = category.Items.Where(item => item.Matches(keyword) || category.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 0)
            {

                VisibleToolboxCategories.Add(new NodeToolboxCategory(category.Name, category.Category, new(matches)) { IsExpanded = true });
            }
        }
    }

    private void AddShortcutCategory(string identity, IReadOnlyList<string> keys)
    {
        var byKey = ToolboxCategories.SelectMany(category => category.Items).ToDictionary(item => item.TypeKey, StringComparer.Ordinal);
        var items = keys.Distinct(StringComparer.Ordinal).Where(byKey.ContainsKey).Select(key => byKey[key]).ToArray();
        if (items.Length > 0)
        {
            VisibleToolboxCategories.Add(CreateToolboxCategory(identity,
                LanguageService.Instance.GetString("Node_Toolbox_" + identity), new(items), [], expanded: true));
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

    private void OnToolboxExpansionChanged(NodeToolboxCategory category)
    {
        _expandedNodeCategories.Clear();
        _expandedNodeCategories.UnionWith(SettingsPersistenceService.Instance.LoadSettings().ExpandedNodeCategories.Take(256));
        if (category.IsExpanded)
        {
            _expandedNodeCategories.Add(category.Identity);
        }
        else
        {
            _expandedNodeCategories.Remove(category.Identity);
        }
        SaveToolboxPreferences(settings => settings.ExpandedNodeCategories = [.. _expandedNodeCategories]);
    }

    private static void SaveToolboxPreferences(Action<AppSettings> update)
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        update(settings);
        SettingsPersistenceService.Instance.SaveSettings(settings);
    }
}

/// <summary>A toolbox category or object family. Search results retain concrete items without nesting.<para>工具箱分类或对象族。搜索结果保留具体条目而不嵌套。</para></summary>
/// <param name="name">Display name of the category.<para>分类的显示名称。</para></param>
/// <param name="category">Node category this entry groups by.<para>此条目据以分组的节点类别。</para></param>
/// <param name="items">Items shown inside the category.<para>分类内展示的条目。</para></param>
public sealed class NodeToolboxCategory(string name, NodeCategory category, ObservableCollection<NodeToolboxItem> items) : ViewModelBase
{
    public string Name { get; } = name;
    public NodeCategory Category { get; } = category;
    public ObservableCollection<NodeToolboxItem> Items { get; } = items;
    public string Identity { get; init; } = string.Empty;
    public IReadOnlyList<NodeToolboxCategory> Families { get; init; } = [];
    public bool HasFamilies => Families.Count > 0;
    public Action<NodeToolboxCategory>? ExpansionChanged { get; init; }

    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ExpansionChanged?.Invoke(this);
            }
        }
    }
}

/// <summary>One concrete operation, with searchable contract identity and a persistent favorite action.<para>单个具体操作，带有可搜索的契约标识与可持久化的收藏动作。</para></summary>
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
    public bool Matches(string keyword) => Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || FamilyName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || _keywords.Any(value => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A named creation preset that changes data defaults without introducing another operation identity.<para>命名创建预设，只改变数据默认值，不引入新的操作标识。</para></summary>
/// <param name="Name">Display name of the preset.<para>预设的显示名称。</para></param>
/// <param name="CreateCommand">Command that creates the preset node.<para>创建预设节点的命令。</para></param>
public sealed record NodeToolboxPreset(string Name, IRelayCommand CreateCommand);