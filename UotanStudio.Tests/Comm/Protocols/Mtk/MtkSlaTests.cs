using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task UploadDownloadAgentWithSlaAsync_InvalidDeadline_RejectsBeforeIo(int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        using var image = new MemoryStream(new byte[8]);
        var provider = new SlaResponder((_, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, slaTimeoutMs: timeout, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_QueuedUpload_CannotInterleaveChallengeOrAgentBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        transport.Enqueue(Convert.FromHexString("08000000"));
        using var otherImage = new MemoryStream(new byte[8]);
        Task? queued = null;
        var provider = new SlaResponder((_, _, _) =>
        {
            queued = session.UploadDownloadAgentAsync(otherImage, 0x200000, 8,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return Task.FromResult(new byte[8]);
        });
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        var before = transport.Writes.Count;

        await session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(0, otherImage.Position);
        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_ChallengeAccepted_ContinuesOriginalRequestAndClearsBuffers()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        transport.Enqueue(Convert.FromHexString("08000000"));
        ReadOnlyMemory<byte> borrowed = default;
        byte[] response = [11, 12, 13, 14, 15, 16, 17, 18];
        var provider = new SlaResponder((device, challenge, _) =>
        {
            Assert.Equal(0x766, device.HardwareCode);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, challenge.ToArray());
            borrowed = challenge;
            return Task.FromResult(response);
        });
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        var before = transport.Writes.Count;

        await session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(new byte[] { 0xE3 }, transport.Writes[before + 4]);
        Assert.Equal(new byte[] { 8, 0, 0, 0 }, transport.Writes[before + 5]);
        Assert.Equal(new byte[] { 11, 12, 13, 14, 15, 16, 17, 18 }, transport.Writes[before + 6]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, transport.Writes[before + 7]);
        Assert.All(response, value => Assert.Equal(0, value));
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_NoChallengeStatus_DoesNotInvokeResponder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("E3701708000000"));
        var provider = new SlaResponder((_, _, _) => throw new InvalidOperationException("Must not sign."));
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        var before = transport.Writes.Count;

        await session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4097u)]
    [InlineData(uint.MaxValue)]
    public async Task UploadDownloadAgentWithSlaAsync_InvalidChallengeLength_RejectsBeforeProvider(uint length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("E30000"));
        var field = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(field, length);
        transport.Enqueue(field);
        var provider = new SlaResponder((_, _, _) => throw new InvalidOperationException("Must not sign."));
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, image.Position);
        Assert.Equal(5, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("length")]
    [InlineData("initial")]
    [InlineData("response-status")]
    [InlineData("final")]
    [InlineData("truncated")]
    public async Task UploadDownloadAgentWithSlaAsync_ExchangeRejected_FaultsWithoutSendingAgent(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        if (failure == "echo")
        {
            transport.Enqueue([0]);
        }
        else
        {
            EnqueueSlaExchange(transport, failure);
        }
        byte[] response = [11, 12, 13, 14, 15, 16, 17, 18];
        var provider = new SlaResponder((_, _, _) => Task.FromResult(response));
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, image.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Single(transport.Writes.Skip(before), part => part.AsSpan().SequenceEqual(new byte[] { 0xD7 }));
        if (failure is not ("echo" or "initial"))
        {
            Assert.All(response, value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task UploadDownloadAgentWithSlaAsync_InvalidProviderResponse_DoesNotSendIt(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        var response = length < 0 ? null : Enumerable.Repeat((byte)7, length).ToArray();
        var provider = new SlaResponder((_, _, _) => Task.FromResult(response!));
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadDownloadAgentWithSlaAsync(
            image, 0x200000, 8, provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(5, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        if (response is not null)
        {
            Assert.All(response, value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_CancelledWhileProviderOwnsChallenge_WaitsBeforeClearing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSlaSessionAsync(transport);
        EnqueueSlaExchange(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> borrowed = default;
        var provider = new SlaResponder((_, challenge, _) =>
        {
            borrowed = challenge;
            return release.Task;
        });
        using var image = new MemoryStream(new byte[8]);
        var pending = session.UploadDownloadAgentWithSlaAsync(image, 0x200000, 8, provider,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, borrowed.ToArray());
        var response = new byte[] { 9, 9 };
        release.SetResult(response);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.All(response, value => Assert.Equal(0, value));
        Assert.Equal(0, image.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentWithSlaAsync_ProviderDeadline_FaultsWithoutRetry()
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

        Assert.Equal(0, image.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_OptionalSlaResponder_InvokesOnlyWhenDaRequestsIt(bool required)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, false, requireSla: required);
        var calls = 0;
        var options = BootstrapOptions(directory) with
        {
            BootRomSlaResponder = new SlaResponder((_, _, _) =>
            {
                calls++;
                return Task.FromResult(new byte[8]);
            })
        };

        using var session = await MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);

        Assert.Equal(required ? 1 : 0, calls);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    private static async Task<MtkSession> CreateSlaSessionAsync(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D70020000000000008000000001D0D"));
        return session;
    }

    private static void EnqueueSlaExchange(ScriptedCommTransport transport, string failure = "")
    {
        transport.Enqueue(failure == "initial" ? [0xE3, 1, 0] : [0xE3, 0, 0]);
        transport.Enqueue(Convert.FromHexString("0000000401020304"));
        transport.Enqueue(failure == "length" ? [0, 0, 0, 7] : [0, 0, 0, 8]);
        transport.Enqueue(failure == "response-status" ? [1, 0] : [0, 0]);
        transport.Enqueue(failure == "final" ? [0, 0, 0, 0xFF]
            : failure == "truncated" ? [0] : new byte[4]);
    }

    private sealed class SlaResponder(
        Func<MtkDeviceInfo, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> respond) : IMtkBootRomSlaResponder
    {
        public Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> wireChallenge,
            CancellationToken cancellationToken) => respond(device, wireChallenge, cancellationToken);
    }
}