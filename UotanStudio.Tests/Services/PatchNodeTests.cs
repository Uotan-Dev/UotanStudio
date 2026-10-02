using System.Text;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-patch-tests-" + Guid.NewGuid().ToString("N"));

    public PatchNodeTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", false)]
    [InlineData("EROFS", true)]
    public async Task Execute_ImageReadText_PreviewsOrPublishesExtractedConfiguration(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "ro.test=1\r\n", new UTF8Encoding(false), token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4")
        {
            UotanStudio.Firmware.FileSystem.Ext4.Builder.Ext4Builder.Build(source, image);
        }
        else
        {
            new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image);
        }
        var read = PatchNodeViewModel.Create("Image.Text.Read");
        Set(read, "Path", image);
        Set(read, "File System", format);
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "extracted.prop");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([read, write],
            [Connect(read, "Text", write, "Text"), Connect(read, "Encoding", write, "Encoding")], token,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("ro.test=1\r\n", read.Output.Single(port => port.Title == "Text").Data);
        Assert.Equal(!preview, File.Exists(target));
        if (!preview) { Assert.Equal("ro.test=1\r\n", await File.ReadAllTextAsync(target, token)); }
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    [InlineData("EXT4", false, true)]
    [InlineData("EROFS", false, true)]
    [InlineData("EXT4", true, true)]
    [InlineData("EROFS", true, true)]
    public async Task Execute_ImageWriteText_PreviewsOrPublishesRebuiltImage(string format, bool preview, bool batch = false)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "original", token);
        await File.WriteAllTextAsync(Path.Combine(source, "other.prop"), "other", token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4")
        {
            UotanStudio.Firmware.FileSystem.Ext4.Builder.Ext4Builder.Build(source, image);
        }
        else
        {
            new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image);
        }
        var write = PatchNodeViewModel.Create(batch ? "Image.Edit.Apply" : "Image.Text.Stage");
        Set(write, "Path", image);
        Set(write, "File System", format);
        if (!batch) { Set(write, "Text", "patched"); }
        Set(write, "Image Capacity", "67108864");
        var first = PatchNodeViewModel.Create("Image.Edit.CollectText");
        var second = PatchNodeViewModel.Create("Image.Edit.CollectText");
        Set(first, "Text", "patched");
        Set(second, "Image Path", "/other.prop");
        Set(second, "Text", "second");
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync(batch ? [first, second, write] : [write],
            batch ? [Connect(first, "Image Edits", second, "Image Edits"), Connect(second, "Image Edits", write, "Image Edits")] : [], token,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(Assert.IsType<bool>(write.Output.Single(port => port.Title == "Changed").Data));
        await using var run = new PatchRun();
        Assert.Equal(preview ? "original" : "patched", (await run.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        Assert.Equal(batch && !preview ? "second" : "other", (await run.ReadImageTextAsync(image, format, "/other.prop", token)).Text);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task Execute_ImageAttributes_QueriesStagedChangeAndPublishesByMode(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "original", token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4") { UotanStudio.Firmware.FileSystem.Ext4.Builder.Ext4Builder.Build(source, image); }
        else { new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image); }
        await using var observer = new PatchRun();
        var original = await observer.ReadImageAttributesAsync(image, format, "/build.prop", token);
        var edit = PatchNodeViewModel.Create("Image.File.SetAttributes");
        var read = PatchNodeViewModel.Create("Image.File.ReadAttributes");
        foreach (var node in new[] { edit, read })
        {
            Set(node, "Path", image);
            Set(node, "File System", format);
        }
        Set(edit, "UID", "1234");
        Set(edit, "GID", "2345");
        Set(edit, "Mode", "0755");
        Set(edit, "Image Capacity", "67108864");
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, read],
            [Connect(edit, "Completed", read, "Flow")], token, new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("1234", read.Output.Single(port => port.Title == "UID").Data);
        Assert.Equal("2345", read.Output.Single(port => port.Title == "GID").Data);
        Assert.Equal("0755", read.Output.Single(port => port.Title == "Mode").Data);
        await using var after = new PatchRun();
        var actual = await after.ReadImageAttributesAsync(image, format, "/build.prop", token);
        Assert.Equal(preview ? original.Uid : 1234u, actual.Uid);
        Assert.Equal(preview ? original.Gid : 2345u, actual.Gid);
        Assert.Equal(preview ? original.Mode : (ushort)0x81ED, actual.Mode);
    }

    [Theory]
    [InlineData("user.binary", true)]
    [InlineData("user.empty", true)]
    [InlineData("user.missing", false)]
    public async Task Execute_ImageReadXattr_DistinguishesMissingAndEmpty(string name, bool exists)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "original", TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "system.img");
        new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image,
            new UotanStudio.Firmware.FileSystem.Erofs.ErofsOptions
            {
                FileXattrs = new Dictionary<string, IReadOnlyList<UotanStudio.Firmware.FileSystem.Erofs.Models.ErofsXattrEntry>>
                {
                    ["build.prop"] = [new() { Name = "user.binary", Value = [0, 255] }, new() { Name = "user.empty", Value = [] }]
                }
            });
        var read = PatchNodeViewModel.Create("Image.Xattr.Read");
        Set(read, "Path", image);
        Set(read, "File System", "EROFS");
        Set(read, "Attribute Name", name);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([read], [], TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(exists, read.Output.Single(port => port.Title == "Exists").Data);
        Assert.Equal(name == "user.binary" ? new byte[] { 0, 255 } : [],
            Assert.IsType<byte[]>(read.Output.Single(port => port.Title == "Data").Data));
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task Execute_ImageSetXattr_PreviewsOrPublishesBinaryValue(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "original", token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4") { UotanStudio.Firmware.FileSystem.Ext4.Builder.Ext4Builder.Build(source, image); }
        else { new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image); }
        var edit = PatchNodeViewModel.Create("Image.Xattr.Set");
        var read = PatchNodeViewModel.Create("Image.Xattr.Read");
        foreach (var node in new[] { edit, read })
        {
            Set(node, "Path", image);
            Set(node, "File System", format);
            Set(node, "Attribute Name", "user.patch");
        }
        Set(edit, "Hex Value", "00FF80");
        Set(edit, "Check Old Value", true);
        Set(edit, "Expected Exists", false);
        Set(edit, "Image Capacity", "67108864");

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, read],
            [Connect(edit, "Completed", read, "Flow")], token, new NodeExecutionOptions { Preview = preview });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(new byte[] { 0, 255, 128 },
            Assert.IsType<byte[]>(read.Output.Single(port => port.Title == "Data").Data));
        Assert.True(Assert.IsType<bool>(read.Output.Single(port => port.Title == "Exists").Data));
        await using var after = new PatchRun();
        var physical = await after.ReadImageXattrAsync(image, format, "/build.prop", "user.patch", token);
        if (preview) { Assert.Null(physical); }
        else { Assert.Equal(new byte[] { 0, 255, 128 }, physical); }
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task Execute_ImageDeleteXattr_RemovesExistingEmptyValue(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "original", token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4") { UotanStudio.Firmware.FileSystem.Ext4.Builder.Ext4Builder.Build(source, image); }
        else { new UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuilder().Build(source, image); }
        await using (var seed = new PatchRun())
        {
            Assert.True(await seed.StageImageXattrAsync(image, format,
                new UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemXattrEdit("/build.prop", "user.patch", []),
                64 * 1024 * 1024, 1024 * 1024, token));
            await seed.CompleteAsync(true, token);
        }
        var delete = PatchNodeViewModel.Create("Image.Xattr.Delete");
        Set(delete, "Path", image);
        Set(delete, "File System", format);
        Set(delete, "Attribute Name", "user.patch");
        Set(delete, "Check Old Value", true);
        Set(delete, "Expected Exists", true);
        Set(delete, "Expected Hex Value", "");
        Set(delete, "Image Capacity", "67108864");

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([delete], [], token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(Assert.IsType<bool>(delete.Output.Single(port => port.Title == "Changed").Data));
        await using var after = new PatchRun();
        Assert.Null(await after.ReadImageXattrAsync(image, format, "/build.prop", "user.patch", token));
    }

    [Fact]
    public void ImageXattrEdit_PersistenceRetainsAbsentGuardAndEmptyValue()
    {
        NodeFactoryRegistrar.RegisterAll();
        var node = PatchNodeViewModel.Create("Image.Xattr.Set");
        Set(node, "Hex Value", "");
        Set(node, "Check Old Value", true);
        Set(node, "Expected Exists", false);
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) };

        var runtime = WorkflowRuntimeBuilder.Build(snapshot, false);

        Assert.Empty(runtime.Errors);
        var restored = Assert.IsType<PatchNodeViewModel>(Assert.Single(runtime.Nodes));
        Assert.Equal("", restored.Input.Single(port => port.Title == "Hex Value").InitialValue);
        Assert.True(Assert.IsType<bool>(restored.Input.Single(port => port.Title == "Check Old Value").InitialValue));
        Assert.False(Assert.IsType<bool>(restored.Input.Single(port => port.Title == "Expected Exists").InitialValue));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_FileContextEntry_PreviewsOrPublishesRuleLifecycle(bool preview, bool delete)
    {
        var edit = PatchNodeViewModel.Create("Config.FileContexts.Edit");
        Set(edit, "Operation", delete ? "Delete" : "Add");
        const string rule = "/path u:object_r:vendor_file:s0\n";
        Set(edit, "Text", delete ? "# keep\n" + rule : "# keep\n");
        Set(edit, "Path Expression", "/path");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "entry_file_contexts");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        var expected = delete ? "# keep\n" : "# keep\n" + rule;
        Assert.Equal(expected, edit.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_FileContextEdit_PreviewsOrPublishesLabel(bool preview)
    {
        var edit = PatchNodeViewModel.Create("Config.FileContexts.Edit");
        Set(edit, "Text", "/vendor/bin/test -- u:object_r:old_exec:s0\r\n");
        Set(edit, "Path Expression", "/vendor/bin/test");
        Set(edit, "File Kind", "Regular");
        Set(edit, "Context", "u:object_r:new_exec:s0");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "vendor_file_contexts");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        const string expected = "/vendor/bin/test -- u:object_r:new_exec:s0\r\n";
        Assert.Equal(expected, edit.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_FsConfigEntry_PreviewsOrPublishesAdditionAndDeletion(bool preview, bool delete)
    {
        var edit = PatchNodeViewModel.Create("Config.FsConfig.Edit");
        Set(edit, "Operation", delete ? "Delete" : "Add");
        Set(edit, "Text", delete ? "# keep\nfile 0 0 420 0\n" : "# keep\n");
        Set(edit, "Image Path", "file");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "entry_fs_config");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        var expected = delete ? "# keep\n" : "# keep\nfile 0 0 420 0\n";
        Assert.Equal(expected, edit.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_FsConfigEdit_PreviewsOrPublishesMetadata(bool preview)
    {
        var edit = PatchNodeViewModel.Create("Config.FsConfig.Edit");
        Set(edit, "Text", "vendor/bin/test 0 0 493 0\r\n");
        Set(edit, "Image Path", "vendor/bin/test");
        Set(edit, "UID", "1000");
        Set(edit, "Mode", "0644");
        var query = PatchNodeViewModel.Create("Config.FsConfig.Edit");
        Set(query, "Operation", "Query");
        Set(query, "Image Path", "vendor/bin/test");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "vendor_fs_config");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, query, write],
            [Connect(edit, "Text", query, "Text"), Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("0644", query.Output.Single(port => port.Title == "Mode").Data);
        Assert.Equal(!preview, File.Exists(target));
        if (!preview)
        {
            Assert.Equal("vendor/bin/test 1000 0 420 0\r\n", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_InitRcReferences_AssertionBlocksUnresolvedPublication(bool externalKnown)
    {
        var check = PatchNodeViewModel.Create("Config.InitRc.FindReferences");
        const string text = "on boot\nstart target\n";
        Set(check, "Text", text);
        Set(check, "External Services", externalKnown ? "target" : "");
        var assert = PatchNodeViewModel.Create("Validation.Boolean.Assert");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.checked.rc");
        Set(write, "Output Path", target);
        Set(write, "Text", text);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([check, assert, write],
            [Connect(check, "References Valid", assert, "Condition"), Connect(assert, "Completed", write, "Flow")],
            TestContext.Current.CancellationToken);
        Assert.Equal(externalKnown ? ExecutionStatus.Succeeded : ExecutionStatus.PartiallySucceeded, result.Status);
        Assert.Equal(externalKnown, File.Exists(target));
        Assert.Equal(externalKnown, (bool)check.Output.Single(port => port.Title == "References Valid").Data!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_InitRcArgument_PreviewsOrPublishesEscapedValue(bool preview)
    {
        var edit = PatchNodeViewModel.Create("Config.InitRc.EditArgument");
        Set(edit, "Text", "on boot\n    setprop key old # keep\n");
        Set(edit, "Section Kind", "Action");
        Set(edit, "Selector", "boot");
        Set(edit, "Directive", "setprop");
        Set(edit, "Argument Index", 1d);
        Set(edit, "Value", "new value");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.argument.rc");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        const string expected = "on boot\n    setprop key new\\ value # keep\n";
        Assert.Equal(expected, edit.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_InitRcAnchoredAdd_PreviewsOrPublishesOrderedCommands(bool preview)
    {
        var add = PatchNodeViewModel.Create("Config.InitRc.AddCommand");
        Set(add, "Text", "on boot\n    start target\n");
        Set(add, "Section Kind", "Action");
        Set(add, "Selector", "boot");
        Set(add, "Statement", "setprop ready 1");
        Set(add, "Placement", "BeforeAnchor");
        Set(add, "Anchor Statement", "start target");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.ordered.rc");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([add, write],
            [Connect(add, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        const string expected = "on boot\n    setprop ready 1\n    start target\n";
        Assert.Equal(expected, add.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_InitRcHeader_PreviewsOrPublishesServiceArguments(bool preview)
    {
        var edit = PatchNodeViewModel.Create("Config.InitRc.EditHeader");
        Set(edit, "Text", "service test /old # retain\r\n    user root\r\n");
        Set(edit, "Selector", "test");
        Set(edit, "Replacement", "service test /vendor/bin/test --ready");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.header.rc");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        const string expected = "service test /vendor/bin/test --ready # retain\r\n    user root\r\n";
        Assert.Equal(expected, edit.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, result.PatchOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_InitRcAddSection_PreviewsOrPublishesCompleteService(bool preview)
    {
        var add = PatchNodeViewModel.Create("Config.InitRc.AddSection");
        Set(add, "Text", "# retained\r\n");
        Set(add, "Section Text", "service test /vendor/bin/test\r\n    user system");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.new.rc");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([add, write],
            [Connect(add, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(!preview, File.Exists(target));
        const string expected = "# retained\r\nservice test /vendor/bin/test\r\n    user system\r\n";
        Assert.Equal(expected, add.Output.Single(port => port.Title == "Text").Data);
        if (!preview)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, result.PatchOutcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_InitRcEdit_PreviewsOrPublishesExactText(bool preview, bool add)
    {
        var edit = PatchNodeViewModel.Create(add ? "Config.InitRc.AddCommand" : "Config.InitRc.EditCommand");
        Set(edit, "Text", add ? "service test /vendor/bin/test\r\n" : "service test /vendor/bin/test\r\n\tuser root # keep\r\n");
        Set(edit, "Selector", "test");
        if (add)
        {
            Set(edit, "Statement", "user system");
        }
        else
        {
            Set(edit, "Directive", "user");
            Set(edit, "Replacement", "user system");
        }
        var query = PatchNodeViewModel.Create("Config.InitRc.Query");
        Set(query, "Selector", "test");
        Set(query, "Directive", "user");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "init.test.rc");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, query, write],
            [Connect(edit, "Text", query, "Text"), Connect(edit, "Text", write, "Text")],
            TestContext.Current.CancellationToken, new NodeExecutionOptions { Preview = preview });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("user system", query.Output.Single(port => port.Title == "Value").Data);
        Assert.Equal(!preview, File.Exists(target));
        if (!preview)
        {
            Assert.Equal(add ? "service test /vendor/bin/test\r\n    user system\r\n" : "service test /vendor/bin/test\r\n\tuser system # keep\r\n",
                await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, result.PatchOutcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_FstabEdit_PreservesLayoutThroughPreviewAndPublication(bool preview, bool add)
    {
        var edit = PatchNodeViewModel.Create("Config.Fstab.Edit");
        Set(edit, "Text", "# keep\r\nsystem\t/system ext4 ro wait,logical\r\n");
        if (add)
        {
            Set(edit, "Operation", "Add");
            Set(edit, "File System", "erofs");
        }
        else
        {
            Set(edit, "Operation", "SetFlag");
            Set(edit, "Flag Name", "first_stage_mount");
        }
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        var target = Path.Combine(_root, "fstab.output");
        Set(write, "Output Path", target);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([edit, write],
            [Connect(edit, "Text", write, "Text")], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Preview = preview });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(add ? "# keep\r\nsystem\t/system ext4 ro wait,logical\r\nsystem /system erofs ro wait,logical\r\n"
            : "# keep\r\nsystem\t/system ext4 ro wait,logical,first_stage_mount\r\n",
            edit.Output.Single(port => port.Title == "Text").Data);
        Assert.Equal(!preview, File.Exists(target));
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, result.PatchOutcome);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public async Task ReadWrite_EncodedConfiguration_PreservesBytes(string encoding)
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "input.prop");
        var codec = encoding switch
        {
            "utf8" => (Encoding)new UTF8Encoding(false),
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true)
        };
        await File.WriteAllTextAsync(path, "# 中文\r\nro.test=1\r\n", codec, token);
        var read = await PatchFileService.ReadAsync(path, token);
        var output = Path.Combine(_root, "output.prop");
        await PatchFileService.WriteNewAsync(output, read.Text, read.Encoding, token);
        Assert.Equal(encoding, read.Encoding);
        Assert.Equal(await File.ReadAllBytesAsync(path, token), await File.ReadAllBytesAsync(output, token));
    }

    [Fact]
    public async Task WriteNew_ExistingOutput_PreservesOriginal()
    {
        var token = TestContext.Current.CancellationToken;
        var output = Path.Combine(_root, "existing.prop");
        await File.WriteAllTextAsync(output, "original", token);
        await Assert.ThrowsAsync<IOException>(() => PatchFileService.WriteNewAsync(output, "changed", "utf8", token));
        Assert.Equal("original", await File.ReadAllTextAsync(output, token));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task WriteNew_Cancelled_DoesNotPublishOrLeaveTemporaryFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PatchFileService.WriteNewAsync(Path.Combine(_root, "out.prop"), "new", "utf8", cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Find_RelativePattern_RequiresExactMatches()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_root, "vendor", "etc"));
        var path = Path.Combine(_root, "vendor", "etc", "fstab.test");
        await File.WriteAllTextAsync(path, "test", token);
        Assert.Equal(path, Assert.Single(PatchFileService.Find(_root, "vendor/etc/fstab.*", 1, token)));
        Assert.Throws<InvalidDataException>(() => PatchFileService.Find(_root, "vendor/etc/*", 0, token));
        Assert.Throws<InvalidDataException>(() => PatchFileService.Find(_root, "../*", 1, token));
    }

    [Fact]
    public async Task Execute_PropertyPatchAndWrite_ProducesExpectedFileThroughRealGraph()
    {
        var token = TestContext.Current.CancellationToken;
        var input = Path.Combine(_root, "input.prop");
        var output = Path.Combine(_root, "output.prop");
        await File.WriteAllTextAsync(input, "# keep\nro.test=old\n", token);
        var read = PatchNodeViewModel.Create("File.Text.Read");
        Set(read, "Path", input);
        var edit = PatchNodeViewModel.Create("Config.AndroidProperty.Edit");
        Set(edit, "Key", "ro.test");
        Set(edit, "Value", "new");
        Set(edit, "Check Old Value", true);
        Set(edit, "Expected Old Value", "old");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", output);

        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([read, edit, write],
            [Connect(read, "Text", edit, "Text"), Connect(edit, "Text", write, "Text"), Connect(read, "Encoding", write, "Encoding")], token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("# keep\nro.test=new\n", await File.ReadAllTextAsync(output, token));
        Assert.Equal("# keep\nro.test=old\n", await File.ReadAllTextAsync(input, token));
    }

    [Fact]
    public async Task Execute_FailedAssertion_SkipsDependentWrite()
    {
        var assertion = PatchNodeViewModel.Create("Validation.Boolean.Assert");
        var output = Path.Combine(_root, "must-not-exist");
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", output);
        Set(write, "Text", "unexpected");
        await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([assertion, write],
            [Connect(assertion, "Completed", write, "Flow")], TestContext.Current.CancellationToken);
        Assert.Equal(NodeExecutionStatus.Failed, assertion.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, write.ExecutionStatus);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Execute_MissingLegacyFirmwareInput_ReportsFailureInsteadOfSuccess()
    {
        var unpack = FirmwareNodeViewModel.CreateUnpackNode();
        Set(unpack, "Firmware Path", Path.Combine(_root, "missing.img"));
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([unpack], [], TestContext.Current.CancellationToken);
        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, unpack.ExecutionStatus);
    }

    [Fact]
    public async Task Read_InvalidUtf8_RejectsSilentReplacementCharacters()
    {
        var token = TestContext.Current.CancellationToken;
        var input = Path.Combine(_root, "invalid.prop");
        await File.WriteAllBytesAsync(input, new byte[] { 0xc0, 0xaf }, token);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => PatchFileService.ReadAsync(input, token));
    }

    [Theory]
    [InlineData("14", "13", "15", true)]
    [InlineData("14.0", "14", "14", true)]
    [InlineData("12", "13", "15", false)]
    public async Task Execute_VersionRange_NormalizesAndroidMajorVersions(string value, string minimum, string maximum, bool expected)
    {
        var condition = PatchNodeViewModel.Create("Validation.Condition.Evaluate");
        Set(condition, "Operation", "VersionRange");
        Set(condition, "Value", value);
        Set(condition, "Expected Value", minimum);
        Set(condition, "Upper Version", maximum);
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([condition], [], TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(expected, condition.Output.Single(port => port.Title == "Match").Data);
    }

    [Fact]
    public void BuildRuntime_PersistedOptionsAndMultilineText_RestoresStableValues()
    {
        NodeFactoryRegistrar.RegisterAll();
        var node = PatchNodeViewModel.Create("Config.Text.Edit");
        Set(node, "Operation", "InsertAfter");
        Set(node, "Replacement", "\nfirst\nsecond");
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) };
        var runtime = WorkflowRuntimeBuilder.Build(snapshot, false);
        Assert.Empty(runtime.Errors);
        var restored = Assert.Single(runtime.Nodes);
        Assert.Equal("InsertAfter", restored.Input.Single(port => port.Title == "Operation").SelectedChoice?.Value);
        Assert.Equal("\nfirst\nsecond", restored.Input.Single(port => port.Title == "Replacement").InitialValue);
        Assert.True(restored.Input.Single(port => port.Title == "Replacement").IsMultiline);
    }

    private static void Set(FlowNodeViewModel node, string port, object value) => node.Input.Single(input => input.Title == port).InitialValue = value;

    [Theory]
    [InlineData("Config.Json.Edit", "{\"enabled\":false}", "JSON Value", "true", "{\"enabled\":true}", false)]
    [InlineData("Config.Json.Edit", "{\"enabled\":false}", "JSON Value", "true", "{\"enabled\":true}", true)]
    [InlineData("Config.Ini.Edit", "[service]\nenabled=false\n", "Value", "true", "[service]\nenabled=true\n", false)]
    [InlineData("Config.Ini.Edit", "[service]\nenabled=false\n", "Value", "true", "[service]\nenabled=true\n", true)]
    public async Task Execute_StructuredPatch_PreviewsPublishesAndRestoresOptions(string type, string inputText,
        string valuePort, string value, string expected, bool composite)
    {
        var token = TestContext.Current.CancellationToken;
        var input = Path.Combine(_root, "input.conf");
        var output = Path.Combine(_root, "output.conf");
        await File.WriteAllTextAsync(input, inputText, token);
        var read = PatchNodeViewModel.Create("File.Text.Read");
        Set(read, "Path", input);
        var edit = PatchNodeViewModel.Create(type);
        Set(edit, valuePort, value);
        Set(edit, "Check Old Value", true);
        if (type == "Config.Json.Edit")
        {
            Set(edit, "JSON Pointer", "/enabled");
            Set(edit, "Expected Old JSON", "false");
        }
        else
        {
            Set(edit, "Section", "service");
            Set(edit, "Key", "enabled");
            Set(edit, "Expected Old Value", "false");
        }
        var write = PatchNodeViewModel.Create("File.Text.Publish");
        Set(write, "Output Path", output);
        NodeFactoryRegistrar.RegisterAll();
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([edit]) };
        var restored = WorkflowRuntimeBuilder.Build(snapshot, false);
        Assert.Empty(restored.Errors);
        edit = Assert.IsType<PatchNodeViewModel>(Assert.Single(restored.Nodes));
        FlowNodeViewModel operation = edit;
        if (composite)
        {
            operation = new CompositeNodeViewModel(new VisualPluginPackage
            {
                Kind = VisualPluginKind.CompositeNode,
                Manifest = new VisualPluginManifest { Id = "tests.structured-patch", Name = "Structured patch", RequiredNodeTypes = [type] },
                Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([edit]) },
                Inputs = [new VisualPluginPortDefinition { Id = "source", Name = "Text", NodeInstanceId = edit.InstanceId, PortId = "text", PortType = "Text" }],
                Outputs = [new VisualPluginPortDefinition { Id = "result", Name = "Text", NodeInstanceId = edit.InstanceId, PortId = "text", PortType = "Text" }]
            });
        }
        var connections = new[] { Connect(read, "Text", operation, composite ? "source" : "Text"), Connect(operation, composite ? "result" : "Text", write, "Text"), Connect(read, "Encoding", write, "Encoding") };
        var preview = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([read, operation, write], connections, token,
            new NodeExecutionOptions { Preview = true });
        Assert.Equal(ExecutionStatus.Succeeded, preview.Status);
        Assert.Equal(expected, Assert.Single(preview.PatchChanges).AfterPreview);
        Assert.False(File.Exists(output));
        var run = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([read, operation, write], connections, token);
        Assert.Equal(ExecutionStatus.Succeeded, run.Status);
        Assert.Equal(expected, await File.ReadAllTextAsync(output, token));
        Assert.Equal(inputText, await File.ReadAllTextAsync(input, token));
    }

    [Theory]
    [InlineData("Config.Json.Edit", "{\"x\":null}", "JSON Value", "null", true)]
    [InlineData("Config.Json.Edit", "{}", "JSON Value", "", false)]
    [InlineData("Config.Ini.Edit", "x=", "Value", "", true)]
    [InlineData("Config.Ini.Edit", "", "Value", "", false)]
    public async Task Execute_StructuredQuery_ReportsMissingSeparately(string type, string text, string port, string expected, bool exists)
    {
        var node = PatchNodeViewModel.Create(type);
        Set(node, "Text", text);
        Set(node, "Operation", "Query");
        Set(node, type == "Config.Json.Edit" ? "JSON Pointer" : "Key", type == "Config.Json.Edit" ? "/x" : "x");
        var result = await new NodeExecutionEngine(new MemoryLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(expected, node.Output.Single(output => output.Title == port).Data);
        Assert.Equal(exists, node.Output.Single(output => output.Title == "Exists").Data);
    }

    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = output,
        TargetInstanceId = target.InstanceId,
        TargetConnector = input
    };

    private sealed class MemoryLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }

    public void Dispose() => Directory.Delete(_root, true);
}