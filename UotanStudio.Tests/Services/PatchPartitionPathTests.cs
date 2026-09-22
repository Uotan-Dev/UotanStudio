using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchPartitionPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_LinkInWorkspace_OnlyRejectsWhenMappedPathTraversesIt(bool onMappedPath)
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "vendor"));
        var link = Path.Combine(temp.Path, onMappedPath ? "vendor" : "", "etc");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic link creation is unavailable: " + error.Message);
            return;
        }
        try
        {
            await using var run = new PatchRun();
            var workspace = run.OpenWorkspace(temp.Path);
            if (onMappedPath)
            {
                Assert.Throws<IOException>(() => PatchPartitionPath.Resolve(workspace, "vendor", "PartitionDirectories", "/etc/config"));
            }
            else
            {
                Assert.Equal("vendor/etc/config", PatchPartitionPath.Resolve(workspace, "vendor", "PartitionDirectories", "/etc/config"));
            }
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TargetChangedAfterResolution_RejectsPublication(bool initiallyExists)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var target = Path.Combine(temp.Path, "vendor", "etc", "build.prop");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (initiallyExists)
        {
            await File.WriteAllTextAsync(target, "original", token);
        }
        await using var run = new PatchRun();
        var node = PatchNodeViewModel.Create("Workspace.Partition.Resolve");
        node.Input.Single(port => port.Title == "Workspace").InitialValue = run.OpenWorkspace(temp.Path);
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token,
            new NodeExecutionOptions { SharedPatches = run });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(initiallyExists, node.Output.Single(port => port.Title == "Exists").Data);
        var output = Path.Combine(temp.Path, "pending");
        await run.StageTextAsync(output, "not published", "utf8", PatchFileMode.Add, token);
        await File.WriteAllTextAsync(target, "external change", token);

        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, token));

        Assert.False(File.Exists(output));
        Assert.Equal("external change", await File.ReadAllTextAsync(target, token));
    }

    [Theory]
    [InlineData("vendor", "PartitionDirectories", "/etc/config.xml", "vendor/etc/config.xml")]
    [InlineData("product", "PartitionDirectories", "etc/config.xml", "product/etc/config.xml")]
    [InlineData("system", "PartitionRoot", "/build.prop", "build.prop")]
    [InlineData("system", "SystemAsRoot", "/etc/config.xml", "system/etc/config.xml")]
    public async Task Resolve_ExplicitLayout_ReturnsWorkspaceRelativeFile(string partition, string layout, string path, string expected)
    {
        using var temp = new TempDirectory();
        await using var run = new PatchRun();
        Assert.Equal(expected, PatchPartitionPath.Resolve(run.OpenWorkspace(temp.Path), partition, layout, path));
    }

    [Theory]
    [InlineData("vendor", "SystemAsRoot", "/etc/config")]
    [InlineData("../vendor", "PartitionDirectories", "/etc/config")]
    [InlineData("vendor", "Automatic", "/etc/config")]
    [InlineData("vendor", "PartitionDirectories", "/../escape")]
    [InlineData("vendor", "PartitionDirectories", "//server/share")]
    [InlineData("vendor", "PartitionDirectories", "C:/escape")]
    [InlineData("vendor", "PartitionDirectories", "etc\\config")]
    [InlineData("vendor", "PartitionDirectories", "/")]
    public async Task Resolve_InvalidTarget_RejectsWithoutCreatingFiles(string partition, string layout, string path)
    {
        using var temp = new TempDirectory();
        await using var run = new PatchRun();
        var error = Record.Exception(() => PatchPartitionPath.Resolve(run.OpenWorkspace(temp.Path), partition, layout, path));
        Assert.NotNull(error);
        Assert.True(error is InvalidDataException or ArgumentException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Execute_StagedTarget_ReportsExistenceWithoutPublishing()
    {
        using var temp = new TempDirectory();
        await using var run = new PatchRun(preview: true);
        var token = TestContext.Current.CancellationToken;
        var workspace = run.OpenWorkspace(temp.Path);
        var target = workspace.Resolve("vendor/etc/build.prop");
        await run.StageTextAsync(target, "ro.test=1", "utf8", PatchFileMode.Add, token);
        var node = PatchNodeViewModel.Create("Workspace.Partition.Resolve");
        node.Input.Single(port => port.Title == "Workspace").InitialValue = workspace;
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token,
            new NodeExecutionOptions { SharedPatches = run });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("vendor/etc/build.prop", node.Output.Single(port => port.Title == "Relative Path").Data);
        Assert.Equal(target, node.Output.Single(port => port.Title == "Path").Data);
        Assert.True(Assert.IsType<bool>(node.Output.Single(port => port.Title == "Exists").Data));
        Assert.Same(workspace, node.Output.Single(port => port.Title == "Workspace").Data);
        await run.CompleteAsync(true, token);
        Assert.False(File.Exists(target));
    }

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}