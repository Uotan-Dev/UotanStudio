using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task ReadLegacyNandStorageAsync_SdkRawPackets_UsesBigEndianWithoutHostOrStatusBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var bytes = EnqueueLegacyNandRead(transport, 2112, 3, 2, fragment: true);
        transport.Enqueue([0x5A, 0x5A]);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadLegacyNandStorageAsync(2048, 6144, destination, LegacyNandReadOptions() with { PagesPerPacket = 2 },
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("DF00010000000000000800000000000000180000000000"), transport.Writes[before]);
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, transport.Writes[before + 1]);
        Assert.All(transport.Writes.Skip(before + 2), bytes => Assert.Equal(new byte[] { 0x5A }, bytes));
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(new long[] { 4224, 6336 }, progress.Values);
        Assert.True(destination.CanWrite);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);

        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Theory]
    [InlineData(MtkLegacyNandReadMode.PageAndSpare, 2112)]
    [InlineData(MtkLegacyNandReadMode.PageOnly, 2048)]
    [InlineData(MtkLegacyNandReadMode.SpareOnly, 64)]
    [InlineData(MtkLegacyNandReadMode.PageWithEcc, 2048)]
    [InlineData(MtkLegacyNandReadMode.PageAndSpareWithEcc, 2112)]
    [InlineData(MtkLegacyNandReadMode.PageAndSpareNoRandom, 2112)]
    public async Task ReadLegacyNandStorageAsync_SelectedPageMode_ValidatesAndPublishesExactRecords(MtkLegacyNandReadMode mode, int size)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var bytes = EnqueueLegacyNandRead(transport, size, 2, 1);
        var options = LegacyNandReadOptions() with { Mode = mode, LengthUnit = MtkLegacyNandLengthUnit.StreamBytes };
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await session.ReadLegacyNandStorageAsync(0, 4096, destination, options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((byte)mode, transport.Writes[before][1]);
        Assert.Equal((ulong)size * 2, BinaryPrimitives.ReadUInt64BigEndian(transport.Writes[before].AsSpan(11)));
        Assert.Equal(new byte[4], transport.Writes[before][19..]);
        Assert.Equal(size * 2, options.GetTransferLength(session.LegacyStorage!.Nand!, 4096));
        Assert.Equal(bytes, destination.ToArray());
    }

    [Theory]
    [InlineData(MtkLegacyNandAddressType.Physical)]
    [InlineData(MtkLegacyNandAddressType.Logical)]
    public async Task ReadLegacyNandStorageAsync_AddressAboveFourGiB_DoesNotTruncate(MtkLegacyNandAddressType addressType)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport, capacity: 8UL * 1024 * 1024 * 1024);
        var data = EnqueueLegacyNandRead(transport, 2112, 1, 1);
        using var destination = new MemoryStream();
        var options = LegacyNandReadOptions() with
        {
            AddressType = addressType,
            LogicalCapacityBytes = addressType == MtkLegacyNandAddressType.Logical ? 6L * 1024 * 1024 * 1024 : null
        };
        var before = transport.Writes.Count;

        await session.ReadLegacyNandStorageAsync(0x1_0000_0000, 2048, destination, options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((byte)addressType, transport.Writes[before][2]);
        Assert.Equal(0x1_0000_0000UL, BinaryPrimitives.ReadUInt64BigEndian(transport.Writes[before].AsSpan(3)));
        Assert.Equal(data, destination.ToArray());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("spare")]
    [InlineData("unit")]
    [InlineData("zero-unit")]
    [InlineData("oversized-unit")]
    public async Task ReadLegacyNandStorageAsync_MismatchedGeometry_FaultsBeforeRequestingPackets(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue([0x5A]);
        var geometry = Convert.FromHexString("000008000000004000000840");
        var offset = failure == "page" ? 0 : failure == "spare" ? 4 : 8;
        BinaryPrimitives.WriteUInt32BigEndian(geometry.AsSpan(offset), failure == "zero-unit" ? 0U
            : failure == "oversized-unit" ? uint.MaxValue : 1U);
        transport.Enqueue(geometry);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 1, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(2048UL)]
    [InlineData(2111UL)]
    [InlineData(2113UL)]
    [InlineData(ulong.MaxValue)]
    public async Task ReadLegacyNandStorageAsync_MismatchedTotal_FaultsBeforePublishing(ulong total)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A000008000000004000000840"));
        var length = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, total);
        transport.Enqueue(length);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("truncated-geometry")]
    [InlineData("truncated-total")]
    [InlineData("truncated-data")]
    [InlineData("truncated-checksum")]
    [InlineData("bad-checksum")]
    public async Task ReadLegacyNandStorageAsync_FailedResponse_DoesNotRetryOrAcknowledge(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(failure == "header" ? [0xA5] : [0x5A]);
        if (failure != "header")
        {
            transport.Enqueue(failure == "truncated-geometry" ? [0, 0, 8, 0]
                : Convert.FromHexString("000008000000004000000840"));
            if (failure != "truncated-geometry")
            {
                transport.Enqueue(failure == "truncated-total" ? [0, 0, 8, 0]
                    : Convert.FromHexString("0000000000000840"));
                if (failure != "truncated-total")
                {
                    transport.Enqueue(new byte[failure == "truncated-data" ? 2111 : 2112]);
                    if (failure != "truncated-data")
                    {
                        transport.Enqueue(failure == "truncated-checksum" ? [0] : [0, 1]);
                    }
                }
            }
        }
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(failure is "header" or "truncated-geometry" ? 1 : 2, transport.Writes.Count - before);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    private static byte[] EnqueueLegacyNandRead(ScriptedCommTransport transport, int recordSize, int pages,
        int pagesPerPacket, bool fragment = false)
    {
        var prefix = new byte[21];
        prefix[0] = 0x5A;
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), 2048);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(5), 64);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(9), (uint)recordSize);
        BinaryPrimitives.WriteUInt64BigEndian(prefix.AsSpan(13), (ulong)recordSize * (uint)pages);
        transport.Enqueue(prefix);
        var data = Enumerable.Range(0, recordSize * pages).Select(index => (byte)(index % 251)).ToArray();
        data[0] = 0x5A;
        for (var offset = 0; offset < data.Length; offset += recordSize * pagesPerPacket)
        {
            var count = Math.Min(recordSize * pagesPerPacket, data.Length - offset);
            var packet = data.AsSpan(offset, count).ToArray();
            if (fragment)
            {
                foreach (var value in packet)
                {
                    transport.Enqueue([value]);
                }
            }
            else
            {
                transport.Enqueue(packet);
            }
            var checksum = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(checksum, (ushort)(packet.Sum(value => (int)value) & 0xFFFF));
            transport.Enqueue(checksum);
        }
        return data;
    }
}