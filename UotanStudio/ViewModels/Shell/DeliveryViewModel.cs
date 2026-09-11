using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services.Delivery;

namespace UotanStudio.ViewModels.Shell;

/// <summary>A source-backed partition or a generated container in the delivery editor.</summary>
internal sealed partial class DeliveryImage : ObservableObject
{
    internal DeliveryImage(DeliveryFile file)
    {
        FilePath = file.SourcePath.Length == 0 ? null : file.SourcePath;
        FileName = file.FileName;
        PartitionName = file.Partition;
        GroupName = file.SourceGroup;
        SizeBytes = file.SourceLength;
        IsOptional = file.Optional;
        IsDefault = file.Default;
        Generated = file.Generated;
        IncludeInSuper = file.IncludeInSuper;
        Fastbootd = file.Fastbootd;
    }

    public string? FilePath { get; }
    public string GroupName { get; }
    public bool Generated { get; }
    public bool IsZeroSize => FilePath is null && !Generated;
    [ObservableProperty] private bool _showSuperChoice;
    public string Name => FileName.Length > 0 ? FileName : PartitionName;
    public string Description => Generated ? "待生成 / To be generated" : FilePath ?? "0 B";
    public string DisplaySize => Generated ? "待生成 / Pending" : SizeBytes is { } size ? $"{size:N0} B" : IsZeroSize ? "0 B" : "—";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name))] private string _fileName = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name))] private string _partitionName = "";
    [ObservableProperty] private bool _isOptional;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _fastbootd;
    [ObservableProperty] private bool _includeInSuper;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplaySize))] private long? _sizeBytes;

    internal DeliveryFile Snapshot() => new()
    {
        SourcePath = FilePath ?? "", FileName = FileName, Partition = PartitionName, SourceGroup = GroupName,
        SourceLength = SizeBytes, IncludeInSuper = IncludeInSuper, Optional = IsOptional, Default = IsDefault, Generated = Generated, Fastbootd = Fastbootd
    };
}

/// <summary>Owns delivery membership and stage transitions, independent of visual enabled states.</summary>
internal sealed partial class DeliveryViewModel : ObservableObject
{
    internal ObservableCollection<DeliveryImage> Container { get; } = [];
    internal ObservableCollection<DeliveryImage> Output { get; } = [];
    private HashSet<string> _previousSelection = new(PathComparer);
    private readonly Dictionary<string, DeliveryImage> _known = new(PathComparer);
    private bool _updating;
    [ObservableProperty] private bool _packSuper;
    [ObservableProperty] private bool _packPayload;
    [ObservableProperty] private bool _organizeDirectory = true;
    [ObservableProperty] private bool _confirmed;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    internal event EventHandler? Changed;
    internal bool EditingContainer => (PackSuper || PackPayload) && !Confirmed;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal DeliveryViewModel()
    {
        Container.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Output.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetMode(bool directory, bool super, bool payload = false)
    {
        if (PackSuper != super || PackPayload != payload || OrganizeDirectory != directory) Confirmed = false;
        PackSuper = super; PackPayload = payload;
        foreach (var item in Container) item.ShowSuperChoice = payload;
        OrganizeDirectory = directory;
        if (!Confirmed) foreach (var item in Output.Where(i => i.Generated).ToArray()) Output.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SelectSources(IReadOnlyList<DeliveryFile> selected)
    {
        if (IsRunning) return;
        var paths = selected.Select(f => f.SourcePath).ToHashSet(PathComparer);
        var target = EditingContainer ? Container : Output;
        foreach (var path in _previousSelection.Except(paths, PathComparer).ToArray())
        {
            var row = target.FirstOrDefault(i => i.FilePath is { } p && PathComparer.Equals(p, path));
            if (row is not null) target.Remove(row);
        }
        foreach (var file in selected)
        {
            var existing = Container.Concat(Output).FirstOrDefault(i => i.FilePath is { } path && PathComparer.Equals(path, file.SourcePath));
            if (existing is not null)
            {
                if (existing.SizeBytes != file.SourceLength)
                {
                    existing.SizeBytes = file.SourceLength;
                    if (Container.Contains(existing)) Confirmed = false;
                }
                continue;
            }
            if (_previousSelection.Contains(file.SourcePath)) continue;
            if (!_known.TryGetValue(file.SourcePath, out var row))
            {
                row = Observe(new DeliveryImage(file));
                _known.Add(file.SourcePath, row);
            }
            row.SizeBytes = file.SourceLength;
            row.ShowSuperChoice = PackPayload;
            target.Add(row);
        }
        _previousSelection = paths;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Confirm(DeliverySuperOptions options)
    {
        if ((!PackSuper && !PackPayload) || Container.Count == 0) throw new InvalidDataException("请先添加 Super 分区 / Add Super partitions first.");
        if (PackSuper) _ = DeliverySuperBuilder.ExpandPartitions(Container.Select(i => i.Snapshot()).ToArray(), options);
        foreach (var row in Output.Where(i => i.Generated).ToArray()) Output.Remove(row);
        Output.Insert(0, Observe(new DeliveryImage(new DeliveryFile
        {
            Generated = true, FileName = PackPayload ? "payload.bin" : DeliveryOutputService.SuperFileName(options), Partition = PackPayload ? "payload" : options.Empty ? "super_empty" : "super"
        })));
        Confirmed = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ReturnToContainer()
    {
        Confirmed = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void AddZero(string partition)
    {
        if (!DeliveryValidation.IsPartition(partition) || Container.Any(i => i.PartitionName == partition))
            throw new InvalidDataException("分区名无效或重复 / Invalid or duplicate partition.");
        Container.Add(Observe(new DeliveryImage(new DeliveryFile { Partition = partition, SourceLength = 0 })));
    }

    internal void Remove(DeliveryImage image)
    {
        Container.Remove(image);
        Output.Remove(image);
        if (image.FilePath is { } path) _previousSelection.Remove(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Reset(DeliveryPlan? plan = null)
    {
        foreach (var item in _known.Values.Concat(Container).Concat(Output).Distinct()) item.PropertyChanged -= OnItemChanged;
        _known.Clear();
        _previousSelection.Clear();
        Container.Clear(); Output.Clear();
        if (plan is not null)
        {
            foreach (var file in plan.Container) Container.Add(Observe(new DeliveryImage(file)));
            foreach (var file in plan.Files) Output.Add(Observe(new DeliveryImage(file)));
            foreach (var item in Container.Concat(Output).Where(i => i.FilePath is not null)) _known.TryAdd(item.FilePath!, item);
            PackSuper = plan.PackSuper; PackPayload = plan.PackPayload;
            foreach (var item in Container) item.ShowSuperChoice = PackPayload;
            OrganizeDirectory = plan.OrganizeDirectory; Confirmed = plan.ContainerConfirmed;
        }
        else { PackPayload = false; PackSuper = false; OrganizeDirectory = true; Confirmed = false; }
        Status = ""; Progress = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DeliveryImage Observe(DeliveryImage image) { image.PropertyChanged += OnItemChanged; return image; }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updating) return;
        if (sender is DeliveryImage { IsDefault: true } row && e.PropertyName == nameof(DeliveryImage.IsDefault))
        {
            _updating = true;
            try { foreach (var other in Output.Where(i => i != row && i.PartitionName == row.PartitionName)) other.IsDefault = false; }
            finally { _updating = false; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
