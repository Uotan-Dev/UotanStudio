using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;

namespace UotanStudio.Views.Controls;

/// <summary><para>交付表单共用的镜像选择列表，读取文件大小并维护本地选择。</para>Shared delivery image picker; reads file sizes and holds local selections.</summary>
public partial class PackImageSelectionView : UserControl
{
    public static readonly StyledProperty<bool> ShowOriginalsProperty =
        AvaloniaProperty.Register<PackImageSelectionView, bool>(nameof(ShowOriginals));
    /// <summary><para>空列表占位的显示属性。</para>Controls the empty-list placeholder.</summary>
    public static readonly StyledProperty<bool> ShowEmptyListProperty =
        AvaloniaProperty.Register<PackImageSelectionView, bool>(nameof(ShowEmptyList));

    private readonly List<PackImageGroup> _groups = [];
    private readonly PackImageGroup _emptyGroup = new(string.Empty, []);
    private CancellationTokenSource? _load;
    private string? _solutionPath;
    private bool _attached;
    private bool _loading;
    private bool _picking;
    private int _page;
    private int _pageSize = 3;
    private int _externalGroupNumber;
    private bool _selectionNotificationPending;

    /// <summary><para>是否显示原包镜像。</para>Whether to include original images.</summary>
    public bool ShowOriginals { get => GetValue(ShowOriginalsProperty); set => SetValue(ShowOriginalsProperty, value); }
    /// <summary><para>没有镜像分组时是否显示一个空列表。</para>Whether to show an empty list when no image groups exist.</summary>
    public bool ShowEmptyList { get => GetValue(ShowEmptyListProperty); set => SetValue(ShowEmptyListProperty, value); }
    /// <summary><para>是否正在加载或选择文件。</para>Whether files are being loaded or picked.</summary>
    public bool IsBusy => _loading || _picking;
    /// <summary><para>加载及文件选择状态发生变化。</para>Loading or file-picker state changed.</summary>
    public event EventHandler? StateChanged;
    /// <summary><para>所选镜像集合更新，包括全选、原包开关及项目刷新。</para>Selected images changed, including select-all, originals and project refresh.</summary>
    public event EventHandler? SelectedImagesChanged;

    internal event EventHandler? LayoutChanged;
    internal bool ImagesOnRight => LayoutToggle.IsChecked == true;

    private void OnLayoutToggleChanged(object? sender, RoutedEventArgs e) => LayoutChanged?.Invoke(this, EventArgs.Empty);

