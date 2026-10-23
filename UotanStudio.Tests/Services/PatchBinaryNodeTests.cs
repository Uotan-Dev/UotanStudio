using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchBinaryNodeTests
{
    [Fact]
    public async Task Stage_RevertedBinaryEdits_PreservesBoundedOperationEvidence()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "image");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xAA, 65).ToArray(), token);
        await using var run = new PatchRun(preview: true);
        await run.StageBinaryAsync(path, "AA", "", "BB", "", 0, -1, 1, 0, 65, token);
        await run.StageBinaryAsync(path, "BB", "", "AA", "", 0, -1, 1, 0, 65, token);
        await run.CompleteAsync(true, token);

        var change = Assert.Single(run.Changes);
        Assert.Equal(PatchChangeKind.Unchanged, change.Kind);
        Assert.Equal(64, change.BinaryPreviews.Count);
        Assert.True(change.BinaryPreviewTruncated);
        Assert.Equal(Enumerable.Range(0, 64).Select(value => (long)value), change.BinaryPreviews.Select(row => row.Offset));
        Assert.All(change.BinaryPreviews, row =>
        {
            Assert.Equal("AA", row.BeforeHex);
            Assert.Equal("BB", row.AfterHex);
        });
    }

    [Fact]
    public async Task Stage_LongReplacementWindow_CapsBytePreviewAndRetainsWindowLength()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "image");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, new byte[128], token);
        await using var run = new PatchRun(preview: true);
        await run.StageBinaryAsync(path, new string('0', 256), "", new string('F', 256), "", 0, -1, 1, 0, 1, token);
        await run.CompleteAsync(true, token);
        var row = Assert.Single(Assert.Single(run.Changes).BinaryPreviews);
        Assert.Equal(128, row.Length);
        Assert.Equal(new string('0', 128), row.BeforeHex);
        Assert.Equal(new string('F', 128), row.AfterHex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_BinaryReplace_PublishesOnlyOutsidePreview(bool preview)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "image");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, [0xA1, 0xB2, 0xC3], token);
        var node = PatchNodeViewModel.Create("Binary.Search");
        node.Input.Single(port => port.Title == "Operation").InitialValue = "Replace";
        node.Input.Single(port => port.Title == "Path").InitialValue = path;
        node.Input.Single(port => port.Title == "Find").InitialValue = "A1 B2";
        node.Input.Single(port => port.Title == "Replacement").InitialValue = "00 0F";
        node.Input.Single(port => port.Title == "Replace Mask").InitialValue = "00 0F";
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token, new NodeExecutionOptions { Preview = preview });

        Assert.True(result.Status == ExecutionStatus.Succeeded, "DIAG status=" + result.Status + " err=" + result.ErrorMessage + " nodeErr=" + node.ExecutionError);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, result.PatchOutcome);
        Assert.Equal(new byte[] { 0xA1, preview ? (byte)0xB2 : (byte)0xBF, 0xC3 }, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(PatchChangeKind.Modified, Assert.Single(result.PatchChanges).Kind);
        var review = Assert.Single(result.PatchChanges[0].BinaryPreviews);
        Assert.Equal(0, review.Offset);
        Assert.Equal("A1B2", review.BeforeHex);
        Assert.Equal("A1BF", review.AfterHex);
        Assert.False(result.PatchChanges[0].BinaryPreviewTruncated);
    }

    [Fact]
    public async Task Execute_UnguardedBinaryReplace_RejectsBeforeWriting()
    {
        var gate = PatchNodeViewModel.Create("Flow.Applicability.Gate");
        var replace = PatchNodeViewModel.Create("Binary.Search");
        replace.Input.Single(port => port.Title == "Operation").InitialValue = "Replace";
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([gate, replace], [], TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Idle, replace.ExecutionStatus);
        Assert.Empty(result.PatchChanges);
    }

    [Fact]
    public async Task Execute_StagedBinaryPattern_ReadsOverlayAndRetainsOriginalDependency()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "old", token);
        await using var run = new PatchRun();
        await run.StageTextAsync(path, "hello", "utf8", PatchFileMode.Replace, token);
        var node = PatchNodeViewModel.Create("Binary.Search");
        node.Input.Single(port => port.Title == "Operation").InitialValue = "Find";
        node.Input.Single(port => port.Title == "Path").InitialValue = path;
        node.Input.Single(port => port.Title == "Find").InitialValue = "65 6c";
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token, new NodeExecutionOptions { SharedPatches = run });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(new long[] { 1 }, Assert.IsAssignableFrom<IReadOnlyList<long>>(node.Output.Single(port => port.Title == "Offsets").Data));
        await File.WriteAllTextAsync(path, "external", token);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, token));
        Assert.Equal("external", await File.ReadAllTextAsync(path, token));
    }

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}