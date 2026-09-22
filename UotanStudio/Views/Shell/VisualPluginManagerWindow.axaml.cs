using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.Shell;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>Independent manager for portable JSON workflow templates and composite nodes.</summary>
public partial class VisualPluginManagerWindow : Window
{
    private readonly VisualPluginManager _manager = VisualPluginManager.Instance;
    private WindowToastManager? _toastManager;
    private VisualPluginManagerViewModel Model => (VisualPluginManagerViewModel)DataContext!;

    public VisualPluginManagerWindow()
    {
        InitializeComponent();
        DataContext = new VisualPluginManagerViewModel();
        Closed += (_, _) =>
        {
            _toastManager?.CloseAll();
            Model.Dispose();
        };
    }

    private async void ImportClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Plugin_Import"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LanguageService.Instance.GetString("Plugin_Import"))
            {
                Patterns = ["*.uotanplugin", "*.uotanplugin.json", "*.json"]
            }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        await RunAsync(() => _manager.InstallAsync(path), "Plugin_Installed");
    }

    private void RefreshClick(object? sender, RoutedEventArgs e) => Model.Refresh();

    private void UseTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is not { Package.Kind: VisualPluginKind.WorkflowTemplate } registration) return;
        if (ProjectOperationsService.Instance.NodeEditorVM is not { } editor)
        {
            ShowResult(LanguageService.Instance.GetString("Plugin_EditorUnavailable"), NotificationType.Warning, true);
            return;
        }

        try
        {
            editor.ImportTemplate(registration.Package);
            ShowResult("Plugin_TemplateApplied", NotificationType.Success);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            ShowFailure(ex);
        }
    }

    private void EditGraphClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is { Kind: VisualPluginKind.CompositeNode } registration)
            new VisualPluginEditorWindow(registration).Show();
    }

    private async void EnableClick(object? sender, RoutedEventArgs e) => await SetEnabledAsync(true);

    private async void DisableClick(object? sender, RoutedEventArgs e) => await SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        if (Model.SelectedPlugin is not { Kind: VisualPluginKind.CompositeNode } registration) return;
        await RunAsync(() => _manager.SetEnabledAsync(registration.Id, enabled));
    }

    private async void RemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is not { } registration) return;
        var language = LanguageService.Instance;
        var confirmed = await DialogService.ConfirmAsync(this, language.GetString("Plugin_Remove"),
            string.Format(language.GetString("Plugin_RemoveConfirm"), registration.Name),
            language.GetString("Plugin_Remove"));
        if (!confirmed) return;
        await RunAsync(() => _manager.RemoveAsync(registration), "Plugin_Removed");
    }

    private async Task RunAsync(Func<Task> operation, string? successKey = null)
    {
        try
        {
            await operation();
            if (successKey is not null) ShowResult(successKey, NotificationType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowFailure(ex);
        }
    }

    private void ShowFailure(Exception exception) => ShowResult(
        string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"), exception.Message),
        NotificationType.Error, true);

    private void ShowResult(string textOrResourceKey, NotificationType type, bool localized = false)
    {
        _toastManager ??= WindowToastManager.TryGetToastManager(this, out var existing)
            ? existing
            : new WindowToastManager(this) { MaxItems = 1 };
        var text = localized ? textOrResourceKey : LanguageService.Instance.GetString(textOrResourceKey);
        _toastManager?.Show(text, type, TimeSpan.FromSeconds(type == NotificationType.Error ? 6 : 2),
            showIcon: true, showClose: type == NotificationType.Error);
    }
}