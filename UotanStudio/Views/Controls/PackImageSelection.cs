using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Controls;

/// <summary><para>交付页面所选镜像及其来源项目。</para>Selected delivery image and its source project.</summary>
public sealed record PackImageSelection(string GroupName, string FilePath)
{
    /// <summary><para>实际文件大小；无法读取时为空。</para>Actual file size, or null when unavailable.</summary>
    public long? SizeBytes { get; init; }
}

internal sealed partial class PackImageEntry(string filePath, bool isOriginal = false) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string Name { get; } = Path.GetFileName(filePath);
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
        _entries = entries.ToArray();
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
