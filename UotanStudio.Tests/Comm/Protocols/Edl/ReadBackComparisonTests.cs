using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class ReadBackComparisonTests
{
    [Fact]
    public async Task FragmentedSourceAndDeviceReads_CompareAcrossBufferBoundaries()
    {
        byte[] expected = new byte[180123];
        new Random(9).NextBytes(expected);
        using var source = new ShortReadStream(expected);
        source.Position = 123;
        using var comparison = new ReadBackComparisonStream(source, "test");
        await comparison.WriteAsync(expected.AsMemory(0, 70001), TestContext.Current.CancellationToken);
        await comparison.WriteAsync(expected.AsMemory(70001), TestContext.Current.CancellationToken);
        await comparison.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected.Length, source.Position);
        Assert.InRange(source.MaximumRequest, 1, 65536);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(131072, 131072, false)]
    [InlineData(131072, 131073, true)]
    [InlineData(131073, 131072, true)]
    public async Task FailuresAreDeferred_AndSizeMismatchTakesPrecedence(int sourceSize, int deviceSize, bool sizeMismatch)
    {
        using var source = new MemoryStream(new byte[sourceSize]);
        using var comparison = new ReadBackComparisonStream(source, "test");
        byte[] data = new byte[deviceSize];
        data[65537] = 42;
        await comparison.WriteAsync(data.AsMemory(), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<EdlException>(() => comparison.CompleteAsync(TestContext.Current.CancellationToken));
        Assert.Contains(sizeMismatch ? "size mismatch" : "at byte 65537", error.Message, StringComparison.Ordinal);
        Assert.Equal(sourceSize, source.Position);
    }

    [Fact]
    public async Task SourceFailure_IsDeferredUntilComplete()
    {
        using var source = new FailingSource();
        using var comparison = new ReadBackComparisonStream(source, "test");
        await comparison.WriteAsync(new byte[512].AsMemory(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => comparison.CompleteAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutConsumingData()
    {
        using var source = new MemoryStream(new byte[512]);
        using var comparison = new ReadBackComparisonStream(source, "test");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await comparison.WriteAsync(new byte[512].AsMemory(), cancellation.Token));
        Assert.Equal(0, comparison.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyProgram_ConsumesTrailingResponseBeforeReportingMismatch(bool mismatch)
    {
        const string ack = "<?xml version=\"1.0\"?><data><response value=\"ACK\" rawmode=\"false\"/></data>";
        using var transport = new ScriptedCommTransport();
        byte[] payload = new byte[1024];
        payload.AsSpan().Fill(0x41);
        transport.OnWrite = bytes => Encoding.UTF8.GetString(bytes).Contains("<read ", StringComparison.Ordinal)
            ? [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>"), payload, Encoding.UTF8.GetBytes(ack)]
            : [Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><data><response value=\"NAK\"/></data>")];
        var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, ResponseTimeoutSeconds = 1 }, timeoutMs: 100);
        byte[] original = (byte[])payload.Clone();
        if (mismatch) original[17] = 0;
        using var source = new MemoryStream(original);
        if (mismatch)
            await Assert.ThrowsAsync<EdlException>(() => protocol.VerifyProgramAsync(source, 0, 2, cancellationToken: TestContext.Current.CancellationToken));
        else
            await protocol.VerifyProgramAsync(source, 0, 2, cancellationToken: TestContext.Current.CancellationToken);
        var next = await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(next.IsAck);
    }

    private sealed class ShortReadStream(byte[] data) : MemoryStream(data)
    {
        internal int MaximumRequest { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 997)], cancellationToken);
        }
    }

    private sealed class FailingSource : MemoryStream
    {
        public override long Length => 512;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("source read failed"));
    }
}