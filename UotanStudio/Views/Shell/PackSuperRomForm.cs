using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Views.Shell;

/// <summary><para>打包窗口确认的本地表单快照，不执行打包。</para>Local packaging form snapshot; does not perform packaging.</summary>
public sealed record PackSuperRomFormResult(
    bool PackZip, bool PackSuper, bool PackPayload,
    string ZipName, string ArchiveFormat, string CompressionAlgorithm, bool SuperAsZst,
    string SuperName, string OutputLocation, string PayloadName,
    IReadOnlyList<PackSuperRomImageSelection> Images);

/// <summary><para>所选镜像及其 SUPER 归属。</para>Selected image and its SUPER membership.</summary>
public sealed record PackSuperRomImageSelection(string GroupName, string FilePath, bool IncludeInSuper);

internal sealed record PackCompressionChoice(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed partial class PackImageEntry(string filePath, bool isOriginal = false) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string Name { get; } = Path.GetFileName(filePath);
    public bool IsOriginal { get; } = isOriginal;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _includeInSuper;
    [ObservableProperty] private bool _showSuperColumn;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value) IncludeInSuper = false;
    }

    partial void OnIncludeInSuperChanged(bool value)
    {
        if (value) IsSelected = true;
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
    public IEnumerable<string> FilePaths => _entries.Select(entry => entry.FilePath);
    [ObservableProperty] private bool _showSuperColumn;

    public bool? AllSelected
    {
        get => Images.Count == 0 || Images.All(entry => !entry.IsSelected) ? false
            : Images.All(entry => entry.IsSelected) ? true : null;
        set
        {
            if (_updatingSelection || value is null) return;
            _updatingSelection = true;
            try { foreach (var entry in Images) entry.IsSelected = value.Value; }
            finally { _updatingSelection = false; }
            OnPropertyChanged();
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
        OnPropertyChanged(nameof(AllSelected));
    }

    partial void OnShowSuperColumnChanged(bool value)
    {
        foreach (var entry in _entries) entry.ShowSuperColumn = value;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSelection && e.PropertyName == nameof(PackImageEntry.IsSelected))
            OnPropertyChanged(nameof(AllSelected));
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.PropertyChanged -= OnSelectionChanged;
    }
}
