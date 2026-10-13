using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_Enabled_CompletesPolicyAndClearsBuffers()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        var document = XmlSlaDocument();
        EnqueueXmlStorage(transport, document);
        EnqueueXmlSecurityResource(transport, 32, 16);
        var response = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        ReadOnlyMemory<byte> borrowedInformation = default;
        ReadOnlyMemory<byte> borrowedChallenge = default;
        var responder = new DaSlaResponder((device, information, challenge, _) =>
        {
            Assert.Same(session.DeviceInfo, device);
            Assert.Equal(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8.GetString(information.Span));
            Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), challenge.ToArray());
            borrowedInformation = information;
            borrowedChallenge = challenge;
            return Task.FromResult(response);
        });
        var before = transport.Writes.Count;

        Assert.True(await session.AuthenticateXmlStageTwoAsync("test_properties", responder,
            cancellationToken: TestContext.Current.CancellationToken));
        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before).ToArray());
        Assert.Equal(2, frames.Count(frame => frame.SequenceEqual(Enumerable.Repeat((byte)0xA5, 16))));
        Assert.Equal(1, responder.Calls);
        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
        Assert.All(response, value => Assert.Equal(0, value));
        Assert.All(borrowedInformation.ToArray(), value => Assert.Equal(0, value));
        Assert.All(borrowedChallenge.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("DISABLED", false)]
    [InlineData("disabled", true)]
    [InlineData("UNKNOWN", true)]
    public async Task AuthenticateXmlStageTwoAsync_StatusReport_DoesNotGuessAuthorization(string status, bool fails)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", status));
        var responder = new DaSlaResponder((_, _, _, _) => throw new InvalidOperationException());

        if (fails)
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXmlStageTwoAsync(
                "test_properties", responder, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
        else
        {
            Assert.False(await session.AuthenticateXmlStageTwoAsync("test_properties", responder,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
        }
        Assert.Equal(0, responder.Calls);
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_MissingProvider_FaultsBeforeChallenge()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthenticateXmlStageTwoAsync(
            "test_properties", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_FailedFinalStatus_FaultsDespiteFullTransfer()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        EnqueueXmlStorage(transport, XmlSlaDocument());
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlSecurityRequest(16, 16).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "ERR!AUTH_FAILED"));
        var response = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(response));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXmlStageTwoAsync(
            "test_properties", responder, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.All(response, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_ConcurrentPropertyQuery_WaitsUntilAuthenticationCompletes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        EnqueueXmlStorage(transport, XmlSlaDocument());
        EnqueueXmlSecurityResource(transport, 16, 16);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "DISABLED"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = new DaSlaResponder(async (_, _, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new byte[16];
        });
        var authentication = session.AuthenticateXmlStageTwoAsync("test_properties", responder,
            cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        var query = session.QueryXmlSlaStatusAsync("test_properties",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(query.IsCompleted);
        Assert.Equal(before, transport.Writes.Count);
        release.SetResult();

        Assert.True(await authentication);
        Assert.Equal(MtkXmlSlaIndication.DisabledReported, (await query).Indication);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("namespace")]
    [InlineData("nested")]
    [InlineData("odd")]
    [InlineData("nonhex")]
    [InlineData("short")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("extra")]
    public void ParseXmlSlaChallenge_InvalidDocument_Rejects(string failure)
    {
        var document = XmlSlaDocument();
        switch (failure)
        {
            case "duplicate": document.Add(new XElement(document.Element("rnd")!)); break;
            case "namespace": document.Name = XName.Get("sla", "untrusted"); break;
            case "nested": document.Element("rnd")!.Add(new XElement("data", "00")); break;
            case "odd": document.Element("rnd")!.Value = "001"; break;
            case "nonhex": document.Element("rnd")!.Value = new string('Z', 32); break;
            case "short": document.Element("rnd")!.Value = "00"; break;
            case "missing": document.Element("rnd")!.Remove(); break;
            case "version": document.SetAttributeValue("version", "2.0"); break;
            case "extra": document.Add(new XElement("source", "/private/file")); break;
        }

        Assert.Throws<InvalidDataException>(() => MtkSession.ParseXmlSlaChallenge(
            document.ToString(SaveOptions.DisableFormatting)));
    }

    [Fact]
    public void ParseXmlSlaChallenge_Dtd_Rejects()
    {
        Assert.Throws<XmlException>(() => MtkSession.ParseXmlSlaChallenge(
            "<!DOCTYPE sla [<!ENTITY x '00'>]>" + XmlSlaDocument().ToString(SaveOptions.DisableFormatting)));
    }

    private static XElement XmlSlaDocument() => new("sla", new XAttribute("version", "1.0"),
        new XElement("rnd", "00112233445566778899AABBCCDDEEFF"),
        new XElement("hrid", "01020304"), new XElement("socid", "11223344"));

    private static XElement XmlSecurityRequest(int length, int packetLength) => new("host",
        new XElement("version", "1.0"), new XElement("command", "CMD:DOWNLOAD-FILE"),
        new XElement("arg", new XElement("checksum", "CHK_NO"), new XElement("info", "MEM"),
            new XElement("source_file", $"MEM://0x0:0x{length:x}"),
            new XElement("packet_length", $"0x{packetLength:x}")));

    private static void EnqueueXmlSecurityResource(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlSecurityRequest(length, packetLength)
            .ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }
}