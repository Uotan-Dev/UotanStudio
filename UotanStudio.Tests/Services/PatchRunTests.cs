using System.Security.Cryptography;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-patch-run-tests-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public PatchRunTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Complete_MultipleStagedOperations_PublishesOnlyAfterSuccess()
    {
        var existing = await WriteAsync("existing.prop", "original");
        var removed = await WriteAsync("remove.prop", "remove");
        await using var run = new PatchRun();
        var workspace = run.OpenWorkspace(_root);
        await run.StageTextAsync(existing, "changed", "utf8", PatchFileMode.Replace, Token);
        await run.StageCopyAsync(existing, workspace.Resolve("nested/copy.prop"), PatchFileMode.Add, "", Token);
        await run.StageDeleteAsync(removed, "", Token);

        Assert.Equal("original", await File.ReadAllTextAsync(existing, Token));
        Assert.True(File.Exists(removed));
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));
        Assert.Equal("changed", (await run.ReadTextAsync(workspace.Resolve("nested/copy.prop"), Token)).Text);
        Assert.False(await run.ExistsAsync(removed, Token));

        await run.VerifyChangesAsync(workspace, ["existing.prop", "nested/copy.prop", "remove.prop"], Token);
        await run.CompleteAsync(true, Token);

        Assert.Equal(PatchRunOutcome.Applied, run.Outcome);
        Assert.Equal("changed", await File.ReadAllTextAsync(existing, Token));
        Assert.Equal("changed", await File.ReadAllTextAsync(workspace.Resolve("nested/copy.prop"), Token));
        Assert.False(File.Exists(removed));
        Assert.Equal(3, run.Changes.Count);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), path => path.Contains(".uotan-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_Preview_LeavesEveryOriginalUntouched()
    {
        var input = await WriteAsync("input.prop", "original");
        var stamp = File.GetLastWriteTimeUtc(input);
        await using var run = new PatchRun(preview: true);
        await run.StageTextAsync(input, "changed", "utf8", PatchFileMode.Replace, Token);
        await run.StageTextAsync(Path.Combine(_root, "new.prop"), "new", "utf8", PatchFileMode.Add, Token);
        await run.CompleteAsync(true, Token);
        Assert.Equal(PatchRunOutcome.Previewed, run.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(input, Token));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(input));
        Assert.Single(Directory.GetFiles(_root));
        Assert.Equal("original", run.Changes[0].BeforePreview);
        Assert.Equal("changed", run.Changes[0].AfterPreview);
    }

    [Fact]
    public async Task Complete_SourceChangedAfterStaging_DoesNotOverwriteExternalChanges()
    {
        var input = await WriteAsync("input.prop", "original");
        await using var run = new PatchRun();
        await run.StageTextAsync(input, "patch", "utf8", PatchFileMode.Replace, Token);
        await File.WriteAllTextAsync(input, "external", Token);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, Token));
        Assert.Equal(PatchRunOutcome.Conflict, run.Outcome);
        Assert.Equal("external", await File.ReadAllTextAsync(input, Token));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Complete_ReadOnlyDependencyChanged_RejectsOutputs()
    {
        var source = await WriteAsync("source.prop", "version=1");
        var target = Path.Combine(_root, "output.prop");
        await using var run = new PatchRun();
        _ = await run.ReadTextAsync(source, Token);
        await run.StageTextAsync(target, "new", "utf8", PatchFileMode.Add, Token);
        await File.WriteAllTextAsync(source, "version=2", Token);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, Token));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Complete_FileAddedAfterVerification_RejectsUnexpectedEffect()
    {
        await using var run = new PatchRun();
        var workspace = run.OpenWorkspace(_root);
        await run.StageTextAsync(workspace.Resolve("expected.prop"), "first", "utf8", PatchFileMode.Add, Token);
        await run.VerifyChangesAsync(workspace, ["expected.prop"], Token);
        await run.StageTextAsync(workspace.Resolve("unexpected.prop"), "second", "utf8", PatchFileMode.Add, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => run.CompleteAsync(true, Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Complete_AlreadyApplied_PreservesFileTimestamp()
    {
        var input = await WriteAsync("input.prop", "correct");
        var stamp = File.GetLastWriteTimeUtc(input);
        await using var run = new PatchRun();
        await run.StageTextAsync(input, "correct", "utf8", PatchFileMode.Replace, Token);
        await run.CompleteAsync(true, Token);
        Assert.Equal(PatchRunOutcome.AlreadyApplied, run.Outcome);
        Assert.Equal(PatchChangeKind.Unchanged, Assert.Single(run.Changes).Kind);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(input));
    }

    [Fact]
    public async Task Complete_CancelledBeforePublication_DiscardsStaging()
    {
        var input = await WriteAsync("input.prop", "original");
        await using var run = new PatchRun();
        await run.StageTextAsync(input, "patch", "utf8", PatchFileMode.Replace, Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.CompleteAsync(true, cancelled.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(input, Token));
        Assert.Equal(PatchRunOutcome.Discarded, run.Outcome);
    }

    [Fact]
    public async Task Complete_SecondPublicationFails_RestoresFirstFileAndKeepsSecond()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows file sharing semantics to reject the second atomic replacement.");
        var first = await WriteAsync("first.prop", "first-original");
        var second = await WriteAsync("second.prop", "second-original");
        var firstStamp = File.GetLastWriteTimeUtc(first);
        await using var run = new PatchRun();
        await run.StageTextAsync(first, "first-new", "utf8", PatchFileMode.Replace, Token);
        await run.StageTextAsync(second, "second-new", "utf8", PatchFileMode.Replace, Token);
        await using var blocker = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAnyAsync<IOException>(() => run.CompleteAsync(true, Token));

        Assert.Equal(PatchRunOutcome.RolledBack, run.Outcome);
        Assert.Equal("first-original", await File.ReadAllTextAsync(first, Token));
        Assert.Equal("second-original", await File.ReadAllTextAsync(second, Token));
        Assert.Equal(firstStamp, File.GetLastWriteTimeUtc(first));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/../../outside")]
    [InlineData("file:stream")]
    [InlineData("/absolute")]
    public async Task Resolve_EscapingOrAmbiguousPath_RejectsTarget(string relative)
    {
        await using var run = new PatchRun();
        var workspace = run.OpenWorkspace(_root);
        Assert.Throws<InvalidDataException>(() => workspace.Resolve(relative));
    }

    [Fact]
    public async Task StageCopy_WrongSourceHash_DoesNotStageOrPublish()
    {
        var source = await WriteAsync("source", "data");
        await using var run = new PatchRun();
        await Assert.ThrowsAsync<PatchConflictException>(() => run.StageCopyAsync(source, Path.Combine(_root, "copy"), PatchFileMode.Add, new string('0', 64), Token));
        Assert.Empty(run.Changes);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Hash_LargeFile_UsesCompleteStreamingDigest()
    {
        var path = Path.Combine(_root, "large.img");
        var block = new byte[131_072];
        Random.Shared.NextBytes(block);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            for (var index = 0; index < 160; index++)
            {
                await stream.WriteAsync(block, Token);
                expected.AppendData(block);
            }
        }
        await using var run = new PatchRun();
        Assert.Equal(Convert.ToHexString(expected.GetHashAndReset()), await run.HashAsync(path, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_LaterAssertionFails_DiscardsCompositeWrites(bool nested)
    {
        NodeFactoryRegistrar.RegisterAll();
        var path = Path.Combine(_root, "not-published.prop");
        var writer = PatchNodeViewModel.Create("Patch.WriteText");
        Set(writer, "Output Path", path);
        Set(writer, "Text", "new");
        FlowNodeViewModel source = writer;
        var outputPort = "Completed";
        if (nested)
        {
            var package = new VisualPluginPackage
            {
                Kind = VisualPluginKind.CompositeNode,
                Manifest = new VisualPluginManifest { Id = "tests.atomic-patch", Name = "Atomic patch", RequiredNodeTypes = ["Patch.WriteText"] },
                Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([writer]) },
                Outputs = [new VisualPluginPortDefinition { Id = "done", Name = "Done", NodeInstanceId = writer.InstanceId, PortId = "completed", PortType = "Flow" }]
            };
            source = new CompositeNodeViewModel(package);
            outputPort = "Done";
        }
        var fail = PatchNodeViewModel.Create("Patch.Assert");
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([source, fail],
            [Connect(source, outputPort, fail, "Flow")], Token);
        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(PatchRunOutcome.Discarded, result.PatchOutcome);
        Assert.False(File.Exists(path));
        Assert.Single(result.PatchChanges);
        Assert.Equal(new[] { nested ? 1 : 0 }, result.PatchChanges[0].StagingInvocations);
        if (nested)
        {
            var invocation = Assert.Single(result.CompositeExecutions);
            Assert.Equal("tests.atomic-patch", invocation.PackageId);
            Assert.Equal(NodeExecutionStatus.Succeeded, invocation.Status);
            Assert.Null(invocation.Error);
        }
        else
        {
            Assert.Empty(result.CompositeExecutions);
        }
    }

    [Fact]
    public async Task Engine_WorkspaceFileNodes_ReadOverlayAndPublishOnlyNetChanges()
    {
        var source = await WriteAsync("source.prop", "ro.test=ready\n");
        var open = PatchNodeViewModel.Create("Patch.Workspace");
        Set(open, "Root", _root);
        var import = PatchNodeViewModel.Create("Patch.ImportFile");
        Set(import, "Source", source);
        Set(import, "Relative Path", "temporary.prop");
        var copy = PatchNodeViewModel.Create("Patch.CopyFile");
        Set(copy, "Source", "temporary.prop");
        Set(copy, "Relative Path", "result.prop");
        var delete = PatchNodeViewModel.Create("Patch.DeleteFile");
        Set(delete, "Relative Path", "temporary.prop");
        var read = PatchNodeViewModel.Create("Patch.WorkspaceRead");
        Set(read, "Relative Path", "result.prop");
        var verify = PatchNodeViewModel.Create("Patch.VerifyChanges");
        Set(verify, "Expected Paths", "result.prop");
        var hash = PatchNodeViewModel.Create("Patch.VerifyHash");
        Set(hash, "Path", Path.Combine(_root, "result.prop"));
        Set(hash, "Expected Hash", Convert.ToHexString(SHA256.HashData("ro.test=ready\n"u8)));
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([open, import, copy, delete, read, verify, hash],
            [Connect(open, "Workspace", import, "Workspace"), Connect(import, "Workspace", copy, "Workspace"),
             Connect(copy, "Workspace", delete, "Workspace"), Connect(delete, "Workspace", read, "Workspace"),
             Connect(read, "Workspace", verify, "Workspace"), Connect(verify, "Completed", hash, "Flow")], Token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(PatchRunOutcome.Applied, result.PatchOutcome);
        Assert.Equal("ro.test=ready\n", read.Output.Single(port => port.Title == "Text").Data);
        Assert.True((bool)hash.Output.Single(port => port.Title == "Valid").Data!);
        Assert.False(File.Exists(Path.Combine(_root, "temporary.prop")));
        Assert.Equal("ro.test=ready\n", await File.ReadAllTextAsync(Path.Combine(_root, "result.prop"), Token));
        Assert.Single(result.PatchChanges, change => change.Kind != PatchChangeKind.Unchanged);
    }

    [Fact]
    public async Task Engine_Preview_ProducesReportWithoutWriting()
    {
        var path = Path.Combine(_root, "preview.prop");
        var writer = PatchNodeViewModel.Create("Patch.WriteText");
        Set(writer, "Output Path", path);
        Set(writer, "Text", "new");
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([writer], [], Token,
            new NodeExecutionOptions { Preview = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(PatchRunOutcome.Previewed, result.PatchOutcome);
        Assert.False(File.Exists(path));
        Assert.Equal(path, Assert.Single(result.PatchChanges).Path);
    }

    [Fact]
    public async Task Engine_PatchMixedWithImmediateOutput_RejectsBeforeAnyWrite()
    {
        var patch = PatchNodeViewModel.Create("Patch.WriteText");
        Set(patch, "Output Path", Path.Combine(_root, "patch.prop"));
        var unsafeOutput = OutputNodeViewModel.CreateSaveNode();
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([patch, unsafeOutput], [], Token);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(Directory.GetFiles(_root));
    }

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text, Token);
        return path;
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