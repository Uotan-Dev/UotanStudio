using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.Streaming;

internal static class StreamingTestDiag
{
    internal static byte[] DecodeRequest(byte[] frame)
    {
        Assert.True(DiagHdlcCodec.TryDecode(frame, withCrc: true, verifyCrc: true, out var payload));
        return payload!;
    }

    internal static byte[] Reply(params byte[] payload) => DiagHdlcCodec.Encode(payload, withCrc: true);

    internal static byte[] HelloResponse(
        uint maxPreferredBlockSize = 1024,
        uint baseFlashAddress = 0,
        string flashId = "NAND",
        uint[]? sectorSizes = null,
        byte featureBits = 0x03)
    {
        sectorSizes ??= [512];
        var id = Encoding.ASCII.GetBytes(flashId);
        using var stream = new MemoryStream();
        stream.WriteByte(StreamingConstants.DiagHelloResponse);
        stream.Write(Encoding.ASCII.GetBytes(StreamingConstants.HostMagic));
        stream.WriteByte(0x03);
        stream.WriteByte(0x03);
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, maxPreferredBlockSize);
        stream.Write(word);
        BinaryPrimitives.WriteUInt32LittleEndian(word, baseFlashAddress);
        stream.Write(word);
        stream.WriteByte((byte)id.Length);
        stream.Write(id);
        Span<byte> half = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(half, 4);
        stream.Write(half);
        BinaryPrimitives.WriteUInt16LittleEndian(half, (ushort)sectorSizes.Length);
        stream.Write(half);
        foreach (var size in sectorSizes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, size);
            stream.Write(word);
        }

        stream.WriteByte(featureBits);
        return stream.ToArray();
    }
}

