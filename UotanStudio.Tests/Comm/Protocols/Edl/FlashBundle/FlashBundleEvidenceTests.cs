using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public sealed class FlashBundleEvidenceTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public async Task FlashBundleAsync_AllItemsSucceed_RecordsCompletedEvidenceForPreflightAndProgram()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 4));
        var transport = EvidenceTransport();
        using var session = BundleSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Null(result.FailureKind);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(CommandOutcomeKind.Completed, result.FinalOutcome);
        Assert.True(result.AllPhasesCompleted);
        var preflight = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Preflight);
        Assert.Equal(CommandOutcomeKind.Completed, preflight.Outcome);
        var program = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Program);
        Assert.Equal("boot.img", program.FileName);
        Assert.Equal(CommandOutcomeKind.Completed, program.Outcome);
        Assert.Equal(4096 * 4, program.AcknowledgedBytes);
        Assert.NotNull(program.Range);
        Assert.Equal(0, program.Range!.PhysicalPartitionNumber);
        Assert.Equal(4, program.Range.BlockCount);
        Assert.Equal(4096, program.Range.SectorSize);
        Assert.Equal(result.Items.Count, result.Evidence.Count);
        Assert.All(result.Evidence, evidence => Assert.Equal("edl", evidence.Protocol));
        Assert.Equal(result.Items.Select(static item => item.Outcome),
            result.Evidence.Select(static evidence => evidence.Outcome));
        Assert.True(result.StartedAtUtc <= result.CompletedAtUtc);
        Assert.NotNull(program.ElapsedMs);
    }

    [Fact]
    public async Task FlashBundleAsync_PayloadCompletionNak_RecordedAsUnknownWithCompletionLostAndReconnect()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 4));
        var transport = EvidenceTransport(rejectPayload: 1);
        using var session = BundleSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(EdlFailureKind.CompletionLost, result.FailureKind);
        Assert.True(result.RequiresReconnect);
        Assert.Equal(CommandOutcomeKind.Unknown, result.FinalOutcome);
        Assert.False(result.AllPhasesCompleted);
        var program = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Program);
        Assert.Equal(CommandOutcomeKind.Unknown, program.Outcome);
        Assert.Equal(EdlFailureKind.CompletionLost, program.FailureKind);
        Assert.Equal(0, program.AcknowledgedBytes);
    }

    [Fact]
    public async Task FlashBundleAsync_SecondItemFailsAfterFirstCompleted_PartialCompletionIsDistinguishable()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 2), ("system.img", 4096 * 2));
        var transport = EvidenceTransport(rejectPayload: 2);
        using var session = BundleSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var programs = result.Items.Where(item => item.Kind == FlashBundleItemKind.Program).ToArray();
        Assert.Equal(2, programs.Length);
        Assert.Equal(CommandOutcomeKind.Completed, programs[0].Outcome);
        Assert.Equal(4096 * 2, programs[0].AcknowledgedBytes);
        Assert.Equal(CommandOutcomeKind.Unknown, programs[1].Outcome);
        Assert.Equal(4096 * 2, result.TotalBytesWritten);
        Assert.Equal(EdlFailureKind.CompletionLost, result.FailureKind);
        Assert.True(result.RequiresReconnect);
    }

    [Fact]
    public async Task FlashBundleAsync_CancelledDuringProgramCommand_ThrowsCancellationCarryingStructuredResult()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 4));
        using var cancellation = new CancellationTokenSource();
        var transport = EvidenceTransport(cancelOnProgramXml: () => cancellation.Cancel());
        using var session = BundleSession(transport);

        var error = await Assert.ThrowsAsync<FlashBundleCancellationException>(() =>
            new FlashBundleOrchestrator(session).FlashBundleAsync(
                Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
                cancellation.Token));

        var result = error.Result;
        Assert.False(result.IsSuccess);
        Assert.Equal(CommandOutcomeKind.Cancelled, result.FinalOutcome);
        Assert.Equal(EdlFailureKind.Cancelled, result.FailureKind);
        Assert.True(result.RequiresReconnect);
        var program = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Program);
        Assert.Equal(CommandOutcomeKind.Cancelled, program.Outcome);
        Assert.Equal(EdlFailureKind.Cancelled, program.FailureKind);
    }

    [Fact]
    public async Task FlashBundleAsync_CancelledBeforeAnyDeviceByte_IsCancelledWithoutReconnect()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 4));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var transport = EvidenceTransport();
        using var session = BundleSession(transport);

        var error = await Assert.ThrowsAsync<FlashBundleCancellationException>(() =>
            new FlashBundleOrchestrator(session).FlashBundleAsync(
                Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
                cancellation.Token));

        Assert.Equal(EdlFailureKind.Cancelled, error.Result.FailureKind);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashBundleAsync_SessionInitializedWithProgrammerIdentity_BindsHashAndGenerationToResult()
    {
        using var directory = BundleDirectory(("boot.img", 4096 * 4));
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport);
        EnqueueDoneResponse(transport);
        transport.Enqueue("""
            <data><response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576" Erase="true" Patch="true" Wipe="true" UfsProvisioning="true"/></data>
            """);
        using var session = new EdlSession(transport,
            new FirehoseConfiguration { SectorSize = 4096, AutoDetectSectorSize = false });
        using var programmer = new MemoryStream("abc"u8.ToArray());
        await session.InitializeAsync(new EdlProgrammerSelection
        {
            Decision = EdlProgrammerMatchDecision.Matched,
            Descriptor = new EdlProgrammerDescriptor { FileName = "prog_firehose.bin", Sha256Hex = AbcSha256 },
            PerCandidateReasons = []
        }, programmer, null, TestContext.Current.CancellationToken);
        session.AttachRelinkContext(new DeviceEndpoint
        {
            Source = "test",
            Transport = "usb",
            Address = "edl:test"
        }, new TransportGeneration(7));
        transport.OnWrite = BundleHook();

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Path.Combine(directory.Path, "rawprogram0.xml"), null, directory.Path, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(AbcSha256, result.ResourceSha256Hex);
        Assert.Equal(7UL, result.TransportGeneration);
        Assert.Contains(result.Items, item => item.Kind == FlashBundleItemKind.Preflight
            && item.Outcome == CommandOutcomeKind.Completed);
        Assert.Contains(result.Items, item => item.Kind == FlashBundleItemKind.Program
            && item.Outcome == CommandOutcomeKind.Completed);
    }

    private static TempDirectory BundleDirectory(params (string Name, int Size)[] images)
    {
        var directory = new TempDirectory();
        var manifest = new StringBuilder("<data>");
        var start = 0;
        foreach (var (name, size) in images)
        {
            directory.WriteBytes(name, new byte[size]);
            manifest.Append($"""<program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="{name}" physical_partition_number="0" start_sector="{start}" num_partition_sectors="{size / 4096}" label="{Path.GetFileNameWithoutExtension(name)}" sparse="false" />""");
            start += size / 4096;
        }

        manifest.Append("</data>");
        directory.Write("rawprogram0.xml", $"""<?xml version="1.0" encoding="UTF-8"?>{manifest}""");
        return directory;
    }

    private static EdlSession BundleSession(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 4096, AutoDetectSectorSize = false });

    private static ScriptedCommTransport EvidenceTransport(int rejectPayload = 0,
        Action? cancelOnProgramXml = null)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = BundleHook(rejectPayload, cancelOnProgramXml);
        return transport;
    }

    private static Func<byte[], IEnumerable<byte[]>> BundleHook(int rejectPayload = 0,
        Action? cancelOnProgramXml = null)
    {
        const string configureAck = """
            <data><response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576" Erase="true" Patch="true" Wipe="true" UfsProvisioning="true"/></data>
            """;
        var payloads = 0;
        return bytes =>
        {
            if (bytes.Length == 0 || bytes[0] != '<')
            {
                return [Encoding.UTF8.GetBytes(++payloads == rejectPayload
                    ? "<data><response value=\"NAK\" rawmode=\"false\"/></data>"
                    : "<data><response value=\"ACK\" rawmode=\"false\"/></data>")];
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(configureAck)];
            }

            if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("""
                    <data><log value='{"storage_info":{"block_size":4096,"total_blocks":1048576}}'/><response value="ACK"/></data>
                    """)];
            }

            if (text.Contains("<nop", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("""<data><log value="booted"/></data><data><response value="ACK" rawmode="false"/></data>""")];
            }

            if (text.Contains("<program", StringComparison.Ordinal))
            {
                cancelOnProgramXml?.Invoke();
                return [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>")];
            }

            return [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"false\"/></data>")];
        };
    }

    private static void EnqueueHello(ScriptedCommTransport transport)
    {
        var hello = new SaharaHelloPacket
        {
            Command = SaharaCommand.Hello,
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SaharaHelloPacket>(),
            Version = EdlConstants.SaharaVersion,
            MinVersion = 1,
            MaxCommandLength = 3,
            Mode = SaharaMode.ImageTxPending
        };
        transport.Enqueue(PacketBytes(hello));
    }

    private static void EnqueueEndOfImage(ScriptedCommTransport transport)
    {
        var packet = new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = 0
        };
        transport.Enqueue(PacketBytes(packet));
    }

    private static void EnqueueDoneResponse(ScriptedCommTransport transport)
    {
        var packet = new SaharaDoneResponsePacket
        {
            Command = SaharaCommand.DoneResponse,
            Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SaharaDoneResponsePacket>(),
            Status = 1
        };
        transport.Enqueue(PacketBytes(packet));
    }

    private static byte[] PacketBytes<T>(T value) where T : struct
    {
        var bytes = new byte[System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}