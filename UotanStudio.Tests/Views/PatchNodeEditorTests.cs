using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Views;

[CollectionDefinition("Node editor UI", DisableParallelization = true)]
public sealed class NodeEditorUiCollection : ICollectionFixture<NodeEditorUiFixture>;

/// <summary>One dispatcher for UI tests, including third-party control caches with thread affinity.</summary>
public sealed class NodeEditorUiFixture : IAsyncLifetime
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => Session.DisposeAsync();

    /// <summary>Keeps the isolated UI dispatcher alive while async commands post their completion.</summary>
    public Task DispatchAsync(Func<Task> action, CancellationToken token) => Session.Dispatch(() =>
    {
        var work = action();
        if (!work.IsCompleted)
        {
            using var finished = CancellationTokenSource.CreateLinkedTokenSource(token);
            _ = work.ContinueWith(_ => finished.Cancel(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(finished.Token);
        }
        return work;
    }, token).Unwrap();
}

[Collection("Node editor UI")]
public sealed class PatchNodeEditorTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Preview_BinaryReplacement_ShowsBeforeAfterBytesWithoutPublishing()
    {
        using var temp = new Comm.TestDoubles.TempDirectory();
        var path = Path.Combine(temp.Path, "binary");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, [0xAA, 0xBB], token);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var editor = new NodeEditorViewModel();
                var node = PatchNodeViewModel.Create("Binary.Pattern.Replace");
                node.Input.Single(port => port.Title == "Path").InitialValue = path;
                node.Input.Single(port => port.Title == "Find").InitialValue = "AA BB";
                node.Input.Single(port => port.Title == "Replacement").InitialValue = "CC DD";
                editor.Nodes.Add(node);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                try
                {
                    LanguageService.Instance.ApplyLanguage("en-US");
                    window.Show();
                    await editor.PreviewCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var row = Assert.Single(editor.PatchChanges);
                    Assert.True(row.HasBinaryPreview);
                    Assert.Contains("AABB", row.BinaryPreview);
                    Assert.Contains("CCDD", row.BinaryPreview);
                    Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                        block => block.IsVisible && block.Text == row.BinaryPreview);
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                    window.Close();
                }
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, await File.ReadAllBytesAsync(path, token));
    }

    [Theory]
    [InlineData("Workspace.Partition.Resolve", "Layout", "Resolve partition file path", "解析分区文件路径")]
    [InlineData("Binary.Pattern.Find", "Find", "Find masked binary pattern", "查找掩码二进制模式")]
    [InlineData("Binary.Pattern.Replace", "Find", "Replace masked binary pattern", "替换掩码二进制模式")]
    [InlineData("Config.Fstab.Edit", "Text", "Edit Android fstab", "编辑 Android fstab")]
    [InlineData("Config.InitRc.Query", "Text", "Query Android init.rc", "查询 Android init.rc")]
    [InlineData("Image.Text.Read", "Path", "Read text inside filesystem image", "读取文件系统镜像内文本")]
    [InlineData("Image.Text.Stage", "Path", "Write text inside filesystem image", "写入文件系统镜像内文本")]
    [InlineData("Image.Edit.CollectText", "Image Edits", "Collect image text edit", "收集镜像文本修改")]
    [InlineData("Image.Edit.Apply", "Image Edits", "Apply image text edits", "应用镜像文本修改集")]
    [InlineData("Image.File.ReadAttributes", "Path", "Read image file attributes", "读取镜像文件属性")]
    [InlineData("Image.Xattr.Read", "Path", "Read image extended attribute", "读取镜像扩展属性")]
    [InlineData("Image.Xattr.Set", "Path", "Set image user attribute", "设置镜像用户扩展属性")]
    [InlineData("Image.Xattr.Delete", "Path", "Delete image user attribute", "删除镜像用户扩展属性")]
    [InlineData("Image.File.SetAttributes", "Path", "Set image file attributes", "设置镜像文件属性")]
    [InlineData("Config.FileContexts.Edit", "Text", "Edit file_contexts label", "编辑 file_contexts 标签")]
    [InlineData("Config.FsConfig.Edit", "Text", "Edit UotanStudio fs_config", "编辑 UotanStudio fs_config")]
    [InlineData("Config.InitRc.FindReferences", "Text", "Check init service references", "检查 init 服务引用")]
    [InlineData("Config.InitRc.EditArgument", "Text", "Edit Android init.rc argument", "编辑 Android init.rc 参数")]
    [InlineData("Config.InitRc.AddCommand", "Placement", "Add Android init.rc statement", "新增 Android init.rc 条目")]
    [InlineData("Config.InitRc.EditHeader", "Text", "Edit Android init.rc header", "编辑 Android init.rc 头部")]
    [InlineData("Config.InitRc.AddSection", "Text", "Add Android init.rc section", "新增 Android init.rc 区块")]
    [InlineData("Config.InitRc.AddCommand", "Text", "Add Android init.rc statement", "新增 Android init.rc 条目")]
    [InlineData("Config.InitRc.EditCommand", "Text", "Edit Android init.rc", "编辑 Android init.rc")]
    public async Task Properties_TargetQuery_ShowsLocalizedGuidance(string type, string hintPort, string english, string chinese)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var node = PatchNodeViewModel.Create(type);
                editor.Nodes.Add(node);
                editor.SetSelection([node]);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                var language = LanguageService.Instance.CurrentLanguage;
                try
                {
                    window.Show();
                    foreach (var locale in new[] { "en-US", "zh-CN" })
                    {
                        LanguageService.Instance.ApplyLanguage(locale);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        var layout = node.Input.Single(port => port.Title == hintPort);
                        if (type == "Workspace.Partition.Resolve")
                        {
                            Assert.Equal(3, layout.Choices.Count);
                            Assert.Equal("PartitionDirectories", layout.InitialValue);
                        }
                        Assert.All(layout.Choices, choice => Assert.DoesNotContain("Node_Choice_", choice.DisplayName));
                        Assert.Equal(locale == "en-US" ? english : chinese, node.LocalizedTitle);
                        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                            block => block.IsVisible && block.Text == layout.LocalizedHint);
                    }
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(language);
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Run_CompositeRejected_ShowsFailureAndReplacesPreviousInvocationRows()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var editor = new NodeEditorViewModel();
                var package = new UotanStudio.Services.Projects.VisualPluginPackage
                {
                    Kind = UotanStudio.Services.Projects.VisualPluginKind.CompositeNode,
                    Manifest = new UotanStudio.Services.Projects.VisualPluginManifest
                    {
                        Id = "tests.result-ui",
                        Name = "Result test",
                        Version = "1.2.3"
                    }
                };
                editor.Nodes.Add(new CompositeNodeViewModel(package, () => ["Required package is unavailable"]));
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                try
                {
                    window.Show();
                    for (var run = 0; run < 2; run++)
                    {
                        await editor.RunCommand.ExecuteAsync(null);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        var row = Assert.Single(editor.CompositeExecutions);
                        Assert.Equal(1, row.Execution.Invocation);
                        Assert.Equal("1.2.3", row.Execution.Version);
                        Assert.Equal(NodeExecutionStatus.Failed, row.Execution.Status);
                        Assert.Equal("Required package is unavailable", row.Error);
                        Assert.Equal(LanguageService.Instance.GetString("Node_CompositeStatus_Failed"), row.Status);
                        Assert.Equal(LanguageService.Instance.GetString("Node_CompositeApplicability_None"), row.Applicability);
                        Assert.True(editor.HasPatchReview);
                        Assert.True(view.FindControl<Expander>("PatchChangesPanel")!.IsVisible);
                        Assert.Single(view.FindControl<ListBox>("CompositeExecutionList")!.Items);
                    }
                }
                finally
                {
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Run_NotApplicableWithoutFiles_ShowsOutcomePanel()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                using var editor = new NodeEditorViewModel();
                editor.Nodes.Add(PatchNodeViewModel.Create("Validation.Applicability.Check"));
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                try
                {
                    window.Show();
                    await editor.RunCommand.ExecuteAsync(null);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.False(editor.HasPatchChanges);
                    Assert.True(editor.HasPatchReview);
                    Assert.True(view.FindControl<Expander>("PatchChangesPanel")!.IsVisible);
                    Assert.Equal(LanguageService.Instance.GetString("Node_PatchOutcome_NotApplicable"), editor.PatchOutcomeText);
                }
                finally
                {
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Validation.Applicability.Check", "Check patch applicability", "检查补丁适用性")]
    [InlineData("Flow.Applicability.Gate", "Gate patch application", "控制补丁应用")]
    public async Task Properties_Applicability_ExposesLocalizedStateAndBranchGuidance(string type, string english, string chinese)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var node = PatchNodeViewModel.Create(type);
                editor.Nodes.Add(node);
                editor.SetSelection([node]);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                var language = LanguageService.Instance.CurrentLanguage;
                try
                {
                    window.Show();
                    var output = node.Output.Single(port => port.Title == "Applicability");
                    output.Data = "AlreadyApplied";
                    foreach (var locale in new[] { "en-US", "zh-CN" })
                    {
                        LanguageService.Instance.ApplyLanguage(locale);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Assert.Equal(locale == "en-US" ? "Already applied" : "已应用", output.DataPreview);
                        Assert.Equal(locale == "en-US" ? english : chinese, node.LocalizedTitle);
                        var compatible = node.Input.Single(port => port.Title == "Compatible");
                        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                            block => block.IsVisible && block.Text == compatible.LocalizedHint);
                        Assert.Equal("AlreadyApplied", output.Data);
                    }
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(language);
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Properties_FormatCondition_LocalizesChoicesAndEvidenceWithoutChangingStableValues()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(() =>
            {
                Assert.Equal("Inspect file format", LanguageService.Instance.GetEnglish("Node_Validation_Format_Detect"));
                Assert.Contains("探测文件格式", LanguageService.Instance.GetAllTranslations("Node_Validation_Format_Detect"));
                using var editor = new NodeEditorViewModel();
                var node = PatchNodeViewModel.Create("Validation.Format.Detect");
                editor.Nodes.Add(node);
                editor.SetSelection([node]);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                var language = LanguageService.Instance.CurrentLanguage;
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var expected = node.Input.Single(port => port.Title == "Expected Format");
                    var required = node.Input.Single(port => port.Title == "Required Evidence");
                    var output = node.Output.Single(port => port.Title == "Evidence");
                    output.Data = "Signature";
                    var selector = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(),
                        combo => ReferenceEquals(combo.DataContext, expected) && combo.IsVisible);
                    selector.SelectedItem = expected.Choices.Single(choice => choice.Value == "Elf");
                    foreach (var locale in new[] { "zh-CN", "en-US" })
                    {
                        LanguageService.Instance.ApplyLanguage(locale);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Assert.Equal(locale == "zh-CN" ? "探测文件格式" : "Inspect file format", node.LocalizedTitle);
                        Assert.Equal(locale == "zh-CN" ? "内容签名" : "Content signature", output.DataPreview);
                        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                            block => block.IsVisible && block.Text == output.DataPreview);
                        Assert.Equal("Signature", output.Data);
                        Assert.Equal("Elf", expected.InitialValue);
                        Assert.Equal("Structure", required.InitialValue);
                        Assert.All(expected.Choices, choice => Assert.DoesNotContain("Node_Choice_", choice.DisplayName, StringComparison.Ordinal));
                    }
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(language);
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PreviewAndRun_ConfigurationOutput_ShowsReviewThenPublishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-patch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Task dispatch;
            using (ExecutionContext.SuppressFlow())
            {
                dispatch = fixture.DispatchAsync(async () =>
                {
                    using var editor = new NodeEditorViewModel();
                    var writer = PatchNodeViewModel.Create("File.Text.Publish");
                    var output = Path.Combine(root, "output.prop");
                    writer.Input.Single(port => port.Title == "Output Path").InitialValue = output;
                    writer.Input.Single(port => port.Title == "Text").InitialValue = "ro.test=new\n";
                    editor.Nodes.Add(writer);
                    var view = new NodeEditorView { DataContext = editor };
                    var window = new Window { Content = view, Width = 1400, Height = 900 };
                    try
                    {
                        window.Show();
                        window.UpdateLayout();
                        Assert.NotNull(view.FindControl<Button>("PreviewPatchButton"));
                        await editor.PreviewCommand.ExecuteAsync(null);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Assert.False(File.Exists(output));
                        Assert.True(editor.HasPatchChanges);
                        Assert.Equal("ro.test=new\n", Assert.Single(editor.PatchChanges).AfterText);
                        Assert.True(view.FindControl<Expander>("PatchChangesPanel")!.IsVisible);
                        Assert.NotEmpty(view.FindControl<ListBox>("PatchChangeList")!.Items);

                        await editor.RunCommand.ExecuteAsync(null);
                        Assert.True(File.Exists(output));
                        Assert.False(editor.IsExecuting);
                    }
                    finally { window.Close(); }
                }, TestContext.Current.CancellationToken);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal("ro.test=new\n", await File.ReadAllTextAsync(Path.Combine(root, "output.prop"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Config.Text.Edit", "Replacement", "InsertAfter", "\nfirst\nsecond")]
    [InlineData("Config.Json.Edit", "JSON Value", "Set", "{\n  \"enabled\": true\n}")]
    [InlineData("Config.Ini.Edit", "Text", "Delete", "[service]\nenabled=true")]
    public async Task Properties_PatchSelected_ChoiceAndMultilineEditorsUpdatePersistedValues(
        string type, string valuePort, string selectedOperation, string content)
    {
        var session = fixture.Session;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = session.Dispatch(() =>
            {
                using var editor = new NodeEditorViewModel();
                var patch = PatchNodeViewModel.Create(type);
                editor.Nodes.Add(patch);
                editor.SetSelection([patch]);
                var view = new NodeEditorView { DataContext = editor };
                var window = new Window { Content = view, Width = 1400, Height = 900 };
                var originalLanguage = LanguageService.Instance.CurrentLanguage;
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var operation = patch.Input.Single(port => port.Title == "Operation");
                    var selector = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(),
                        combo => ReferenceEquals(combo.DataContext, operation) && combo.IsVisible);
                    selector.SelectedItem = operation.Choices.Single(choice => choice.Value == selectedOperation);
                    var replacement = patch.Input.Single(port => port.Title == valuePort);
                    var text = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                        box => ReferenceEquals(box.DataContext, replacement) && box.IsVisible);
                    Assert.True(text.AcceptsReturn);
                    text.Text = content;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(selectedOperation, operation.InitialValue);
                    Assert.Equal(content, replacement.InitialValue);
                    Assert.True(text.Bounds.Height >= 76);
                    if (type is "Config.Json.Edit" or "Config.Ini.Edit")
                    {
                        var source = patch.Input.Single(port => port.Title == "Text");
                        foreach (var language in new[] { "zh-CN", "en-US" })
                        {
                            LanguageService.Instance.ApplyLanguage(language);
                            Dispatcher.UIThread.RunJobs();
                            window.UpdateLayout();
                            Assert.DoesNotContain("Node_Patch_", source.LocalizedHint, StringComparison.Ordinal);
                            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                                block => block.IsVisible && block.Text == source.LocalizedHint);
                            Assert.Equal(language == "zh-CN" ? type == "Config.Json.Edit" ? "编辑 JSON 值" : "编辑 INI 值"
                                : type == "Config.Json.Edit" ? "Edit JSON value" : "Edit INI value", patch.LocalizedTitle);
                            Assert.Equal(content, replacement.InitialValue);
                        }
                    }
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(originalLanguage);
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}