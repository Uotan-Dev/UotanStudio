using System.Text;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class FlashBundleSourceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FlashBundleAsync_SparseAccounting_CountsOnlyCompletedDataExtents(bool allHoles, bool fillOnly)
    {
        using var directory = new TempDirectory();
        var image = allHoles
            ? FirehoseSparseTests.BuildImage((0xCAC3, 4, []))
            : fillOnly
                ? FirehoseSparseTests.BuildImage((0xCAC2, 4, [1, 2, 3, 4]))
                : FirehoseSparseTests.BuildImage((0xCAC1, 1, new byte[512]), (0xCAC3, 2, []), (0xCAC2, 1, [1, 2, 3, 4]));
        directory.WriteBytes("source.img", image);
        var manifest = directory.Write("rawprogram.xml", """
            <data><program filename="source.img" start_sector="0" num_partition_sectors="4" SECTOR_SIZE_IN_BYTES="512"/></data>
            """);
        using var transport = Transport();
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesProcessed);
        Assert.Equal(allHoles ? 0 : fillOnly ? 2048 : 1024, result.TotalBytesWritten);
        Assert.NotEqual(image.Length, result.TotalBytesWritten);
    }

    [Fact]
    public async Task FlashBundleAsync_LaterSparseExtentRejected_PreservesEarlierAcknowledgedBytes()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", FirehoseSparseTests.BuildImage(
            (0xCAC1, 1, new byte[512]), (0xCAC2, 1, [1, 2, 3, 4])));
        var manifest = directory.Write("rawprogram.xml", """
            <data><program filename="source.img" start_sector="0" num_partition_sectors="2" SECTOR_SIZE_IN_BYTES="512"/></data>
            """);
        using var transport = AccountingTransport(2);
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(512, result.TotalBytesWritten);
        Assert.Equal(2, transport.Writes.Count(bytes => bytes.Length == 512));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashBundleAsync_RawRejected_DoesNotCountUnacknowledgedPayload(bool readyRejected)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = readyRejected ? Transport(rejectProgram: true) : AccountingTransport(1);
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Manifest(directory), null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.TotalBytesWritten);
        Assert.Equal(readyRejected ? 0 : 1, transport.Writes.Count(bytes => bytes.Length == 512));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashBundleAsync_WipeAccounting_CountsOnlyFinalAcknowledgement(bool rejectWipe)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = AccountingTransport(rejectWipe ? 2 : 0);
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = Manifest(directory),
            ImageDirectory = directory.Path,
            WipeFirstSectors = 2
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(!rejectWipe, result.IsSuccess);
        Assert.Equal(rejectWipe ? 512 : 1536, result.TotalBytesWritten);
        Assert.Equal(rejectWipe ? 0 : 1, result.WipeEntriesProcessed);
        Assert.Equal(1, result.ProgramEntriesProcessed);
    }

    private static ScriptedCommTransport AccountingTransport(int rejectPayload)
    {
        var transport = Transport();
        var respond = transport.OnWrite!;
        var payloads = 0;
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != '<' && ++payloads == rejectPayload)
            {
                return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
            }
            return respond(bytes);
        };
        return transport;
    }
}