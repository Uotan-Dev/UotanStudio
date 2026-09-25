using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task ExecuteXFlashPlanAsync_EraseWriteVerify_CompletesOnceWithPhaseProgress()
    {
        using var dir = new TempDirectory();
        var bytes = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.img", bytes);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
        [
            new(MtkFlashStepKind.Erase, 8, 0, 512),
            new(MtkFlashStepKind.Raw, 8, 0, 512, path, Verify: true)
        ], TestContext.Current.CancellationToken);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040005);
        EnqueuePlanWrite(transport);
        EnqueueStorageRead(transport, bytes);
        var progress = new List<MtkFlashPlanProgress>();

        var result = await session.ExecuteXFlashPlanAsync(plan, new InlinePlanProgress(progress.Add),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new MtkFlashPlanResult(2, 512, 512, 512), result);
        Assert.Equal(MtkFlashPlanState.Completed, plan.State);
        Assert.Contains(progress, value => value.Phase == MtkFlashPhase.Erasing && value.ProcessedBytes == 0);
        Assert.Contains(progress, value => value.Phase == MtkFlashPhase.Writing && value.ProcessedBytes == 512);
        Assert.Contains(progress, value => value.Phase == MtkFlashPhase.Verifying && value.ProcessedBytes == 512);
        Assert.Equal(2, progress.Count(value => value.Phase == MtkFlashPhase.OperationCompleted));
        var count = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(count, transport.Writes.Count);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_SparseExtents_WritesRawAndFillWithoutTouchingHole()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("sparse.img", PlanSparse());
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
            [new(MtkFlashStepKind.Sparse, 8, 512, 1536, path)], TestContext.Current.CancellationToken);
        EnqueuePlanWrite(transport);
        EnqueuePlanWrite(transport);
        var before = transport.Writes.Count;

        var result = await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1024, result.WrittenBytes);
        Assert.Equal(512ul, BinaryPrimitives.ReadUInt64LittleEndian(transport.Writes[before + 3].AsSpan(8)));
        Assert.Equal(1536ul, BinaryPrimitives.ReadUInt64LittleEndian(transport.Writes[before + 13].AsSpan(8)));
        Assert.All(transport.Writes[before + 9], value => Assert.Equal(0x4A, value));
        Assert.Equal(20, transport.Writes.Count - before);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_VerificationMismatch_StopsWithoutReplayAndKeepsSynchronizedSession()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
        [
            new(MtkFlashStepKind.Raw, 8, 0, 512, path, Verify: true),
            new(MtkFlashStepKind.Erase, 8, 512, 512)
        ], TestContext.Current.CancellationToken);
        EnqueuePlanWrite(transport);
        EnqueueStorageRead(transport, Enumerable.Repeat((byte)1, 512).ToArray());
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(16, transport.Writes.Count - before);
        Assert.Equal(MtkFlashPlanState.Failed, plan.State);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_QueuedRead_DoesNotInterleaveAndRunningPlanCannotBeDisposed()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
        [
            new(MtkFlashStepKind.Raw, 8, 0, 512, path, Verify: true),
            new(MtkFlashStepKind.Raw, 8, 512, 512, path)
        ], TestContext.Current.CancellationToken);
        EnqueuePlanWrite(transport);
        EnqueueStorageRead(transport, new byte[512]);
        EnqueuePlanWrite(transport);
        EnqueueStorageRead(transport, new byte[512]);
        using var destination = new MemoryStream();
        Task? queued = null;
        var before = transport.Writes.Count;
        var progress = new InlinePlanProgress(value =>
        {
            if (value.Phase == MtkFlashPhase.Verifying && value.ProcessedBytes == 0)
            {
                Assert.Throws<InvalidOperationException>(plan.Dispose);
                var count = transport.Writes.Count;
                queued = session.ReadXFlashStorageAsync(8, 0, 512, destination,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(count, transport.Writes.Count);
            }
        });

        await session.ExecuteXFlashPlanAsync(plan, progress, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(Convert.FromHexString("04000100"), transport.Writes[before + 17]);
        Assert.Equal(Convert.FromHexString("05000100"), transport.Writes[before + 27]);
        Assert.Equal(512, destination.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteXFlashPlanAsync_Cancellation_DistinguishesBeforeStartAndBetweenOperations(bool between)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
        [
            new(MtkFlashStepKind.Raw, 8, 0, 512, path),
            new(MtkFlashStepKind.Raw, 8, 512, 512, path)
        ], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        EnqueuePlanWrite(transport);
        var before = transport.Writes.Count;
        if (!between)
        {
            cancellation.Cancel();
        }
        var progress = new InlinePlanProgress(value =>
        {
            if (value.Phase == MtkFlashPhase.OperationCompleted)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExecuteXFlashPlanAsync(plan, progress, cancellationToken: cancellation.Token));

        Assert.Equal(before + (between ? 10 : 0), transport.Writes.Count);
        Assert.Equal(between ? MtkFlashPlanState.Failed : MtkFlashPlanState.Prepared, plan.State);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_DifferentStorageSnapshot_RejectsBeforeConsumingPlan()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var other = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        using var plan = MtkFlashPlan.Prepare(other, [new(MtkFlashStepKind.Erase, 8, 0, 512)],
            TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkFlashPlanState.Prepared, plan.State);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_AllSparseHoles_CompletesAsExplicitNoOp()
    {
        using var dir = new TempDirectory();
        var bytes = PlanSparse()[..40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 0xCAC3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 12);
        var path = dir.WriteBytes("holes.img", bytes);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
            [new(MtkFlashStepKind.Sparse, 8, 0, 1536, path)], TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        var result = await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new MtkFlashPlanResult(0, 0, 0, 0), result);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkFlashPlanState.Completed, plan.State);
    }

    internal static void EnqueuePlanWrite(ScriptedCommTransport transport)
    {
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteXFlashPlanAsync_InFlightFailure_FaultsSessionAndConsumesPlan(bool cancel)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var plan = MtkFlashPlan.Prepare(session.XFlashStorage!,
        [
            new(MtkFlashStepKind.Raw, 8, 0, 512, path),
            new(MtkFlashStepKind.Erase, 8, 512, 512)
        ], TestContext.Current.CancellationToken);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, cancel ? 0u : 0xC0070004u);
        using var cancellation = new CancellationTokenSource();
        var before = transport.Writes.Count;
        var progress = new InlinePlanProgress(value =>
        {
            if (cancel && value.Phase == MtkFlashPhase.Writing && value.ProcessedBytes == 512)
            {
                cancellation.Cancel();
            }
        });

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.ExecuteXFlashPlanAsync(plan, progress, cancellationToken: cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() =>
                session.ExecuteXFlashPlanAsync(plan, progress, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(MtkFlashPlanState.Failed, plan.State);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_QueuedPlanCancelled_RemainsPrepared()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var active = MtkFlashPlan.Prepare(session.XFlashStorage!, [new(MtkFlashStepKind.Erase, 8, 0, 512)],
            TestContext.Current.CancellationToken);
        using var queuedPlan = MtkFlashPlan.Prepare(session.XFlashStorage!, [new(MtkFlashStepKind.Erase, 8, 512, 512)],
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040005);
        Task<MtkFlashPlanResult>? queued = null;
        var before = transport.Writes.Count;
        var progress = new InlinePlanProgress(value =>
        {
            if (value.Phase == MtkFlashPhase.Erasing)
            {
                queued = session.ExecuteXFlashPlanAsync(queuedPlan, cancellationToken: cancellation.Token);
                cancellation.Cancel();
            }
        });

        await session.ExecuteXFlashPlanAsync(active, progress, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkFlashPlanState.Completed, active.State);
        Assert.Equal(MtkFlashPlanState.Prepared, queuedPlan.State);
    }

    private sealed class InlinePlanProgress(Action<MtkFlashPlanProgress> report) : IProgress<MtkFlashPlanProgress>
    {
        public void Report(MtkFlashPlanProgress value) => report(value);
    }
}