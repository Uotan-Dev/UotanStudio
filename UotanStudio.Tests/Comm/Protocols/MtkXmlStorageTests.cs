using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Fact]
    public async Task InitializeXmlStageTwoHardwareAsync_CompletePair_RequiresDa2AndPreservesMetadata()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        var before = transport.Writes.Count;
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.SetXmlHostInfoAsync("DA2", TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        await session.InitializeXmlStageTwoHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.XFlashStorage);
        var commands = transport.Writes.Skip(before).Where(value => value[0] == '<')
            .Select(value => XDocument.Parse(Encoding.UTF8.GetString(value).TrimEnd('\0')).Root!).ToArray();
        Assert.Equal(new[] { "CMD:HOST-SUPPORTED-COMMANDS", "CMD:NOTIFY-INIT-HW", "CMD:SET-HOST-INFO" },
            commands.Select(value => value.Element("command")!.Value));
        Assert.Equal("CMD:PROGRESS-REPORT^1@", commands[0].Element("arg")!.Element("host_capability")!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeXmlStageTwoHardwareAsync_Rejection_FaultsWithoutAdvancing(bool secondCommand)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoSessionAsync(transport);
        if (secondCommand)
        {
            EnqueueXmlCompletion(transport);
        }
        EnqueueXmlText(transport, "ERR!REJECTED");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlStageTwoHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task XmlStageTwoOperationsAsync_BeforeDa2_RejectBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeXmlStageTwoHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverXmlStorageAsync_CompleteExchange_PublishesGeometryOnlyAfterHandoff(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(ufs));
        var before = transport.Writes.Count;
        transport.OnWrite = _ =>
        {
            Assert.Null(session.XmlStorage);
            Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
            return [];
        };

        var storage = await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(storage, session.XmlStorage);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Equal(ufs ? MtkXmlStorageKind.Ufs : MtkXmlStorageKind.Emmc, storage.Kind);
        Assert.Equal(512, storage.BlockSize);
        Assert.Equal("0011AABB", storage.CidHex);
        Assert.Equal(ufs ? 4 : 8, storage.Regions.Count);
        Assert.Equal(0x4000, storage.Regions[0].CapacityBytes);
        Assert.Equal(0x8000, storage.Regions[1].CapacityBytes);
        Assert.Null(session.XFlashStorage);
        var commands = transport.Writes.Skip(before).Where(value => value[0] == '<')
            .Select(value => XDocument.Parse(Encoding.UTF8.GetString(value).TrimEnd('\0')).Root!).ToArray();
        Assert.Equal("CMD:PROGRESS-REPORT^1@CMD:UPLOAD-FILE^1@", commands[0].Element("arg")!.Element("host_capability")!.Value);
        Assert.Equal("CMD:GET-HW-INFO", commands[1].Element("command")!.Value);
        Assert.Equal("MEM://0x0:0x10000", commands[1].Element("arg")!.Element("target_file")!.Value);
        Assert.Equal(20, transport.Writes.Count - before);
        transport.OnWrite = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("target_file", "C:/secret.bin")]
    [InlineData("target_file", "MEM://0x0:0x10001")]
    [InlineData("checksum", "CHK_SUM")]
    [InlineData("info", "")]
    [InlineData("packet_length", "0x0")]
    [InlineData("packet_length", "0x40001")]
    [InlineData("packet_length", "4096")]
    [InlineData("packet_length", "0xgg")]
    [InlineData("extra", "value")]
    public async Task DiscoverXmlStorageAsync_InvalidCallback_FaultsBeforeDestinationAcceptance(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        var request = XmlStorageRequest();
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("OK@0x0")]
    [InlineData("OK@0x10001")]
    [InlineData("OK@0xffffffff")]
    [InlineData("OK@65536")]
    [InlineData("OK@0xzz")]
    [InlineData("ERR!REJECTED")]
    public async Task DiscoverXmlStorageAsync_InvalidLength_FaultsBeforeDataAcceptance(string length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStoragePrefix(transport);
        EnqueueXmlText(transport, length);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(18)]
    public async Task DiscoverXmlStorageAsync_DataExceedsBounds_FaultsWithoutPublishing(int frameSize)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStoragePrefix(transport);
        EnqueueXmlText(transport, "OK@0x8");
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, new byte[frameSize]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task DiscoverXmlStorageAsync_FrameExceedsPacketButNotTotal_Rejects()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStoragePrefix(transport);
        EnqueueXmlText(transport, "OK@0x40");
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, new byte[18]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task DiscoverXmlStorageAsync_InvalidUtf8_RejectsWithoutReplacementOrPublication()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStoragePrefix(transport);
        EnqueueXmlText(transport, "OK@0x2");
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, [0xc3, 0x28]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DiscoverXmlStorageAsync_ControlPhaseRejected_FaultsWithoutPublication(int phase)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        if (phase == 0)
        {
            EnqueueXmlText(transport, "ERR!UNSUPPORTED");
        }
        else if (phase == 1)
        {
            EnqueueXmlCompletion(transport);
            EnqueueXmlText(transport, "ERR!UNSUPPORTED");
        }
        else
        {
            EnqueueXmlStoragePrefix(transport);
            EnqueueXmlText(transport, "OK@0x10");
            EnqueueXmlText(transport, "ERR!UNSUPPORTED");
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task XmlStageTwoOperationsAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.InitializeXmlStageTwoHardwareAsync(
            timeoutMs, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.DiscoverXmlStorageAsync(
            timeoutMs, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverXmlStorageAsync_FinalHandshakeRejected_DiscardsParsedGeometry(bool missingStart)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(false), finish: false);
        EnqueueXmlText(transport, XmlControl("END", missingStart ? "OK" : "FAILED"));
        EnqueueXmlText(transport, XmlControl("END", "OK"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverXmlStorageAsync_Cancellation_DistinguishesPreIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DiscoverXmlStorageAsync(
            cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task DiscoverXmlStorageAsync_QueuedMetadata_HoldsLeaseAndInvalidatesGeometryOnLaterFailure()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(false));
        EnqueueXmlText(transport, "ERR!REJECTED");
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.SetXmlHostInfoAsync("after discovery", TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        var geometry = await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await Assert.ThrowsAsync<MtkProtocolException>(() => queued);

        Assert.Equal(MtkXmlStorageKind.Emmc, geometry.Kind);
        Assert.Null(session.XmlStorage);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XmlStorageInfo_RegionMappingAndRange_SeparatesXmlIdentifiersAndAuthenticatedRegions(bool ufs)
    {
        var storage = MtkXmlStorageInfo.Parse(XmlHardwareDocument(ufs).ToString());
        var ordinary = ufs ? "UFS-LUA2" : "EMMC-USER";
        var authenticated = ufs ? "UFS-LUA3" : "EMMC-RPMB";
        Assert.Equal(ufs
            ? new[] { "UFS-LUA0", "UFS-LUA1", "UFS-LUA2", "UFS-LUA3" }
            : new[] { "EMMC-BOOT1", "EMMC-BOOT2", "EMMC-RPMB", "EMMC-USER", "EMMC-GP1", "EMMC-GP2", "EMMC-GP3", "EMMC-GP4" },
            storage.Regions.Select(region => region.Name));

        storage.ValidateRange(ordinary, 0, 0x10000);
        Assert.Throws<NotSupportedException>(() => storage.ValidateRange(authenticated, 0, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(ordinary, 1, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(ordinary, 0, 513));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(ordinary, 0x10000, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(ordinary, long.MaxValue - 511, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange("8", 0, 512));
    }

    [Theory]
    [InlineData("block_size", "0x0")]
    [InlineData("block_size", "0x201")]
    [InlineData("block_size", "0x20000")]
    [InlineData("user_size", "0xffffffffffffffff")]
    [InlineData("user_size", "0x8000000000000000")]
    [InlineData("user_size", "0x201")]
    [InlineData("user_size", "-512")]
    [InlineData("user_size", "0x10000000000000000")]
    [InlineData("id", "xyz")]
    [InlineData("emmc_cid", "0xFFFF")]
    public void XmlStorageInfo_InvalidGeometry_RejectsUntrustedValues(string field, string value)
    {
        var document = XmlHardwareDocument(false);
        document.Element("emmc")!.SetElementValue(field, value);

        Assert.Throws<MtkProtocolException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("storage")]
    [InlineData("emmc")]
    public void XmlStorageInfo_MissingRequiredField_Rejects(string field)
    {
        var document = XmlHardwareDocument(false);
        document.Element(field)!.Remove();

        Assert.Throws<MtkProtocolException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    [Fact]
    public void XmlStorageInfo_CidVariantOrAbsent_PreservesOptionalIdentityWithoutInventingOne()
    {
        var document = XmlHardwareDocument(true);
        var geometry = document.Element("ufs")!;
        geometry.Element("id")!.Remove();
        Assert.Null(MtkXmlStorageInfo.Parse(document.ToString()).CidHex);
        geometry.SetElementValue("ufs_cid", "0011aabb");
        Assert.Equal("0011AABB", MtkXmlStorageInfo.Parse(document.ToString()).CidHex);
        geometry.SetElementValue("id", "11223344");
        var storage = MtkXmlStorageInfo.Parse(document.ToString());
        Assert.Equal("0011AABB", storage.CidHex);
        Assert.Equal("11223344", storage.ReportedIdHex);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("attribute")]
    [InlineData("nested")]
    [InlineData("namespace")]
    [InlineData("comment")]
    [InlineData("mixed")]
    [InlineData("conflict")]
    public void XmlStorageInfo_StructuralAttack_Rejects(string attack)
    {
        var document = XmlHardwareDocument(false);
        switch (attack)
        {
            case "duplicate":
                document.Add(new XElement("storage", "EMMC"));
                break;
            case "attribute":
                document.SetAttributeValue("unexpected", "value");
                break;
            case "nested":
                document.Element("emmc")!.Element("user_size")!.Add(new XElement("nested", "0"));
                break;
            case "namespace":
                document.Element("storage")!.Name = XName.Get("storage", "urn:test");
                break;
            case "comment":
                document.Add(new XComment("unexpected"));
                break;
            case "mixed":
                document.Add(new XText("unexpected"));
                break;
            default:
                document.Add(new XElement("ufs"));
                break;
        }

        Assert.Throws<InvalidDataException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    [Theory]
    [InlineData("storage", "NAND")]
    [InlineData("storage", "NONE")]
    [InlineData("storage", "ufs")]
    [InlineData("version", "1.0")]
    [InlineData("version", "1.3")]
    public void XmlStorageInfo_UnsupportedProfile_DoesNotGuess(string field, string value)
    {
        var document = XmlHardwareDocument(false);
        document.SetElementValue(field, value);

        Assert.Throws<MtkProtocolException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    [Fact]
    public void XmlStorageInfo_NoOrdinaryCapacity_RejectsEvenWithRpmb()
    {
        var document = XmlHardwareDocument(false);
        foreach (var field in document.Element("emmc")!.Elements())
        {
            if (field.Name.LocalName.EndsWith("_size", StringComparison.Ordinal)
                && field.Name.LocalName is not ("rpmb_size" or "block_size"))
            {
                field.Value = "0";
            }
        }

        Assert.Throws<MtkProtocolException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    [Fact]
    public void XmlStorageInfo_XmlResourceLimits_RejectBeforeTreeUse()
    {
        var document = XmlHardwareDocument(false);
        var xml = document.ToString(SaveOptions.DisableFormatting);
        Assert.Throws<XmlException>(() => MtkXmlStorageInfo.Parse("<!DOCTYPE da_hw_info [<!ENTITY x 'test'>]>" + xml));
        Assert.Throws<InvalidDataException>(() => MtkXmlStorageInfo.Parse(new string(' ', 65537)));
        document.Element("product_id")!.Value = new string('A', 4097);
        Assert.Throws<InvalidDataException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
        document.Element("product_id")!.Value = "";
        for (var index = 0; index < 33; index++)
        {
            document.Add(new XElement("metadata" + index, "0"));
        }
        Assert.Throws<InvalidDataException>(() => MtkXmlStorageInfo.Parse(document.ToString()));
    }

    private static XElement XmlHardwareDocument(bool ufs)
    {
        string[] names = ufs
            ? ["lua0_size", "lua1_size", "lua2_size", "lua3_size"]
            : ["boot1_size", "boot2_size", "rpmb_size", "user_size", "gp1_size", "gp2_size", "gp3_size", "gp4_size"];
        string[] sizes = ufs
            ? ["0x4000", "0x8000", "0x10000", "0x2000"]
            : ["0x4000", "0x8000", "0x2000", "0x10000", "0", "0", "0", "0"];
        return new XElement("da_hw_info", new XElement("version", "1.2"),
            new XElement("ram_size", "0x100000000"), new XElement("battery_voltage", "3800"),
            new XElement("storage", ufs ? "UFS" : "EMMC"),
            new XElement(ufs ? "ufs" : "emmc", new XElement("block_size", "0x200"),
                names.Select((name, index) => new XElement(name, sizes[index])), new XElement("id", "0011aabb")),
            new XElement("product_id", ""));
    }

    private static XElement XmlStorageRequest() => new("host", new XElement("version", "1.0"),
        new XElement("command", "CMD:UPLOAD-FILE"), new XElement("arg",
            new XElement("checksum", "CHK_NO"), new XElement("info", "hardware-info"),
            new XElement("target_file", "MEM://0x0:0x10000"), new XElement("packet_length", "0x11")));

    private static void EnqueueXmlStoragePrefix(ScriptedCommTransport transport)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlStorageRequest().ToString(SaveOptions.DisableFormatting));
    }

    private static void EnqueueXmlStorage(ScriptedCommTransport transport, XElement document, bool finish = true)
    {
        EnqueueXmlStoragePrefix(transport);
        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting) + "\0");
        EnqueueXmlText(transport, $"OK@0x{bytes.Length:x}");
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < bytes.Length; offset += 17)
        {
            EnqueueXFlashFrame(transport, bytes.AsSpan(offset, Math.Min(17, bytes.Length - offset)).ToArray());
        }
        if (finish)
        {
            EnqueueXmlText(transport, XmlControl("END", "OK"));
            EnqueueXmlText(transport, XmlControl("START"));
        }
    }

    private static async Task<MtkSession> CreateXmlStageTwoSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        EnqueueXmlUpload(transport, 8, 8);
        await session.UploadXmlStageTwoAsync(source, 0x400000, 8, cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    private static async Task<MtkSession> CreateXmlStageTwoHardwareSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateXmlStageTwoSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        await session.InitializeXmlStageTwoHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}