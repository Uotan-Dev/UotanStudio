using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class FlashBundleSourceTests
{
    [Fact]
    public async Task FlashBundleAsync_UfsSlotChangesWhileQueued_RejectsBeforeInitialization()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        var ufs = directory.Write("ufs-provision.xml", """
            <data><ufs type="common"/><ufs type="body" LUNum="0"/><ufs type="epilogue" commit="0"/></data>
            """);
        using var transport = Transport();
        using var session = Session(transport);
        using var held = await session.Firehose!.EnterOperationAsync(TestContext.Current.CancellationToken);
        var pending = new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = Manifest(directory),
            ImageDirectory = directory.Path,
            UfsProvisionPath = ufs
        }, null, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        session.Firehose.Configuration.Slot = 2;
        held.Dispose();

        var result = await pending;

        Assert.False(result.IsSuccess);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Theory]
    [InlineData("missing-common")]
    [InlineData("missing-epilogue")]
    [InlineData("duplicate-common")]
    [InlineData("duplicate-epilogue")]
    [InlineData("order")]
    [InlineData("duplicate-lun")]
    [InlineData("negative-size")]
    [InlineData("byte-overflow")]
    [InlineData("commit")]
    [InlineData("slot")]
    public async Task FlashBundleAsync_InvalidUfs_RejectsBeforeEarlierEraseOrInitialization(string failure)
    {
        using var directory = new TempDirectory();
        var manifest = directory.Write("rawprogram.xml", """
            <data><program filename="" start_sector="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/></data>
            """);
        var common = new XElement("ufs", new XAttribute("type", "common"), new XAttribute("bNumberLU", 1));
        var body = new XElement("ufs", new XAttribute("type", "body"), new XAttribute("LUNum", 0));
        var epilogue = new XElement("ufs", new XAttribute("type", "epilogue"), new XAttribute("commit", 0));
        var document = new XElement("data", common, body, epilogue);
        switch (failure)
        {
            case "missing-common": common.Remove(); break;
            case "missing-epilogue": epilogue.Remove(); break;
            case "duplicate-common": common.AddAfterSelf(new XElement(common)); break;
            case "duplicate-epilogue": epilogue.AddBeforeSelf(new XElement(epilogue)); break;
            case "order": body.Remove(); document.Add(body); break;
            case "duplicate-lun": body.AddAfterSelf(new XElement(body)); break;
            case "negative-size": body.SetAttributeValue("size_in_kb", -1); break;
            case "byte-overflow": body.SetAttributeValue("bMemoryType", 256); break;
            case "commit": epilogue.SetAttributeValue("commit", 2); break;
            case "slot": body.SetAttributeValue("slot", 3); break;
        }
        var ufs = directory.Write("ufs-provision.xml", document.ToString());
        using var transport = Transport();
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = manifest,
            ImageDirectory = directory.Path,
            UfsProvisionPath = ufs
        }, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }
}