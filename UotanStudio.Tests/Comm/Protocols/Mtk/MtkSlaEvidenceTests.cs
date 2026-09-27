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
    public async Task UploadDownloadAgentWithSlaAsync_SlaRequiredWithoutProvider_FailsBeforeAnyChallengeResponseBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadDownloadAgentAsync(image, 0x200000, 8,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes.Skip(before), part => part.Length == 1 && part[0] == 0xE3);
        var evidence = SlaEvidence(session, "bootrom");
        Assert.Single(evidence);
        Assert.Equal("sla-bootrom-rejected", evidence[0].Phase);
        Assert.Equal(CommandOutcomeKind.Rejected, evidence[0].Outcome);
        Assert.Contains("SlaNoProvider", evidence[0].Detail, StringComparison.Ordinal);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentRequest, result.Reached);
        Assert.Equal("sla-bootrom-rejected", result.Evidence[0].Phase);
        Assert.Contains("SlaNoProvider", result.Evidence[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_AcceptedExchange_RecordsRequiredAndAcceptedAsSeparateDigestEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        transport.Enqueue(Convert.FromHexString("08000000"));
        var provider = new SlaResponder((_, _, _) => Task.FromResult<byte[]>([11, 12, 13, 14, 15, 16, 17, 18]));
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        await session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: TestContext.Current.CancellationToken);

        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal(2, evidence.Length);
        Assert.Equal("sla-bootrom-required", evidence[0].Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence[0].Outcome);
        Assert.Null(evidence[0].ResourceSha256Hex);
        Assert.Equal("sla-bootrom-accepted", evidence[1].Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence[1].Outcome);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(),
            evidence[1].ResourceSha256Hex);
        Assert.All(evidence, entry => Assert.Null(entry.RawResponseHex));
        Assert.All(evidence, entry => Assert.True(entry.ResourceSha256Hex is null || entry.ResourceSha256Hex.Length == 64));
        Assert.DoesNotContain(evidence, entry => (entry.Detail ?? string.Empty).Contains("01020304", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_WaivedExchange_RecordsChallengeWaivedWithoutResponseEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("E3701708000000"));
        var provider = new SlaResponder((_, _, _) => throw new InvalidOperationException("Must not sign."));
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        await session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: TestContext.Current.CancellationToken);

        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal(2, evidence.Length);
        Assert.Equal("sla-bootrom-required", evidence[0].Phase);
        Assert.Equal("sla-bootrom-waived", evidence[1].Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence[1].Outcome);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_InvalidLengthFrame_RecordsSlaRejectedWithChallengeDigest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport, "length");
        var provider = new SlaResponder((_, _, _) => Task.FromResult<byte[]>([1]));
        using var image = new MemoryStream(new byte[8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, cancellationToken: TestContext.Current.CancellationToken));

        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal("sla-bootrom-rejected", evidence[^1].Phase);
        Assert.Contains("SlaRejected", evidence[^1].Detail, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(),
            evidence[^1].ResourceSha256Hex);
        Assert.Equal(CommandOutcomeKind.Rejected, session.LastUploadResult!.FinalOutcome);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_ProviderThrows_RecordsSlaRejectedWithoutSendingResponse()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        var provider = new SlaResponder((_, _, _) => throw new InvalidOperationException("key unavailable"));
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(5, transport.Writes.Count - before);
        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal("sla-bootrom-rejected", evidence[^1].Phase);
        Assert.Contains("SlaRejected", evidence[^1].Detail, StringComparison.Ordinal);
        Assert.Contains("key unavailable", evidence[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_CancelledWhileProviderOwnsChallenge_RecordsProviderCancelledWithoutStaleResponse()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SlaResponder((_, _, _) => release.Task);
        using var image = new MemoryStream(new byte[8]);
        var pending = session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        var response = new byte[] { 9, 8, 7, 6 };
        release.SetResult(response);
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(before, transport.Writes.Count);
        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal("sla-bootrom-cancelled", evidence[^1].Phase);
        Assert.Contains("ProviderCancelled", evidence[^1].Detail, StringComparison.Ordinal);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentRequest, result.Reached);
        Assert.Equal("sla-bootrom-cancelled", result.Evidence[0].Phase);
        Assert.Equal(CommandOutcomeKind.Cancelled, result.Evidence[^1].Outcome);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_ProviderDeadline_RecordsSlaTimeoutInEvidenceAndResult()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        var provider = new SlaResponder(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        using var image = new MemoryStream(new byte[8]);

        await Assert.ThrowsAsync<TimeoutException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, slaTimeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken));

        var evidence = SlaEvidence(session, "bootrom");
        Assert.Equal("sla-bootrom-timeout", evidence[^1].Phase);
        Assert.Contains("SlaTimeout", evidence[^1].Detail, StringComparison.Ordinal);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentRequest, result.Reached);
        Assert.Equal("sla-bootrom-timeout", result.Evidence[0].Phase);
    }

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