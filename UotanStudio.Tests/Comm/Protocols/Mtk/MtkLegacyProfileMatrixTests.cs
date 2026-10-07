using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData("Emmc")]
    [InlineData("Nand")]
    [InlineData("Nor")]
    public async Task InitializeLegacyAgentAsync_SelectedProfile_RecordsFrozenProfileEvidence(string kind)
    {
        using var transport = new ScriptedCommTransport();
        using var session = kind switch
        {
            "Nand" => await CreateLegacyNandSessionAsync(transport),
            "Nor" => await CreateLegacyNorSessionAsync(transport),
            _ => await CreateLegacyStorageSessionAsync(transport)
        };

        var evidence = session.Evidence.Single(entry => entry.Phase == "legacy-da-profile");
        Assert.Equal(UotanStudio.Comm.Protocols.Common.Evidence.CommandOutcomeKind.Completed, evidence.Outcome);
        Assert.Contains($"kind={kind}", evidence.Detail, StringComparison.Ordinal);
        Assert.Contains("da1-response=C10000 (asserted C10000)", evidence.Detail, StringComparison.Ordinal);
        Assert.Contains("completion=Standard", evidence.Detail, StringComparison.Ordinal);
        Assert.Contains("agent-packet=4", evidence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeLegacyAgentAsync_StandardCompletion_PreservesExactStatusWordsAndSocOk()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);

        var completion = session.LegacyCompletion!;
        Assert.False(completion.WaitedForProcessing);
        Assert.Equal(0U, completion.DownloadStatus);
        Assert.Equal("5A0000000000000000C1", completion.ResponseHex);
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("soc")]
    public async Task InitializeLegacyAgentAsync_CompletionStatusOrSocFailure_DoesNotReportSuccess(string failure)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, failure);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, LegacyBootstrapOptions(dir), TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task EraseLegacyNandStorageAsync_StatisticsReportErrorBlocks_FaultsWithoutPublishingSuccess()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A0000000064"));
        transport.Enqueue([0x69]);
        transport.Enqueue(LegacyEraseStatistics(131072, 262144, 2, 0, 1));
        var options = LegacyNandEraseOptions() with { Wire = LegacySdkEraseOptions() with { StatisticsMarker = 0x69 } };

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNandStorageAsync(
            131072, 262144, options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("1 error blocks", error.Message, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
        Assert.Null(session.StorageCapability);
    }

    [Fact]
    public async Task EraseLegacyNandStorageAsync_BusyStatusThenDeviceFailure_FaultsWithoutContinuation()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000BD132"));
        transport.Enqueue(Convert.FromHexString("00000010" + "0000000000020000" + "FE"));
        var progress = new LegacyEraseProgressRecorder();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNandStorageAsync(
            0, 131072, LegacyNandEraseOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("no continuation was acknowledged", error.Message, StringComparison.Ordinal);
        Assert.Equal(0x10u, error.Status);
        Assert.Equal(new[] { 50 }, progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_ImmediateErrorStatus_IsNotReportedAsEraseCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000000C1"));
        transport.Enqueue(Convert.FromHexString("0000000000000400" + "02"));
        var progress = new LegacyEraseProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(
            0, 1024, 1024, LegacySdkEraseOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteLegacyNorStorageAsync_PacketRejectedByAgent_FaultsWithoutClaimingFullTransfer()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69, 0x00]);
        using var source = new MemoryStream(Enumerable.Repeat((byte)0xAA, 1024).ToArray());
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteLegacyNorStorageAsync(
            0, 1024, source, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 512 }, progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.True(transport.Writes.Count > before);
    }

    [Fact]
    public async Task ShutdownLegacyAsync_BothAcknowledgements_TerminatesSessionAndClearsSnapshot()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        Assert.NotNull(session.StorageCapability);
        transport.Enqueue([0x5A, 0x5A]);

        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
        Assert.False(session.IsConnected);
        Assert.Null(session.LegacyCompletion);
        Assert.Null(session.StorageCapability);
        Assert.False(transport.IsConnected);
    }

}