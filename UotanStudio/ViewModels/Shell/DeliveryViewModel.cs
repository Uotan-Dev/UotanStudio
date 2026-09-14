using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Firmware;

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
        IsAutoZero = file.AutoZero;
        SourceFormat = FilePath is null ? "generated" : DetectSourceFormat(FilePath);
    }

    public string? FilePath { get; }
    public string GroupName { get; }
    public bool Generated { get; }
    public bool IsZeroSize => FilePath is null && !Generated && !IsAutoZero;
    internal IReadOnlySet<string>? SourceDynamicPartitions { get; set; }
    [ObservableProperty] private bool _showSuperChoice;
    public string Name => IsAutoZero
        ? $"{(FileName.Length > 0 ? FileName : PartitionName)} (B 槽 · 只读)"
        : FileName.Length > 0 ? FileName : PartitionName;
    public string Description => Generated ? LanguageService.Instance.GetString("Delivery_PendingImage") :
        $"{FilePath ?? "0 B"} · {SourceFormat.ToUpperInvariant()} · {(IsReadOnly ? "read-only" : "read-write")}";
    public string SourceFormat { get; }
    public bool IsReadOnly => !string.Equals(SourceFormat, "ext4", StringComparison.OrdinalIgnoreCase);
    public string SourceDisplay => string.IsNullOrEmpty(GroupName) ? SourceFormat.ToUpperInvariant() : $"{GroupName} · {SourceFormat.ToUpperInvariant()}";
    public string DisplaySize => Generated ? LanguageService.Instance.GetString("Delivery_PendingImage") : SizeBytes is { } size ? $"{size:N0} B" : IsZeroSize ? "0 B" : "—";
    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplaySize));
    }
    private static string DetectSourceFormat(string path)
    {
        try { return ImageInputService.DetectFormat(path); }
        catch { return "unknown"; }
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name))] private string _fileName = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name))] private string _partitionName = "";
    // 最近一次合法的分区名；编辑校验失败时用于回退。仅在 VM 的编辑校验路径中消费。
    // Last accepted partition name, used to roll back a rejected edit; consumed only by the VM edit validation path.
    internal string PreviousPartitionName { get; private set; } = "";
    partial void OnPartitionNameChanging(string value) => PreviousPartitionName = PartitionName;
    [ObservableProperty] private bool _isOptional;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _fastbootd;
    [ObservableProperty] private bool _includeInSuper;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name)), NotifyPropertyChangedFor(nameof(Description))] private bool _isAutoZero;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplaySize))] private long? _sizeBytes;

    internal DeliveryFile Snapshot() => new()
    {
        SourcePath = FilePath ?? "",
        FileName = FileName,
        Partition = PartitionName,
        SourceGroup = GroupName,
        SourceLength = SizeBytes,
        IncludeInSuper = IncludeInSuper,
        Optional = IsOptional,
        Default = IsDefault,
        Generated = Generated,
        Fastbootd = Fastbootd,
        AutoZero = IsAutoZero
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
    // 恢复计划期间抑制 AutoZero 切换的清空语义（OnAutoZeroChanged 用）；普通交互不受影响。
    // Suppresses the auto-zero clearing semantics while restoring a plan (used by OnAutoZeroChanged); normal interaction is unaffected.
    internal bool AutoZeroPartitionsRestoreGuard { get; set; }
    [ObservableProperty] private bool _packSuper;
    [ObservableProperty] private bool _packPayload;
    [ObservableProperty] private bool _organizeDirectory = true;
    [ObservableProperty] private bool _confirmed;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _autoZero;
    // 自动零分区开关切换会清空编排列表（勾选归属随 SetAssignedPaths 同步取消）并重置确认状态。
    // Toggling auto-zero clears the composition list (assigned source checkboxes reset via SetAssignedPaths) and the confirmed state.
    partial void OnAutoZeroChanged(bool value)
    {
        // 恢复计划期间抑制清空语义，仅做常规刷新；Reset 末尾会显式 RefreshAutoZero。
        // During plan restore the clearing semantics are suppressed and only a plain refresh runs; Reset calls RefreshAutoZero explicitly at the end.
        if (AutoZeroPartitionsRestoreGuard)
        {
            AutoZeroPartitionsRestoreGuard = false;
            RefreshAutoZero();
            return;
        }
        if (Container.Count == 0 && !Confirmed)
        {
            RefreshAutoZero();
            return;
        }
        Confirmed = false;
        ClearImages();
        RefreshAutoZero();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    internal event EventHandler? Changed;
    internal bool EditingContainer => (PackSuper || PackPayload) && !Confirmed;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal DeliveryViewModel()
    {
        Container.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Output.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    internal bool SetMode(bool directory, bool super, bool payload = false)
    {
        var changed = PackSuper != super || PackPayload != payload || OrganizeDirectory != directory;
        if (changed)
        {
            Confirmed = false;
            ClearImages();
        }
        PackSuper = super; PackPayload = payload;
        foreach (var item in Container) item.ShowSuperChoice = payload;
        OrganizeDirectory = directory;
        if (!Confirmed) foreach (var item in Output.Where(i => i.Generated).ToArray()) Output.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return changed;
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
        RefreshAutoZero();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Confirm(DeliveryPlan plan)
    {
        if ((!PackSuper && !PackPayload) || Container.Count == 0) throw new InvalidDataException(LanguageService.Instance.GetString("Delivery_AddPartitionsFirst"));
        if (plan.PackPayload) DeliveryPayloadBuilder.Validate(plan);
        if (plan.PackSuper) _ = DeliverySuperBuilder.ExpandPartitions(Container.Select(i => i.Snapshot()).ToArray(), plan.Super);
        foreach (var row in Output.Where(i => i.Generated).ToArray()) Output.Remove(row);
        // 仅生成Super_Empty：Empty 时唯一生成条目是 super_empty.img；否则按打包类型生成容器。
        // Super_Empty only: with Empty the single generated entry is super_empty.img; otherwise the container per pack mode.
        if (plan.PackSuper && plan.Super.Empty)
            Output.Insert(0, Observe(new DeliveryImage(new DeliveryFile
            {
                Generated = true,
                FileName = "super_empty.img",
                Partition = "super_empty"
            })));
        else
            Output.Insert(0, Observe(new DeliveryImage(new DeliveryFile
            {
                Generated = true,
                FileName = plan.PackPayload ? "ota.zip" : DeliveryOutputService.ContainerFileName(plan),
                Partition = plan.PackPayload ? "payload" : "super"
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
            throw new InvalidDataException(LanguageService.Instance.GetString("Delivery_InvalidPartition"));
        Container.Add(Observe(new DeliveryImage(new DeliveryFile { Partition = partition, SourceLength = 0 })));
    }

    internal void Remove(DeliveryImage image)
    {
        Container.Remove(image);
        Output.Remove(image);
        if (image.FilePath is { } path) _previousSelection.Remove(path);
        RefreshAutoZero();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><para>按自动零分区开关物化 VAB B 槽伴随条目；关闭时移除自动生成条目，文件驱动的 0 字节 _b 行派生只读状态。</para>
    /// Materializes VAB B-slot zero-size companions per the auto-zero toggle; removes generated rows when off and derives read-only state for file-backed empty _b rows.</summary>
    internal void RefreshAutoZero()
    {
        if (IsRunning) return;
        if (PackPayload) return;
        var autoZero = AutoZero;
        foreach (var row in Container.Where(i => i.IsAutoZero && i.FilePath is null).ToArray()) Container.Remove(row);
        // 打包 Super 时，无 A/B 后缀的文件承载行在列表阶段即改名 _a 并物化 _b 伴随条目，与确认时 DeliverySuperBuilder.ExpandPartitions 的兜底行为一致。
        // When packing Super, file-backed rows without an A/B suffix are renamed _a at list stage with a materialized _b companion, matching the confirm-time fallback in DeliverySuperBuilder.ExpandPartitions.
        if (autoZero && PackSuper)
            foreach (var row in Container.Where(i => i.FilePath is not null && !i.PartitionName.EndsWith("_a", StringComparison.Ordinal) && !i.PartitionName.EndsWith("_b", StringComparison.Ordinal)).ToArray())
            {
                var target = row.PartitionName + "_a";
                if (Container.Any(i => i.PartitionName == target)) continue;
                _updating = true;
                try { row.PartitionName = target; }
                finally { _updating = false; }
            }
        if (autoZero)
            foreach (var source in Container.Where(i => i.PartitionName.EndsWith("_a", StringComparison.Ordinal)).ToArray())
            {
                var name = source.PartitionName[..^2] + "_b";
                if (Container.Any(i => i.PartitionName == name)) continue;
                Container.Insert(Math.Min(Container.IndexOf(source) + 1, Container.Count),
                    Observe(new DeliveryImage(new DeliveryFile { Partition = name, SourceLength = 0, AutoZero = true })));
            }
        foreach (var row in Container.Where(i => i.FilePath is not null && i.PartitionName.EndsWith("_b", StringComparison.Ordinal)))
            row.IsAutoZero = autoZero && row.SizeBytes == 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Reset(DeliveryPlan? plan = null)
    {
        ResetProgressStatistics();
        ClearImages();
        if (plan is not null)
        {
            foreach (var file in plan.Container) Container.Add(Observe(new DeliveryImage(file)));
            foreach (var file in plan.Files) Output.Add(Observe(new DeliveryImage(file.Generated && (file.Partition == "payload" || file.Partition == "super")
                ? file with { FileName = DeliveryOutputService.ContainerFileName(plan) } : file)));
            foreach (var item in Container.Concat(Output).Where(i => i.FilePath is not null)) _known.TryAdd(item.FilePath!, item);
            PackSuper = plan.PackSuper; PackPayload = plan.PackPayload;
            foreach (var item in Container) item.ShowSuperChoice = PackPayload;
            OrganizeDirectory = plan.OrganizeDirectory; Confirmed = plan.ContainerConfirmed && !plan.IsSuperEmptyOnly;
            // 恢复计划期间设置 AutoZero，经恢复护栏仅做常规刷新，不触发清空语义。
            // Setting AutoZero during plan restore runs the restore guard (plain refresh only) without the clearing semantics.
            AutoZeroPartitionsRestoreGuard = true;
            AutoZero = plan.Super.AutoZero;
        }
        else
        {
            PackPayload = false; PackSuper = false; OrganizeDirectory = true; Confirmed = false;
            // 无计划重置同样走恢复护栏，避免 AutoZero=false 触发清空语义（此时列表已清空，仅需常规刷新）。
            // Plan-less reset also uses the restore guard so AutoZero=false does not trigger the clearing semantics (the list is already empty; a plain refresh suffices).
            AutoZeroPartitionsRestoreGuard = true;
            AutoZero = false;
        }
        if (plan is not null) RefreshAutoZero();
        Status = ""; Progress = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearImages()
    {
        foreach (var item in _known.Values.Concat(Container).Concat(Output).Distinct()) item.PropertyChanged -= OnItemChanged;
        _known.Clear();
        _previousSelection.Clear();
        Container.Clear(); Output.Clear();
    }

    private DeliveryImage Observe(DeliveryImage image) { image.PropertyChanged += OnItemChanged; return image; }
    internal void ApplySourcePartitions(string sourcePath, IReadOnlySet<string>? partitions)
    {
        foreach (var row in Container.Concat(Output).Where(row => row.FilePath is { } path && PathComparer.Equals(path, sourcePath)))
        {
            row.SourceDynamicPartitions = partitions;
            if (partitions is not null) row.Fastbootd = DeliveryDynamicPartitions.Contains(partitions, row.PartitionName);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updating) return;
        if (e.PropertyName == nameof(DeliveryImage.PartitionName))
        {
            if (sender is DeliveryImage { SourceDynamicPartitions: { } partitions } changed)
                changed.Fastbootd = DeliveryDynamicPartitions.Contains(partitions, changed.PartitionName);
            // 手动编辑分区名的即时校验：非法名或与其他行重复时回退到编辑前的名字并提示；包装在 _updating 中防止回退再次递归。
            // Immediate validation for manual partition-name edits: an invalid or duplicate name rolls back to the pre-edit value with a status hint; wrapped in _updating so the rollback does not recurse.
            if (sender is DeliveryImage changedRow && changedRow.PartitionName != changedRow.PreviousPartitionName
                && (!DeliveryValidation.IsPartition(changedRow.PartitionName, PackPayload)
                    || Container.Concat(Output).Any(i => i != changedRow && i.PartitionName == changedRow.PartitionName
                        && !(Output.Contains(i) && Output.Contains(changedRow)
                            && DeliveryPackageBuilder.SameDatSet(i.FileName, changedRow.FileName)))))
            {
                _updating = true;
                try
                {
                    changedRow.PartitionName = changedRow.PreviousPartitionName;
                    Status = LanguageService.Instance.GetString("Delivery_InvalidPartition");
                }
                finally { _updating = false; }
            }
            RefreshAutoZero();
        }
        if (sender is DeliveryImage { IsDefault: true } row && e.PropertyName == nameof(DeliveryImage.IsDefault))
        {
            _updating = true;
            try
            {
                foreach (var other in Output.Where(i => i != row && i.PartitionName == row.PartitionName
                && !DeliveryPackageBuilder.SameDatSet(i.FileName, row.FileName))) other.IsDefault = false;
            }
            finally { _updating = false; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}