using UotanStudio.Firmware.Patching;
using UotanStudio.Projects;
using UotanStudio.Services.Patching;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchCompositeExecutionTests
{
    [Fact]
    public async Task Stage_ParallelCompositeWriters_PreservesBothParticipantsAndOriginalOnDiscard()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "shared.prop");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "original", token);
        await using var run = new PatchRun();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(branch => Task.Run(async () =>
        {
            var invocation = run.BeginComposite(Node("writer" + branch));
            try
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    ready.SetResult();
                }
                await ready.Task.WaitAsync(token);
                await run.StageTextAsync(path, "branch" + branch, "utf8", PatchFileMode.Replace, token);
            }
            finally
            {
                run.EndComposite(invocation, NodeExecutionStatus.Succeeded, null);
            }
        }, token)));

        await run.CompleteAsync(false, token);

        var change = Assert.Single(run.Changes);
        Assert.Equal(new[] { 1, 2 }, change.StagingInvocations);
        Assert.Equal(PatchChangeKind.Modified, change.Kind);
        Assert.Equal(PatchRunOutcome.Discarded, run.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(path, token));
        Assert.All(run.CompositeExecutions, row => Assert.Null(row.ParentInvocation));
    }

    [Fact]
    public async Task Stage_RepeatedCompositeEditsAndRootRestore_RetainsParticipantsWithUnchangedNetEffect()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "original", token);
        await using var run = new PatchRun(preview: true);
        var first = run.BeginComposite(Node("first"));
        await run.StageTextAsync(path, "changed", "utf8", PatchFileMode.Replace, token);
        await run.StageTextAsync(path, "changed twice", "utf8", PatchFileMode.Replace, token);
        run.EndComposite(first, NodeExecutionStatus.Succeeded, null);
        var second = run.BeginComposite(Node("second"));
        await run.StageCopyAsync(path, path, PatchFileMode.Replace, string.Empty, token);
        run.EndComposite(second, NodeExecutionStatus.Succeeded, null);
        await run.StageTextAsync(path, "original", "utf8", PatchFileMode.Replace, token);

        await run.CompleteAsync(true, token);

        var change = Assert.Single(run.Changes);
        Assert.Equal(PatchChangeKind.Unchanged, change.Kind);
        Assert.Equal(new[] { 0, 1, 2 }, change.StagingInvocations);
        Assert.Equal("original", await File.ReadAllTextAsync(path, token));
        Assert.Equal(PatchRunOutcome.Previewed, run.Outcome);
    }

    [Fact]
    public async Task Record_ParallelNestedInvocations_PreservesParentsAndRestoresScope()
    {
        await using var run = new PatchRun();
        var root = run.BeginComposite(Node("root"));
        var snapshot = run.CompositeExecutions;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(branch => Task.Run(async () =>
        {
            var parent = run.BeginComposite(Node("branch" + branch));
            if (Interlocked.Increment(ref count) == 2)
            {
                entered.SetResult();
            }
            await entered.Task.WaitAsync(token);
            var child = run.BeginComposite(Node("child" + branch));
            run.RecordApplicability(branch == 0 ? PatchApplicability.Applicable : PatchApplicability.AlreadyApplied);
            run.EndComposite(child, NodeExecutionStatus.Succeeded, null);
            run.EndComposite(parent, NodeExecutionStatus.Failed, "failure");
        }, token)));
        run.EndComposite(root, NodeExecutionStatus.Failed, "outer failure");
        var next = run.BeginComposite(Node("next"));
        run.EndComposite(next, NodeExecutionStatus.Succeeded, null);

        var rows = run.CompositeExecutions;
        Assert.Equal(6, rows.Count);
        Assert.Null(rows[0].ParentInvocation);
        Assert.Null(rows[^1].ParentInvocation);
        Assert.Equal(CompositeApplicability.None, rows[^1].Applicability);
        Assert.Equal(CompositeApplicability.Applicable | CompositeApplicability.AlreadyApplied, rows[0].Applicability);
        foreach (var branch in Enumerable.Range(0, 2))
        {
            var parent = Assert.Single(rows, row => row.PackageId == "branch" + branch);
            var child = Assert.Single(rows, row => row.PackageId == "child" + branch);
            Assert.Equal(1, parent.ParentInvocation);
            Assert.Equal(parent.Invocation, child.ParentInvocation);
            var expected = branch == 0 ? CompositeApplicability.Applicable : CompositeApplicability.AlreadyApplied;
            Assert.Equal(expected, parent.Applicability);
            Assert.Equal(expected, child.Applicability);
        }
        Assert.Equal(NodeExecutionStatus.Running, Assert.Single(snapshot).Status);
        Assert.Equal(CompositeApplicability.None, snapshot[0].Applicability);
    }

    [Fact]
    public async Task Record_AdmissionLimit_RejectsWithoutChangingExistingEvidence()
    {
        await using var run = new PatchRun();
        var node = Node("bounded");
        for (var index = 0; index < 4096; index++)
        {
            var invocation = run.BeginComposite(node);
            run.EndComposite(invocation, NodeExecutionStatus.Failed, new string('x', 5000));
        }
        Assert.Throws<InvalidDataException>(() => run.BeginComposite(node));
        run.RecordSkippedComposite(node, CompositeSkipReason.Condition);
        Assert.True(run.CompositeReportTruncated);
        Assert.Equal(4096, run.CompositeExecutions.Count);
        Assert.All(run.CompositeExecutions, row =>
        {
            Assert.Null(row.ParentInvocation);
            Assert.Equal(4096, row.Error!.Length);
        });
    }

    private static CompositeNodeViewModel Node(string id) => new(new VisualPluginPackage
    {
        Kind = VisualPluginKind.CompositeNode,
        Manifest = new VisualPluginManifest { Id = id, Name = id }
    });
}