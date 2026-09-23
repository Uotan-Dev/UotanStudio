using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class FirehoseGptTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task ReadGptAsync_ValidCopies_ReturnsBoundSnapshotAndExactSectorReads(int sectorSize)
    {
        using var transport = Transport(GptReaderTests.CreateDisk(sectorSize), sectorSize);
        using var protocol = Protocol(transport, sectorSize);
        protocol.Configuration.Slot = 1;

        var snapshot = await protocol.ReadGptAsync(3, TestContext.Current.CancellationToken);

        Assert.Same(protocol, snapshot.Owner);
        Assert.Equal(3, snapshot.Geometry.PhysicalPartitionNumber);
        Assert.Equal(128, snapshot.Geometry.SectorCount);
        Assert.Equal(sectorSize, snapshot.Table.BlockSize);
        Assert.Equal(1, snapshot.Slot);
        Assert.Equal("boot", Assert.Single(snapshot.Table.Partitions).Name);
        var reads = Commands(transport).Where(command => command.Name == "read").ToArray();
        Assert.Equal(new long[] { 1, 127, 2, 127 - 16384 / sectorSize },
            reads.Select(command => (long)command.Attribute("start_sector")!));
        Assert.All(reads, command => Assert.Equal(3, (int)command.Attribute("physical_partition_number")!));
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadGptAsync_DamagedTable_RejectsWithoutFaultingCompletedRead()
    {
        using var transport = Transport(new byte[128 * 512], 512);
        using var protocol = Protocol(transport, 512);

        await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ReadGptAsync(0, TestContext.Current.CancellationToken));

        Assert.False(protocol.RequiresReconnect);
        Assert.True((await protocol.NopAsync(TestContext.Current.CancellationToken)).IsAck);
        Assert.Single(Commands(transport), command => command.Name == "read");
    }

    [Fact]
    public async Task ReadGptAsync_FinalNak_FaultsWithoutReturningSnapshot()
    {
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512, rejectRead: true);
        using var protocol = Protocol(transport, 512);

        await Assert.ThrowsAnyAsync<EdlException>(() => protocol.ReadGptAsync(0, TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(Commands(transport), command => command.Name == "read");
    }

    [Fact]
    public async Task ReadGptAsync_ConfigurationMismatch_DoesNotReadUsingGuessedUnits()
    {
        using var transport = Transport(GptReaderTests.CreateDisk(4096), 4096);
        using var protocol = Protocol(transport, 512);

        await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ReadGptAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal("getstorageinfo", Assert.Single(Commands(transport)).Name.LocalName);
        Assert.Equal(512, protocol.Configuration.SectorSize);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadGptAsync_QueuedProbe_WaitsUntilBothCopiesAreRead()
    {
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<read ", StringComparison.Ordinal) && queued is null)
            {
                queued = protocol.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return respond(bytes);
        };

        await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        Assert.True((await queued).IsAck);

        Assert.Equal(new[] { "getstorageinfo", "read", "read", "read", "read", "nop" },
            Commands(transport).Select(command => command.Name.LocalName));
    }

    [Fact]
    public async Task ReadGptAsync_PreCancelled_DoesNotQueryGeometry()
    {
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ReadGptAsync(0, cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    private static FirehoseProtocol Protocol(ScriptedCommTransport transport, int sectorSize) => new(transport,
        new FirehoseConfiguration { SectorSize = sectorSize, AutoDetectSectorSize = false, MaxPayloadSizeToTarget = 65536 });

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single()).ToArray();

    private static ScriptedCommTransport Transport(byte[] disk, int sectorSize, bool rejectRead = false)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getstorageinfo")
            {
                return [Encoding.UTF8.GetBytes($$$"""<data><log value='{"storage_info":{"block_size":{{{sectorSize}}},"total_blocks":128}}'/><response value='ACK'/></data>""")];
            }
            if (command.Name == "read")
            {
                var offset = checked((int)command.Attribute("start_sector")! * sectorSize);
                var count = checked((int)command.Attribute("num_partition_sectors")! * sectorSize);
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    disk.AsSpan(offset, count).ToArray(),
                    Encoding.UTF8.GetBytes($"<data><response value='{(rejectRead ? "NAK" : "ACK")}' rawmode='false'/></data>")
                ];
            }
            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        return transport;
    }
}