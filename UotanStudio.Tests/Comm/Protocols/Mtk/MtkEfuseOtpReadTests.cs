using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task ReadXmlEfuseCellsAsync_TwoReadableCells_PeeksEachResolvedAddressAndKeepsTableOrder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        for (var cell = 0; cell < 2; cell++)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXFlashFrame(transport, [(byte)(0xA0 + cell), 0xB0, 0xC0, 0xD0]);
            EnqueueXmlText(transport, XmlControl("END", "OK"));
            EnqueueXmlText(transport, XmlControl("START"));
        }

        var before = transport.Writes.Count;

        var reading = await session.ReadXmlEfuseCellsAsync(0x6570, 0x11C1_0000, 0, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal((ushort)0x6570, reading.HardwareCode);
        Assert.Equal(0x11C10000u, reading.EfuseBase);
        Assert.Equal(2, reading.ReadCellCount);
        Assert.Equal(0x11C10020u, reading.Cells[0].Cell.Address);
        Assert.Equal(0x11C10030u, reading.Cells[1].Cell.Address);
        Assert.Equal("A0B0C0D0", reading.Cells[0].BytesHex);
        Assert.Equal("A1B0C0D0", reading.Cells[1].BytesHex);
        Assert.Equal(0xD0C0B0A0u, reading.Cells[0].Value);
        var frames = DaMemoryFrames(transport, before);
        Assert.Contains("CMD:CUSTOMMEMR", System.Text.Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
        Assert.Equal(0x11C10020UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[1]));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(frames[2]));
        Assert.Equal(0x11C10030UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[6]));
        Assert.Equal(10, frames.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlEfuseCellsAsync_NonAddressCell_ReportsTheValueWithoutAnyDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var reading = await session.ReadXmlEfuseCellsAsync(0x1208, 0x11C1_0000, 10, 1,
            TestContext.Current.CancellationToken);

        Assert.True(reading.Cells[0].Cell.IsReportedWithoutRead);
        Assert.Equal(0x11C10000u, reading.EfuseBase);
        Assert.Equal(0x0000_000Au, reading.Cells[0].Cell.Address);
        Assert.Equal("0A000000", reading.Cells[0].BytesHex);
        Assert.Equal(0, reading.ReadCellCount);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Theory]
    [InlineData("noTable")]
    [InlineData("rangeOutsideTable")]
    [InlineData("noXmlStage")]
    public async Task ReadXmlEfuseCellsAsync_RefusableRequest_FailsBeforeDeviceIo(string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = shape == "noXmlStage"
            ? await CreateStartedSessionAsync(transport)
            : await CreateXmlStorageSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => shape switch
        {
            "noTable" => session.ReadXmlEfuseCellsAsync(0x8125, 0x11C1_0000, 0, 1,
                TestContext.Current.CancellationToken),
            "rangeOutsideTable" => session.ReadXmlEfuseCellsAsync(0x6570, 0x11C1_0000, 0, 33,
                TestContext.Current.CancellationToken),
            _ => session.ReadXmlEfuseCellsAsync(0x6570, 0x11C1_0000, 0, 1,
                TestContext.Current.CancellationToken)
        });

        Assert.True(shape switch
        {
            "noTable" => error is NotSupportedException,
            "rangeOutsideTable" => error is ArgumentOutOfRangeException,
            _ => error is InvalidOperationException
        });
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task ReadXmlEfuseCellsAsync_ShortCellReply_FaultsTheXmlFamily()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, [0xA0, 0xB0, 0xC0]);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlEfuseCellsAsync(0x6570,
            0x11C1_0000, 0, 1, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXmlEfuseCellsAsync_CancelledBeforeTheFirstCell_SendsNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXmlEfuseCellsAsync(0x6570,
            0x11C1_0000, 0, 2, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXFlashEfuseCellsAsync_EfuseBandAddresses_StillUseTheRegisterReadPath()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        for (var cell = 0; cell < 2; cell++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashFrame(transport, [(byte)(0xE0 + cell), 0x00, 0x00, 0x00]);
            EnqueueXFlashValue(transport, 0);
        }

        var reading = await session.ReadXFlashEfuseCellsAsync(0x6570, 0x11C1_0000, 0, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reading.ReadCellCount);
        Assert.Equal(0x11C10020u, reading.Cells[0].Cell.Address);
        Assert.Equal("E0000000", reading.Cells[0].BytesHex);
    }

    private static List<byte[]> DaMemoryFrames(ScriptedCommTransport transport, int before)
    {
        var wire = transport.Writes.Skip(before).SelectMany(value => value).ToArray();
        var frames = new List<byte[]>();
        for (var offset = 0; offset + 12 <= wire.Length;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + 8));
            frames.Add(wire.AsSpan(offset + 12, length).ToArray());
            offset += length + 12;
        }

        return frames;
    }
}