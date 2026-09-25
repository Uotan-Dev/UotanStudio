using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class EdlSessionConcurrencyTests
{
    private const string Ack = "<data><response value='ACK' rawmode='false'/></data>";

    [Fact]
    public async Task InitializeAsync_ConcurrentCallers_ConfigureOnceBeforeOtherCommands()
    {
        using var transport = new ScriptedCommTransport();
        // Sahara's first probe consumes a 48-byte packet and recognizes an existing programmer.
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        using var session = CreateSession(transport);
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnWrite = _ =>
        {
            if (transport.Writes.Count == 1)
            {
                configured.TrySetResult();
                return [];
            }

            return [Encoding.UTF8.GetBytes(Ack)];
        };

        var first = session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await configured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var sahara = session.Sahara;
        var second = session.EnsureInitializedAsync(cancellationToken: TestContext.Current.CancellationToken);
        var pending = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.False(pending.IsCompleted);
        Assert.Single(transport.Writes);

        transport.Enqueue(Ack);
        await Task.WhenAll(first, second, pending).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.True(session.IsConnected);
        Assert.Same(sahara, session.Sahara);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("<configure ", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Contains("<nop ", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_CancelledOwner_RejectsQueuedInitialization()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        using var session = CreateSession(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = session.InitializeAsync(cancellationToken: cancellation.Token);
        var second = session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<EdlException>(() => second);
        await Assert.ThrowsAsync<EdlException>(() =>
            session.EnsureInitializedAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(session.IsConfigured);
        Assert.False(session.IsConnected);
        Assert.True(session.Firehose!.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    private static EdlSession CreateSession(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        AutoDetectSectorSize = false,
        SectorSize = 512,
        ConfigureFirstAttemptTimeoutMs = 5000
    });
}