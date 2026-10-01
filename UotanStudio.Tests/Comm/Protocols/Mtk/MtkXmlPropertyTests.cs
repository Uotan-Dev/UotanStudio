using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData("DISABLED", MtkXmlSlaIndication.DisabledReported)]
    [InlineData("ENABLED", MtkXmlSlaIndication.EnabledReported)]
    [InlineData("DISABLE", MtkXmlSlaIndication.Unknown)]
    [InlineData("disabled", MtkXmlSlaIndication.Unknown)]
    [InlineData(" DISABLED ", MtkXmlSlaIndication.Unknown)]
    [InlineData("UNKNOWN", MtkXmlSlaIndication.Unknown)]
    public async Task QueryXmlSlaStatusAsync_ExactValue_ReturnsReportWithoutChangingAuthorization(
        string value, MtkXmlSlaIndication indication)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", value));
        var before = transport.Writes.Count;

        var report = await session.QueryXmlSlaStatusAsync("test_properties",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(indication, report.Indication);
        Assert.Equal(value, report.Property.Value);
        Assert.Equal("DA.SLA", report.Property.Key);
        Assert.Equal("test_properties", report.Property.EnvelopeName);
        Assert.Equal("1.0", report.Property.Version);
        Assert.Null(session.XmlStorage);
        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:GET-SYS-PROPERTY", command.Element("command")!.Value);
        Assert.Equal("DA.SLA", command.Element("arg")!.Element("key")!.Value);
        Assert.Equal("MEM://0x0:0x10000", command.Element("arg")!.Element("target_file")!.Value);
        Assert.Equal(20, transport.Writes.Count - before);
    }

    [Fact]
    public async Task QueryXmlSystemPropertyAsync_EscapedKeyAfterDiscovery_PreservesIdentityAndGeometry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        const string key = "DA.A&B<value>";
        EnqueueXmlStorage(transport, XmlPropertyDocument(key, "value & <data>"));
        var geometry = session.XmlStorage;
        var before = transport.Writes.Count;

        var property = await session.QueryXmlSystemPropertyAsync(key, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(key, property.Key);
        Assert.Equal("value & <data>", property.Value);
        Assert.Same(geometry, session.XmlStorage);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal(key, command.Element("arg")!.Element("key")!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryXmlSlaStatusAsync_MismatchedKeyOrEnvelope_FaultsWithoutDisabledReport(bool envelope)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var document = XmlPropertyDocument(envelope ? "DA.SLA" : "OTHER", "DISABLED");
        if (envelope)
        {
            document.Name = "unselected_profile";
        }
        EnqueueXmlStorage(transport, document);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.QueryXmlSlaStatusAsync(
            "test_properties", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryXmlSlaStatusAsync_FinalHandshakeFailure_DiscardsDisabledValue(bool start)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "DISABLED"), finish: false);
        EnqueueXmlText(transport, XmlControl("END", start ? "OK" : "FAILED"));
        EnqueueXmlText(transport, XmlControl("END", "OK"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.QueryXmlSlaStatusAsync(
            "test_properties", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task QueryXmlSlaStatusAsync_UnsupportedCommand_DoesNotReturnDisabled()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "ERR!UNSUPPORTED");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.QueryXmlSlaStatusAsync(
            "test_properties", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task QueryXmlSystemPropertyAsync_InvalidUtf8_RejectsWithoutReplacement()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStoragePrefix(transport);
        EnqueueXmlText(transport, "OK@0x2");
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, [0xc3, 0x28]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => session.QueryXmlSystemPropertyAsync(
            "DA.SLA", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryXmlSystemPropertyAsync_Cancellation_DistinguishesPreIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ => { cancellation.Cancel(); return []; };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryXmlSystemPropertyAsync(
            "DA.SLA", cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
    }

    [Fact]
    public async Task QueryXmlSystemPropertyAsync_QueuedSecurityUpload_WaitsForFinalHandoff()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var resource = new MemoryStream(new byte[512]);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        EnqueueXmlSecurityResource(transport, 512, 512);
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.UploadXmlSecurityResourceAsync(MtkXmlSecurityResourceKind.FlashPolicy,
                resource, 512, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        var report = await session.QueryXmlSlaStatusAsync("test_properties",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(MtkXmlSlaIndication.EnabledReported, report.Indication);
        Assert.Equal(512, resource.Position);
        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
    }

    [Fact]
    public async Task QueryXmlSystemPropertyAsync_Da1_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryXmlSystemPropertyAsync(
            "DA.SLA", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task QueryXmlSystemPropertyAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.QueryXmlSystemPropertyAsync(
            "DA.SLA", timeoutMs, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task QueryXmlSystemPropertyAsync_InvalidKeyOrProfile_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => session.QueryXmlSystemPropertyAsync(
            " ", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.QueryXmlSystemPropertyAsync(
            new string('A', 129), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<XmlException>(() => session.QueryXmlSystemPropertyAsync(
            "DA.\0SLA", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<XmlException>(() => session.QueryXmlSlaStatusAsync(
            "prefix:properties", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-key")]
    [InlineData("missing-key")]
    [InlineData("attribute")]
    [InlineData("namespace")]
    [InlineData("empty")]
    [InlineData("oversized-value")]
    [InlineData("nested-empty")]
    [InlineData("mixed")]
    [InlineData("comment")]
    [InlineData("extra")]
    [InlineData("version-duplicate")]
    public void XmlSystemProperty_Parse_UntrustedShapeIsRejected(string attack)
    {
        var document = XmlPropertyDocument("DA.SLA", "DISABLED");
        var item = document.Element("item")!;
        switch (attack)
        {
            case "duplicate":
                document.Add(new XElement("item", new XAttribute("key", "DA.SLA"), "ENABLED"));
                break;
            case "wrong-key":
                item.SetAttributeValue("key", "OTHER");
                break;
            case "missing-key":
                item.Attribute("key")!.Remove();
                break;
            case "attribute":
                item.SetAttributeValue("extra", "ignored");
                break;
            case "namespace":
                item.Name = XName.Get("item", "urn:test");
                break;
            case "empty":
                item.Value = "";
                break;
            case "oversized-value":
                item.Value = new string('A', 4097);
                break;
            case "nested-empty":
                item.Add(new XElement("ignored"));
                break;
            case "mixed":
                document.Add(new XText("ENABLED"));
                break;
            case "comment":
                document.Add(new XComment("ignored"));
                break;
            case "version-duplicate":
                document.Add(new XElement("version", "1.0"));
                break;
            default:
                document.Add(new XElement("error", "FAILED"));
                break;
        }

        Assert.Throws<InvalidDataException>(() => MtkXmlSystemProperty.Parse(
            document.ToString(SaveOptions.DisableFormatting), "DA.SLA", "test_properties"));
    }

    [Fact]
    public void XmlSystemProperty_Parse_ResourceLimitsAndDtdAreRejected()
    {
        var xml = XmlPropertyDocument("DA.SLA", "DISABLED").ToString(SaveOptions.DisableFormatting);
        Assert.Throws<XmlException>(() => MtkXmlSystemProperty.Parse(
            "<!DOCTYPE test_properties [<!ENTITY x 'DISABLED'>]>" + xml, "DA.SLA", null));
        Assert.Throws<InvalidDataException>(() => MtkXmlSystemProperty.Parse(new string(' ', 65537), "DA.SLA", null));
        Assert.Throws<InvalidDataException>(() => MtkXmlSystemProperty.Parse(
            "<root><nested><item key=\"DA.SLA\">DISABLED</item></nested></root>", "DA.SLA", null));
    }

    private static XElement XmlPropertyDocument(string key, string value) => new("test_properties",
        new XElement("version", "1.0"), new XElement("item", new XAttribute("key", key), value));
}