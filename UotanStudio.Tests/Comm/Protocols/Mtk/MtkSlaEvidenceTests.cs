using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_MissingProvider_RecordsSlaNoProviderAndFreezesResult()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthenticateXFlashStageTwoAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 4, transport.Writes.Count);
        var evidence = SlaEvidence(session, "da-xflash");
        Assert.Single(evidence);
        Assert.Equal("sla-da-xflash-rejected", evidence[0].Phase);
        Assert.Contains("SlaNoProvider", evidence[0].Detail, StringComparison.Ordinal);
        Assert.Equal("sla-da-xflash-rejected", session.LastUploadResult!.Evidence[0].Phase);
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_AcceptedExchange_RecordsRequiredAndAcceptedWithChallengeDigest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var info = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var expectedDigest = Convert.ToHexString(SHA256.HashData(info[4..20])).ToLowerInvariant();
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, info);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult<byte[]>([0x82, 0x71, 0xFF]));

        Assert.True(await session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));

        var evidence = SlaEvidence(session, "da-xflash");
        Assert.Equal(2, evidence.Length);
        Assert.Equal("sla-da-xflash-required", evidence[0].Phase);
        Assert.Equal("sla-da-xflash-accepted", evidence[1].Phase);
        Assert.Equal(expectedDigest, evidence[1].ResourceSha256Hex);
        Assert.DoesNotContain(evidence, entry => (entry.Detail ?? string.Empty).Contains(Convert.ToHexString(info),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_ProviderDeadline_RecordsSlaTimeoutAndFreezesResult()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, Enumerable.Repeat((byte)0xAA, 20).ToArray());
        var responder = new DaSlaResponder(async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new byte[1];
        });

        await Assert.ThrowsAsync<TimeoutException>(() => session.AuthenticateXFlashStageTwoAsync(
            responder, 1000, TestContext.Current.CancellationToken));

        var evidence = SlaEvidence(session, "da-xflash");
        Assert.Equal("sla-da-xflash-timeout", evidence[^1].Phase);
        Assert.Contains("SlaTimeout", evidence[^1].Detail, StringComparison.Ordinal);
        Assert.Equal("sla-da-xflash-timeout", session.LastUploadResult!.Evidence[0].Phase);
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_MissingProvider_RecordsRequiredThenNoProviderRejection()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthenticateXmlStageTwoAsync(
            "test_properties", cancellationToken: TestContext.Current.CancellationToken));

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before).ToArray());
        Assert.DoesNotContain(frames, frame => Encoding.UTF8.GetString(frame)
            .Contains("SECURITY-GET-DEV-FW-INFO", StringComparison.Ordinal));
        var evidence = SlaEvidence(session, "da-xml");
        Assert.Equal(2, evidence.Length);
        Assert.Equal("sla-da-xml-required", evidence[0].Phase);
        Assert.Equal("sla-da-xml-rejected", evidence[1].Phase);
        Assert.Contains("SlaNoProvider", evidence[1].Detail, StringComparison.Ordinal);
        Assert.Equal("sla-da-xml-rejected", session.LastUploadResult!.Evidence[0].Phase);
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_AcceptedExchange_RecordsAcceptedWithChallengeDigest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "ENABLED"));
        EnqueueXmlStorage(transport, XmlSlaDocument());
        EnqueueXmlSecurityResource(transport, 32, 16);
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(Enumerable.Repeat((byte)0xA5, 32).ToArray()));

        Assert.True(await session.AuthenticateXmlStageTwoAsync("test_properties", responder,
            cancellationToken: TestContext.Current.CancellationToken));

        var challenge = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var evidence = SlaEvidence(session, "da-xml");
        Assert.Equal(2, evidence.Length);
        Assert.Equal("sla-da-xml-required", evidence[0].Phase);
        Assert.Equal("sla-da-xml-accepted", evidence[1].Phase);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(challenge)).ToLowerInvariant(),
            evidence[1].ResourceSha256Hex);
        Assert.DoesNotContain(evidence, entry => entry.RawResponseHex is not null);
    }

    [Fact]
    public async Task AuthenticateXmlStageTwoAsync_UnknownIndication_RecordsSlaRejectedEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", "UNKNOWN"));
        var responder = new DaSlaResponder((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXmlStageTwoAsync(
            "test_properties", responder, cancellationToken: TestContext.Current.CancellationToken));

        var evidence = SlaEvidence(session, "da-xml");
        Assert.Single(evidence);
        Assert.Equal("sla-da-xml-rejected", evidence[0].Phase);
        Assert.Contains("SlaRejected", evidence[0].Detail, StringComparison.Ordinal);
        Assert.Equal(0, responder.Calls);
    }

    private static ProtocolEvidence[] SlaEvidence(MtkSession session, string flow) =>
        [.. session.Evidence.Where(entry => entry.Phase.StartsWith("sla-" + flow + "-", StringComparison.Ordinal))];
}