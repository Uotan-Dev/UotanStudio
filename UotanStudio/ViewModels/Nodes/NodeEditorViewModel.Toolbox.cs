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
        string[] categories = ["Inputs", "Data", "Validation", "Configuration", "Firmware", "Build", "Device", "Flow", "Interaction", "Composite"];
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
                // Search presents concrete operations directly, without another family-selection step.
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

    /// <summary>Records deliberate toolbox insertion; previews and deserialization do not alter recent items.</summary>
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

/// <summary>A toolbox category or object family. Search results retain concrete items without nesting.</summary>
public sealed class NodeToolboxCategory(string name, NodeCategory category, ObservableCollection<NodeToolboxItem> items) : ViewModelBase
{
    private bool _isExpanded;
    public string Name { get; } = name;
    public NodeCategory Category { get; } = category;
    public ObservableCollection<NodeToolboxItem> Items { get; } = items;
    public string Identity { get; init; } = string.Empty;
    public IReadOnlyList<NodeToolboxCategory> Families { get; init; } = [];
    public bool HasFamilies => Families.Count > 0;
    public Action<NodeToolboxCategory>? ExpansionChanged { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                ExpansionChanged?.Invoke(this);
            }
        }
    }
}

/// <summary>One concrete operation, with searchable contract identity and a persistent favorite action.</summary>
public sealed class NodeToolboxItem : ViewModelBase
{
    private bool _isFavorite;
    private readonly IReadOnlyList<string> _keywords = [];

    public string Name { get; }
    public Func<FlowNodeViewModel> CreateNode { get; }
    public string TypeKey { get; }
    public string FamilyId { get; } = string.Empty;
    public string FamilyName { get; } = string.Empty;
    public bool IsDestructive { get; }
    public NodeCapability Capabilities { get; }
    public IRelayCommand ToggleFavoriteCommand { get; }
    public IReadOnlyList<NodeToolboxPreset> Presets { get; } = [];
    public bool HasPresets => Presets.Count > 0;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteHint => LanguageService.Instance.GetString(IsFavorite ? "Node_Toolbox_Unfavorite" : "Node_Toolbox_Favorite");

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
                OnPropertyChanged(nameof(FavoriteHint));
            }
        }
    }

    public NodeToolboxItem(string name, Func<FlowNodeViewModel> createNode)
    {
        Name = name;
        CreateNode = createNode;
        TypeKey = string.Empty;
        ToggleFavoriteCommand = new RelayCommand(() => { });
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
        IsDestructive = descriptor.IsDestructive;
        Capabilities = descriptor.Capabilities;
        _isFavorite = isFavorite;
        _keywords = [.. descriptor.SearchKeywords, .. NodeSearchIndex.GetLabels(descriptor.NameResourceKey)];
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite?.Invoke(this));
        if (descriptor.TypeKey == "Device.Partition.Flash")
        {
            Presets = new[] { "boot", "bootloader", "super" }.Select(partition => new NodeToolboxPreset(
                language.GetString("Node_Preset_Flash_" + partition), new RelayCommand(() =>
                {
                    var node = descriptor.Factory();
                    node.Input.Single(input => input.PortId == "partition").InitialValue = partition;
                    addPreset?.Invoke(node);
                }))).ToArray();
        }
    }

    /// <summary>Finds localized names, technical identities and aliases without depending on the active category.</summary>
    public bool Matches(string keyword) => Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || FamilyName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || _keywords.Any(value => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A named creation preset that changes data defaults without introducing another operation identity.</summary>
public sealed record NodeToolboxPreset(string Name, IRelayCommand CreateCommand);
