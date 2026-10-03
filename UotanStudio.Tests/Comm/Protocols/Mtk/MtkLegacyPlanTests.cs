using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task PrepareLegacy_ExplicitRangeSteps_ValidatesExtentsAndRejectsSparse()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x5A, 512).ToArray());

        using var plan = PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 512, 512, image)]);

        Assert.Equal(512, plan.TotalBytes);
        Assert.Equal(new byte[] { 8 }, plan.Operations.Select(operation => operation.PartitionId).ToArray());
        Assert.Equal(512, plan.Operations[0].Offset);
        Assert.Equal(MtkFlashPlanState.Prepared, plan.State);

        Assert.Throws<ArgumentOutOfRangeException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Sparse, 512, 512, image)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 1, 512, image)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(9, MtkFlashStepKind.Raw, 512, 512, image)]));
    }

    [Fact]
    public async Task PrepareLegacy_EraseAndSourcePolicies_AreEnforcedBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x5A, 512).ToArray());
        var shortImage = dir.WriteBytes("short.img", [0x01]);
        var before = transport.Writes.Count;

        Assert.Throws<ArgumentException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Erase, 512, 512, image)]));
        Assert.Throws<ArgumentException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Erase, 512, 512, null, 0, Verify: true)]));
        Assert.Throws<InvalidDataException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 512, 1024, image)]));
        Assert.Throws<InvalidDataException>(() => PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 512, 512, shortImage)]));
        Assert.Equal(before, transport.Writes.Count);

        using var erased = PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Erase, 512, 1024)]);
        Assert.True(erased.Operations[0].IsErase);
        Assert.Equal(1024, erased.TotalBytes);
    }

    [Fact]
    public async Task PrepareLegacy_NamedSteps_ResolvesPartitionRelativeExtents()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount));
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x22, 512).ToArray());

        using var plan = PrepareLegacy(snapshot,
            [new MtkLegacyPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image)]);

        Assert.Same(snapshot, plan.GptSnapshot);
        Assert.Equal(40 * 512, plan.Operations[0].Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => PrepareLegacy(snapshot,
            [new MtkLegacyPartitionFlashStep("boot", MtkFlashStepKind.Raw, 2048, 512, null)]));
        Assert.Throws<KeyNotFoundException>(() => PrepareLegacy(snapshot,
            [new MtkLegacyPartitionFlashStep("absent", MtkFlashStepKind.Raw, 0, 512, null)]));
    }

    [Fact]
    public async Task ExecuteLegacyPlanAsync_RawWriteWithVerification_TransfersAndComparesTheRange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x73, 512).ToArray();
        var image = dir.WriteBytes("boot.img", payload);
        using var plan = PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 512, 512, image, Verify: true)]);
        transport.Enqueue([0x5A, 0x69]);
        EnqueueLegacyRead(transport, payload, 0, 512);
        var progress = new LegacyPlanProgressRecorder();
        var before = transport.Writes.Count;

        var result = await session.ExecuteLegacyPlanAsync(plan, progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(512, result.WrittenBytes);
        Assert.Equal(512, result.VerifiedBytes);
        Assert.Equal(0, result.ErasedBytes);
        Assert.Equal(1, result.CompletedOperations);
        var header = transport.Writes.Skip(before).Single(bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(8, header[2]);
        Assert.Equal(512UL, BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(3)));
        Assert.Equal(512UL, BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(11)));
        Assert.Contains((byte)0x73, transport.Writes.Skip(before).SelectMany(bytes => bytes).ToArray());
        Assert.Contains(MtkFlashPhase.Verifying, progress.Phases);
        Assert.Equal(MtkFlashPlanState.Completed, plan.State);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ExecuteLegacyPlanAsync_EraseStep_DrivesTheFormatReportToCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var plan = PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Erase, 512, 1024)]);
        EnqueueLegacyErase(transport);
        var before = transport.Writes.Count;

        var result = await session.ExecuteLegacyPlanAsync(plan, cancellationToken:
            TestContext.Current.CancellationToken);

        Assert.Equal(1024, result.ErasedBytes);
        Assert.Equal(0, result.WrittenBytes);
        var header = transport.Writes.Skip(before).Single(bytes => bytes.Length == 21 && bytes[0] == 0xD4);
        Assert.Equal(1024UL, BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(13)));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ExecuteLegacyPlanAsync_PlanBuiltOnAnotherInventory_IsRefusedWithoutExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var otherTransport = new ScriptedCommTransport();
        using var other = await CreateLegacyGptSessionAsync(otherTransport);
        using var plan = PrepareLegacy(other.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Erase, 512, 512)]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteLegacyPlanAsync(plan,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkFlashPlanState.Prepared, plan.State);
    }

    [Fact]
    public async Task ExecuteLegacyPlanAsync_NamedPlanWithChangedGpt_RefusesBeforeFirstWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount));
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x11, 512).ToArray());
        using var plan = PrepareLegacy(snapshot,
            [new MtkLegacyPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image)]);
        EnqueueLegacyGptReads(transport, new byte[512 * LegacyGptBlockCount]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => session.ExecuteLegacyPlanAsync(plan,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes.Skip(before), bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(MtkFlashPlanState.Failed, plan.State);
    }

    [Fact]
    public async Task ExecuteLegacyPlanAsync_ShortSourceWithExplicitPadding_TransfersZeroFilledTail()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x41, 512 * 3).ToArray();
        var image = dir.WriteBytes("three.img", payload);
        using var plan = PrepareLegacy(session.LegacyStorage!,
            [new MtkLegacyFlashStep(8, MtkFlashStepKind.Raw, 512, 512 * 4, image, PadSourceTail: true)]);
        transport.Enqueue(Convert.FromHexString("5A69696969"));
        var before = transport.Writes.Count;

        var result = await session.ExecuteLegacyPlanAsync(plan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2048, result.WrittenBytes);
        var packets = transport.Writes.Skip(before).Where(bytes => bytes.Length == 512).ToArray();
        Assert.Equal(4, packets.Length);
        Assert.Equal(payload[..512], packets[0]);
        Assert.Equal(payload[1024..1536], packets[2]);
        Assert.Equal(new byte[512], packets[3]);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyPartitionToFileAsync_MatchingSnapshot_PublishesExactlyThePartitionBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount);
        EnqueueLegacyGptReads(transport, disk);
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "boot.img");
        EnqueueLegacyGptReads(transport, disk);
        EnqueueLegacyRead(transport, disk, 40 * 512, 4 * 512);

        await session.ReadLegacyPartitionToFileAsync(snapshot, "boot", target,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(disk.AsSpan(40 * 512, 4 * 512).ToArray(), File.ReadAllBytes(target));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyPartitionToFileAsync_AbsentPartitionName_WritesNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount));
        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "absent.img");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => session.ReadLegacyPartitionToFileAsync(snapshot,
            "absent", target, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    private static MtkLegacyFlashPlan PrepareLegacy(MtkLegacyStorageInfo storage,
        IReadOnlyList<MtkLegacyFlashStep> steps) =>
        MtkLegacyFlashPlan.Prepare(storage, steps, TestContext.Current.CancellationToken);

    private static MtkLegacyFlashPlan PrepareLegacy(MtkLegacyGptSnapshot snapshot,
        IReadOnlyList<MtkLegacyPartitionFlashStep> steps) =>
        MtkLegacyFlashPlan.Prepare(snapshot, steps, TestContext.Current.CancellationToken);

    private static void EnqueueLegacyErase(ScriptedCommTransport transport)
    {
        transport.Enqueue(Convert.FromHexString("5A015A5A5A5A"));
        transport.Enqueue(new byte[4]);
        transport.Enqueue([100]);
        transport.Enqueue([0x5A, 0x5A]);
    }
}

internal sealed class LegacyPlanProgressRecorder : IProgress<MtkFlashPlanProgress>
{
    internal List<MtkFlashPhase> Phases { get; } = [];

    public void Report(MtkFlashPlanProgress value) => Phases.Add(value.Phase);
}