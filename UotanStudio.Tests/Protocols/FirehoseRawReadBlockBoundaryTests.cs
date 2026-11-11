using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Real-device regression for the Firehose raw payload read. The programmer acknowledges &lt;read&gt; with a
/// raw-mode response, streams the payload, and completes every negotiated block with a zero-length transfer before
/// the next one begins. Treating that zero-length completion as an expired deadline aborted a healthy 64 MiB boot
/// read exactly one payload size in, so these cases pin the accepted block boundary and the bounded silence that
/// must still fail.
/// <para>Firehose 原始负载读取的真机回归。programmer 以原始模式确认 &lt;read&gt;，随后流式发送负载，并在每个
/// 协商块结束后、下一块开始前以一次零长度传输收尾。把该零长度完成当成截止时间已过，会让一次正常的 64 MiB
/// boot 读取在恰好一个负载大小处中止，因此这些用例固定可接受的块边界，以及必须失败的有限静默。</para>
/// </summary>
public sealed class FirehoseRawReadBlockBoundaryTests
{
    private const int SectorSize = 4096;

    [Fact]
    public async Task ReadPartitionToStreamAsync_ZeroLengthAtEachBlockBoundary_ReadsWholeRange()
    {
        var transport = new BlockBoundaryTransport(blockBytes: 64 * 1024, blocks: 4, zeroLengthAtBlockEnd: true);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, transport.PayloadLength / SectorSize,
            "boot_a.bin", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.DeviceAcknowledged);
        Assert.Equal(transport.PayloadLength, result.BytesWritten);
        Assert.Equal(transport.Payload, destination.ToArray());
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_ZeroLengthBeforeEveryChunk_ReadsWholeRange()
    {
        var transport = new BlockBoundaryTransport(blockBytes: 4 * 1024, blocks: 3, zeroLengthAtBlockEnd: true,
            zeroLengthEveryChunk: true);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        var result = await protocol.ReadPartitionToStreamAsync(destination, 0, transport.PayloadLength / SectorSize,
            "boot_a.bin", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.DeviceAcknowledged);
        Assert.Equal(transport.Payload, destination.ToArray());
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_PermanentSilence_FailsInsteadOfHanging()
    {
        var transport = new BlockBoundaryTransport(blockBytes: 4 * 1024, blocks: 2, zeroLengthAtBlockEnd: true)
        {
            GoSilentAfterFirstBlock = true
        };
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        var failure = await Assert.ThrowsAsync<EdlException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, transport.PayloadLength / SectorSize, "boot_a.bin", 4,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("went silent", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates a protocol configured like the real negotiation this regression came from: a 4096-byte sector and an
    /// 8192-byte target-to-host payload, so the read chunk stays far below the delivered block and every block
    /// boundary is exercised.
    /// <para>按本次回归的真机协商结果创建协议实例：4096 字节扇区与 8192 字节目标到主机负载，使读取块远小于
    /// 投递块，从而覆盖每一个块边界。</para>
    /// </summary>
    /// <param name="transport">The transport the protocol exchanges on. <para>协议交换所用的传输。</para></param>
    /// <returns>The configured protocol. <para>已配置的协议实例。</para></returns>
    private static FirehoseProtocol NewProtocol(ICommTransport transport)
    {
        var protocol = new FirehoseProtocol(transport);
        protocol.Configuration.SectorSize = SectorSize;
        protocol.Configuration.MaxPayloadSizeFromTarget = 8192;
        return protocol;
    }

    /// <summary>
    /// Transport that plays a Firehose target: it answers the command write with a raw-mode ACK, serves the payload
    /// in fixed chunks, emits one zero-length completion per chunk when asked to, and answers the follow-up
    /// completion read with a normal-mode ACK.
    /// <para>扮演 Firehose 目标的传输：以原始模式 ACK 应答命令写入，按固定块提供负载，按需在每块后发出一次
    /// 零长度完成，并以普通模式 ACK 应答随后的完成读取。</para>
    /// </summary>
    private sealed class BlockBoundaryTransport : ICommTransport
    {
        private readonly int _blockBytes;
        private readonly bool _zeroLengthAtBlockEnd;
        private readonly bool _zeroLengthEveryChunk;
        private readonly byte[] _payload;
        private readonly Queue<byte[]> _responses = new();
        private int _delivered;
        private int _commandWrites;
        private bool _zeroPending;
        private bool _completionPending;

        internal BlockBoundaryTransport(int blockBytes, int blocks, bool zeroLengthAtBlockEnd,
            bool zeroLengthEveryChunk = false)
        {
            _blockBytes = blockBytes;
            _zeroLengthAtBlockEnd = zeroLengthAtBlockEnd;
            _zeroLengthEveryChunk = zeroLengthEveryChunk;
            _payload = new byte[blockBytes * blocks];
            for (var index = 0; index < _payload.Length; index++)
            {
                _payload[index] = (byte)(index * 31 + 7);
            }
        }

        internal bool GoSilentAfterFirstBlock { get; init; }

        internal int PayloadLength => _payload.Length;

        internal byte[] Payload => _payload;

        public bool IsConnected => true;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset()
        {
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count > 0)
            {
                var response = _responses.Dequeue();
                Buffer.BlockCopy(response, 0, buffer, offset, response.Length);
                return Task.FromResult(response.Length);
            }

            if (_completionPending)
            {
                _completionPending = false;
                var completion = Response(rawMode: false);
                Buffer.BlockCopy(completion, 0, buffer, offset, completion.Length);
                return Task.FromResult(completion.Length);
            }

            if (_zeroPending)
            {
                _zeroPending = false;
                return Task.FromResult(0);
            }

            if (GoSilentAfterFirstBlock && _delivered >= _blockBytes)
            {
                return Task.FromResult(0);
            }

            if (_delivered >= _payload.Length)
            {
                _completionPending = true;
                return Task.FromResult(0);
            }

            var count = Math.Min(length, _blockBytes - (_delivered % _blockBytes));
            Buffer.BlockCopy(_payload, _delivered, buffer, offset, count);
            _delivered += count;
            if (_zeroLengthEveryChunk)
            {
                _zeroPending = true;
            }
            else if (_zeroLengthAtBlockEnd && _delivered % _blockBytes == 0 && _delivered < _payload.Length)
            {
                _zeroPending = true;
            }

            return Task.FromResult(count);
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs) => length;

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            _commandWrites++;
            _responses.Enqueue(_commandWrites == 1 ? Response(rawMode: true) : Response(rawMode: false));
            return Task.FromResult((long)length);
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