public class StreamingProtocolTests
{
    [Fact]
    public async Task HelloAsync_ValidHelloResponse_ParsesDeviceDescription()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagHelloCommand, payload[0]);
            Assert.Equal(StreamingConstants.HostMagic,
                Encoding.ASCII.GetString(payload.AsSpan(1, StreamingConstants.HostMagic.Length)));
            return [StreamingTestDiag.Reply(StreamingTestDiag.HelloResponse(
                maxPreferredBlockSize: 131072, baseFlashAddress: 0x79B0000,
                flashId: "Toshiba", sectorSizes: [512, 2048], featureBits: 0x0F))];
        };
        using var protocol = new StreamingProtocol(transport);

        var device = await protocol.HelloAsync(TestContext.Current.CancellationToken);

        Assert.Equal(131072u, device.MaxPreferredBlockSize);
        Assert.Equal(0x79B0000u, device.BaseFlashAddress);
        Assert.Equal("Toshiba", Encoding.ASCII.GetString(device.FlashId));
        Assert.Equal(4u, device.WindowSize);
        Assert.Equal(2, device.NumberOfSectors);
        Assert.Equal([512u, 2048u], device.SectorSizes);
        Assert.Equal(0x0F, device.FeatureBits);
        Assert.Same(device, protocol.Device);
    }

    [Fact]
    public async Task HelloAsync_WrongResponseIdentifier_ResendsOnceThenThrows()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.NakFailed)];
        using var protocol = new StreamingProtocol(transport);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.HelloAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TargetNak, error.ErrorCode);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task HelloAsync_RejectsTruncatedHelloReply_ThrowsStreamingException()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.DiagHelloResponse, 0x00)];
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.HelloAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HelloAsync_UnrecognizedFlashDevice_SendsResetAndThrows()
    {
        using var transport = new ScriptedCommTransport();
        var marker = Encoding.ASCII.GetBytes("Unrecognized flash device");
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            if (payload[0] == StreamingConstants.DiagResetCommand)
            {
                return [StreamingTestDiag.Reply(StreamingConstants.DiagResetCommand)];
            }

            return [StreamingTestDiag.Reply(marker)];
        };
        using var protocol = new StreamingProtocol(transport);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.HelloAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TargetNak, error.ErrorCode);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(StreamingConstants.DiagResetCommand,
            StreamingTestDiag.DecodeRequest(transport.Writes[^1])[0]);
    }

    [Fact]
    public async Task HelloAsync_MalformedReplyFrame_ThrowsStreamingException()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [[0x02, 0x7D, 0x30, 0x7E]];
        using var protocol = new StreamingProtocol(transport);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.HelloAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
    }

    [Fact]
    public async Task SecureModeAsync_AcknowledgedWithSecureResponse_ReturnsTrue()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal([StreamingConstants.DiagSecureCommand, 0x01], payload);
            return [StreamingTestDiag.Reply(StreamingConstants.DiagSecureResponse)];
        };
        using var protocol = new StreamingProtocol(transport);

        Assert.True(await protocol.SecureModeAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecureModeAsync_RejectedWithNakStatus_ReturnsFalse()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.NakInvalidSecCode)];
        using var protocol = new StreamingProtocol(transport);

        Assert.False(await protocol.SecureModeAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloseAsync_QcloseAcknowledged_ReturnsTrue()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            Assert.Equal(StreamingConstants.DiagCloseCommand, StreamingTestDiag.DecodeRequest(frame)[0]);
            return [StreamingTestDiag.Reply(StreamingConstants.DiagCloseResponse)];
        };
        using var protocol = new StreamingProtocol(transport);

        Assert.True(await protocol.CloseAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloseAsync_WrongResponseIdentifier_ReturnsFalse()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.NakFailed)];
        using var protocol = new StreamingProtocol(transport);

        Assert.False(await protocol.CloseAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetAsync_WithoutConfirmation_ThrowsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            protocol.ResetAsync(confirm: false, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ResetAsync_Confirmed_SendsResetFrameAndToleratesSilence()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            Assert.Equal(StreamingConstants.DiagResetCommand, StreamingTestDiag.DecodeRequest(frame)[0]);
            return [];
        };
        using var protocol = new StreamingProtocol(transport, timeoutMs: 150);

        await protocol.ResetAsync(confirm: true, TestContext.Current.CancellationToken);

        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task IdentifyChipsetAsync_PatchedMarker_ResolvesPatchedMode()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagArmExecuteCommand, payload[0]);
            Assert.Equal(StreamingProtocol.SearchBlob, payload.AsSpan(2, StreamingProtocol.SearchBlob.Length).ToArray());
            return
            [
                StreamingTestDiag.Reply(0xAA, 0x08),
                StreamingTestDiag.Reply(0xAA)
            ];
        };
        using var protocol = new StreamingProtocol(transport);

        var chipset = await protocol.IdentifyChipsetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0x08, chipset);
        Assert.Equal(StreamingMemoryLinkMode.Patched, protocol.NegotiatedLinkMode);
    }

    [Fact]
    public async Task IdentifyChipsetAsync_PowerOffMessage_ResolvesQualcommMode()
    {
        using var transport = new ScriptedCommTransport();
        var message = Encoding.ASCII.GetBytes("Power off not supported");
        transport.OnWrite = _ =>
        [
            StreamingTestDiag.Reply(message),
            StreamingTestDiag.Reply(0x00)
        ];
        using var protocol = new StreamingProtocol(transport);

        var chipset = await protocol.IdentifyChipsetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0xFF, chipset);
        Assert.Equal(StreamingMemoryLinkMode.Qualcomm, protocol.NegotiatedLinkMode);
    }

    [Fact]
    public async Task IdentifyChipsetAsync_MissingMarkerAfterRetry_ReturnsMinusOne()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(StreamingTestDiag.Reply(0x07));
        transport.Enqueue(StreamingTestDiag.Reply(0x00));
        transport.Enqueue(StreamingTestDiag.Reply(0x07));
        transport.Enqueue(StreamingTestDiag.Reply(0x00));
        using var protocol = new StreamingProtocol(transport);

        Assert.Equal(-1, await protocol.IdentifyChipsetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendPartitionTableAsync_OversizedTable_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.SendPartitionTableAsync(new byte[StreamingConstants.PartitionTableBytes + 1],
                overrideExisting: false, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task SendPartitionTableAsync_EmptyTable_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.SendPartitionTableAsync(new byte[0], overrideExisting: false,
                confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task SendPartitionTableAsync_WithoutConfirmation_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            protocol.SendPartitionTableAsync(new byte[64], overrideExisting: true,
                confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task SendPartitionTableAsync_NakResponseIdentifier_ThrowsStreamingException()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.NakFailed, 0x00)];
        using var protocol = new StreamingProtocol(transport);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.SendPartitionTableAsync(new byte[64], overrideExisting: false,
                confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TargetNak, error.ErrorCode);
    }

    [Fact]
    public async Task WriteFlashAsync_WithoutConfirmation_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var source = new MemoryStream([1, 2, 3]);
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => protocol.WriteFlashAsync(0x1000, "system",
            source, 3, null, confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task WriteFlashAsync_EmptyPartitionName_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var source = new MemoryStream([1, 2, 3]);
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => protocol.WriteFlashAsync(0x1000, string.Empty,
            source, 3, null, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryAsync_ZeroLength_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.ReadMemoryAsync(0x1000, 0, StreamingMemoryLinkMode.Qualcomm,
                TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryAsync_OversizedLength_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.ReadMemoryAsync(0x1000, StreamingConstants.MaxMemoryReadBytes + 1,
                StreamingMemoryLinkMode.Qualcomm, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryAsync_WindowLeavingAddressSpace_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.ReadMemoryAsync(0xFFFFFFF8, 1024, StreamingMemoryLinkMode.Qualcomm,
                TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task MemPokeAsync_WithoutConfirmation_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            protocol.MemPokeAsync(0x79B0000, 0x12, confirm: false, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task MemWriteAsync_OversizedData_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.MemWriteAsync(0x1000, new byte[StreamingConstants.PatchedMemoryBlockBytes + 1],
                confirm: true, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact(Skip = "Requires UOTAN_EDL_STREAMING target (065C:9008/9006 command-mode Sahara)")]
    public async Task HelloAsync_OnCommandModeTarget_NegotiatesHostMagic()
    {
        await Task.CompletedTask;
        Assert.Fail("The gated hardware path never executed.");
    }
}