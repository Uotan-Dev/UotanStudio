using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BootstrapInitializeAsync_DaSlaEnabled_RequiresProviderBeforeStorage(bool supplyProvider)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, false, requireDaSla: true);
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(new byte[256]));
        var options = BootstrapOptions(directory) with { DaSlaResponder = supplyProvider ? responder : null };

        if (supplyProvider)
        {
            using var session = await MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);
            Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
            Assert.Equal(1, responder.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MtkXFlashBootstrap.InitializeAsync(
                transport, options, TestContext.Current.CancellationToken));
            Assert.Equal(1, transport.DisposeCount);
            Assert.Equal(0, responder.Calls);
        }
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_Disabled_DoesNotInvokeResponder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, new byte[4]);
        var responder = new DaSlaResponder((_, _, _, _) => throw new InvalidOperationException());

        Assert.False(await session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, responder.Calls);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_Enabled_SubmitsExactResponseAndClearsBuffers()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var info = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, info);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var response = new byte[] { 0x82, 0x71, 0xFF };
        ReadOnlyMemory<byte> borrowed = default;
        var responder = new DaSlaResponder((device, information, challenge, token) =>
        {
            Assert.Same(session.DeviceInfo, device);
            Assert.True(token.CanBeCanceled);
            Assert.Equal(info, information.ToArray());
            Assert.Equal(info.AsSpan(4, 16).ToArray(), challenge.ToArray());
            borrowed = information;
            return Task.FromResult(response);
        });

        Assert.True(await session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, responder.Calls);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0x82, 0x71, 0xFF }));
        Assert.All(response, value => Assert.Equal(0, value));
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("02000000")]
    [InlineData("0100000000")]
    public async Task AuthenticateXFlashStageTwoAsync_InvalidStatus_FaultsWithoutProvider(string status)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString(status));
        var responder = new DaSlaResponder((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, responder.Calls);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_MissingProvider_FaultsBeforeChallengeRequest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AuthenticateXFlashStageTwoAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(65537)]
    public async Task AuthenticateXFlashStageTwoAsync_InvalidChallenge_FaultsWithoutProvider(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, new byte[length]);
        var responder = new DaSlaResponder((_, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, responder.Calls);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task AuthenticateXFlashStageTwoAsync_InvalidResponse_FaultsAndClearsResponse(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, new byte[20]);
        var response = Enumerable.Repeat((byte)0xAA, length).ToArray();
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(response));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.All(response, value => Assert.Equal(0, value));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AuthenticateXFlashStageTwoAsync_DeviceRejects_FaultsAndClearsResponse(int rejectionStage)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, new byte[20]);
        for (var index = 0; index <= rejectionStage; index++)
        {
            EnqueueXFlashValue(transport, index == rejectionStage ? 0xC0020004u : 0u);
        }
        var response = new byte[] { 0xAA };
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(response));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AuthenticateXFlashStageTwoAsync(responder,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, responder.Calls);
        Assert.Equal(0, response[0]);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_CancelledProvider_IsAwaitedBeforeBuffersAreCleared()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, Enumerable.Repeat((byte)0xAA, 20).ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> borrowed = default;
        var response = new byte[] { 0xAA };
        var responder = new DaSlaResponder(async (_, information, _, _) =>
        {
            borrowed = information;
            entered.SetResult();
            await release.Task;
            return response;
        });
        var pending = session.AuthenticateXFlashStageTwoAsync(responder, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0xAA, value));
        var before = transport.Writes.Count;
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, transport.Writes.Count);
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0, response[0]);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task AuthenticateXFlashStageTwoAsync_ProviderDeadline_FaultsAndClearsChallenge()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, [1, 0, 0, 0]);
        EnqueueControlReply(transport, Enumerable.Repeat((byte)0xAA, 20).ToArray());
        ReadOnlyMemory<byte> borrowed = default;
        var responder = new DaSlaResponder(async (_, information, _, token) =>
        {
            borrowed = information;
            await Task.Delay(Timeout.Infinite, token);
            return new byte[1];
        });

        await Assert.ThrowsAsync<TimeoutException>(() => session.AuthenticateXFlashStageTwoAsync(
            responder, 1000, TestContext.Current.CancellationToken));
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private sealed class DaSlaResponder(
        Func<MtkDeviceInfo, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> callback)
        : IMtkDaSlaResponder
    {
        internal int Calls { get; private set; }

        public Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> deviceFirmwareInfo,
            ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken)
        {
            Calls++;
            return callback(device, deviceFirmwareInfo, challenge, cancellationToken);
        }
    }
}
