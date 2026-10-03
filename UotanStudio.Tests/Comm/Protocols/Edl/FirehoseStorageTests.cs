using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehoseStorageTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(4096)]
    public async Task GetStorageGeometryAsync_FragmentedJsonLogAndAck_ReturnsExactQueriedGeometry(int fragmentSize)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);
        var wire = Encoding.UTF8.GetBytes(Report("""INFO: {"storage_info":{"block_size":1,"total_blocks":9007199254740993,"physical_partition_number":3}}"""));
        for (var offset = 0; offset < wire.Length; offset += fragmentSize)
        {
            transport.Enqueue(wire.AsSpan(offset, Math.Min(fragmentSize, wire.Length - offset)).ToArray());
        }

        var geometry = await protocol.GetStorageGeometryAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(3, geometry.PhysicalPartitionNumber);
        Assert.Equal(1, geometry.SectorSize);
        Assert.Equal(9007199254740993L, geometry.SectorCount);
        Assert.Equal(9007199254740993L, geometry.CapacityBytes);
        Assert.Contains("physical_partition_number=\"3\"", Encoding.UTF8.GetString(Assert.Single(transport.Writes)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"storage_info":{"block_size":0,"total_blocks":100}}""")]
    [InlineData("""{"storage_info":{"block_size":65537,"total_blocks":100}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":0}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":-1}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":9223372036854775807}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":1.5}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":"100"}}""")]
    [InlineData("""{"storage_info":{"block_size":512}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":100,"total_blocks":200}}""")]
    [InlineData("""{"storage_info":{"block_size":512,"total_blocks":100,"physical_partition_number":2}}""")]
    [InlineData("""{"storage_info":""")]
    public async Task GetStorageGeometryAsync_InvalidCapacity_RejectsInsteadOfGuessing(string json)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Report(json));
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.GetStorageGeometryAsync(0, TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task GetStorageGeometryAsync_ConflictingReports_RejectsAmbiguousCapacity()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Report("""{"storage_info":{"block_size":512,"total_blocks":100}}""", false)
            + Report("""{"storage_info":{"block_size":512,"total_blocks":200}}"""));
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetStorageGeometryAsync_ExcessiveReports_FaultsBeforeUnboundedCollection()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(string.Concat(Enumerable.Repeat(
            Report("""{"storage_info":{"block_size":512,"total_blocks":100}}""", false), 9)));
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() =>
            protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task GetStorageGeometryAsync_UnexpectedRawMode_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value='ACK' rawmode='true'/></data>");
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() =>
            protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ConfigureAsync_GeometryInJsonLog_AutoDetectsReportedSectorSize()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1
            ? "<data><response value='ACK'/></data>"
            : Report("""{"storage_info":{"block_size":4096,"total_blocks":100}}"""))];
        using var protocol = new FirehoseProtocol(transport);

        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4096, protocol.Configuration.SectorSize);
        Assert.Equal(2, transport.Writes.Count);
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("NAK")]
    public async Task GetStorageGeometryAsync_MissingCapacity_DoesNotReusePreviousReport(string response)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1
            ? Report("""{"storage_info":{"block_size":512,"total_blocks":100}}""")
            : $"<data><response value='{response}'/></data>")];
        using var protocol = new FirehoseProtocol(transport);
        await protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.GetStorageGeometryAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task GetStorageGeometryAsync_DescriptiveFields_MergesLastStorageDetails()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Report("""{"storage_info":{"mem_type":"ufs","block_size":4096,"total_blocks":100,"page_size":2048}}""", false)
            + Report("""{"storage_info":{"storage_type":"UFS","block_size":4096,"total_blocks":100,"serial_num":"0xABCDEF","fw_ver":"3.1","manufacturer_id":"Samsung","density":617545818112,"physical_partition_number":1}}"""));
        using var protocol = new FirehoseProtocol(transport);

        var geometry = await protocol.GetStorageGeometryAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(4096, geometry.SectorSize);
        var details = protocol.LastStorageDetails;
        Assert.NotNull(details);
        Assert.Equal("UFS", details.StorageType);
        Assert.Equal("ufs", details.MemType);
        Assert.Equal(2048, details.PageSize);
        Assert.Equal(617545818112L, details.Density);
        Assert.Equal("0xABCDEF", details.SerialNumber);
        Assert.Equal("3.1", details.FirmwareVersion);
        Assert.Equal("Samsung", details.ManufacturerId);
        Assert.Equal(1, details.PhysicalPartitionNumber);
    }

    [Fact]
    public async Task GetStorageGeometryAsync_GeometryOnlyReports_LeavesDescriptiveFieldsNull()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Report("""{"storage_info":{"block_size":512,"total_blocks":100}}"""));
        using var protocol = new FirehoseProtocol(transport);

        await protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(protocol.LastStorageDetails);
        Assert.Null(protocol.LastStorageDetails.StorageType);
        Assert.Null(protocol.LastStorageDetails.SerialNumber);
    }

    [Fact]
    public async Task GetStorageGeometryAsync_MalformedDescriptiveFields_KeepsGeometryStrictness()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Report("""{"storage_info":{"block_size":512,"total_blocks":100,"density":"not-a-number","page_size":[]}}"""));
        using var protocol = new FirehoseProtocol(transport);

        var geometry = await protocol.GetStorageGeometryAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(512, geometry.SectorSize);
        Assert.Null(protocol.LastStorageDetails!.PageSize);
        Assert.Null(protocol.LastStorageDetails.Density);
    }

    [Theory]
    [InlineData("0", 100, 0)]
    [InlineData("99", 1, 99)]
    [InlineData("NUM_DISK_SECTORS-1.", 1, 99)]
    [InlineData("NUM_DISK_SECTORS-100.", 100, 0)]
    public void ResolveRange_WithinCapacity_ResolvesExactly(string start, long count, long expected)
    {
        var geometry = new FirehoseStorageGeometry(0, 512, 100);

        Assert.Equal(expected, geometry.ResolveRange(start, count));
    }

    [Theory]
    [InlineData("100", 1)]
    [InlineData("99", 2)]
    [InlineData("NUM_DISK_SECTORS-101.", 1)]
    [InlineData("NUM_DISK_SECTORS-1.", 2)]
    public void ResolveRange_OutsideCapacity_Rejects(string start, long count)
    {
        var geometry = new FirehoseStorageGeometry(0, 512, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => geometry.ResolveRange(start, count));
    }

    private static string Report(string json, bool acknowledge = true)
    {
        var root = new XElement("data", new XElement("log", new XAttribute("value", json)));
        if (acknowledge)
        {
            root.Add(new XElement("response", new XAttribute("value", "ACK")));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }
}