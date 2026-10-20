using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlGptLunResolutionTests
{
    [Fact]
    public async Task ResolvePartitionSnapshotAsync_ExplicitLun_ReadsOnlyThatLun()
    {
        using var transport = Transport(new Dictionary<int, byte[]> { [3] = GptReaderTests.CreateDisk(512) });
        using var protocol = CreateProtocol(transport);

        var (snapshot, entry) = await EdlGptPartitionService.ResolvePartitionSnapshotAsync(
            protocol, "boot", 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.Geometry.PhysicalPartitionNumber);
        Assert.Equal("boot", entry.Name);
        Assert.All(Commands(transport).Where(command => command.Name == "read"),
            command => Assert.Equal(3, (int)command.Attribute("physical_partition_number")!));
    }

    [Fact]
    public async Task ResolvePartitionSnapshotAsync_ExplicitLunMissingName_ThrowsKeyNotFound()
    {
        using var transport = Transport(new Dictionary<int, byte[]> { [3] = GptReaderTests.CreateDisk(512) });
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            EdlGptPartitionService.ResolvePartitionSnapshotAsync(protocol, "userdata", 3,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolvePartitionSnapshotAsync_NullLun_ScansAndFindsPartitionOnLaterLun()
    {
        var disks = new Dictionary<int, byte[]>
        {
            [0] = GptReaderTests.CreateDisk(512),
            [1] = CreateNamedDisk("userdata")
        };
        using var transport = Transport(disks);
        using var protocol = CreateProtocol(transport);

        var (snapshot, entry) = await EdlGptPartitionService.ResolvePartitionSnapshotAsync(
            protocol, "userdata", null, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.Geometry.PhysicalPartitionNumber);
        Assert.Equal("userdata", entry.Name);
    }

    [Fact]
    public async Task ResolvePartitionSnapshotAsync_NullLun_SkipsUnreadableLuns()
    {
        var disks = new Dictionary<int, byte[]>
        {
            [0] = new byte[128 * 512],
            [1] = CreateNamedDisk("userdata")
        };
        using var transport = Transport(disks);
        using var protocol = CreateProtocol(transport);

        var (snapshot, entry) = await EdlGptPartitionService.ResolvePartitionSnapshotAsync(
            protocol, "userdata", null, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.Geometry.PhysicalPartitionNumber);
        Assert.Equal("userdata", entry.Name);
    }

    [Fact]
    public async Task ResolvePartitionSnapshotAsync_NoLunReadable_ThrowsWithOriginalFailure()
    {
        var disks = Enumerable.Range(0, 8).ToDictionary(lun => lun, _ => new byte[128 * 512]);
        using var transport = Transport(disks);
        using var protocol = CreateProtocol(transport);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdlGptPartitionService.ResolvePartitionSnapshotAsync(protocol, "boot", null,
                TestContext.Current.CancellationToken));

        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task ResolvePartitionSnapshotAsync_NameAbsentOnEveryReadableLun_ThrowsKeyNotFound()
    {
        var disks = Enumerable.Range(0, 8).ToDictionary(lun => lun, _ => GptReaderTests.CreateDisk(512));
        using var transport = Transport(disks);
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            EdlGptPartitionService.ResolvePartitionSnapshotAsync(protocol, "userdata", null,
                TestContext.Current.CancellationToken));
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, MaxPayloadSizeToTarget = 65536 });

    private static ScriptedCommTransport Transport(IReadOnlyDictionary<int, byte[]> disks)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            var lun = command.Attribute("physical_partition_number") is { } attribute
                ? (int)attribute
                : 0;
            if (command.Name == "getstorageinfo")
            {
                return [Encoding.UTF8.GetBytes(
                    "<data><log value='{\"storage_info\":{\"block_size\":512,\"total_blocks\":128}}'/>" +
                    "<response value='ACK'/></data>")];
            }

            if (command.Name == "read")
            {
                var disk = disks.TryGetValue(lun, out var mapped) ? mapped : new byte[128 * 512];
                var offset = checked((int)command.Attribute("start_sector")! * 512);
                var count = checked((int)command.Attribute("num_partition_sectors")! * 512);
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    disk.AsSpan(offset, count).ToArray(),
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
                ];
            }

            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        return transport;
    }

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single()).ToArray();

    private static byte[] CreateNamedDisk(string name)
    {
        var disk = GptReaderTests.CreateDisk(512);
        var nameBytes = Encoding.Unicode.GetBytes(name);
        foreach (var arrayStart in new[] { 2 * 512, 95 * 512 })
        {
            var nameField = disk.AsSpan(arrayStart + 56, 72);
            nameField.Clear();
            nameBytes.CopyTo(nameField);
        }

        var entryCrc = Crc32.HashToUInt32(disk.AsSpan(2 * 512, 16384));
        foreach (var headerStart in new[] { 512, 127 * 512 })
        {
            var header = disk.AsSpan(headerStart, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..], entryCrc);
            header[16..20].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32.HashToUInt32(header[..92]));
        }

        return disk;
    }
}