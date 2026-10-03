using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private const int LegacyGptBlockCount = 128;

    [Fact]
    public async Task ReadLegacyGptAsync_UserRegion_ReadsBothCopiesAndValidatesTheTable()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512, 128, LegacyGptBlockCount);
        EnqueueLegacyGptReads(transport, disk);
        var before = transport.Writes.Count;

        var snapshot = await session.ReadLegacyGptAsync(8, TestContext.Current.CancellationToken);

        Assert.Same(session.LegacyStorage, snapshot.Storage);
        Assert.Equal(8, snapshot.PartitionId);
        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        var headers = transport.Writes.Skip(before)
            .Where(bytes => bytes.Length == 23 && bytes[0] == 0xD6)
            .ToArray();
        Assert.Equal(4, headers.Length);
        Assert.Equal(new long[] { 512, 127 * 512, 1024, 95 * 512 },
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(3))));
        Assert.Equal(new long[] { 512, 512, 16384, 16384 },
            headers.Select(bytes => (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(11))));
        Assert.Contains(transport.Writes.Skip(before), bytes => bytes.Length == 1 && bytes[0] == 8);
        Assert.Equal(4, transport.Writes.Skip(before).Count(bytes => bytes.Length == 1 && bytes[0] == 0x72));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyGptAsync_DamagedMetadata_KeepsTheSessionUsableForExplicitReads()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, new byte[512 * LegacyGptBlockCount]);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadLegacyGptAsync(8,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        EnqueueLegacyRead(transport, new byte[512], 0, 512);
        using var output = new MemoryStream();
        await session.ReadLegacyStorageAsync(8, 0, 512, output,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(512, output.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task ReadLegacyGptAsync_UndiscoveredRegion_FailsBeforeAnyExchange(byte partitionId)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyGptAsync(partitionId,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadLegacyGptAsync_AuthenticatedRegion_IsRefusedBeforeAnyExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadLegacyGptAsync(3,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadLegacyGptAsync_NandSession_IsRefusedWithoutStorageExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadLegacyGptAsync(8,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    private static async Task<MtkSession> CreateLegacyGptSessionAsync(ScriptedCommTransport transport)
    {
        using var dir = new TempDirectory();
        var inventory = new byte[92];
        for (var index = 0; index < 8; index++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(4 + index * 8), 512 * LegacyGptBlockCount);
        }

        for (var index = 0; index < 16; index++)
        {
            inventory[68 + index] = (byte)index;
        }

        EnqueueLegacyBootstrap(transport, false, emmcInventory: inventory);
        return await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, LegacyBootstrapOptions(dir),
            TestContext.Current.CancellationToken);
    }

    private static void EnqueueLegacyGptReads(ScriptedCommTransport transport, byte[] disk)
    {
        foreach (var (offset, length) in new[] { (512L, 512L), (127L * 512, 512L), (1024L, 16384L), (95L * 512, 16384L) })
        {
            EnqueueLegacyRead(transport, disk, offset, length);
        }
    }

    private static void EnqueueLegacyRead(ScriptedCommTransport transport, byte[] disk, long offset, long length)
    {
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        for (var done = 0L; done < length; done += 512)
        {
            var chunk = disk.AsSpan((int)offset + (int)done, 512).ToArray();
            var checksum = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(checksum, LegacyPacketSum(chunk));
            transport.Enqueue(chunk);
            transport.Enqueue(checksum);
        }
    }

    private static ushort LegacyPacketSum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
        }

        return (ushort)(sum & 0xFFFF);
    }
}