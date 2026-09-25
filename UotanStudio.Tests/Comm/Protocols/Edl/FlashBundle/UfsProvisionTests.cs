using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class UfsProvisionTests
{
    [Theory]
    [InlineData("common", "bNumberLU", "BNumberLU")]
    [InlineData("common", "bBootEnable", "BBootEnable")]
    [InlineData("common", "bDescrAccessEn", "BDescrAccessEn")]
    [InlineData("common", "bInitPowerMode", "BInitPowerMode")]
    [InlineData("common", "bHighPriorityLUN", "BHighPriorityLUN")]
    [InlineData("common", "bSecureRemovalType", "BSecureRemovalType")]
    [InlineData("common", "bInitActiveICCLevel", "BInitActiveICCLevel")]
    [InlineData("common", "wPeriodicRTCUpdate", "WPeriodicRTCUpdate")]
    [InlineData("common", "bConfigDescrLock", "BConfigDescrLock")]
    [InlineData("body", "bLUEnable", "BLUEnable")]
    [InlineData("body", "bBootLunID", "BBootLUNID")]
    [InlineData("body", "bDataReliability", "BDataReliability")]
    [InlineData("body", "bLUWriteProtect", "BLUWriteProtect")]
    [InlineData("body", "bMemoryType", "BMemoryType")]
    [InlineData("body", "bLogicalBlockSize", "BLogicalBlockSize")]
    [InlineData("body", "bProvisioningType", "BProvisioningType")]
    [InlineData("body", "wContextCapabilities", "WContextCapacities")]
    [InlineData("epilogue", "LUNtoGrow", "LUNumToGrow")]
    public void Parse_CanonicalAndLegacyAttributes_PreserveValuesAndEmitCanonicalNames(string type, string canonical, string legacy)
    {
        foreach (var key in new[] { canonical, legacy })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<data><ufs type='{type}' {key}='7'/></data>"));
            var entry = Assert.Single(UfsProvisionParser.Parse(stream).Entries);
            var wire = entry switch
            {
                UfsProvisionCommon common => FirehoseXmlBuilder.BuildUfsCommon(new UfsCommon
                {
                    BNumberLU = common.BNumberLU,
                    BBootEnable = common.BBootEnable,
                    BDescrAccessEn = common.BDescrAccessEn,
                    BInitPowerMode = common.BInitPowerMode,
                    BHighPriorityLUN = common.BHighPriorityLUN,
                    BSecureRemovalType = common.BSecureRemovalType,
                    BInitActiveICCLevel = common.BInitActiveICCLevel,
                    WPeriodicRTCUpdate = common.WPeriodicRTCUpdate,
                    BConfigDescrLock = common.BConfigDescrLock
                }),
                UfsProvisionBody body => FirehoseXmlBuilder.BuildUfsBody(new UfsBody
                {
                    BLUEnable = body.BLUEnable,
                    BBootLUNID = body.BBootLUNID,
                    BDataReliability = body.BDataReliability,
                    BLUWriteProtect = body.BLUWriteProtect,
                    BMemoryType = body.BMemoryType,
                    BLogicalBlockSize = body.BLogicalBlockSize,
                    BProvisioningType = body.BProvisioningType,
                    WContextCapacities = body.WContextCapacities
                }),
                UfsProvisionEpilogue epilogue => FirehoseXmlBuilder.BuildUfsEpilogue(new UfsEpilogue { LUNumToGrow = epilogue.LUNumToGrow }, false),
                _ => throw new InvalidOperationException()
            };
            var element = XDocument.Parse(wire).Root!.Element("ufs")!;
            Assert.Equal("7", (string?)element.Attribute(canonical));
            Assert.Null(element.Attribute(legacy));
        }
        using var ambiguous = new MemoryStream(Encoding.UTF8.GetBytes(
            $"<data><ufs type='{type}' {canonical}='7' {legacy}='7'/></data>"));
        Assert.Throws<EdlException>(() => UfsProvisionParser.Parse(ambiguous));
    }

    [Theory]
    [InlineData("negative-byte")]
    [InlineData("large-byte")]
    [InlineData("large-word")]
    [InlineData("body-byte")]
    [InlineData("body-word")]
    [InlineData("duplicate")]
    [InlineData("null-body")]
    [InlineData("empty")]
    [InlineData("many")]
    [InlineData("size")]
    [InlineData("description")]
    [InlineData("xml-description")]
    [InlineData("grow")]
    [InlineData("slot")]
    public async Task ProvisionUfsAsync_InvalidLaterParameters_RejectsBeforeFirstCommand(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { Slot = failure == "slot" ? -1 : 0 });
        var common = new UfsCommon();
        var bodies = new List<UfsBody> { new() };
        var epilogue = new UfsEpilogue();
        switch (failure)
        {
            case "negative-byte": common.BNumberLU = -1; break;
            case "large-byte": common.BBootEnable = 256; break;
            case "large-word": common.WPeriodicRTCUpdate = 65536; break;
            case "body-byte": bodies[0].BLogicalBlockSize = 256; break;
            case "body-word": bodies[0].WContextCapacities = -1; break;
            case "duplicate": bodies.Add(new UfsBody()); break;
            case "null-body": bodies.Add(null!); break;
            case "empty": bodies.Clear(); break;
            case "many": bodies = Enumerable.Range(0, 257).Select(index => new UfsBody { LUNum = index }).ToList(); break;
            case "size": bodies[0].SizeInKb = long.MaxValue; break;
            case "description": bodies[0].Desc = new string('x', 4097); break;
            case "xml-description": bodies[0].Desc = "\0"; break;
            case "grow": epilogue.LUNumToGrow = -1; break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => protocol.ProvisionUfsAsync(common, bodies, epilogue,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProvisionUfsAsync_InputsMutatedAfterFirstCommand_SendsPreparedSnapshot()
    {
        using var transport = new ScriptedCommTransport();
        var config = new FirehoseConfiguration { Slot = 2 };
        using var protocol = new FirehoseProtocol(transport, config);
        var common = new UfsCommon { BNumberLU = 1 };
        var body = new UfsBody { LUNum = 0, SizeInKb = 1024, Desc = "a<&b" };
        var bodies = new List<UfsBody> { body };
        var epilogue = new UfsEpilogue { LUNumToGrow = 0 };
        transport.OnWrite = _ =>
        {
            bodies.Clear();
            body.SizeInKb = -1;
            epilogue.LUNumToGrow = -1;
            config.Slot = 9;
            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };

        var result = await protocol.ProvisionUfsAsync(common, bodies, epilogue, true, TestContext.Current.CancellationToken);

        Assert.True(result.IsAck);
        var commands = transport.Writes.Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Element("ufs")!).ToArray();
        Assert.Equal(3, commands.Length);
        Assert.All(commands, command => Assert.Equal("2", (string?)command.Attribute("slot")));
        Assert.Equal("1024", (string?)commands[1].Attribute("size_in_kb"));
        Assert.Equal("a<&b", (string?)commands[1].Attribute("desc"));
        Assert.Equal("0", (string?)commands[2].Attribute("LUNtoGrow"));
        Assert.Equal("1", (string?)commands[2].Attribute("commit"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionUfsAsync_InvalidResponse_StopsBeforeBodyAndCommit(bool raw)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(raw ? "<data><response value='ACK' rawmode='true'/></data>" : "<data><response value='NAK'/></data>");
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<EdlException>(() => protocol.ProvisionUfsAsync(new UfsCommon(), [new UfsBody()],
            new UfsEpilogue(), true, TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.Equal(raw, protocol.RequiresReconnect);
    }
}