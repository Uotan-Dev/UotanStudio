using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private static readonly (long Offset, long Length)[] XmlGptReads =
    [
        (512, 512),
        (127L * 512, 512),
        (1024, 16384),
        (95L * 512, 16384)
    ];

    private static void EnqueueXmlGptReads(ScriptedCommTransport transport, byte[] disk, int packetLength = 512)
    {
        foreach (var (offset, length) in XmlGptReads)
        {
            EnqueueXmlRead(transport, disk[(int)offset..(int)(offset + length)], packetLength);
        }
    }

    [Fact]
    public async Task ReadXmlGptAsync_ValidTable_BindsSnapshotToRegionAndReportedGeometry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        var before = transport.Writes.Count;

        var snapshot = await session.ReadXmlGptAsync("EMMC-USER",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(session.XmlStorage, snapshot.Storage);
        Assert.Equal("EMMC-USER", snapshot.RegionName);
        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        Assert.Equal(40 * 512, snapshot.Table.Partitions[0].ByteOffset);
        var reads = DecodeXmlWriteFrames(transport.Writes.Skip(before))
            .Where(frame => Encoding.UTF8.GetString(frame).Contains("CMD:READ-FLASH", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            ["0x200", "0xfe00", "0x400", "0xbe00"],
            reads.Select(frame => XDocument.Parse(Encoding.UTF8.GetString(frame).TrimEnd('\0')).Root!
                .Element("arg")!.Element("offset")!.Value).ToArray());
    }

    [Fact]
    public async Task ReadXmlGptAsync_AuthenticatedRegion_IsRejectedBeforeAnyExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXmlGptAsync("EMMC-RPMB",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task PrepareXmlAsync_PreviewResolvesNamedExtentWithoutDeviceWrites()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlGptReads(transport, GptReaderTests.CreateDisk(512, 128, 128));
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x33, 512).ToArray());

        using var preview = await MtkFlashService.PrepareXmlAsync(
            _ => Task.FromResult(session), "EMMC-USER",
            [new MtkXmlPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image)],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, preview.OperationCount);
        Assert.Equal(512, preview.TotalBytes);
        Assert.Equal(40 * 512, preview.Operations[0].Offset);
        Assert.Equal("EMMC-USER", preview.Operations[0].RegionName);
        Assert.Equal(MtkFlashPlanState.Prepared, preview.State);
    }

    [Fact]
    public async Task ExecuteXmlPlanAsync_NamedRawWrite_TransfersExactBytesAndVerifies()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x5A, 512).ToArray();
        var image = dir.WriteBytes("boot.img", payload);

        using var preview = await MtkFlashService.PrepareXmlAsync(
            _ => Task.FromResult(session), "EMMC-USER",
            [new MtkXmlPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image, Verify: true)],
            TestContext.Current.CancellationToken);

        EnqueueXmlGptReads(transport, disk);
        EnqueueXmlWrite(transport, 512, 512);
        EnqueueXmlRead(transport, payload);
        var recorder = new XmlPlanPhaseRecorder();

        var result = await preview.ExecuteAsync(
            recorder, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CompletedOperations);
        Assert.Equal(512, result.WrittenBytes);
        Assert.Equal(512, result.VerifiedBytes);
        Assert.Equal(0, result.ErasedBytes);
        Assert.Equal(MtkFlashPlanState.Completed, preview.State);
        Assert.Equal(
            [MtkFlashPhase.Writing, MtkFlashPhase.Verifying, MtkFlashPhase.OperationCompleted],
            recorder.Phases.Distinct().ToArray());
    }

    [Fact]
    public async Task ExecuteXmlPlanAsync_MetadataChangedAfterPreparation_RefusesBeforeFirstWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        using var dir = new TempDirectory();
        var image = dir.WriteBytes("boot.img", Enumerable.Repeat((byte)0x5A, 512).ToArray());

        using var preview = await MtkFlashService.PrepareXmlAsync(
            _ => Task.FromResult(session), "EMMC-USER",
            [new MtkXmlPartitionFlashStep("boot", MtkFlashStepKind.Raw, 0, 512, image)],
            TestContext.Current.CancellationToken);

        var changed = disk.ToArray();
        Encoding.Unicode.GetBytes("root").CopyTo(changed.AsSpan(1024 + 56));
        GptReaderTests.FixArrays(changed);
        EnqueueXmlGptReads(transport, changed);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => preview.ExecuteAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("XML GPT metadata changed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes.Skip(before),
            bytes => Encoding.UTF8.GetString(bytes).Contains("CMD:WRITE-FLASH", StringComparison.Ordinal));
        Assert.Equal(MtkFlashPlanState.Failed, preview.State);
    }

    [Fact]
    public async Task ExecuteXmlPlanAsync_EraseStep_SendsEraseCommandAndTotalsErasedBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, 128);
        EnqueueXmlGptReads(transport, disk);
        const long offset = 40 * 512;

        using var preview = await MtkFlashService.PrepareXmlAsync(
            _ => Task.FromResult(session), "EMMC-USER",
            [new MtkXmlPartitionFlashStep("boot", MtkFlashStepKind.Erase, 0, 3 * 512)],
            TestContext.Current.CancellationToken);

        EnqueueXmlGptReads(transport, disk);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
        EnqueueXmlText(transport, "0");
        EnqueueXmlText(transport, "100");
        EnqueueXmlText(transport, "OK!EOT");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        EnqueueXmlText(transport, "OK");

        var result = await preview.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3 * 512, result.ErasedBytes);
        Assert.Equal(0, result.WrittenBytes);
        Assert.Contains(transport.Writes,
            bytes => Encoding.UTF8.GetString(bytes).Contains("CMD:ERASE-FLASH", StringComparison.Ordinal));
        Assert.Contains(transport.Writes, bytes =>
            Encoding.UTF8.GetString(bytes).Contains($"0x{offset:x}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MtkXmlFlashPlanPrepare_RejectsSparseUnalignedAuthenticatedShortAndSourcelessSteps()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlGptReads(transport, GptReaderTests.CreateDisk(512, 128, 128));
        using var dir = new TempDirectory();
        var shortImage = dir.WriteBytes("short.img", new byte[256]);
        var writes = new[]
        {
            new MtkXmlFlashStep("EMMC-USER", MtkFlashStepKind.Sparse, 512, 1024, shortImage),
            new MtkXmlFlashStep("EMMC-USER", MtkFlashStepKind.Raw, 512, 511, shortImage),
            new MtkXmlFlashStep("EMMC-RPMB", MtkFlashStepKind.Raw, 0, 512, shortImage),
            new MtkXmlFlashStep("EMMC-USER", MtkFlashStepKind.Raw, 0, 4096, shortImage),
            new MtkXmlFlashStep("EMMC-USER", MtkFlashStepKind.Erase, 0, 512, shortImage),
            new MtkXmlFlashStep("EMMC-USER", MtkFlashStepKind.Raw, 0, 512)
        };
        var failures = new Type[]
        {
            typeof(ArgumentOutOfRangeException), typeof(ArgumentOutOfRangeException),
            typeof(NotSupportedException), typeof(InvalidDataException),
            typeof(ArgumentException), typeof(ArgumentNullException)
        };

        for (var index = 0; index < writes.Length; index++)
        {
            Assert.Throws(failures[index], () => MtkXmlFlashPlan.Prepare(session.XmlStorage!, [writes[index]],
                TestContext.Current.CancellationToken));
        }
    }

    private sealed class XmlPlanPhaseRecorder : IProgress<MtkFlashPlanProgress>
    {
        public List<MtkFlashPhase> Phases { get; } = [];

        public void Report(MtkFlashPlanProgress value) => Phases.Add(value.Phase);
    }
}