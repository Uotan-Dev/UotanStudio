using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Controls;

/// <summary><para>交付页面所选镜像及其来源项目。</para>Selected delivery image and its source project.</summary>
public sealed record PackImageSelection(string GroupName, string FilePath)
{
    /// <summary><para>实际文件大小；无法读取时为空。</para>Actual file size, or null when unavailable.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>Logical partition names from this source project; null when no source metadata exists.</summary>
    public IReadOnlySet<string>? DynamicPartitions { get; init; }
}

internal sealed partial class PackImageEntry(string filePath, bool isOriginal = false, string? sourceLabel = null) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string Name { get; } = Path.GetFileName(filePath);
    /// <summary><para>显示名；与其它来源重名时追加来源标注（如 “boot.img (delivery)”）。</para>
    /// Display name; a source label is appended when the file name collides with another source.</summary>
    public string DisplayName { get; } = sourceLabel is null ? Path.GetFileName(filePath)
        : $"{Path.GetFileName(filePath)} ({sourceLabel})";
    public bool IsOriginal { get; } = isOriginal;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSelectionReadOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplaySize))] private long? _sizeBytes;
    public string DisplaySize => SizeBytes is { } bytes ? PackImageFileInfo.FormatSize(bytes) : "—";

    internal void UpdateFileInfo(PackImageFileInfo info)
    {
        SizeBytes = info.SizeBytes;
    }
}

internal sealed partial class PackImageGroup : ObservableObject, IDisposable
{
    private readonly PackImageEntry[] _entries;
    private bool _updatingSelection;

    public string Name { get; }
    public bool IsExternal { get; }
    /// <summary><para>是否为交付生成镜像分组。</para>Whether this group holds delivery-generated outputs.</summary>
    public bool IsDeliveryOutputs { get; init; }
    /// <summary><para>是否为节点工作流产物分组。</para>Whether this group holds node-workflow published artifacts.</summary>
    public bool IsNodeOutputs { get; init; }
    internal IReadOnlySet<string>? DynamicPartitions { get; init; }
    public ObservableCollection<PackImageEntry> Images { get; } = [];
    public bool HasImages => Images.Count > 0;
    public bool HasSelectableImages => Images.Any(entry => !entry.IsSelectionReadOnly);
    public IEnumerable<string> FilePaths => _entries.Select(entry => entry.FilePath);
    internal IReadOnlyList<PackImageEntry> Entries => _entries;
    internal event EventHandler? SelectionChanged;

    public bool? AllSelected
    {
        get => !HasSelectableImages || Images.Where(entry => !entry.IsSelectionReadOnly).All(entry => !entry.IsSelected) ? false
            : Images.Where(entry => !entry.IsSelectionReadOnly).All(entry => entry.IsSelected) ? true : null;
        set
        {
            if (_updatingSelection || value is null || !HasSelectableImages) return;
            _updatingSelection = true;
            try { foreach (var entry in Images.Where(entry => !entry.IsSelectionReadOnly)) entry.IsSelected = value.Value; }
            finally { _updatingSelection = false; }
            OnPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PackImageGroup(string name, IEnumerable<PackImageEntry> entries, bool isExternal = false)
    {
        Name = name;
        IsExternal = isExternal;
        _entries = [.. entries];
        foreach (var entry in _entries) entry.PropertyChanged += OnSelectionChanged;
        ShowOriginalImages(false);
    }

    public void ShowOriginalImages(bool showOriginal)
    {
        Images.Clear();
        foreach (var entry in _entries)
            if (showOriginal || !entry.IsOriginal) Images.Add(entry);
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(HasSelectableImages));
        OnPropertyChanged(nameof(AllSelected));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackImageEntry.IsSelectionReadOnly))
        {
            OnPropertyChanged(nameof(HasSelectableImages));
            OnPropertyChanged(nameof(AllSelected));
        }
        if (!_updatingSelection && e.PropertyName == nameof(PackImageEntry.IsSelected))
        {
            OnPropertyChanged(nameof(AllSelected));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.PropertyChanged -= OnSelectionChanged;
    }
}