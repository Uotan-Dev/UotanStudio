using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Projects;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>SUPER / ROM 打包表单，只读取镜像文件名并保存本地选择，不创建任何打包产物。</para>
/// SUPER / ROM packaging form; reads image names and records local choices without building output.
/// </summary>
public partial class PackSuperRomWindow : Window
{
    private const int GroupsPerPage = 3;
    private readonly List<PackImageGroup> _groups = [];
    private readonly SubProjectContext[] _projects;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _updatingOptions;
    private bool _loading = true;
    private bool _picking;
    private bool _closed;
    private int _page;
    private int _externalGroupNumber;
    private string _archiveFormat = "ZIP";
    private double _compactHeight = 600;

    /// <summary><para>确认后的表单结果；取消或关闭时为空。</para>Confirmed form result; null on cancellation or close.</summary>
    public PackSuperRomFormResult? Result { get; private set; }

    /// <summary><para>初始化打包窗口，并固定当前项目的子项目列表。</para>Initializes the form using a snapshot of the current sub-projects.</summary>
    public PackSuperRomWindow()
    {
        InitializeComponent();
        var context = ProjectContextService.Instance;
        _projects = context.SubProjects.ToArray();
        OutputLocationBox.Text = _projects.FirstOrDefault()?.OutputDir ?? string.Empty;
        ZipNameBox.Text = (context.SolutionName ?? "firmware") + ".zip";

        PackZipBox.IsCheckedChanged += OnPackageOptionChanged;
        PackSuperBox.IsCheckedChanged += OnPackageOptionChanged;
        PackPayloadBox.IsCheckedChanged += OnPackageOptionChanged;
        ZipFormatBox.IsCheckedChanged += OnArchiveFormatChanged;
        SevenZipFormatBox.IsCheckedChanged += OnArchiveFormatChanged;
        RarFormatBox.IsCheckedChanged += OnArchiveFormatChanged;
        ShowOriginalBox.IsCheckedChanged += OnShowOriginalChanged;
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            foreach (var group in _groups) group.Dispose();
        };
        UpdateCompressionChoices();
        UpdateSections();
        RenderGroups();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        var token = _lifetime.Token;
        try
        {
            // Enumerate names only, without opening or parsing firmware contents.
            // 只枚举文件名，不打开或解析固件内容。
            var discovered = await Task.Run(() => _projects.Select(project =>
            {
                token.ThrowIfCancellationRequested();
                return (project.Name, Output: EnumerateImages(project.OutputDir, token),
                    Original: EnumerateImages(project.ImagesDir, token));
            }).ToArray(), token);
            if (_closed) return;
            foreach (var project in discovered)
                _groups.Add(new PackImageGroup(project.Name,
                    project.Output.Select(path => new PackImageEntry(path))
                        .Concat(project.Original.Select(path => new PackImageEntry(path, isOriginal: true)))));
            ApplyGroupOptions();
            RenderGroups();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { await ShowPickerErrorAsync(ex); }
        finally
        {
            _loading = false;
            if (!_closed) UpdateActions();
        }
    }

