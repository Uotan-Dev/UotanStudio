using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Composition;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Keys;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.Shell;
using Ursa.Controls;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Independent manager for portable JSON workflow templates, composite nodes and Lua script nodes.
/// <para>面向可移植 JSON 工作流模板、复合节点与 Lua 脚本节点的独立管理窗口。</para>
/// </summary>
public partial class VisualPluginManagerWindow : Window
{
    private readonly VisualPluginManager _manager = VisualPluginManager.Instance;
    private WindowToastManager? _toastManager;
    private VisualPluginManagerViewModel Model => (VisualPluginManagerViewModel)DataContext!;

    public VisualPluginManagerWindow()
    {
        InitializeComponent();
        DataContext = ServiceHub.TryGet<VisualPluginManagerViewModel>() ?? new VisualPluginManagerViewModel();
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
                Patterns = ["*.uotanplugin", "*.uotanplugin.json", "*.json", "*.lua"]
            }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        VisualPluginPackage package;
        try
        {
            package = string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase)
                ? await BuildScriptPackageAsync(path)
                : await VisualPluginFile.LoadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException)
        {
            ShowFailure(ex);
            return;
        }
        if (await AskInstallConsentAsync(package) is not { } consent) return;
        await RunAsync(() => _manager.InstallPackageAsync(package, consent), "Plugin_Installed");
    }

    private static async Task<VisualPluginPackage> BuildScriptPackageAsync(string luaPath)
    {
        var script = await File.ReadAllTextAsync(luaPath);
        if (script.Length > VisualPluginValidation.MaximumScriptLength)
        {
            throw new InvalidDataException(
                $"Plugin script exceeds {VisualPluginValidation.MaximumScriptLength} characters.");
        }
        var name = Path.GetFileNameWithoutExtension(luaPath);
        return VisualPluginPackageBuilder.CreateScript(name, VisualPluginPackageBuilder.NormalizeId(name), script);
    }

    private async Task<VisualPluginInstallConsent?> AskInstallConsentAsync(VisualPluginPackage package)
    {
        var language = LanguageService.Instance;
        var status = _manager.TrustStore.GetStatus(package);
        var permissions = VisualPluginManager.DescribePermissions(PluginPermissions.Derive(package));
        var publisher = package.Manifest.Publisher;
        var (title, message) = status switch
        {
            PublisherTrustStatus.Unsigned => (
                language.GetString("Plugin_InstallUnsignedTitle"),
                string.Format(language.GetString("Plugin_InstallUnsignedPrompt"), package.Manifest.Name, permissions)),
            PublisherTrustStatus.FingerprintMismatch => (
                language.GetString("Plugin_InstallTrustChangedTitle"),
                string.Format(language.GetString("Plugin_InstallTrustChangedPrompt"), package.Manifest.Name,
                    DescribePublisher(publisher), publisher?.Fingerprint ?? string.Empty, permissions)),
            _ => (
                language.GetString("Plugin_InstallTitle"),
                string.Format(language.GetString("Plugin_InstallTrustPrompt"), package.Manifest.Name,
                    DescribePublisher(publisher), publisher?.Fingerprint ?? string.Empty, permissions))
        };
        if (!await DialogService.ConfirmAsync(this, title, message, language.GetString("Plugin_Install")))
        {
            return null;
        }
        return new VisualPluginInstallConsent
        {
            AllowUntrusted = true,
            TrustPublisher = package.IsSigned,
            Permissions = PluginPermissions.Derive(package)
        };
    }

    private static string DescribePublisher(VisualPluginPublisher? publisher) => publisher switch
    {
        null => string.Empty,
        { Name.Length: > 0 } => $"{publisher.Name} ({publisher.Id})",
        _ => publisher.Id
    };

    private async void SignClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is not { } registration || Model.SelectedPublisherKey is not { } publisher)
        {
            return;
        }
        await RunAsync(async () =>
        {
            using var privateKey = new KeyLibraryService().LoadPublisherPrivateKey(publisher);
            var package = registration.Package;
            PluginSignature.Sign(package, privateKey, publisher.Id, publisher.Name);
            await _manager.UpdateAsync(registration, package, new VisualPluginInstallConsent
            {
                AllowUntrusted = true,
                TrustPublisher = true,
                Permissions = registration.GrantedPermissions
            });
        }, "Plugin_Signed");
    }

    private async void TrustPublisherClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is not { } registration) return;
        var language = LanguageService.Instance;
        var confirmed = await DialogService.ConfirmAsync(this, language.GetString("Plugin_TrustConfirmTitle"),
            string.Format(language.GetString("Plugin_TrustConfirm"), registration.FingerprintText,
                string.IsNullOrEmpty(registration.PublisherId) ? registration.Name : registration.PublisherId),
            language.GetString("Plugin_TrustPublisher"));
        if (!confirmed) return;
        await RunAsync(() =>
        {
            _manager.TrustPublisher(registration.Id);
            return Task.CompletedTask;
        }, "Plugin_Trusted");
    }

    private async void UntrustPublisherClick(object? sender, RoutedEventArgs e)
    {
        if (Model.SelectedPlugin is not { } registration) return;
        var language = LanguageService.Instance;
        var confirmed = await DialogService.ConfirmAsync(this, language.GetString("Plugin_UntrustPublisher"),
            string.Format(language.GetString("Plugin_UntrustConfirm"), registration.PublisherId),
            language.GetString("Plugin_UntrustPublisher"));
        if (!confirmed) return;
        await RunAsync(() =>
        {
            _manager.UntrustPublisher(registration.Id);
            return Task.CompletedTask;
        }, "Plugin_Untrusted");
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
        switch (Model.SelectedPlugin)
        {
            case { Kind: VisualPluginKind.CompositeNode } composite:
                VisualPluginEditorWindow.ShowRegistration(composite);
                break;
            case { Kind: VisualPluginKind.ScriptNode } script:
                new ScriptPluginEditorWindow(script).Show();
                break;
        }
    }

    private async void EnableClick(object? sender, RoutedEventArgs e) => await SetEnabledAsync(true);

    private async void DisableClick(object? sender, RoutedEventArgs e) => await SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        if (Model.SelectedPlugin is not { Kind: VisualPluginKind.CompositeNode or VisualPluginKind.ScriptNode } registration) return;
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
        string.Format(LanguageService.Instance.GetString("Plugin_OperationFailed"), UserError.Describe(exception)),
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