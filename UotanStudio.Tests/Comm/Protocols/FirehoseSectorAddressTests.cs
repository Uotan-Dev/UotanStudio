using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class FirehoseSectorAddressTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("0x20", "32")]
    [InlineData("512.", "512")]
    [InlineData("NUM_DISK_SECTORS-1.", "NUM_DISK_SECTORS-1.")]
    [InlineData("NUM_DISK_SECTORS-33", "NUM_DISK_SECTORS-33.")]
    public void Parse_KnownForms_NormalizesWithoutEvaluatingDiskSize(string value, string expected)
    {
        Assert.Equal(expected, FirehoseSectorAddress.Parse(value).ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NUM_DISK_SECTORS+1.")]
    [InlineData("NUM_DISK_SECTORS-0.")]
    [InlineData("NUM_DISK_SECTORS-1*2.")]
    [InlineData("0xFFFFFFFFFFFFFFFF")]
    [InlineData("9223372036854775808")]
    [InlineData("1..")]
    public void Parse_UnsupportedOrOverflowingExpression_Rejects(string value)
    {
        Assert.Throws<ArgumentException>(() => FirehoseSectorAddress.Parse(value));
    }

    [Fact]
    public void ValidateRange_BeyondEndOfDisk_Rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirehoseSectorAddress.Parse("NUM_DISK_SECTORS-1.").ValidateRange(2));
    }

    [Fact]
    public async Task PatchAsync_BackupGptExpression_TransmitsDiskPatchUnchanged()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value=\"ACK\"/></data>");
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });

        var response = await protocol.PatchAsync("DISK", 1, "NUM_DISK_SECTORS-1.", 16,
            "CRC32(NUM_DISK_SECTORS-1.,92)", 4, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var xml = Encoding.UTF8.GetString(Assert.Single(transport.Writes));
        Assert.Contains("filename=\"DISK\"", xml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"NUM_DISK_SECTORS-1.\"", xml, StringComparison.Ordinal);
        Assert.Contains("value=\"CRC32(NUM_DISK_SECTORS-1.,92)\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramAsync_BackupGptAddress_PreservesDiskRelativeExpression()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value=\"ACK\" rawmode=\"true\"/></data>");
        transport.Enqueue("<data><response value=\"ACK\" rawmode=\"false\"/></data>");
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        using var source = new MemoryStream(new byte[512]);

        await protocol.ProgramAsync(source, "NUM_DISK_SECTORS-1.", 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("start_sector=\"NUM_DISK_SECTORS-1.\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Equal(512, transport.Writes[1].Length);
    }

    [Fact]
    public async Task EraseAsync_RangeBeyondDiskEnd_RejectsBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.EraseAsync("NUM_DISK_SECTORS-1.", 2, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }
}