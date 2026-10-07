using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.Streaming;

public class StreamingNandTests
{
    [Fact]
    public void ApplyUdSize_ReferenceConfig_KeepsUnrelatedBits()
    {
        Assert.Equal(0x2A0408C0u, StreamingNandRegisters.ApplyUdSize(0x2A0408C0, 516));
        Assert.Equal(0x2A0400C0u, StreamingNandRegisters.ApplyUdSize(0x2A0408C0, 512));
    }

    [Fact]
    public void ApplyUdSizeToEccCfg_BchConfig_PropagatesSize()
    {
        Assert.Equal(0x42000700u, StreamingNandRegisters.ApplyUdSizeToEccCfg(0x0804745C, 0x42040700, 512));
    }

    [Fact]
    public void ApplyUdSizeToEccCfg_NonBchConfig_LeavesEccUntouched()
    {
        Assert.Equal(0x42040700u, StreamingNandRegisters.ApplyUdSizeToEccCfg(0x0004745C, 0x42040700, 512));
    }

    [Fact]
    public void GetUdSize_ReferenceConfig_Reports516()
    {
        Assert.Equal(516u, StreamingNandRegisters.GetUdSize(0x2A0408C0));
    }

    [Fact]
    public void ApplySpareSize_ReferenceConfig_WritesField()
    {
        Assert.Equal(0x2A0408C0u, StreamingNandRegisters.ApplySpareSize(0x2A0408C0, 4));
    }

    [Theory]
    [InlineData(0x0804745Cu, true)]
    [InlineData(0x0004745Cu, false)]
    public void UsesBchEcc_Bit27_ReportsReferenceState(uint cfg1, bool expected)
    {
        Assert.Equal(expected, StreamingNandRegisters.UsesBchEcc(cfg1));
    }

    [Fact]
    public void ApplyBchEccSize_ReferenceConfig_MovesParityField()
    {
        Assert.Equal(0x42040400u, StreamingNandRegisters.ApplyBchEccSize(0x42040700, 4));
    }

    [Fact]
    public void ApplyRsEccSize_ReferenceConfig_MovesParityField()
    {
        Assert.Equal(0x2A2C08C0u, StreamingNandRegisters.ApplyRsEccSize(0x2A0408C0, 5));
    }

    [Fact]
    public void BchResetPulseValue_SetsSoftwareResetBit()
    {
        Assert.Equal(0x42040702u, StreamingNandRegisters.BchResetPulseValue(0x42040700));
    }

    [Theory]
    [InlineData(0x42040700u, 0x42040701u)]
    [InlineData(0x42040701u, 0x42040701u)]
    public void ApplyEccDisable_Bit0_SetsOrClearsFlag(uint register, uint expected)
    {
        Assert.Equal(expected, StreamingNandRegisters.ApplyEccDisable(register, 1));
        Assert.Equal(0x42040700u, StreamingNandRegisters.ApplyEccDisable(register, 0));
    }

    [Fact]
    public void DisableHardwareBadBlock_ReferenceConfig_ClearsMarkerWindow()
    {
        Assert.Equal(0x0804001Cu, StreamingNandRegisters.DisableHardwareBadBlock(0x0804745C));
    }

    [Fact]
    public void EnableHardwareBadBlock_ReferenceValues_RestoresMarkerWindow()
    {
        Assert.Equal(0x0805745Cu, StreamingNandRegisters.EnableHardwareBadBlock(0x0804745C, 0x1D1, 1));
    }

    [Theory]
    [InlineData(3u, 5u, 64u, 0x00C50000u, 0x00u)]
    [InlineData(0u, 0u, 64u, 0x00000000u, 0x00u)]
    [InlineData(2047u, 63u, 64u, 0xFFFF0000u, 0x01u)]
    public void PageAddress_Registers_MatchReferenceSplit(uint block, uint page, uint pagesPerBlock,
        uint expectedAddr0, uint expectedAddr1)
    {
        Assert.Equal(expectedAddr0, StreamingNandRegisters.PageAddress0(block, page, pagesPerBlock));
        Assert.Equal(expectedAddr1, StreamingNandRegisters.PageAddress1(block, page, pagesPerBlock));
    }

    [Fact]
    public void EraseAddress0_ReferenceLayout_UsesUnshiftedLinearStart()
    {
        Assert.Equal(128u, StreamingNandRegisters.EraseAddress0(2, 64));
    }

