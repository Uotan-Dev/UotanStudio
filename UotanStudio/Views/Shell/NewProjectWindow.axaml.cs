using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>新建项目配置窗口；仅维护表单与文件选择，不创建项目或执行解包。</para>
/// New project form; holds local selections without creating projects or unpacking files.
/// </summary>
public partial class NewProjectWindow : Window
{
    private NewProjectImageEntry[] _images = [];
    private bool _updatingSelection;
    private bool _validationRequested;

    /// <summary><para>初始化空白项目表单。</para>Initializes the empty project form.</summary>
    public NewProjectWindow()
    {
        InitializeComponent();
        SelectAllBox.IsCheckedChanged += OnSelectAllChanged;
        UnpackFullyBox.IsCheckedChanged += (_, _) =>
        {
            if (UnpackFullyBox.IsChecked == true) UnpackImagesBox.IsChecked = true;
        };
        UnpackImagesBox.IsCheckedChanged += (_, _) =>
        {
            if (UnpackImagesBox.IsChecked != true) UnpackFullyBox.IsChecked = false;
        };
        ProjectNameBox.TextChanged += (_, _) => UpdateValidation();
        ProjectLocationBox.TextChanged += (_, _) => UpdateValidation();
        Opened += (_, _) => ProjectNameBox.Focus();
        Closed += (_, _) => DetachImageHandlers();
    }

    private async void OnBrowseLocationClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("NewProject_BrowseLocation"),
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                ProjectLocationBox.Text = path;
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(ex);
        }
    }

    private async void OnBrowseImagesClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen) return;
        ImagePicker.IsEnabled = false;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("NewProject_BrowseImages"),
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType(LanguageService.Instance.GetString("NewProject_ImageFiles"))
                    {
                        Patterns = ["*.img", "*.zip", "*.bin", "*.raw", "*.ext4", "*.erofs", "*.f2fs", "*.dat", "*.br", "*.ozip", "*.ofp", "*.ops", "*.pac"]
                    },
                    FilePickerFileTypes.All
                ]
            });
            if (files.Count == 0) return;
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>()
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            if (IsVisible) SetImages(paths.Select(path => new NewProjectImageEntry(path)).ToArray());
        }
        catch (Exception ex)
        {
            await ShowPickerErrorAsync(ex);
        }
        finally
        {
            ImagePicker.IsEnabled = true;
        }
    }

    private Task ShowPickerErrorAsync(Exception exception) => IsVisible
        ? DialogService.ShowAsync(this, LanguageService.Instance.GetString("NewProject_PickerFailed"),
            exception.Message, Ursa.Controls.MessageBoxIcon.Error)
        : Task.CompletedTask;

    private void SetImages(NewProjectImageEntry[] images)
    {
        DetachImageHandlers();
        _images = images;
        foreach (var image in images) image.PropertyChanged += OnImageSelectionChanged;
        ImageList.ItemsSource = images.Chunk(2).Select(pair => new NewProjectImageRow(pair[0], pair.Length > 1 ? pair[1] : null)).ToArray();
        ImagePathsBox.Text = string.Join("; ", images.Select(image => image.FilePath));
        ToolTip.SetTip(ImagePathsBox, ImagePathsBox.Text);
        ImageListPanel.IsVisible = images.Length > 0;
        ClearImagesButton.IsVisible = images.Length > 0;
        UpdateSelectAll();
    }

    private void DetachImageHandlers()
    {
        foreach (var image in _images) image.PropertyChanged -= OnImageSelectionChanged;
    }

    private void OnSelectAllChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        foreach (var image in _images) image.IsSelected = SelectAllBox.IsChecked == true;
        _updatingSelection = false;
    }

    private void OnImageSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSelection && e.PropertyName == nameof(NewProjectImageEntry.IsSelected)) UpdateSelectAll();
    }

    private void UpdateSelectAll()
    {
        _updatingSelection = true;
        SelectAllBox.IsChecked = _images.All(image => image.IsSelected);
        _updatingSelection = false;
    }

    private void OnClearImagesClick(object? sender, RoutedEventArgs e) => SetImages([]);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        _validationRequested = true;
        UpdateValidation();
        if (ProjectNameError.IsVisible) ProjectNameBox.Focus();
        else if (ProjectLocationError.IsVisible) ProjectLocationBox.Focus();
        // Project creation is intentionally deferred; valid input remains available in the form.
    }

    private void UpdateValidation()
    {
        if (!_validationRequested) return;
        SetRequiredError(ProjectNameBox, ProjectNameError);
        SetRequiredError(ProjectLocationBox, ProjectLocationError);
    }

    private static void SetRequiredError(TextBox input, TextBlock error)
    {
        var invalid = string.IsNullOrWhiteSpace(input.Text);
        input.SetValue(DataValidationErrors.HasErrorsProperty, invalid);
        error.IsVisible = invalid;
    }
}

internal sealed partial class NewProjectImageEntry(string filePath) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string Name { get; } = Path.GetFileName(filePath);
    public string Format => "—";
    [ObservableProperty] private bool _isSelected = true;
}

internal sealed record NewProjectImageRow(NewProjectImageEntry Left, NewProjectImageEntry? Right)
{
    public bool HasRight => Right is not null;
}