    /// <summary><para>初始化虚拟化镜像列表。</para>Initializes the virtualized image lists.</summary>
    public PackImageSelectionView()
    {
        InitializeComponent();
        ShowOriginalBox.IsCheckedChanged += (_, _) => ShowOriginals = ShowOriginalBox.IsChecked == true;
        ImageColumns.SizeChanged += (_, _) => UpdatePageSize();
        RenderGroups();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowOriginalsProperty)
        {
            ShowOriginalBox.IsChecked = ShowOriginals;
            foreach (var group in _groups) group.ShowOriginalImages(ShowOriginals);
        }
        else if (change.Property == ShowEmptyListProperty)
            RenderGroups();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _ = RefreshAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _load?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary><para>刷新当前项目的镜像路径，保留仍存在的镜像选择。</para>Refreshes image paths, preserving selections for existing images.</summary>
    public async Task RefreshAsync()
    {
        if (!_attached) return;
        _load?.Cancel();
        using var request = new CancellationTokenSource();
        _load = request;
        var token = request.Token;
        var context = ProjectContextService.Instance;
        var solutionPath = context.CurrentSolutionPath;
        var projects = context.SubProjects.ToArray();
        if (_solutionPath != solutionPath)
        {
            _solutionPath = solutionPath;
            foreach (var group in _groups) ReleaseGroup(group);
            _groups.Clear();
            _page = 0;
            _externalGroupNumber = 0;
            RenderGroups();
        }
        _loading = true;
        LoadError.IsVisible = false;
        UpdateActions();
        try
        {
            var externalEntries = _groups.Where(group => group.IsExternal).SelectMany(group => group.Entries).ToArray();
            var discovered = await Task.Run(() => (Projects: projects.Select(project =>
            {

                if (token.IsCancellationRequested) return default;
                return (project.Name, Output: EnumerateImages(project.OutputDir, token),
                    Original: EnumerateImages(project.ImagesDir, token), DynamicPartitions: DeliveryDynamicPartitions.Read(project.RootPath));
            }).ToArray(), External: externalEntries.Select(entry => PackImageFileInfo.Read(entry.FilePath)).ToArray()), token);
            if (!_attached || token.IsCancellationRequested) return;
            var previous = _groups.Where(group => !group.IsExternal).ToArray();
            var entries = previous.SelectMany(group => group.Entries)
                .GroupBy(entry => entry.FilePath, PathComparer).ToDictionary(group => group.Key, group => group.First(), PathComparer);
            foreach (var group in previous) { _groups.Remove(group); ReleaseGroup(group); }
            var index = 0;
            foreach (var project in discovered.Projects)
                _groups.Insert(index++, ObserveGroup(new PackImageGroup(project.Name,
                    project.Output.Select(path => GetEntry(path, false))
                        .Concat(project.Original.Select(path => GetEntry(path, true))))
                { DynamicPartitions = project.DynamicPartitions }));
            for (var externalIndex = 0; externalIndex < externalEntries.Length; externalIndex++)
                externalEntries[externalIndex].UpdateFileInfo(discovered.External[externalIndex]);
            ApplyGroupOptions();
            UpdatePageSize();
            RenderGroups();

            PackImageEntry GetEntry(PackImageFileInfo info, bool original)
            {
                var entry = entries.TryGetValue(info.FilePath, out var previousEntry) && previousEntry.IsOriginal == original
                    ? previousEntry : new PackImageEntry(info.FilePath, original);
                entry.UpdateFileInfo(info);
                return entry;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_attached && !token.IsCancellationRequested)
            {
                LoadError.Text = LanguageService.Instance.GetString("Delivery_LoadFailed") + " " + global::UotanStudio.Services.UserError.Describe(ex);
                LoadError.IsVisible = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_load, request))
            {
                _load = null;
                _loading = false;
                UpdateActions();
            }
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static PackImageFileInfo[] EnumerateImages(string directory, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];

        return [.. Directory.EnumerateFiles(directory).TakeWhile(_ => !token.IsCancellationRequested)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is
                ".img" or ".bin" or ".raw" or ".simg" or ".sparse" or ".ext4" or ".erofs" or ".f2fs"
                or ".dat" or ".br" or ".zst" or ".dtb" or ".dtbo"
                || DatFileSet.IsTransferList(path))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(PackImageFileInfo.Read)];
    }

    private void ApplyGroupOptions()
    {
        foreach (var group in _groups)
            group.ShowOriginalImages(ShowOriginals);
    }

    private void UpdatePageSize()
    {
        if (ImageColumns is null) return;
        var available = ImageColumns.Bounds.Width - (_groups.Count < 3 ? 52 : 0);
        var size = Math.Clamp((int)((available + 10) / 300), 1, 3);
        if (size == _pageSize) return;
        var first = _page * _pageSize;
        _pageSize = size;
        _page = first / size;
        RenderGroups();
    }

    private void RenderGroups()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, (_groups.Count - 1) / _pageSize));
        ImageGroupsHost.Children.Clear();
        ImageGroupsHost.ColumnDefinitions.Clear();
        var template = (IDataTemplate)Resources["PackImageGroupTemplate"]!;
        IEnumerable<PackImageGroup> visibleGroups = _groups.Count == 0 && ShowEmptyList
            ? [_emptyGroup] : _groups.Skip(_page * _pageSize).Take(_pageSize);
        foreach (var group in visibleGroups)
        {
            var column = ImageGroupsHost.ColumnDefinitions.Count;
            ImageGroupsHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var content = new ContentControl
            {
                Content = group,
                ContentTemplate = template,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            Grid.SetColumn(content, column);
            ImageGroupsHost.Children.Add(content);
        }
        ImageGroupsHost.IsVisible = _groups.Count > 0 || ShowEmptyList;
        ImageColumns.ColumnDefinitions[0].Width = ImageGroupsHost.IsVisible ? GridLength.Star : GridLength.Auto;
        GroupPager.IsVisible = _groups.Count > _pageSize;
        PreviousPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = (_page + 1) * _pageSize < _groups.Count;
        PageLabel.Text = $"{_page + 1} / {Math.Max(1, (_groups.Count + _pageSize - 1) / _pageSize)}";
        UpdateActions();
    }

    private void UpdateActions()
    {
        AddImagesButton.IsVisible = _groups.Count < 3;
        AddImagesButton.IsEnabled = !IsBusy;
        StateChanged?.Invoke(this, EventArgs.Empty);
        QueueSelectionNotification();
    }

    private PackImageGroup ObserveGroup(PackImageGroup group)
    {
        group.SelectionChanged += OnGroupSelectionChanged;
        return group;
    }

    private void ReleaseGroup(PackImageGroup group)
    {
        group.SelectionChanged -= OnGroupSelectionChanged;
        group.Dispose();
    }

    private void OnGroupSelectionChanged(object? sender, EventArgs e) => QueueSelectionNotification();

    private void QueueSelectionNotification()
    {
        if (_selectionNotificationPending) return;
        _selectionNotificationPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionNotificationPending = false;
            if (_attached) SelectedImagesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnPreviousPageClick(object? sender, RoutedEventArgs e) { _page--; RenderGroups(); }
    private void OnNextPageClick(object? sender, RoutedEventArgs e) { _page++; RenderGroups(); }

    private async void OnAddImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_groups.Count >= 3 || IsBusy || TopLevel.GetTopLevel(this) is not { StorageProvider.CanOpen: true } owner) return;
        _picking = true;
        var solutionPath = ProjectContextService.Instance.CurrentSolutionPath;
        UpdateActions();
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("PackDialog_AddImages"),
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType(LanguageService.Instance.GetString("NewProject_ImageFiles"))
                    { Patterns = ["*.img", "*.bin", "*.raw", "*.simg", "*.sparse", "*.ext4", "*.erofs", "*.f2fs", "*.dat", "*.br", "*.transfer.list", "*.zst", "*.dtb", "*.dtbo"] },
                    FilePickerFileTypes.All
                ]
            });
            if (!_attached || solutionPath != ProjectContextService.Instance.CurrentSolutionPath || _groups.Count >= 3) return;
            var existing = _groups.SelectMany(group => group.FilePaths).ToHashSet(PathComparer);
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>()
                .Distinct(PathComparer).Where(path => !existing.Contains(path)).ToArray();
            if (paths.Length == 0) return;
            var fileInfo = await Task.Run(() => paths.Select(PackImageFileInfo.Read).ToArray());
            if (!_attached || solutionPath != ProjectContextService.Instance.CurrentSolutionPath || _groups.Count >= 3) return;
            var name = string.Format(LanguageService.Instance.GetString("PackDialog_ExtraImages"), ++_externalGroupNumber);
            _groups.Add(ObserveGroup(new PackImageGroup(name, fileInfo.Select(info =>
            {
                var entry = new PackImageEntry(info.FilePath);
                entry.UpdateFileInfo(info);
                return entry;
            }), isExternal: true)));
            ApplyGroupOptions();
            UpdatePageSize();
            _page = (_groups.Count - 1) / _pageSize;
            RenderGroups();
        }
        catch (Exception ex)
        {
            if (_attached && owner is Window window)
                await DialogService.ShowAsync(window, LanguageService.Instance.GetString("NewProject_PickerFailed"), global::UotanStudio.Services.UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
        finally { _picking = false; UpdateActions(); }
    }

    private void OnRemoveImageGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PackImageGroup { IsExternal: true } group }) return;
        _groups.Remove(group);
        ReleaseGroup(group);
        UpdatePageSize();
        RenderGroups();
    }

    internal void SetAssignedPaths(IEnumerable<string> assigned, IEnumerable<string>? readOnlyPaths = null)
    {
        var paths = assigned.ToHashSet(PathComparer);
        var readOnly = readOnlyPaths?.ToHashSet(PathComparer);
        foreach (var entry in _groups.SelectMany(g => g.Entries))
        {
            entry.IsSelectionReadOnly = readOnly?.Contains(entry.FilePath) == true;
            entry.IsSelected = paths.Contains(entry.FilePath);
        }
    }

    internal void RestoreAssignedSources(IEnumerable<string> assigned, IEnumerable<string>? readOnlyPaths = null)
    {
        var paths = assigned.Distinct(PathComparer).ToArray();
        var existing = _groups.SelectMany(g => g.FilePaths).ToHashSet(PathComparer);
        var missing = paths.Where(p => !existing.Contains(p)).ToArray();
        if (missing.Length > 0)
        {
            var name = string.Format(LanguageService.Instance.GetString("PackDialog_ExtraImages"), ++_externalGroupNumber);
            _groups.Add(ObserveGroup(new PackImageGroup(name, missing.Select(path =>
            {
                var entry = new PackImageEntry(path);
                entry.UpdateFileInfo(PackImageFileInfo.Read(path));
                return entry;
            }), isExternal: true)));
            ApplyGroupOptions(); UpdatePageSize(); RenderGroups();
        }
        SetAssignedPaths(paths, readOnlyPaths);
    }

    /// <summary><para>移除上一次交付生成镜像分组，随后把本次导出的镜像作为独立分组加入。</para>
    /// Removes the previous delivery-outputs group, then adds this export's images as its own group.</summary>
    internal void AddDeliveryOutputs(IEnumerable<string> filePaths)
    {
        var previous = _groups.FirstOrDefault(group => group.IsDeliveryOutputs);
        if (previous is not null) { _groups.Remove(previous); ReleaseGroup(previous); }

        var paths = filePaths.Distinct(PathComparer).ToArray();
        var existing = _groups.SelectMany(group => group.FilePaths).ToHashSet(PathComparer);
        var added = paths.Where(path => !existing.Contains(path)).ToArray();
        if (added.Length == 0) { ApplyGroupOptions(); return; }

        var takenNames = _groups.SelectMany(group => group.Entries).Select(entry => entry.Name).ToHashSet(PathComparer);
        var label = LanguageService.Instance.GetString("PackDialog_DeliveryOutputsSource");
        _groups.Add(ObserveGroup(new PackImageGroup(LanguageService.Instance.GetString("PackDialog_DeliveryOutputs"),
            added.Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var entry = new PackImageEntry(path, sourceLabel: takenNames.Contains(fileName) ? label : null);
                entry.UpdateFileInfo(PackImageFileInfo.Read(path));
                return entry;
            }))
        { IsDeliveryOutputs = true }));
        ApplyGroupOptions();
        UpdatePageSize();
        _page = (_groups.Count - 1) / _pageSize;
        RenderGroups();
    }

    /// <summary><para>按登记路径整体重建节点工作流产物分组；路径为空时移除该分组。</para>
    /// Rebuilds the node-outputs group from the recorded paths; removes the group when the list is empty.</summary>
    internal void AddNodeArtifacts(IEnumerable<string> filePaths)
    {
        var previous = _groups.FirstOrDefault(group => group.IsNodeOutputs);
        if (previous is not null) { _groups.Remove(previous); ReleaseGroup(previous); }
        var paths = filePaths.Distinct(PathComparer).ToArray();
        var existing = _groups.SelectMany(group => group.FilePaths).ToHashSet(PathComparer);
        var added = paths.Where(path => !existing.Contains(path)).ToArray();
        if (added.Length == 0) { ApplyGroupOptions(); return; }
        var takenNames = _groups.SelectMany(group => group.Entries).Select(entry => entry.Name).ToHashSet(PathComparer);
        var label = LanguageService.Instance.GetString("PackDialog_NodeOutputsSource");
        _groups.Add(ObserveGroup(new PackImageGroup(LanguageService.Instance.GetString("PackDialog_NodeOutputs"),
            added.Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var entry = new PackImageEntry(path, sourceLabel: takenNames.Contains(fileName) ? label : null);
                entry.UpdateFileInfo(PackImageFileInfo.Read(path));
                return entry;
            }), isExternal: true)
        { IsNodeOutputs = true }));
        ApplyGroupOptions();
        UpdatePageSize();
        _page = (_groups.Count - 1) / _pageSize;
        RenderGroups();
    }

    internal PackImageSelection[] GetSelectedImages() => [.. _groups.SelectMany(group =>
        group.Entries.Where(image => image.IsSelected).Select(image => new PackImageSelection(group.Name, image.FilePath)
        { SizeBytes = image.SizeBytes, DynamicPartitions = group.DynamicPartitions }))];
}