using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class VisualPluginResourceEditorTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Editor_DependencyInputs_ValidatePreviewAndPersistVersionBounds()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var basePath = Path.Combine(temp.Path, "base.json");
        var dependentPath = Path.Combine(temp.Path, "dependent.json");
        await VisualPluginFile.SaveAsync(basePath, new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.editor-base", Name = "Base", Version = "1.5.0" }
        }, token);
        await VisualPluginFile.SaveAsync(dependentPath, new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.editor-dependent", Name = "Dependent" }
        }, token);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"));
                await manager.InstallAsync(basePath, token);
                var registration = await manager.InstallAsync(dependentPath, token);
                var window = new VisualPluginEditorWindow(registration, manager);
                var editor = Assert.IsType<NodeEditorViewModel>(window.DataContext);
                try
                {
                    window.Show();
                    window.FindControl<Expander>("RequirementsPanel")!.IsExpanded = true;
                    var dependencyInput = window.FindControl<TextBox>("DependenciesInput")!;
                    dependencyInput.Text = "tests.editor-base 2.0.0 3.0.0";
                    Dispatcher.UIThread.RunJobs();
                    var write = PatchNodeViewModel.Create("Patch.WriteText");
                    var target = Path.Combine(temp.Path, "must-not-publish");
                    write.Input.Single(port => port.Title == "Output Path").InitialValue = target;
                    editor.Nodes.Add(write);
                    await editor.PreviewCommand.ExecuteAsync(null);
                    Assert.NotEqual(NodeExecutionStatus.Succeeded, write.ExecutionStatus);
                    Assert.False(File.Exists(target));

                    dependencyInput.Text = "tests.editor-base 2.0.0 1.0.0";
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(window.FindControl<TextBlock>("RequirementsError")!.IsVisible);
                    dependencyInput.Text = "tests.editor-base 1.0.0 2.0.0";
                    window.FindControl<TextBox>("ConflictsInput")!.Text = "tests.other-patch";
                    window.FindControl<TextBox>("VersionInput")!.Text = "2.3.4";
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.FindControl<TextBlock>("RequirementsError")!.IsVisible);
                    await editor.PreviewCommand.ExecuteAsync(null);
                    Assert.Equal(NodeExecutionStatus.Succeeded, write.ExecutionStatus);
                    Assert.False(File.Exists(target));
                    Assert.Empty(registration.Package.Manifest.Dependencies);

                    var save = window.FindControl<Button>("SavePluginButton")!;
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (!save.IsEnabled)
                    {
                        await Task.Delay(10, token);
                    }
                    var updated = manager.Registrations.Single(item => item.Id == registration.Id);
                    var persisted = await VisualPluginFile.LoadAsync(updated.FilePath, token);
                    Assert.Equal("2.3.4", persisted.Manifest.Version);
                    var dependency = Assert.Single(persisted.Manifest.Dependencies);
                    Assert.Equal("1.0.0", dependency.MinimumVersion);
                    Assert.Equal("2.0.0", dependency.MaximumVersionExclusive);
                    Assert.Equal(["tests.other-patch"], persisted.Manifest.Conflicts);
                }
                finally
                {
                    window.Close();
                }
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(45), token);
    }

    [Fact]
    public async Task Editor_AddPreviewSaveRemoveResource_UpdatesVisibleListAndPortablePackage()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "config.prop");
        await File.WriteAllTextAsync(source, "ro.test=value\n", token);
        var packagePath = Path.Combine(temp.Path, "package.json");
        await VisualPluginFile.SaveAsync(packagePath, new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest { Id = "tests.resource-editor", Name = "Resource editor" }
        }, token);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"));
                var registration = await manager.InstallAsync(packagePath, token);
                var window = new VisualPluginEditorWindow(registration, manager);
                var editor = Assert.IsType<NodeEditorViewModel>(window.DataContext);
                var language = LanguageService.Instance.CurrentLanguage;
                try
                {
                    window.Show();
                    window.FindControl<Expander>("ResourcePanel")!.IsExpanded = true;
                    await window.AddResourceAsync("config", source, token);
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var list = window.FindControl<ListBox>("ResourceList")!;
                    Assert.Equal("config", Assert.IsType<VisualPluginAsset>(Assert.Single(list.Items)).Id);
                    Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "config" && block.IsVisible);

                    var resource = PatchNodeViewModel.Create("Patch.Asset");
                    resource.Input.Single(port => port.Title == "Asset ID").InitialValue = "config";
                    editor.Nodes.Add(resource);
                    await editor.PreviewCommand.ExecuteAsync(null);
                    Assert.Equal(NodeExecutionStatus.Succeeded, resource.ExecutionStatus);
                    Assert.Equal("config.prop", resource.Output.Single(port => port.Title == "Asset").DataPreview);

                    foreach (var locale in new[] { "en-US", "zh-CN" })
                    {
                        LanguageService.Instance.ApplyLanguage(locale);
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(locale == "en-US" ? "Select package resource" : "选择包内资源", resource.LocalizedTitle);
                        Assert.Equal(locale == "en-US" ? "Resource ID" : "资源 ID",
                            resource.Input.Single(port => port.Title == "Asset ID").LocalizedTitle);
                        Assert.Equal(locale == "en-US" ? "Add file" : "添加文件", window.FindControl<Button>("AddResourceButton")!.Content);
                    }

                    var save = window.FindControl<Button>("SavePluginButton")!;
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (!save.IsEnabled)
                    {
                        await Task.Delay(10, token);
                    }
                    var installed = Assert.Single(manager.Registrations);
                    Assert.EndsWith(".uotanplugin", installed.FilePath, StringComparison.Ordinal);
                    Assert.Single(installed.Package.Assets);
                    await VisualPluginBundle.CopyAssetAsync(installed.Package, "config", Stream.Null, token);

                    list.SelectedIndex = 0;
                    window.FindControl<Button>("RemoveResourceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Empty(list.Items);
                    Assert.Empty(editor.ResourcePackage!.Assets);
                    Assert.Single(VisualPluginBundle.Load(installed.FilePath).Assets);
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(language);
                    window.Close();
                }
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(45), token);
    }
}