    [Fact]
    public void BlockEraseCfg0_ReferenceConfig_ClearsReadBits()
    {
        Assert.Equal(0x2A040800u, StreamingNandRegisters.BlockEraseCfg0(0x2A0408C0));
    }

    [Theory]
    [InlineData(0x00FF0000u, false, false)]
    [InlineData(0x00FE0000u, false, true)]
    [InlineData(0xFFFF0000u, true, false)]
    [InlineData(0x00FF0000u, true, true)]
    public void IsBadBlock_BufferStatus_MatchesTstBadBlock(uint bufferStatus, bool isWideFlash, bool expected)
    {
        Assert.Equal(expected, StreamingNandRegisters.IsBadBlock(bufferStatus, isWideFlash));
    }

    [Theory]
    [InlineData(0x110u, 0x079B0000u, 16)]
    [InlineData(0x100u, 0x00000014u, -1)]
    [InlineData(0x100u, 0x000000ECu, 0)]
    [InlineData(0x01Fu, 0x079B0000u, 31)]
    public void EvaluateEccStatus_ReferenceWindow_ProducesReferenceVerdict(uint bufferStatus,
        uint flashCommandAddress, int expected)
    {
        Assert.Equal(expected, StreamingNandRegisters.EvaluateEccStatus(bufferStatus, flashCommandAddress));
    }

    [Fact]
    public void Geometry_TryParse_ObfuscatedParameterPage_DecodesAllFields()
    {
        var page = new byte[512];
        Encoding.ASCII.GetBytes("ABC").CopyTo(page.AsSpan(32));
        Encoding.ASCII.GetBytes("Kioxia").CopyTo(page.AsSpan(44));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(80), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(84), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(86), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(90), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(92), 64);
        page[96] = 32;
        page[97] = 1;
        page[98] = 0x34;

