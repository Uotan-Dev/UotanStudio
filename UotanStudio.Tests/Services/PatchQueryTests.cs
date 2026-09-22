using UotanStudio.Firmware.BoundedContexts.Patching;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-query-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public PatchQueryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Find_StagedOverlay_IncludesAdditionsAndExcludesDeletions()
    {
        var old = Path.Combine(_root, "old.json");
        var added = Path.Combine(_root, "nested", "new.json");
        await File.WriteAllTextAsync(old, "{}", Token);
        await using var run = new PatchRun(true);
        await run.StageDeleteAsync(old, "", Token);
        await run.StageTextAsync(added, "{}", "utf8", PatchFileMode.Add, Token);
        Assert.Equal(added, Assert.Single(await run.FindAsync(_root, "*.json", 1, Token)));
        Assert.Equal(added, Assert.Single(await run.FindAsync(Path.GetDirectoryName(added)!, "*.json", 1, Token)));
        Assert.True(await run.DirectoryExistsAsync(Path.GetDirectoryName(added)!, Token));
        Assert.False(await run.ExistsAsync(_root, Token));
        Assert.False(await run.ExistsAsync(Path.GetPathRoot(_root)!, Token));
        Assert.False(await run.ExistsAsync(old, Token));
        await run.CompleteAsync(true, Token);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(added));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task Complete_ExternalSearchResultChange_RejectsPublication(string operation)
    {
        var source = Path.Combine(_root, "source.json");
        var output = Path.Combine(_root, "output.txt");
        await File.WriteAllTextAsync(source, "{}", Token);
        await using var run = new PatchRun();
        Assert.Single(await run.FindAsync(_root, "*.json", 1, Token));
        await run.StageTextAsync(output, "pending", "utf8", PatchFileMode.Add, Token);
        if (operation == "add") { await File.WriteAllTextAsync(Path.Combine(_root, "new.json"), "{}", Token); }
        else if (operation == "delete") { File.Delete(source); }
        else { File.Move(source, Path.Combine(_root, "renamed.json")); }
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, Token));
        Assert.Equal(PatchRunOutcome.Conflict, run.Outcome);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Complete_NonmatchingExternalFile_DoesNotInvalidateSearch()
    {
        await using var run = new PatchRun();
        Assert.Empty(await run.FindAsync(_root, "*.json", 0, Token));
        await File.WriteAllTextAsync(Path.Combine(_root, "unrelated.txt"), "external", Token);
        await run.CompleteAsync(true, Token);
        Assert.Equal(PatchRunOutcome.None, run.Outcome);
    }

    [Fact]
    public async Task Complete_DirectoryExistenceChanges_RejectsPublication()
    {
        var directory = Path.Combine(_root, "created-externally");
        await using var run = new PatchRun();
        Assert.False(await run.DirectoryExistsAsync(directory, Token));
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, Token));
    }

    [Fact]
    public async Task Find_LaterStagedChangesAndRepeatedSearch_AreNotExternalConflicts()
    {
        await using var run = new PatchRun();
        Assert.Empty(await run.FindAsync(_root, "*.json", 0, Token));
        var target = Path.Combine(_root, "new.json");
        await run.StageTextAsync(target, "{}", "utf8", PatchFileMode.Add, Token);
        Assert.Single(await run.FindAsync(_root, "*.json", 1, Token));
        await run.CompleteAsync(true, Token);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Probe_StagedContentAndMisleadingExtension_UsesBytesAndRecordsDependency()
    {
        var target = Path.Combine(_root, "misleading.ext4");
        await File.WriteAllTextAsync(target, "<root />", Token);
        await using var run = new PatchRun(true);
        Assert.Equal(PatchFormatKind.Xml, (await run.ProbeFormatAsync(target, PatchFormatKind.Unknown, Token)).Format);
        await run.StageTextAsync(target, "{}", "utf8", PatchFileMode.Replace, Token);
        Assert.Equal(PatchFormatKind.Json, (await run.ProbeFormatAsync(target, PatchFormatKind.Unknown, Token)).Format);
        await File.WriteAllTextAsync(target, "external", Token);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, Token));
    }

    [Fact]
    public async Task Execute_StagedDiscoveryConditionsAndFormatCheck_UsesOneViewAndRestoresSettings()
    {
        var open = PatchNodeViewModel.Create("Patch.Workspace");
        Set(open, "Root", _root);
        var stage = PatchNodeViewModel.Create("Patch.StageText");
        Set(stage, "Relative Path", "nested/config.bin");
        Set(stage, "Operation", "Add");
        Set(stage, "Text", "{\"enabled\":true}");
        var find = PatchNodeViewModel.Create("Patch.WorkspaceFind");
        Set(find, "Pattern", "nested/*.bin");
        var check = PatchNodeViewModel.Create("Patch.CheckFormat");
        Set(check, "Expected Format", "Json");
        var condition = PatchNodeViewModel.Create("Patch.Condition");
        Set(condition, "Operation", "FileExists");
        var directory = PatchNodeViewModel.Create("Patch.Condition");
        Set(directory, "Operation", "DirectoryExists");
        Set(directory, "Value", Path.Combine(_root, "nested"));
        NodeFactoryRegistrar.RegisterAll();
        var snapshot = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([check]) };
        check = Assert.IsType<PatchNodeViewModel>(Assert.Single(WorkflowRuntimeBuilder.Build(snapshot, false).Nodes));
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([open, stage, find, check, condition, directory],
            [Connect(open, "Workspace", stage, "Workspace"), Connect(stage, "Workspace", find, "Workspace"),
             Connect(find, "Path", check, "Path"), Connect(find, "Path", condition, "Value"), Connect(stage, "Completed", directory, "Flow")],
            Token, new NodeExecutionOptions { Preview = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("nested/config.bin", find.Output.Single(port => port.Title == "Relative Path").Data);
        Assert.True(Assert.IsType<bool>(check.Output.Single(port => port.Title == "Match").Data));
        Assert.True(Assert.IsType<bool>(condition.Output.Single(port => port.Title == "Match").Data));
        Assert.True(Assert.IsType<bool>(directory.Output.Single(port => port.Title == "Match").Data));
        Assert.Equal("Json", check.Output.Single(port => port.Title == "Format").Data);
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));
    }

    [Fact]
    public async Task Execute_FailedFormatAssertion_DiscardsPriorStaging()
    {
        var writer = PatchNodeViewModel.Create("Patch.WriteText");
        var path = Path.Combine(_root, "not-json.bin");
        Set(writer, "Output Path", path);
        Set(writer, "Text", "{broken}");
        var check = PatchNodeViewModel.Create("Patch.CheckFormat");
        var assert = PatchNodeViewModel.Create("Patch.Assert");
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([writer, check, assert],
            [Connect(writer, "Path", check, "Path"), Connect(check, "Match", assert, "Condition")], Token);
        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(PatchRunOutcome.Discarded, result.PatchOutcome);
        Assert.False(File.Exists(path));
    }

    private static void Set(FlowNodeViewModel node, string port, object value) => node.Input.Single(input => input.Title == port).InitialValue = value;
    private static NodeConnection Connect(FlowNodeViewModel source, string output, FlowNodeViewModel target, string input) => new()
    {
        SourceInstanceId = source.InstanceId,
        SourceConnector = output,
        TargetInstanceId = target.InstanceId,
        TargetConnector = input
    };
    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
    public void Dispose() => Directory.Delete(_root, true);
}