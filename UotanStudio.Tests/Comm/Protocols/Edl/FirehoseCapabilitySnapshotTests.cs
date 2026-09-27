using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class FirehoseCapabilitySnapshotTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData("true", FeatureSupport.Supported)]
    [InlineData("1", FeatureSupport.Supported)]
    [InlineData("supported", FeatureSupport.Supported)]
    [InlineData(" TRUE ", FeatureSupport.Supported)]
    [InlineData("false", FeatureSupport.Unsupported)]
    [InlineData("0", FeatureSupport.Unsupported)]
    [InlineData("unsupported", FeatureSupport.Unsupported)]
    [InlineData(null, FeatureSupport.Unknown)]
    [InlineData("", FeatureSupport.Unknown)]
    [InlineData("maybe", FeatureSupport.Unknown)]
    [InlineData("2", FeatureSupport.Unknown)]
    public void ParseTriState_ReportedAndMalformedValues_MapToThreeStates(string? value, FeatureSupport expected)
        => Assert.Equal(expected, FirehoseCapabilitySnapshot.ParseTriState(value));

    [Fact]
    public void From_MinimalAck_KeepsDestructiveCapabilitiesUnknownAndReadsEvidenced()
    {
        var response = FirehoseResponseParser.Parse("<data><response value=\"ACK\"/></data>");

        var snapshot = FirehoseCapabilitySnapshot.FromConfigureAck(response,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false }, null,
            requiresReconnect: false, DateTimeOffset.UtcNow);

        Assert.Equal(FeatureSupport.Unknown, snapshot.Erase);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Patch);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Wipe);
        Assert.Equal(FeatureSupport.Unknown, snapshot.UfsProvisioning);
        Assert.Equal(FeatureSupport.Unknown, snapshot.RawMode);
        Assert.Equal(FeatureSupport.Unknown, snapshot.VendorAuthentication);
        Assert.Equal(FeatureSupport.Unknown, snapshot.SixtyFourBitAddressing);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Reconnect);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Program);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Sparse);
        Assert.Equal(FeatureSupport.Supported, snapshot.Read);
        Assert.Equal(FeatureSupport.Supported, snapshot.Gpt);
        Assert.Equal(512, snapshot.SectorSizeInBytes);
        Assert.Null(snapshot.Geometry);
        Assert.Null(snapshot.ResourceSha256Hex);
        var evidence = Assert.Single(snapshot.Evidence);
        Assert.Equal("configure", evidence.Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence.Outcome);
    }

    [Fact]
    public void From_ReportedAttributesAndVendorExtension_BindsGeometryVersionAndIgnoresUnknownFields()
    {
        var response = FirehoseResponseParser.Parse("""
            <data><response value="ACK" MaxPayloadSizeToTargetInBytes="1048576"
              MaxPayloadSizeToTargetInBytesSupported="2097152" MaxPayloadSizeFromTargetInBytes="4194304"
              Erase="true" Patch="false" Wipe="TRUE" UfsProvisioning="1" Reconnect="unsupported"
              Version="2.5" VendorAuth="supported" QcomMysteryFeature="yes"/></data>
            """);
        var geometry = new FirehoseStorageGeometry(0, 4096, 1_000_000);

        var snapshot = FirehoseCapabilitySnapshot.FromConfigureAck(response,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false }, geometry,
            requiresReconnect: true, DateTimeOffset.UtcNow);

        Assert.Equal(FeatureSupport.Supported, snapshot.Program);
        Assert.Equal(1_048_576, snapshot.MaxPayloadSizeToTargetInBytes);
        Assert.Equal(2_097_152, snapshot.MaxPayloadSizeToTargetSupportedInBytes);
        Assert.Equal(4_194_304, snapshot.MaxPayloadSizeFromTargetInBytes);
        Assert.Equal(FeatureSupport.Supported, snapshot.Erase);
        Assert.Equal(FeatureSupport.Unsupported, snapshot.Patch);
        Assert.Equal(FeatureSupport.Supported, snapshot.Wipe);
        Assert.Equal(FeatureSupport.Supported, snapshot.UfsProvisioning);
        Assert.Equal(FeatureSupport.Unsupported, snapshot.Reconnect);
        Assert.Equal(FeatureSupport.Supported, snapshot.VendorAuthentication);
        Assert.Equal("2.5", snapshot.FirehoseVersion);
        Assert.Equal(4096, snapshot.SectorSizeInBytes);
        Assert.Same(geometry, snapshot.Geometry);
        Assert.True(snapshot.RequiresReconnect);
    }

    [Fact]
    public void From_NonAckOrRawResponse_ThrowsBeforeAnyStateIsRecorded()
    {
        var nak = FirehoseResponseParser.Parse("<data><response value=\"NACK\"/></data>");
        var raw = FirehoseResponseParser.Parse("<data><response value=\"ACK\" rawmode=\"true\"/></data>");
        var configuration = new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false };

        Assert.Throws<ArgumentException>(() => FirehoseCapabilitySnapshot.FromConfigureAck(nak, configuration, null,
            false, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => FirehoseCapabilitySnapshot.FromConfigureAck(raw, configuration, null,
            false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void For_UnmappedCommandValue_FailsClosedAsUnknown()
    {
        var response = FirehoseResponseParser.Parse("<data><response value=\"ACK\" Erase=\"true\"/></data>");
        var snapshot = FirehoseCapabilitySnapshot.FromConfigureAck(response,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false }, null,
            false, DateTimeOffset.UtcNow);

        Assert.Equal(FeatureSupport.Unknown, snapshot.For((EdlFirehoseCommand)int.MaxValue));
    }

    [Fact]
    public async Task Erase_WhenCapabilityUnknown_IsRefusedBeforeAnyByteReachesTheWire()
    {
        using var transport = ConfigureTransport("<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"4096\"/></data>");
        using var protocol = Protocol(transport);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<EdlException>(() =>
            protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.NotSupported, error.ErrorCode);
        Assert.Equal(EdlFailureKind.CapabilityNotReported, error.FailureKind);
        Assert.Equal(EdlFailureKind.CapabilityNotReported, protocol.LastFailureKind);
        Assert.Equal(before, transport.Writes.Count);
        Assert.DoesNotContain(transport.Writes, bytes =>
            Encoding.UTF8.GetString(bytes).Contains("<erase", StringComparison.Ordinal));
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramErasePatchWipe_WhenEachIsReportedUnsupported_EachIsRefusedWithoutWrites()
    {
        using var transport = ConfigureTransport("<data><response value=\"ACK\" Erase=\"false\" Patch=\"false\" Wipe=\"false\"/></data>");
        using var protocol = Protocol(transport);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        var programError = await Assert.ThrowsAsync<EdlException>(() =>
            protocol.ProgramAsync(new MemoryStream(new byte[512]), 0, 1, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EdlException>(() =>
            protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken));
        var patchError = await Assert.ThrowsAsync<EdlException>(() =>
            protocol.PatchAsync("boot.img", 0, "0x10", 4, "0xff", 1, cancellationToken: TestContext.Current.CancellationToken));
        var wipeError = await Assert.ThrowsAsync<EdlException>(() =>
            protocol.WipeAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlFailureKind.CapabilityNotReported, programError.FailureKind);
        Assert.Equal(EdlFailureKind.CapabilityNotReported, patchError.FailureKind);
        Assert.Equal(EdlFailureKind.CapabilityNotReported, wipeError.FailureKind);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task Program_WhenPayloadSizeAttributeIsUnparsable_IsRefusedWithoutWrites()
    {
        using var transport = ConfigureTransport("<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"banana\"/></data>");
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        Assert.True(snapshot.IsAck);
        Assert.Equal(FeatureSupport.Unknown, protocol.Capabilities!.Program);

        var error = await Assert.ThrowsAsync<EdlException>(() =>
            protocol.ProgramAsync(new MemoryStream(new byte[512]), 0, 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(EdlFailureKind.CapabilityNotReported, error.FailureKind);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task Erase_WhenReportedSupported_IsEmittedAfterConfigureAndSucceeds()
    {
        using var transport = ConfigureTransport("<data><response value=\"ACK\" Erase=\"true\"/></data>");
        using var protocol = Protocol(transport);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);

        var response = await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Contains(transport.Writes, bytes =>
            Encoding.UTF8.GetString(bytes).Contains("<erase", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capabilities_RefreshedOnReconfigure_LaterNegotiationReplacesSnapshot()
    {
        using var transport = ConfigureTransport("<data><response value=\"ACK\" Erase=\"false\"/></data>");
        using var protocol = Protocol(transport);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        var first = protocol.Capabilities;
        Assert.Equal(FeatureSupport.Unsupported, first!.Erase);

        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotSame(first, protocol.Capabilities);
        Assert.Equal(FeatureSupport.Unsupported, protocol.Capabilities!.Erase);
    }

    [Fact]
    public async Task SessionCapabilities_BindProgrammerHashFileName_AndGateDestructiveCommandsBeforeWrites()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport);
        EnqueueDoneResponse(transport);
        transport.Enqueue("<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"4096\"/></data>");
        using var session = new EdlSession(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        using var image = new MemoryStream("abc"u8.ToArray());
        await session.InitializeAsync(new EdlProgrammerSelection
        {
            Decision = EdlProgrammerMatchDecision.Matched,
            Descriptor = new EdlProgrammerDescriptor { FileName = "prog_firehose.bin", Sha256Hex = AbcSha256 },
            PerCandidateReasons = []
        }, image, null, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        Assert.Equal(AbcSha256, session.Capabilities!.ResourceSha256Hex);
        Assert.Equal("prog_firehose.bin", session.Capabilities.ProgrammerFileName);
        Assert.Null(session.Firehose!.Capabilities!.ResourceSha256Hex);
        Assert.Equal(FeatureSupport.Unknown, session.Capabilities.Erase);

        var error = await Assert.ThrowsAsync<EdlException>(() =>
            session.Firehose.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.NotSupported, error.ErrorCode);
        Assert.Equal(before, transport.Writes.Count);
        Assert.DoesNotContain(transport.Writes, bytes =>
            Encoding.UTF8.GetString(bytes).Contains("<erase", StringComparison.Ordinal));
    }

    private static ScriptedCommTransport ConfigureTransport(string configureAckXml)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(configureAckXml)];
            }

            if (text.Contains("<program", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("""<data><response value="ACK" rawmode="true"/></data>""")];
            }

            return [Encoding.UTF8.GetBytes("""<data><response value="ACK" rawmode="false"/></data>""")];
        };
        return transport;
    }

    private static FirehoseProtocol Protocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, ResponseTimeoutSeconds = 1 }, 50);

    private static void EnqueueHello(ScriptedCommTransport transport)
    {
        var hello = new SaharaHelloPacket
        {
            Command = SaharaCommand.Hello,
            Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
            Version = EdlConstants.SaharaVersion,
            MinVersion = 1,
            MaxCommandLength = 3,
            Mode = SaharaMode.ImageTxPending
        };
        transport.Enqueue(StructToBytes(hello));
    }

    private static void EnqueueEndOfImage(ScriptedCommTransport transport)
    {
        var packet = new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = 0
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueDoneResponse(ScriptedCommTransport transport)
    {
        var packet = new SaharaDoneResponsePacket
        {
            Command = SaharaCommand.DoneResponse,
            Length = (uint)Marshal.SizeOf<SaharaDoneResponsePacket>(),
            Status = 1
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}