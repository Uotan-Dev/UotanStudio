using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// A cancelled Firehose raw transfer cannot stop at an arbitrary byte: the programmer still owes the declared
/// payload and a final XML acknowledgement. These cases pin the quiesce that keeps the endpoint clean and the
/// target in command mode, plus the bounded fallback that resets the endpoint when the target never completes.
/// <para>被取消的 Firehose 原始传输不能停在任意字节上：programmer 仍要发完声明的负载并给出最后的 XML 确认。
/// 这些用例固定住让端点保持干净、目标保持命令模式的收尾，以及目标始终不完成时的有界降级复位。</para>
/// </summary>
public sealed class FirehoseCancelledRawTransferTests
{
    private const int SectorSize = 4096;
    private const int ChunkBytes = 4096;
    private const int Chunks = 8;
    private const int PayloadBytes = ChunkBytes * Chunks;

    [Fact]
    public async Task ReadPartitionToStreamAsync_CancelledMidPayload_DrainsRemainderAndConsumesCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesRead);
        Assert.Equal(2 * ChunkBytes, destination.Length);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_CancelledMidPayload_LeavesLinkReusableForNextCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));
        var response = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.False(response.IsRawMode);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_CompletionNeverArrives_ResetsEndpointAndRequiresReconnect()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2) { FailCompletion = true };
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesRead);
        Assert.Equal(1, transport.ResetCalls);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramAsync_CancelledMidPayload_ZeroFillsRemainderAndConsumesCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2, program: true);
        using var protocol = NewProtocol(transport);
        using var source = new MemoryStream(new byte[PayloadBytes]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ProgramAsync(
            source, "0", Chunks, "boot_a.bin", 4, cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesWritten);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
    }

    private static FirehoseProtocol NewProtocol(ICommTransport transport)
    {
        var protocol = new FirehoseProtocol(transport);
        protocol.Configuration.SectorSize = SectorSize;
        protocol.Configuration.MaxPayloadSizeFromTarget = ChunkBytes;
        protocol.Configuration.MaxPayloadSizeToTarget = ChunkBytes;
        protocol.Configuration.MaxPayloadSizeToTargetSupported = ChunkBytes;
        protocol.Configuration.MaxProgramChunkBytes = ChunkBytes;
        return protocol;
    }

    private sealed class CancellableTransfer(CancellationTokenSource cancellation, int cancelAfterChunks, bool program = false)
        : ICommTransport
    {
        private readonly byte[] _payload = new byte[PayloadBytes];
        private readonly Queue<byte[]> _responses = new();
        private int _deliveredRaw;
        private int _acceptedRaw;
        private int _commandWrites;

        internal bool FailCompletion { get; init; }

        internal int RawBytesRead => _deliveredRaw;

        internal int RawBytesWritten => _acceptedRaw;

        internal int CompletionReads { get; private set; }

        internal int ResetCalls { get; private set; }

        public bool IsConnected => true;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => ResetCalls++;

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count > 0)
            {
                return Task.FromResult(Copy(_responses.Dequeue(), buffer, offset));
            }

            if (_deliveredRaw >= PayloadBytes)
            {
                if (FailCompletion)
                {
                    throw new IOException("The target never emitted the transfer completion.");
                }

                CompletionReads++;
                return Task.FromResult(Copy(Response(rawMode: false), buffer, offset));
            }

            var count = Math.Min(length, PayloadBytes - _deliveredRaw);
            Buffer.BlockCopy(_payload, _deliveredRaw, buffer, offset, count);
            _deliveredRaw += count;
            if (!cancellation.IsCancellationRequested && _deliveredRaw >= cancelAfterChunks * ChunkBytes)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(count);
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs) => length;

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length > 0 && data[offset] == (byte)'<')
            {
                _commandWrites++;
                _responses.Enqueue(Response(rawMode: _commandWrites == 1));
                return Task.FromResult((long)length);
            }

            _acceptedRaw += length;
            if (!program)
            {
                return Task.FromResult((long)length);
            }

            if (!cancellation.IsCancellationRequested && _acceptedRaw >= cancelAfterChunks * ChunkBytes)
            {
                cancellation.Cancel();
            }

            return Task.FromResult((long)length);
        }

        private static int Copy(byte[] source, byte[] buffer, int offset)
        {
            Buffer.BlockCopy(source, 0, buffer, offset, source.Length);
            return source.Length;
        }

        private static byte[] Response(bool rawMode)
            => Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><response value=\"ACK\" rawmode=\"{(rawMode ? "true" : "false")}\" /></data>");

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}