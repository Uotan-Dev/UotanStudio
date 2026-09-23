using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class MtkXFlashChannelTests
{
    [Fact]
    public async Task SendStreamAsync_TruncatedNonSeekableSource_FaultsWithoutPadding()
    {
        using var transport = new ScriptedCommTransport();
        var channel = new MtkXFlashChannel(transport);
        using var source = new NonSeekableSource(new byte[600]);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            channel.SendStreamAsync(1, source, 1024, null, TestContext.Current.CancellationToken));

        Assert.True(channel.IsFaulted);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(512, transport.Writes[1].Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendStreamAsync_SourceCompletesAfterCancellationOrDisconnect_DoesNotWritePayload(bool disconnect)
    {
        using var transport = new ScriptedCommTransport();
        using var cancellation = new CancellationTokenSource();
        var channel = new MtkXFlashChannel(transport);
        using var source = new InterruptingSource(new byte[512], () =>
        {
            if (disconnect)
            {
                transport.Disconnect();
            }
            else
            {
                cancellation.Cancel();
            }
        });

        if (disconnect)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.SendStreamAsync(1, source, 512, null, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                channel.SendStreamAsync(1, source, 512, null, cancellation.Token));
        }

        Assert.True(channel.IsFaulted);
        Assert.Single(transport.Writes);
    }

    private sealed class NonSeekableSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class InterruptingSource(byte[] bytes, Action interrupt) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            interrupt();
            return count;
        }
    }

    [Fact]
    public async Task SendAsync_KnownPayload_UsesLittleEndianHeaderAndExactBytes()
    {
        using var transport = new ScriptedCommTransport();
        var channel = new MtkXFlashChannel(transport);

        await channel.SendAsync(1, [0x12, 0x34], TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("EFEEEEFE0100000002000000"), transport.Writes[0]);
        Assert.Equal(new byte[] { 0x12, 0x34 }, transport.Writes[1]);
        Assert.False(channel.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAsync_FragmentedOrCoalescedFrames_PreservesFrameBoundaries(bool fragmented)
    {
        using var transport = new ScriptedCommTransport();
        var bytes = Convert.FromHexString("EFEEEEFE01000000020000001234EFEEEEFE0200000000000000");
        if (fragmented)
        {
            foreach (var value in bytes)
            {
                transport.Enqueue([value]);
            }
        }
        else
        {
            transport.Enqueue(bytes);
        }
        var channel = new MtkXFlashChannel(transport);

        var first = await channel.ReceiveAsync(TestContext.Current.CancellationToken);
        var second = await channel.ReceiveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1u, first.DataType);
        Assert.Equal(new byte[] { 0x12, 0x34 }, first.Payload);
        Assert.Equal(2u, second.DataType);
        Assert.Empty(second.Payload);
    }

    [Theory]
    [InlineData("000000000100000000000000")]
    [InlineData("EFEEEEFE01000000FFFFFFFF")]
    [InlineData("EFEEEEFE010000000200000012")]
    [InlineData("EFEEEEFE")]
    public async Task ReceiveAsync_InvalidFrame_FaultsAndRejectsReuse(string hex)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Convert.FromHexString(hex));
        var channel = new MtkXFlashChannel(transport);

        await Assert.ThrowsAsync<MtkProtocolException>(() => channel.ReceiveAsync(TestContext.Current.CancellationToken));
        var reads = transport.ReadCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReceiveAsync(TestContext.Current.CancellationToken));

        Assert.True(channel.IsFaulted);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task SendAsync_CancellationAfterHeader_FaultsWithoutSendingPayload()
    {
        using var transport = new ScriptedCommTransport();
        using var cancellation = new CancellationTokenSource();
        transport.OnWrite = _ =>
        {
            cancellation.Cancel();
            return [];
        };
        var channel = new MtkXFlashChannel(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync(1, [1], cancellation.Token));

        Assert.Single(transport.Writes);
        Assert.True(channel.IsFaulted);
    }

    [Fact]
    public async Task SendAsync_OversizedPayload_RejectsBeforeIoWithoutFaulting()
    {
        using var transport = new ScriptedCommTransport();
        var channel = new MtkXFlashChannel(transport, maximumPayload: 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            channel.SendAsync(1, [1, 2], TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(channel.IsFaulted);
    }
}