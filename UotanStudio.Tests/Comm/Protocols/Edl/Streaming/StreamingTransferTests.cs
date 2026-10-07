using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.Streaming;

public class StreamingTransferTests
{
    [Fact]
    public async Task WriteFlashAsync_TwoChunks_WritesExactFramesAndTotalsProgress()
    {
        var image = Enumerable.Range(0, 1500).Select(index => (byte)(index % 253)).ToArray();
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = DiagFlashResponder();
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(image);
        var reports = new List<long>();
        var progress = new CollectingProgress(reports);

        var closed = await protocol.WriteFlashAsync(0x10000, "system", source, image.Length,
            progress, confirmWrite: true, TestContext.Current.CancellationToken);

        Assert.True(closed);
        Assert.Equal([1024L, 1500L], reports);
        Assert.Equal(4, transport.Writes.Count);
        var header = StreamingTestDiag.DecodeRequest(transport.Writes[0]);
        Assert.Equal([StreamingConstants.DiagSectionHeaderCommand, StreamingConstants.DiagMultiImageUserDefined],
            header[..2]);
        Assert.Equal((byte)0x00, header[^1]);
        var first = StreamingTestDiag.DecodeRequest(transport.Writes[1]);
        Assert.Equal(StreamingConstants.DiagProgramCommand, first[0]);
        Assert.Equal(0x10000u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(1)));
        Assert.Equal(image[..1024], first.AsSpan(5, 1024).ToArray());
        var second = StreamingTestDiag.DecodeRequest(transport.Writes[2]);
        Assert.Equal(0x10400u, BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(1)));
        var expectedTail = new List<byte>(image[1024..]);
        expectedTail.AddRange(Enumerable.Repeat((byte)0xFF, 1024 - image.Length % 1024));
        Assert.Equal(expectedTail, second.AsSpan(5).ToArray());
        Assert.Equal([StreamingConstants.DiagCloseCommand], StreamingTestDiag.DecodeRequest(transport.Writes[3]));
        Assert.Equal(image.Length, source.Position);
    }

    [Theory]
    [InlineData(0xFFFFFC00u, 2048L)]
    [InlineData(0xFFFFFF00u, 1024L)]
    public async Task WriteFlashAsync_WindowCrossingTwoPower32_RejectsWithZeroIo(uint lba, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(new byte[(int)length]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.WriteFlashAsync(lba,
            "system", source, length, null, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task WriteFlashAsync_ProgramChunkNak_ThrowsStreamingException()
    {
        using var transport = new ScriptedCommTransport();
        var chunks = 0;
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            return payload[0] switch
            {
                StreamingConstants.DiagSectionHeaderCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagSectionHeaderResponse)],
                StreamingConstants.DiagProgramCommand => [StreamingTestDiag.Reply(++chunks == 1
                    ? StreamingConstants.DiagProgramResponse
                    : StreamingConstants.NakFailed)],
                _ => []
            };
        };
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(new byte[2048]);

        var error = await Assert.ThrowsAsync<StreamingException>(() => protocol.WriteFlashAsync(0,
            "boot", source, 2048, null, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TargetNak, error.ErrorCode);
        Assert.Equal(3, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteFlashAsync_SectionHeaderNak_ThrowsBeforeAnyProgramChunk()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.NakInvalidCmd)];
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(new byte[512]);

        var error = await Assert.ThrowsAsync<StreamingException>(() => protocol.WriteFlashAsync(0,
            "boot", source, 512, null, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TargetNak, error.ErrorCode);
        Assert.Single(transport.Writes);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task WriteFlashAsync_TruncatedSource_ThrowsFileIoError()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = DiagFlashResponder();
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(new byte[10]);

        var error = await Assert.ThrowsAsync<StreamingException>(() => protocol.WriteFlashAsync(0x8000,
            "recovery", source, 1024, null, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.FileIO, error.ErrorCode);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task WriteFlashAsync_FinalCloseRejected_ReturnsFalse()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            return payload[0] switch
            {
                StreamingConstants.DiagSectionHeaderCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagSectionHeaderResponse)],
                StreamingConstants.DiagProgramCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagProgramResponse)],
                StreamingConstants.DiagCloseCommand => [StreamingTestDiag.Reply(0x10)],
                _ => []
            };
        };
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream(new byte[1024]);

        Assert.False(await protocol.WriteFlashAsync(0, "modem", source, 1024, null,
            confirmWrite: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteFlashAsync_ZeroLengthSection_OnlyHeadersStreamAndClose()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = DiagFlashResponder();
        using var protocol = new StreamingProtocol(transport);
        using var source = new MemoryStream([]);

        Assert.True(await protocol.WriteFlashAsync(0x40000, "empty", source, 0, null,
            confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task SendPartitionTableAsync_LegacyTable_TransmitsModeByteAndRawBytes()
    {
        var table = Enumerable.Range(0, StreamingConstants.PartitionTableBytes)
            .Select(index => (byte)(index * 7 + 3)).ToArray();
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagFlashModeTableCommand, payload[0]);
            Assert.Equal(0x01, payload[1]);
            Assert.Equal(table, payload.AsSpan(2).ToArray());
            return [StreamingTestDiag.Reply(StreamingConstants.DiagFlashModeTableResponse, 0x01)];
        };
        using var protocol = new StreamingProtocol(transport);

        Assert.True(await protocol.SendPartitionTableAsync(table, overrideExisting: true,
            confirmWrite: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendPartitionTableAsync_EraseFailedStatus_ReturnsFalse()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.DiagFlashModeTableResponse, 0x03)];
        using var protocol = new StreamingProtocol(transport);

        Assert.False(await protocol.SendPartitionTableAsync(new byte[128], overrideExisting: false,
            confirmWrite: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMemoryAsync_QualcommPages_AggregatesExactPageBytes()
    {
        const uint Start = 0x8000;
        const int Length = 1100;
        using var transport = new ScriptedCommTransport();
        var requestedSizes = new List<int>();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagMemReadCommand, payload[0]);
            var address = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5));
            requestedSizes.Add(size);
            var reply = new byte[5 + size];
            reply[0] = StreamingConstants.DiagMemReadResponse;
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1), address);
            for (var index = 0; index < size; index++)
            {
                reply[5 + index] = (byte)((address + (uint)index) % 251);
            }

            return [StreamingTestDiag.Reply(reply)];
        };
        using var protocol = new StreamingProtocol(transport);

        var data = await protocol.ReadMemoryAsync(Start, Length, StreamingMemoryLinkMode.Qualcomm,
            TestContext.Current.CancellationToken);

        Assert.Equal([512, 512, 76], requestedSizes);
        for (var index = 0; index < Length; index++)
        {
            Assert.Equal((byte)((Start + (uint)index) % 251), data[index]);
        }
    }

    [Fact]
    public async Task ReadMemoryAsync_QualcommShortPage_ThrowsStreamingException()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [StreamingTestDiag.Reply(StreamingConstants.DiagMemReadResponse, 0x01, 0x00, 0x00, 0x00, 0xAA)];
        using var protocol = new StreamingProtocol(transport);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.ReadMemoryAsync(0x1000, 512, StreamingMemoryLinkMode.Qualcomm,
                TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
    }

    [Fact]
    public async Task ReadMemoryAsync_PatchedBlocks_StripStatusWordAndKeepBlockData()
    {
        const uint Start = 0x1000;
        const int Length = 1500;
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagArmExecuteCommand, payload[0]);
            Assert.Equal(StreamingProtocol.ReadBlob,
                payload.AsSpan(2, StreamingProtocol.ReadBlob.Length).ToArray());
            var address = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.ReadBlob.Length));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.ReadBlob.Length + 4));
            var reply = new byte[4 + size];
            reply[0] = StreamingConstants.DiagArmExecuteResponse;
            for (var index = 0; index < size; index++)
            {
                reply[4 + index] = (byte)((address + (uint)index) % 197);
            }

            return [StreamingTestDiag.Reply(reply)];
        };
        using var protocol = new StreamingProtocol(transport);

        var data = await protocol.ReadMemoryAsync(Start, Length, StreamingMemoryLinkMode.Patched,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Writes.Count);
        for (var index = 0; index < Length; index++)
        {
            Assert.Equal((byte)((Start + (uint)index) % 197), data[index]);
        }
    }

    [Fact]
    public async Task MemPeekAsync_QualcommMode_DecodesLittleEndianWord()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagMemReadCommand, payload[0]);
            return [StreamingTestDiag.Reply(StreamingConstants.DiagMemReadResponse,
                payload[1], payload[2], payload[3], payload[4], 0x78, 0x56, 0x34, 0x12)];
        };
        using var protocol = new StreamingProtocol(transport, timeoutMs: 500);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Qualcomm);

        var word = await protocol.MemPeekAsync(0x79B0020, TestContext.Current.CancellationToken);

        Assert.Equal(0x12345678u, word);
    }

    [Fact]
    public async Task MemPokeAsync_PatchedMode_TransmitsWriteBlobWithWordParameters()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagArmExecuteCommand, payload[0]);
            Assert.Equal(StreamingProtocol.WriteBlob,
                payload.AsSpan(2, StreamingProtocol.WriteBlob.Length).ToArray());
            Assert.Equal(0x79B0020u, BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.WriteBlob.Length)));
            Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.WriteBlob.Length + 4)));
            Assert.Equal([0x11, 0x22, 0x33, 0x44],
                payload.AsSpan(2 + StreamingProtocol.WriteBlob.Length + 8, 4).ToArray());
            return [StreamingTestDiag.Reply(StreamingConstants.DiagArmExecuteResponse)];
        };
        using var protocol = new StreamingProtocol(transport, timeoutMs: 500);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Patched);

        Assert.True(await protocol.MemPokeAsync(0x79B0020, 0x44332211, confirm: true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemPokeAsync_QualcommMode_IsRefusedAsUnsupported()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport, timeoutMs: 500);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Qualcomm);

        var error = await Assert.ThrowsAsync<StreamingException>(() =>
            protocol.MemPokeAsync(0x1000, 0x20, confirm: true, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.NotSupported, error.ErrorCode);
        Assert.Empty(transport.Writes);
    }

    private static Func<byte[], IEnumerable<byte[]>> DiagFlashResponder()
        => frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            return payload[0] switch
            {
                StreamingConstants.DiagSectionHeaderCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagSectionHeaderResponse)],
                StreamingConstants.DiagProgramCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagProgramResponse)],
                StreamingConstants.DiagCloseCommand => [StreamingTestDiag.Reply(StreamingConstants.DiagCloseResponse)],
                _ => []
            };
        };

    private sealed class CollectingProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}