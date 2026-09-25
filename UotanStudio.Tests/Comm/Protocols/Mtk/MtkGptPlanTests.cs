using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task ExecuteXFlashPlanAsync_NamedRawVerifyAndErase_RevalidatesBeforeDestructiveCommands()
    {
        using var dir = new TempDirectory();
        var bytes = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.img", [.. new byte[512], .. bytes]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        using var plan = MtkFlashPlan.Prepare(snapshot,
        [
            new(MtkFlashStepKind.Raw, "boot", 512, 512, path, 512, true),
            new(MtkFlashStepKind.Erase, "boot", 1024, 512)
        ], TestContext.Current.CancellationToken);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Same(snapshot, plan.GptSnapshot);
        Assert.Equal(41 * 512, plan.Operations[0].Offset);
        EnqueueGpt(transport, disk);
        EnqueuePlanWrite(transport);
        EnqueueStorageRead(transport, bytes);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040005);

        var result = await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new MtkFlashPlanResult(2, 512, 512, 512), result);
        Assert.Equal(MtkFlashPlanState.Completed, plan.State);
        var requests = transport.Writes.Skip(before).Where(value => value.Length == 56).ToArray();
        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512, 41 * 512, 41 * 512, 42 * 512 },
            requests.Select(value => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8))));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("attribute")]
    [InlineData("unused-entry")]
    [InlineData("header")]
    public async Task ExecuteXFlashPlanAsync_ChangedGpt_StopsBeforeWriteAndConsumesPlan(string change)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var plan = MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Erase, "boot", 0, 512)], TestContext.Current.CancellationToken);
        switch (change)
        {
            case "range":
                BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(1024 + 32), 41);
                break;
            case "attribute":
                disk[1024 + 48] = 1;
                break;
            case "unused-entry":
                disk[1024 + 128 + 56] = (byte)'x';
                break;
            case "header":
                foreach (var lba in new[] { 1, 127 })
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(lba * 512 + 40), 35);
                }
                break;
        }
        GptReaderTests.FixArrays(disk);
        EnqueueGpt(transport, disk);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkFlashPlanState.Failed, plan.State);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Equal(4, transport.Writes.Skip(before).Count(value => value.Length == 56));
        var after = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(after, transport.Writes.Count);
    }

    [Theory]
    [InlineData(-512, 512)]
    [InlineData(0, 0)]
    [InlineData(0, 2560)]
    [InlineData(1536, 1024)]
    [InlineData(1, 512)]
    [InlineData(long.MaxValue, 512)]
    public async Task Prepare_NamedRangeOutsidePartitionOrUnaligned_RejectsWithoutCommands(long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() => MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Erase, "boot", offset, length)], TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData("BOOT")]
    [InlineData("boot_a")]
    [InlineData("missing")]
    public async Task Prepare_NamedPartitionMissing_DoesNotInferAliasOrSlot(string name)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        Assert.Throws<KeyNotFoundException>(() => MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Erase, name, 0, 512)], TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_NamedSparse_PreservesHolesWithinPartition()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("sparse.img", PlanSparse());
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var plan = MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Sparse, "boot", 0, 2048, path)], TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 40 * 512, 42 * 512 }, plan.Operations.Select(value => value.Offset));
        EnqueueGpt(transport, disk);
        EnqueuePlanWrite(transport);
        EnqueuePlanWrite(transport);

        var result = await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new MtkFlashPlanResult(2, 1024, 0, 0), result);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_NamedPlanQueuedRead_CannotEnterBetweenRevalidationAndErase()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var plan = MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Erase, "boot", 0, 512)], TestContext.Current.CancellationToken);
        EnqueueGpt(transport, disk);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040005);
        EnqueueStorageRead(transport, new byte[512]);
        using var output = new MemoryStream();
        Task? queued = null;
        var before = transport.Writes.Count;
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == 56 && queued is null)
            {
                Assert.Equal(MtkFlashPlanState.Running, plan.State);
                Assert.Throws<InvalidOperationException>(plan.Dispose);
                queued = session.ReadXFlashStorageAsync(8, 0, 512, output, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return [];
        };

        await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512, 40 * 512, 0 },
            transport.Writes.Skip(before).Where(value => value.Length == 56)
                .Select(value => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8))));
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_NamedRevalidationRejected_FaultsSessionAndConsumesPlan()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        using var plan = MtkFlashPlan.Prepare(snapshot,
            [new(MtkFlashStepKind.Erase, "boot", 0, 512)], TestContext.Current.CancellationToken);
        EnqueueStorageRead(transport, disk[512..1024], 0xC0070004);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkFlashPlanState.Failed, plan.State);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }
}