        Assert.True(StreamingNandGeometry.TryParse(page, out var geometry));
        Assert.Equal("ABC", geometry!.Vendor);
        Assert.Equal("Kioxia", geometry.Model);
        Assert.Equal(4096u, geometry.BytesPerPage);
        Assert.Equal((ushort)256, geometry.SparePerPage);
        Assert.Equal(64u, geometry.PagesPerBlock);
        Assert.Equal((byte)0x34, geometry.AddressCycles);
        Assert.Equal(4, geometry.RowAddressBytes);
        Assert.Equal(3, geometry.ColumnAddressBytes);
        Assert.Equal(2048u, geometry.TotalPages);
    }

    [Theory]
    [InlineData(98)]
    [InlineData(0)]
    public void Geometry_TryParse_ShortPage_ReturnsFalse(int length)
    {
        Assert.False(StreamingNandGeometry.TryParse(new byte[length], out var geometry));
        Assert.Null(geometry);
    }

    [Fact]
    public void Geometry_TryParse_ZeroPagesPerBlock_ReturnsFalse()
    {
        var page = new byte[512];
        Assert.False(StreamingNandGeometry.TryParse(page, out _));
    }

    [Fact]
    public void PartitionTable_Parse_LegacyLayout_AccumulatesOffsetsAndMasksLengths()
    {
        var table = BuildLegacyTable();

        var parsed = StreamingPartitionTable.Parse(table);

        Assert.Equal(StreamingPartitionTable.LegacyMagic1, parsed.Magic1);
        Assert.Equal(StreamingPartitionTable.LegacyMagic2, parsed.Magic2);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("sdi", parsed.Entries[0].Name);
        Assert.Equal(0u, parsed.Entries[0].StartLba);
        Assert.Equal(10u + 2u, parsed.Entries[0].Sectors);
        Assert.Equal("boot", parsed.Entries[1].Name);
        Assert.Equal(12u, parsed.Entries[1].StartLba);
        Assert.Equal(0x1234u + 0x56u, parsed.Entries[1].Sectors);
    }

    [Fact]
    public void PartitionTable_Parse_OffsetLayout_KeepsExplicitOffsets()
    {
        var table = new byte[StreamingConstants.PartitionTableBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(table, StreamingPartitionTable.Partition9kMagic1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), StreamingPartitionTable.Partition9kMagic2);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), 1);
        Encoding.ASCII.GetBytes("b:").CopyTo(table.AsSpan(16));
        Encoding.ASCII.GetBytes("recovery").CopyTo(table.AsSpan(18));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(32), 0x9000);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(36), 0x1000);

        var parsed = StreamingPartitionTable.Parse(table);

        Assert.Equal(0x9000u, parsed.Entries[0].StartLba);
        Assert.Equal(0x1000u, parsed.Entries[0].Sectors);
        Assert.Equal("recovery", parsed.Entries[0].Name);
    }

    [Fact]
    public void PartitionTable_Parse_RecordCountBeyondImage_RejectsBeforeReadingRecords()
    {
        var table = new byte[StreamingConstants.PartitionTableBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), 100);

        var error = Assert.Throws<StreamingException>(() => StreamingPartitionTable.Parse(table));
        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
    }

    [Fact]
    public void PartitionTable_Parse_ShortImage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingPartitionTable.Parse(new byte[8]));
    }

    [Fact]
    public async Task BadBlockTable_DisableOrIgnorePolicy_NeverTouchesTransport()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);
        foreach (var policy in new[] { StreamingBadBlockPolicy.Disable, StreamingBadBlockPolicy.Ignore })
        {
            var table = new StreamingBadBlockTable(protocol, 0x79B0000, 64, 4, 0x1D1, 1, policy);
            Assert.False(await table.CheckBlockAsync(9, TestContext.Current.CancellationToken));
        }

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task BadBlockTable_CheckBlock_GoodMarkerReturnsFalseAndCaches()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = NandScriptedResponder(0x00FF0000);
        using var protocol = new StreamingProtocol(transport);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Patched);
        var table = new StreamingBadBlockTable(protocol, 0x79B0000, 64, 4, 0x1D1, 1,
            StreamingBadBlockPolicy.Mark);

        Assert.False(await table.CheckBlockAsync(5, TestContext.Current.CancellationToken));
        var writesAfterFirst = transport.Writes.Count;
        Assert.True(writesAfterFirst > 0);
        Assert.False(await table.CheckBlockAsync(5, TestContext.Current.CancellationToken));

        Assert.Equal(writesAfterFirst, transport.Writes.Count);
    }

    [Fact]
    public async Task BadBlockTable_CheckBlock_BadMarkerReturnsTrue()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = NandScriptedResponder(0x00FE0000);
        using var protocol = new StreamingProtocol(transport);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Patched);
        var table = new StreamingBadBlockTable(protocol, 0x79B0000, 64, 4, 0x1D1, 1,
            StreamingBadBlockPolicy.Mark);

        Assert.True(await table.CheckBlockAsync(7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BadBlockTable_MarkBad_WithoutConfirmation_RejectsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new StreamingProtocol(transport);
        protocol.AdoptLinkMode(StreamingMemoryLinkMode.Patched);
        var table = new StreamingBadBlockTable(protocol, 0x79B0000, 64, 4, 0x1D1, 1,
            StreamingBadBlockPolicy.Mark);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            table.MarkBadAsync(3, confirm: false, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    private static byte[] BuildLegacyTable()
    {
        var table = new byte[StreamingConstants.PartitionTableBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(table, StreamingPartitionTable.LegacyMagic1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), StreamingPartitionTable.LegacyMagic2);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), 2);
        Encoding.ASCII.GetBytes("b:").CopyTo(table.AsSpan(16));
        Encoding.ASCII.GetBytes("sdi").CopyTo(table.AsSpan(18));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(32), 10);
        table[36] = 2;
        Encoding.ASCII.GetBytes("b:").CopyTo(table.AsSpan(16 + 28));
        Encoding.ASCII.GetBytes("boot").CopyTo(table.AsSpan(18 + 28));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(32 + 28), 0x1234);
        table[36 + 28] = 0x56;
        return table;
    }

    private static Func<byte[], IEnumerable<byte[]>> NandScriptedResponder(uint bufferStatusValue)
        => frame =>
        {
            var payload = StreamingTestDiag.DecodeRequest(frame);
            Assert.Equal(StreamingConstants.DiagArmExecuteCommand, payload[0]);
            var blob = payload.AsSpan(2);
            if (blob.StartsWith(StreamingProtocol.WriteBlob))
            {
                return [StreamingTestDiag.Reply(StreamingConstants.DiagArmExecuteResponse)];
            }

            Assert.True(blob.StartsWith(StreamingProtocol.ReadBlob));
            var address = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.ReadBlob.Length));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(2 + StreamingProtocol.ReadBlob.Length + 4));
            uint value;
            if (address == 0x79B0000 + 0x18)
            {
                value = bufferStatusValue;
            }
            else
            {
                value = 0;
            }

            var reply = new byte[4 + size];
            reply[0] = StreamingConstants.DiagArmExecuteResponse;
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), value);
            return [StreamingTestDiag.Reply(reply)];
        };
}