    private static string[] EnumerateImages(string directory, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory).Where(path =>
        {
            token.ThrowIfCancellationRequested();
            return Path.GetExtension(path).ToLowerInvariant() is
                ".img" or ".bin" or ".raw" or ".simg" or ".sparse" or ".ext4" or ".erofs" or ".f2fs"
                or ".dat" or ".br" or ".zst" or ".dtb" or ".dtbo";
        }).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void OnPackageOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingOptions) return;
        _updatingOptions = true;
        try
        {
            if (ReferenceEquals(sender, PackSuperBox) && PackSuperBox.IsChecked == true)
                PackPayloadBox.IsChecked = false;
            else if (ReferenceEquals(sender, PackPayloadBox) && PackPayloadBox.IsChecked == true)
                PackSuperBox.IsChecked = false;
        }
        finally { _updatingOptions = false; }
        UpdateSections();
    }

    private void UpdateSections()
    {
        ZipSection.IsVisible = PackZipBox.IsChecked == true;
        SuperSection.IsVisible = PackSuperBox.IsChecked == true;
        PayloadSection.IsVisible = PackPayloadBox.IsChecked == true;
        foreach (var group in _groups) group.ShowSuperColumn = ZipSection.IsVisible && SuperSection.IsVisible;
        UpdateActions();
    }

    private void OnArchiveFormatChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingOptions || sender is not CheckBox selected) return;
        _updatingOptions = true;
        try
        {
            // One archive format is always selected while the archive section is used.
            // 归档选项保持单一格式，避免表单出现没有格式的状态。
            if (selected.IsChecked != true) { selected.IsChecked = true; return; }
            ZipFormatBox.IsChecked = ReferenceEquals(selected, ZipFormatBox);
            SevenZipFormatBox.IsChecked = ReferenceEquals(selected, SevenZipFormatBox);
            RarFormatBox.IsChecked = ReferenceEquals(selected, RarFormatBox);
            _archiveFormat = ZipFormatBox.IsChecked == true ? "ZIP" : SevenZipFormatBox.IsChecked == true ? "7z" : "RAR";
            var name = ZipNameBox.Text ?? string.Empty;
            if (Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".7z" or ".rar" or "")
                ZipNameBox.Text = Path.ChangeExtension(name, _archiveFormat.ToLowerInvariant());
            UpdateCompressionChoices();
        }
        finally { _updatingOptions = false; }
    }

    private void UpdateCompressionChoices()
    {
        var algorithms = _archiveFormat switch
        {
            "7z" => new[] { "LZMA2", "LZMA", "BZip2", "Deflate", "Store" },
            "RAR" => new[] { "RAR", "Store" },
            _ => new[] { "Deflate", "BZip2", "LZMA", "Zstandard", "Store" }
        };
        var choices = algorithms.Select(id => new PackCompressionChoice(id,
            id == "Store" ? LanguageService.Instance.GetString("PackDialog_Store") : id)).ToArray();
        CompressionBox.ItemsSource = choices;
        CompressionBox.SelectedItem = choices[0];
    }

    private void OnShowOriginalChanged(object? sender, RoutedEventArgs e)
    {
        foreach (var group in _groups) group.ShowOriginalImages(ShowOriginalBox.IsChecked == true);
        if (ShowOriginalBox.IsChecked == true)
        {
            _compactHeight = Height;
            var screen = Screens.ScreenFromWindow(this);
            var maximumHeight = screen is null ? 760 : screen.WorkingArea.Height / screen.Scaling - 48;
            Height = Math.Max(Height, Math.Min(760, maximumHeight));
            if (screen is not null)
            {
                var bottom = screen.WorkingArea.Bottom - (int)(Height * screen.Scaling);
                Position = new PixelPoint(Position.X, Math.Max(screen.WorkingArea.Y, Math.Min(Position.Y, bottom)));
            }
        }
        else Height = _compactHeight;
    }

    private void ApplyGroupOptions()
    {
        foreach (var group in _groups)
        {
            group.ShowOriginalImages(ShowOriginalBox.IsChecked == true);
            group.ShowSuperColumn = PackZipBox.IsChecked == true && PackSuperBox.IsChecked == true;
        }
    }

    private void RenderGroups()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, (_groups.Count - 1) / GroupsPerPage));
        ImageGroupsHost.Children.Clear();
        ImageGroupsHost.ColumnDefinitions.Clear();
        var template = (IDataTemplate)Resources["PackImageGroupTemplate"]!;
        foreach (var group in _groups.Skip(_page * GroupsPerPage).Take(GroupsPerPage))
        {
            var column = ImageGroupsHost.ColumnDefinitions.Count;
            ImageGroupsHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var content = new ContentControl
            {
                Content = group, ContentTemplate = template,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            Grid.SetColumn(content, column);
            ImageGroupsHost.Children.Add(content);
        }
        ImageGroupsHost.IsVisible = _groups.Count > 0;
        ImageColumns.ColumnDefinitions[0].Width = _groups.Count > 0 ? GridLength.Star : GridLength.Auto;
        GroupPager.IsVisible = _groups.Count > GroupsPerPage;
        PreviousPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = (_page + 1) * GroupsPerPage < _groups.Count;
        PageLabel.Text = $"{_page + 1} / {Math.Max(1, (_groups.Count + GroupsPerPage - 1) / GroupsPerPage)}";
        UpdateActions();
    }

    private void UpdateActions()
    {
        AddImagesButton.IsVisible = _groups.Count < GroupsPerPage;
        AddImagesButton.IsEnabled = !_loading && !_picking;
        ConfirmButton.IsEnabled = !_loading && !_picking &&
            (PackZipBox.IsChecked == true || PackSuperBox.IsChecked == true || PackPayloadBox.IsChecked == true);
    }

    private void OnPreviousPageClick(object? sender, RoutedEventArgs e) { _page--; RenderGroups(); }
    private void OnNextPageClick(object? sender, RoutedEventArgs e) { _page++; RenderGroups(); }

    private async void OnAddImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_groups.Count >= GroupsPerPage || _loading || _picking || !StorageProvider.CanOpen) return;
        _picking = true;
        UpdateActions();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("PackDialog_AddImages"), AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType(LanguageService.Instance.GetString("NewProject_ImageFiles"))
                    { Patterns = ["*.img", "*.bin", "*.raw", "*.simg", "*.sparse", "*.ext4", "*.erofs", "*.f2fs", "*.dat", "*.br", "*.zst", "*.dtb", "*.dtbo"] },
                    FilePickerFileTypes.All
                ]
            });
            if (_closed || files.Count == 0) return;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var existing = _groups.SelectMany(group => group.FilePaths).ToHashSet(comparer);
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>()
                .Distinct(comparer).Where(path => !existing.Contains(path)).ToArray();
            if (paths.Length == 0)
            {
                await DialogService.ShowAsync(this, Title ?? string.Empty,
                    LanguageService.Instance.GetString("PackDialog_AlreadyAdded"), Ursa.Controls.MessageBoxIcon.Information);
                return;
            }
            var name = string.Format(LanguageService.Instance.GetString("PackDialog_ExtraImages"), ++_externalGroupNumber);
            _groups.Add(new PackImageGroup(name, paths.Select(path => new PackImageEntry(path)), isExternal: true));
            ApplyGroupOptions();
            RenderGroups();
        }
        catch (Exception ex) { await ShowPickerErrorAsync(ex); }
        finally { _picking = false; if (!_closed) UpdateActions(); }
    }

    private void OnRemoveImageGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PackImageGroup { IsExternal: true } group }) return;
        _groups.Remove(group);
        group.Dispose();
        RenderGroups();
    }

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = LanguageService.Instance.GetString("PackDialog_BrowseOutput"), AllowMultiple = false });
            if (!_closed && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) OutputLocationBox.Text = path;
        }
        catch (Exception ex) { await ShowPickerErrorAsync(ex); }
    }

    private Task ShowPickerErrorAsync(Exception exception) => _closed ? Task.CompletedTask
        : DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_PickerFailed"),
            exception.Message, Ursa.Controls.MessageBoxIcon.Error);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled) return;
        var zip = PackZipBox.IsChecked == true;
        var super = PackSuperBox.IsChecked == true;
        var payload = PackPayloadBox.IsChecked == true;
        Result = new PackSuperRomFormResult(zip, super, payload,
            ZipNameBox.Text?.Trim() ?? string.Empty, _archiveFormat,
            (CompressionBox.SelectedItem as PackCompressionChoice)?.Id ?? string.Empty,
            zip && SuperZstBox.IsChecked == true,
            SuperNameBox.Text?.Trim() ?? string.Empty, OutputLocationBox.Text?.Trim() ?? string.Empty,
            PayloadNameBox.Text?.Trim() ?? string.Empty,
            _groups.SelectMany(group => group.Images.Where(image => image.IsSelected).Select(image =>
                new PackSuperRomImageSelection(group.Name, image.FilePath, super && (!zip || image.IncludeInSuper)))).ToArray());
        Close();
    }
}
