using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkDaUploadStageTests
{
    [Fact]
    public async Task InitializeAsync_HandshakeEchoMismatch_RecordsHandshakeRejectedAndKeepsFaultedBehavior()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([0]);
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(MtkDaUploadStage.Handshake, session.UploadStage);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.Handshake, result.Reached);
        Assert.Equal(CommandOutcomeKind.Rejected, result.FinalOutcome);
        Assert.True(result.RetrySafe);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_AuthenticationRequiredWithoutResponder_RecordsAgentRequestRejected()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0x1D, 0x0D]);
        using var image = new MemoryStream(new byte[8]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((ushort)0x1D0D, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(MtkDaUploadStage.AgentRequest, session.UploadStage);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentRequest, result.Reached);
        Assert.Equal(CommandOutcomeKind.Rejected, result.FinalOutcome);
        Assert.True(result.RetrySafe);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_CancellationDuringDataTransfer_RecordsDataTransferringCancelledAsRetrySafe()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0x08, 0, 0, 0, 0, 0, 0, 0]);
        using var cancellation = new CancellationTokenSource();
        using var image = new SelfCancellingSource(new byte[2048], cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 2048, cancellationToken: cancellation.Token));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(MtkDaUploadStage.DataTransferring, session.UploadStage);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.DataTransferring, result.Reached);
        Assert.Equal(CommandOutcomeKind.Cancelled, result.FinalOutcome);
        Assert.True(result.RetrySafe);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_CompletionLostAfterFullTransfer_RecordsExecutionUnknownRequiringReconnect()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0]);
        transport.Enqueue([0x08]);
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(MtkDaUploadStage.DataTransferring, session.UploadStage);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.DataTransferring, result.Reached);
        Assert.Equal(CommandOutcomeKind.Unknown, result.FinalOutcome);
        Assert.False(result.RetrySafe);
    }

    [Fact]
    public async Task StartDownloadAgentAsync_TargetRejectsJump_RecordsAgentStartedRejectedButRetrySafe()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        await UploadAgentAsync(session, transport);
        transport.Enqueue([0xD5, 0, 0x20, 0, 0, 0x00, 0xC0]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.StartDownloadAgentAsync(TestContext.Current.CancellationToken));

        Assert.Equal((ushort)0x00C0, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentStarted, result.Reached);
        Assert.Equal(CommandOutcomeKind.Rejected, result.FinalOutcome);
        Assert.True(result.RetrySafe);
    }

    [Fact]
    public async Task StartDownloadAgentAsync_CancellationAfterJumpIssued_RecordsAgentStartedNotRetrySafe()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        await UploadAgentAsync(session, transport);
        transport.Enqueue([0xD5]);
        using var cancellation = new CancellationTokenSource();
        transport.OnWrite = written =>
        {
            if (written.SequenceEqual((byte[])[0, 0x20, 0, 0]))
            {
                cancellation.Cancel();
            }

            return [];
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.StartDownloadAgentAsync(cancellation.Token));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var result = session.LastUploadResult!;
        Assert.Equal(MtkDaUploadStage.AgentStarted, result.Reached);
        Assert.Equal(CommandOutcomeKind.Cancelled, result.FinalOutcome);
        Assert.False(result.RetrySafe);
    }

    [Fact]
    public async Task UploadAndStartAgents_AllAcknowledged_AdvancesStageWithoutFailureResult()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateInitializedSessionAsync(transport);
        await UploadAgentAsync(session, transport);
        transport.Enqueue([0xD5, 0, 0x20, 0, 0, 0, 0]);

        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.AgentStarted, session.State);
        Assert.Equal(MtkDaUploadStage.AgentStarted, session.UploadStage);
        Assert.Null(session.LastUploadResult);
    }

    [Fact]
    public async Task InitializeAsync_KnownIdentity_LeavesStageAtChipInfoRequestedWithoutFailure()
    {
        using var transport = new ScriptedCommTransport();

        using var session = await CreateInitializedSessionAsync(transport);

        Assert.Equal(MtkDaUploadStage.ChipInfoRequested, session.UploadStage);
        Assert.Null(session.LastUploadResult);
    }

    private static async Task<MtkSession> CreateInitializedSessionAsync(ScriptedCommTransport transport)
    {
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static async Task UploadAgentAsync(MtkSession session, ScriptedCommTransport transport)
    {
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0]);
        transport.Enqueue([8, 0, 0, 0]);
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        await session.UploadDownloadAgentAsync(image, 0x200000, 8, signatureLength: 2,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class SelfCancellingSource(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private int _reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (++_reads == 2)
            {
                cancellation.Cancel();
            }

            return count;
        }
    }
}