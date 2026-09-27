using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class XiaomiAuthenticationTests
{
    private const string Ack = "<data><response value=\"ACK\"/></data>";
    private const string RawAck = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";

    [Theory]
    [InlineData("attribute")]
    [InlineData("element")]
    [InlineData("separate")]
    public async Task AuthenticateXiaomiAsync_ValidChallenge_CompletesAndClearsBorrowedBuffers(string form)
    {
        using var transport = new ScriptedCommTransport();
        QueueChallenge(transport, form);
        transport.Enqueue(RawAck);
        transport.Enqueue(Ack);
        using var protocol = new FirehoseProtocol(transport);
        var signature = Enumerable.Repeat((byte)0xA5, 256).ToArray();
        ReadOnlyMemory<byte> borrowed = default;
        var provider = new Provider((identity, challenge, token) =>
        {
            Assert.Null(identity);
            Assert.True(token.CanBeCanceled);
            Assert.Equal("challenge&42", Encoding.UTF8.GetString(challenge.Span));
            borrowed = challenge;
            return Task.FromResult(signature);
        });

        var result = await protocol.AuthenticateXiaomiAsync(provider, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAck);
        Assert.False(protocol.RequiresReconnect);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(3, transport.Writes.Count);
        var request = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[0])).Root!.Element("sig")!;
        Assert.Equal("req", request.Attribute("TargetName")!.Value);
        var submission = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[1])).Root!.Element("sig")!;
        Assert.Equal("sig", submission.Attribute("TargetName")!.Value);
        Assert.Equal("256", submission.Attribute("size_in_bytes")!.Value);
        Assert.Equal(Enumerable.Repeat((byte)0xA5, 256), transport.Writes[2]);
        Assert.All(signature, value => Assert.Equal(0, value));
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("<data><response value=\"NACK\" sig=\"challenge\"/></data>")]
    [InlineData("<data><response value=\"ACK\" rawmode=\"true\" sig=\"challenge\"/></data>")]
    [InlineData("<data><response value=\"ACK\"/></data>")]
    [InlineData("<data><response value=\"ACK\" sig=\" \"/></data>")]
    [InlineData("<data><sig value=\"a\"/><sig value=\"b\"/><response value=\"ACK\"/></data>")]
    [InlineData("<data><sig value=\"a\"/><response value=\"ACK\" sig=\"a\"/></data>")]
    [InlineData("<data><sig value=\"a\"><nested/></sig><response value=\"ACK\"/></data>")]
    public async Task AuthenticateXiaomiAsync_InvalidChallenge_FaultsBeforeProvider(string xml)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(xml);
        using var protocol = new FirehoseProtocol(transport);
        var provider = new Provider((_, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.AuthenticateXiaomiAsync(
            provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(0, provider.Calls);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(257)]
    public async Task AuthenticateXiaomiAsync_InvalidSignature_FaultsWithoutSendingIt(int length)
    {
        using var transport = new ScriptedCommTransport();
        QueueChallenge(transport, "attribute");
        using var protocol = new FirehoseProtocol(transport);
        var signature = Enumerable.Repeat((byte)0xAA, length).ToArray();
        var provider = new Provider((_, _, _) => Task.FromResult(signature));

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.AuthenticateXiaomiAsync(
            provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
        Assert.All(signature, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AuthenticateXiaomiAsync_DeviceRejects_FaultsWithoutCredentialReplay(bool preamble, bool rawFinal)
    {
        using var transport = new ScriptedCommTransport();
        QueueChallenge(transport, "attribute");
        transport.Enqueue(preamble ? Ack : RawAck);
        if (!preamble)
        {
            transport.Enqueue(rawFinal ? RawAck : "<data><response value=\"NAK\"/></data>");
        }
        using var protocol = new FirehoseProtocol(transport);
        var signature = new byte[256];
        var provider = new Provider((_, _, _) => Task.FromResult(signature));

        await Assert.ThrowsAnyAsync<EdlException>(() => protocol.AuthenticateXiaomiAsync(
            provider, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(preamble ? 2 : 3, transport.Writes.Count);
    }

    [Fact]
    public async Task AuthenticateXiaomiAsync_CancelledProvider_WaitsBeforeClearingAndDoesNotSendSignature()
    {
        using var transport = new ScriptedCommTransport();
        QueueChallenge(transport, "attribute");
        using var protocol = new FirehoseProtocol(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> borrowed = default;
        var signature = Enumerable.Repeat((byte)0xAA, 256).ToArray();
        var provider = new Provider(async (_, challenge, _) =>
        {
            borrowed = challenge;
            entered.SetResult();
            await release.Task;
            return signature;
        });
        var pending = protocol.AuthenticateXiaomiAsync(provider, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        Assert.Equal("challenge&42", Encoding.UTF8.GetString(borrowed.Span));
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(transport.Writes);
        Assert.All(signature, value => Assert.Equal(0, value));
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task AuthenticateXiaomiAsync_ConcurrentConfigure_DoesNotInterleave()
    {
        using var transport = new ScriptedCommTransport();
        QueueChallenge(transport, "attribute");
        transport.Enqueue(RawAck);
        transport.Enqueue(Ack);
        transport.Enqueue(Ack);
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { AutoDetectSectorSize = false });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new byte[256];
        });
        var pending = protocol.AuthenticateXiaomiAsync(provider, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var configure = protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(configure.IsCompleted);
        Assert.Single(transport.Writes);
        release.SetResult();

        Assert.True((await pending).IsAck);
        Assert.True((await configure).IsAck);
        Assert.Contains("<configure", Encoding.UTF8.GetString(transport.Writes[3]));
    }

    private static void QueueChallenge(ScriptedCommTransport transport, string form)
    {
        if (form == "attribute")
        {
            transport.Enqueue("<data><response value=\"ACK\" sig=\"challenge&amp;42\"/></data>");
        }
        else if (form == "element")
        {
            transport.Enqueue("<data><sig value=\"challenge&amp;42\"/><response value=\"ACK\"/></data>");
        }
        else
        {
            transport.Enqueue("<data><sig value=\"challenge&amp;42\"/></data>");
            transport.Enqueue(Ack);
        }
    }

    private sealed class Provider(
        Func<SaharaDeviceIdentity?, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> callback)
        : IEdlXiaomiAuthenticationProvider
    {
        internal int Calls { get; private set; }

        public Task<byte[]> CreateSignatureAsync(SaharaDeviceIdentity? identity, ReadOnlyMemory<byte> challenge,
            CancellationToken cancellationToken)
        {
            Calls++;
            return callback(identity, challenge, cancellationToken);
        }
    }
}