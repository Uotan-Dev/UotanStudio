using System.Xml.Linq;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.XmlProfile;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task AttachXmlAgentAsync_TolerantUnknown_StartWithIgnorableField_AcksAndRecordsWarningEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        session.XmlFieldPolicy = MtkXmlFieldPolicy.TolerantUnknown;
        EnqueueXmlText(transport, XmlStartWithExtraField("status"));
        var before = transport.Writes.Count;

        await session.AttachXmlAgentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
        Assert.Equal(2, transport.Writes.Count - before);
        var warning = session.Evidence.Single(entry => entry.Phase == "xml-profile-warning");
        Assert.Equal(CommandOutcomeKind.Completed, warning.Outcome);
        Assert.Contains("status", warning.Detail, StringComparison.Ordinal);
        Assert.Contains("TolerantUnknown", warning.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachXmlAgentAsync_Strict_UnknownFieldRejectedBeforeAnyAcknowledgementByte()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        EnqueueXmlText(transport, XmlStartWithExtraField("status"));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => session.AttachXmlAgentAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task AttachXmlAgentAsync_TolerantUnknown_UnknownVersionStillRejectedBeforeAcknowledgement()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        session.XmlFieldPolicy = MtkXmlFieldPolicy.TolerantUnknown;
        EnqueueXmlText(transport, "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XElement("host",
            new XElement("version", "2.0"), new XElement("command", "CMD:START")).ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => session.AttachXmlAgentAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Erase")]
    public async Task XmlStorageOperation_AuthenticatedRegion_FaultsClosedBeforeAnyIo(string operation)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        Assert.NotNull(session.StorageCapability);
        Assert.True(session.StorageCapability!.FindRegion("EMMC-RPMB")!.AuthenticatedOnly);
        var before = transport.Writes.Count;
        using var buffer = new MemoryStream(new byte[512]);

        var error = operation switch
        {
            "Read" => await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXmlStorageAsync(
                "EMMC-RPMB", 0, 512, buffer, cancellationToken: TestContext.Current.CancellationToken)),
            "Write" => await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteXmlStorageAsync(
                "EMMC-RPMB", 0, 512, buffer, cancellationToken: TestContext.Current.CancellationToken)),
            _ => await Assert.ThrowsAsync<NotSupportedException>(() => session.EraseXmlStorageAsync(
                "EMMC-RPMB", 0, 512, cancellationToken: TestContext.Current.CancellationToken))
        };
        Assert.NotNull(error);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task RebootXmlAsync_Acknowledged_OfferRecordsIdentityLossGenerationAndBlocksReuse()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        session.LinkedTransportGeneration = new TransportGeneration(7);
        EnqueueXmlCompletion(transport);

        await session.RebootXmlAsync(new() { Mode = MtkXmlBootMode.Normal }, timeoutMs: 30000,
            cancellationToken: TestContext.Current.CancellationToken);

        var offer = session.LastRebootTransitionOffer!;
        Assert.Null(offer.Identity);
        Assert.Equal(7UL, offer.Generation!.Value.Value);
        Assert.Equal(30000, offer.ReconnectTimeoutMs);
        Assert.Equal(1UL, offer.StorageGenerationAtReboot);
        Assert.Contains(session.Evidence, entry => entry.Phase == "xml-reboot-transition-offer"
            && entry.Outcome == CommandOutcomeKind.Completed);
        Assert.Equal(MtkSessionState.XmlRebootAcknowledged, session.State);
        Assert.False(session.IsConnected);
        Assert.Null(session.StorageCapability);
        var before = transport.Writes.Count;
        using var buffer = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, buffer, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    private static string XmlStartWithExtraField(string extraName) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XElement("host", new XElement("version", "1.0"),
            new XElement("command", "CMD:START"), new XElement(extraName, "device-text"))
        .ToString(SaveOptions.DisableFormatting);
}