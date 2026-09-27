using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkLegacyCompletionProfile.Standard, false, 0U)]
    [InlineData(MtkLegacyCompletionProfile.Standard, false, 0x12345678U)]
    [InlineData(MtkLegacyCompletionProfile.SdkPostProcessWait, false, 0xFFFFFFFFU)]
    [InlineData(MtkLegacyCompletionProfile.SdkPostProcessWait, true, 0U)]
    [InlineData(MtkLegacyCompletionProfile.SdkPostProcessWait, true, 0x12345678U)]
    public async Task LegacyBootstrapAsync_SelectedCompletion_PreservesFieldsWithoutConsumingNextCommand(
        MtkLegacyCompletionProfile profile, bool wait, uint bootStyle)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var response = new byte[wait ? 11 : 10];
        if (wait)
        {
            response[0] = 0xA5;
        }
        response[^10] = 0x5A;
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(response.Length - 5), bootStyle);
        response[^1] = 0xC1;
        EnqueueLegacyBootstrap(transport, false, completionResponse: []);
        foreach (var value in response)
        {
            transport.Enqueue([value]);
        }
        transport.Enqueue([0x5A, 0x5A]);
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { CompletionProfile = profile }
        };

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(new MtkLegacyCompletionInfo(wait, 0, bootStyle, Convert.ToHexString(response)), session.LegacyCompletion);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
        Assert.Null(session.LegacyCompletion);
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Theory]
    [InlineData("A55A0000000000000000C1", MtkLegacyCompletionProfile.Standard)]
    [InlineData("A5A50000000000000000C1", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("A5000000000000000000C1", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("695A0000000000000000C1", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("5A0000000000000000CF", MtkLegacyCompletionProfile.Standard)]
    [InlineData("A55A0000000000000000CF", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("", MtkLegacyCompletionProfile.Standard)]
    [InlineData("A5", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("A55A", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("A55A0000000000000000", MtkLegacyCompletionProfile.SdkPostProcessWait)]
    [InlineData("5A0000000000000000", MtkLegacyCompletionProfile.Standard)]
    public async Task LegacyBootstrapAsync_InvalidCompletion_FailsWithoutLayoutFallback(
        string responseHex, MtkLegacyCompletionProfile profile)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, completionResponse: Convert.FromHexString(responseHex));
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { CompletionProfile = profile }
        };

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, transport.Writes.Count(bytes => bytes.Length == 18));
        Assert.DoesNotContain(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0xD9 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBootstrapAsync_FailedDownloadStatus_PreservesOriginalWord(bool wait)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, completionResponse: Convert.FromHexString(
            wait ? "A55A1234567800000000C1" : "5A1234567800000000C1"));
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { CompletionProfile = MtkLegacyCompletionProfile.SdkPostProcessWait }
        };

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.Equal(0x12345678U, error.Status);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task LegacyBootstrapAsync_UnknownCompletionProfile_RejectsBeforeIo()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { CompletionProfile = (MtkLegacyCompletionProfile)2 }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task LegacySession_StorageFailure_ClearsCompletionEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        Assert.NotNull(session.LegacyCompletion);
        using var source = new MemoryStream(new byte[512]);
        transport.Enqueue([0xA5]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteLegacyStorageAsync(8, 0, 512, source,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyCompletion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyBootstrapAsync_ProcessingWait_UsesStartupDeadlineInsteadOfPacketTimeout(int delayedRead)
    {
        using var dir = new TempDirectory();
        using var scripted = new ScriptedCommTransport();
        using var transport = new LegacyCompletionWaitTransport(scripted, delayedRead);
        EnqueueLegacyBootstrap(scripted, false, completionResponse: Convert.FromHexString("A55A0000000000000000C1"));
        var options = LegacyBootstrapOptions(dir) with
        {
            TransferTimeoutMs = 20,
            Legacy = LegacyProfile() with { CompletionProfile = MtkLegacyCompletionProfile.SdkPostProcessWait, StartupTimeoutMs = 200 }
        };

        await Assert.ThrowsAsync<TimeoutException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.True(transport.Entered.Task.IsCompletedSuccessfully);
        Assert.InRange(transport.WaitTimeoutMs, 21, 200);
        Assert.Equal(1, scripted.DisposeCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyBootstrapAsync_CancelledProcessingWait_DisposesWithoutRetry(int delayedRead)
    {
        using var dir = new TempDirectory();
        using var scripted = new ScriptedCommTransport();
        using var transport = new LegacyCompletionWaitTransport(scripted, delayedRead);
        EnqueueLegacyBootstrap(scripted, false, completionResponse: Convert.FromHexString("A55A0000000000000000C1"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { CompletionProfile = MtkLegacyCompletionProfile.SdkPostProcessWait }
        };
        var startup = MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options, cancellation.Token);
        try
        {
            await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);

        Assert.Equal(1, scripted.DisposeCount);
        Assert.Equal(1, scripted.Writes.Count(bytes => bytes.Length == 18));
    }

    private sealed class LegacyCompletionWaitTransport(ScriptedCommTransport inner, int delayedRead) : ICommTransport
    {
        private bool _configurationRead;
        private int _completionReads;
        internal int WaitTimeoutMs { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => inner.IsConnected;
        public void Connect() => inner.Connect();
        public void Disconnect() => inner.Disconnect();
        public void Reset() => inner.Reset();
        public Stream GetStream() => inner.GetStream();
        public void Dispose() => inner.Dispose();
        public long Write(byte[] data, int offset, int length, int timeoutMs) => inner.Write(data, offset, length, timeoutMs);
        public int Read(byte[] data, int offset, int length, int timeoutMs) => inner.Read(data, offset, length, timeoutMs);
        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(data, offset, length, timeoutMs, cancellationToken);

        public async Task<int> ReadAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (_configurationRead && ++_completionReads == delayedRead)
            {
                WaitTimeoutMs = timeoutMs;
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var count = await inner.ReadAsync(data, offset, length, timeoutMs, cancellationToken);
            if (length == 38)
            {
                _configurationRead = true;
            }
            return count;
        }
    }
}