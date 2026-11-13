using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlLunEnumerationTests
{
    [Fact]
    public async Task ScanLunSnapshotsAsync_MainLunUnreadable_KeepsScanningAndReportsLaterLuns()
    {
        using var transport = Transport(new Dictionary<int, byte[]>
        {
            [1] = GptReaderTests.CreateDisk(512),
            [3] = GptReaderTests.CreateDisk(512)
        });
        using var protocol = CreateProtocol(transport);

        var scan = await EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, null, null, null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(scan);
        Assert.Equal([1, 3], scan.Snapshots.Select(snapshot => snapshot.Geometry.PhysicalPartitionNumber).ToArray());
        Assert.Equal([0, 2, 4, 5, 6, 7], scan.Skipped.Select(skipped => skipped.Lun).ToArray());
        Assert.False(scan.IsEmpty);
    }

    [Fact]
    public async Task ScanLunSnapshotsAsync_EveryLunReadable_ReturnsTheWholeProbeWindowInOrder()
    {
        using var transport = Transport(Enumerable.Range(0, EdlGptPartitionService.ScannedLunCount)
            .ToDictionary(lun => lun, _ => GptReaderTests.CreateDisk(512)));
        using var protocol = CreateProtocol(transport);

        var scan = await EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, null, null, null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(scan);
        Assert.Equal(Enumerable.Range(0, EdlGptPartitionService.ScannedLunCount).ToArray(),
            scan.Snapshots.Select(snapshot => snapshot.Geometry.PhysicalPartitionNumber).ToArray());
        Assert.Empty(scan.Skipped);
    }

    [Fact]
    public async Task ScanLunSnapshotsAsync_NoLunReadable_ReportsTheEmptyScanInsteadOfThrowing()
    {
        using var transport = Transport(new Dictionary<int, byte[]>());
        using var protocol = CreateProtocol(transport);

        var scan = await EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, null, null, null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(scan);
        Assert.True(scan.IsEmpty);
        Assert.Equal(EdlGptPartitionService.ScannedLunCount, scan.Skipped.Count);
    }

    [Fact]
    public async Task ScanLunSnapshotsAsync_EntryOverridesScope_AppliesToTheNamedLunOnly()
    {
        using var transport = Transport(Enumerable.Range(0, EdlGptPartitionService.ScannedLunCount)
            .ToDictionary(lun => lun, _ => GptReaderTests.CreateDisk(512)));
        using var protocol = CreateProtocol(transport);

        var scan = await EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, 40, null, null,
            TestContext.Current.CancellationToken, partitionEntryOverridesLun: 3);

        Assert.NotNull(scan);
        Assert.Equal([3], scan.Skipped.Select(skipped => skipped.Lun).ToArray());
        Assert.Equal(EdlGptPartitionService.ScannedLunCount - 1, scan.Snapshots.Count);
    }

    [Fact]
    public async Task ScanLunSnapshotsAsync_LinkLost_StopsAndReportsNoPartialResult()
    {
        var disks = new Dictionary<int, byte[]>
        {
            [0] = GptReaderTests.CreateDisk(512),
            [1] = GptReaderTests.CreateDisk(512)
        };
        using var transport = Transport(disks);
        var scripted = transport.OnWrite!;
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Attribute("physical_partition_number") is { } attribute && (int)attribute >= 2)
            {
                throw new EdlException(EdlErrorCode.ConnectionLost, "The device stopped answering.");
            }

            return scripted(bytes);
        };
        using var protocol = CreateProtocol(transport);

        var scan = await EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, null, null, null,
            TestContext.Current.CancellationToken);

        Assert.Null(scan);
        Assert.DoesNotContain(Commands(transport), command =>
            command.Attribute("physical_partition_number") is { } attribute && (int)attribute > 2);
    }

    [Fact]
    public async Task ScanLunSnapshotsAsync_Cancelled_ThrowsInsteadOfReturningAPartialScan()
    {
        using var transport = Transport(Enumerable.Range(0, EdlGptPartitionService.ScannedLunCount)
            .ToDictionary(lun => lun, _ => GptReaderTests.CreateDisk(512)));
        using var protocol = CreateProtocol(transport);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EdlGptPartitionService.ScanLunSnapshotsAsync(protocol, null, null, null, cancellation.Token));
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, MaxPayloadSizeToTarget = 65536 });

    private static ScriptedCommTransport Transport(IReadOnlyDictionary<int, byte[]> disks)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            var lun = command.Attribute("physical_partition_number") is { } attribute ? (int)attribute : 0;
            if (command.Name == "getstorageinfo")
            {
                return disks.ContainsKey(lun)
                    ? [Encoding.UTF8.GetBytes(
                        "<data><log value='{\"storage_info\":{\"block_size\":512,\"total_blocks\":128}}'/>" +
                        "<response value='ACK'/></data>")]
                    : [Encoding.UTF8.GetBytes("<data><response value='NAK'/></data>")];
            }

            if (command.Name == "read")
            {
                var disk = disks.TryGetValue(lun, out var mapped) ? mapped : new byte[128 * 512];
                var offset = checked((int)command.Attribute("start_sector")! * 512);
                var count = checked((int)command.Attribute("num_partition_sectors")! * 512);
                var available = Math.Clamp(disk.Length - offset, 0, count);
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    disk.AsSpan(offset, available).ToArray().Concat(new byte[count - available]).ToArray(),
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
                ];
            }

            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        return transport;
    }

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single())
        .ToArray();
}