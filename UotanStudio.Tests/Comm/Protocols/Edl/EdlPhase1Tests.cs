using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class SaharaDiagnosticsTests
{
    [Fact]
    public async Task GetCommandIdListAsync_ExchangesExecuteAndPayload()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            Assert.Equal(12, bytes.Length);
            var command = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (command == (uint)SaharaCommand.Execute)
            {
                Assert.Equal((uint)EdlConstants.SaharaExecCmdGetCommandIdList,
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
                var response = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(response, (uint)SaharaCommand.ExecuteResponse);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8),
                    (uint)EdlConstants.SaharaExecCmdGetCommandIdList);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 8);
                return [response];
            }
            Assert.Equal((uint)SaharaCommand.ExecuteData, command);
            return [new byte[] { 0x01, 0x02, 0x05, 0x08, 0x09, 0x0A, 0xFF, 0xFF }];
        };
        var sahara = new SaharaProtocol(transport);

        var list = await sahara.GetCommandIdListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, list.Length);
        Assert.Equal(0x05, list[2]);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task SwitchToStreamDloadAsync_SendsCommandAndAcceptsEmptyPayload()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (command == (uint)SaharaCommand.Execute)
            {
                Assert.Equal((uint)EdlConstants.SaharaExecCmdSwitchToStreamDload,
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
                var response = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(response, (uint)SaharaCommand.ExecuteResponse);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8),
                    (uint)EdlConstants.SaharaExecCmdSwitchToStreamDload);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 0);
                return [response];
            }
            return [];
        };
        var sahara = new SaharaProtocol(transport);

        var payload = await sahara.SwitchToStreamDloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(payload);
    }

    [Fact]
    public async Task ReadDebugDataAsync_OversizedLength_IsRejected()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (command == (uint)SaharaCommand.Execute)
            {
                var response = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(response, (uint)SaharaCommand.ExecuteResponse);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8),
                    (uint)EdlConstants.SaharaExecCmdReadDebugData);
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), 1024 * 1024 + 1);
                return [response];
            }
            return [];
        };
        var sahara = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.ReadDebugDataAsync(TestContext.Current.CancellationToken));
    }
}

public class FirehoseDebugTests
{
    [Fact]
    public async Task PokeAsync_SendsBigEndianHexLiteral()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var xml = Encoding.UTF8.GetString(bytes);
            return [Encoding.UTF8.GetBytes(
                "<data><response value='ACK' rawmode='false'/></data>")];
        };
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var response = await protocol.PokeAsync(0x100000, new byte[] { 0x22, 0x00, 0x00 },
            TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var xml = Encoding.UTF8.GetString(transport.Writes[0]);
        Assert.Contains("<poke address64=\"1048576\"", xml);
        Assert.Contains("size_in_bytes=\"3\"", xml);
        Assert.Contains("value64=\"0x220000\"", xml);
    }

    [Fact]
    public async Task PokeAsync_NakWithLowercase_RetriesUppercaseAttribute()
    {
        using var transport = new ScriptedCommTransport();
        var attempts = 0;
        transport.OnWrite = bytes =>
        {
            attempts++;
            var xml = Encoding.UTF8.GetString(bytes);
            if (attempts == 1)
            {
                Assert.Contains("size_in_bytes", xml);
                return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
            }
            Assert.Contains("SizeInBytes", xml);
            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var response = await protocol.PokeAsync(0x400, new byte[] { 0xFF }, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public async Task PokeAsync_InvalidPayloadLength_ThrowsWithoutWriting(long length)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.PokeAsync(0x400,
            new byte[length == -1 ? 0 : 9], TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task MemcpyAsync_PeekThenChunkedPokes()
    {
        var payload = Enumerable.Range(0, 20).Select(index => (byte)index).ToArray();
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var xml = Encoding.UTF8.GetString(bytes);
            if (xml.Contains("<peek", StringComparison.Ordinal))
            {
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    payload,
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
                ];
            }
            Assert.Contains("<poke", xml);
            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var response = await protocol.MemcpyAsync(0x2000, 0x1000, 20, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var pokes = transport.Writes
            .Select(bytes => Encoding.UTF8.GetString(bytes))
            .Where(xml => xml.Contains("<poke", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, pokes.Length);
        Assert.Contains("address64=\"8192\"", pokes[0]);
        Assert.Contains("address64=\"8200\"", pokes[1]);
        Assert.Contains("address64=\"8208\"", pokes[2]);
    }

    [Fact]
    public async Task PeekAsync_NakWithLowercase_RetriesUppercaseAttribute()
    {
        var payload = new byte[] { 0xDE, 0xAD };
        using var transport = new ScriptedCommTransport();
        var attempts = 0;
        transport.OnWrite = bytes =>
        {
            attempts++;
            var xml = Encoding.UTF8.GetString(bytes);
            if (!xml.Contains("<peek", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
            }

            if (attempts == 1)
            {
                Assert.Contains("size_in_bytes", xml);
                return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
            }

            Assert.Contains("SizeInBytes", xml);
            return
            [
                Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                payload,
                Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
            ];
        };
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var (response, data) = await protocol.PeekAsync(payload.Length, 0x400, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(payload, data);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void DetectActiveSlot_BootAActiveFlag_ReturnsA()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        var snapshot = CreateSnapshot(protocol, [("boot_a", 1UL << 6), ("boot_b", 0UL)]);

        Assert.Equal("a", FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    [Fact]
    public void DetectActiveSlot_NoActiveFlag_ReturnsNull()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        var snapshot = CreateSnapshot(protocol, [("boot_a", 0UL), ("boot_b", 0UL)]);

        Assert.Null(FirehoseProtocol.DetectActiveSlot(snapshot));
    }

    private static FirehoseGptSnapshot CreateSnapshot(FirehoseProtocol protocol,
        (string Name, ulong Attributes)[] partitions)
    {
        var table = new GptTable(Guid.Parse("11111111-2222-3333-4444-555555555555"), 512, 128,
            partitions.Select((entry, index) => new GptPartition(index, Guid.Empty, Guid.Empty,
                (ulong)(34 + index), (ulong)(34 + index), entry.Attributes, entry.Name,
                (34 + index) * 512L, 512L)).ToList(),
            "0000000000000000000000000000000000000000000000000000000000000000", 92, 6, 94, 128, 128, 2, 3);
        return new FirehoseGptSnapshot(protocol,
            new FirehoseStorageGeometry(0, 512, 128), 0, table);
    }
}