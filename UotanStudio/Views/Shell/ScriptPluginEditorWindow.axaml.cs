using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Edits the inline Lua source of one installed script-node package.
/// <para>编辑一个已安装脚本节点包的内联 Lua 源码。</para>
/// </summary>
public partial class ScriptPluginEditorWindow : Window
{
    private VisualPluginRegistration? _registration;
    private readonly VisualPluginManager _manager = VisualPluginManager.Instance;
    private WindowToastManager? _toastManager;
    private readonly CancellationTokenSource _lifetime = new();

    public ScriptPluginEditorWindow()
    {
        InitializeComponent();
        SaveScriptButton.IsEnabled = false;
        ExportScriptButton.IsEnabled = false;
        ExportBundleButton.IsEnabled = false;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _toastManager?.CloseAll();
        };
    }

    public ScriptPluginEditorWindow(VisualPluginRegistration registration, VisualPluginManager? manager = null) : this()
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Kind != VisualPluginKind.ScriptNode)
            throw new ArgumentException("Only script nodes expose editable Lua source.", nameof(registration));

        _registration = registration;
        _manager = manager ?? VisualPluginManager.Instance;
        Title = $"{LanguageService.Instance.GetString("Plugin_EditScript")} - {registration.Name}";
        PluginNameText.Text = registration.Name;
        PluginVersionText.Text = registration.Version;
        ScriptEditor.Text = registration.Package.Script;
        SaveScriptButton.IsEnabled = true;
        ExportScriptButton.IsEnabled = true;
        ExportBundleButton.IsEnabled = true;
    }

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        if (_registration is null) return;
        try
        {
            var package = VisualPluginPackageBuilder.CreateScript(_registration.Name, _registration.Id,
                ScriptEditor.Text ?? string.Empty, _registration.Package.Manifest.Author, _registration.Version);
            _registration = await _manager.UpdateAsync(_registration, package, _lifetime.Token);
            ShowResult(LanguageService.Instance.GetString("Plugin_ScriptSaved"), NotificationType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowResult(string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"),
                UserError.Describe(ex)), NotificationType.Error);
        }
        catch (OperationCanceledException) { }
    }

    private async void ExportScriptClick(object? sender, RoutedEventArgs e)
    {
        if (_registration is null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_ExportScript"),
                SuggestedFileName = _registration.Id + ".lua",
                DefaultExtension = "lua",
                FileTypeChoices = [new FilePickerFileType(LanguageService.Instance.GetString("Plugin_ExportScript"))
                {
                    Patterns = ["*.lua"]
                }]
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await File.WriteAllTextAsync(path, ScriptEditor.Text ?? string.Empty, _lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowResult(string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"),
                UserError.Describe(ex)), NotificationType.Error);
        }
        catch (OperationCanceledException) { }
    }

    private async void ExportBundleClick(object? sender, RoutedEventArgs e)
    {
        if (_registration is null) return;
        try
        {
            var package = VisualPluginPackageBuilder.CreateScript(_registration.Name, _registration.Id,
                ScriptEditor.Text ?? string.Empty, _registration.Package.Manifest.Author, _registration.Version);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Plugin_ExportBundle"),
                SuggestedFileName = _registration.Id + ".uotanplugin",
                DefaultExtension = "uotanplugin",
                FileTypeChoices = [new FilePickerFileType(LanguageService.Instance.GetString("Plugin_ExportBundle"))
                {
                    Patterns = ["*.uotanplugin"]
                }]
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await VisualPluginBundle.SaveAsync(path, package, _lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ShowResult(string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"),
                UserError.Describe(ex)), NotificationType.Error);
        }
        catch (OperationCanceledException) { }
    }

    private void ShowResult(string text, NotificationType type)
    {
        _toastManager ??= WindowToastManager.TryGetToastManager(this, out var existing)
            ? existing
            : new WindowToastManager(this) { MaxItems = 1 };
        _toastManager?.Show(text, type, TimeSpan.FromSeconds(type == NotificationType.Error ? 6 : 2),
            showIcon: true, showClose: type == NotificationType.Error);
    }
}