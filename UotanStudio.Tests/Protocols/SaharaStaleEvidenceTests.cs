using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Replays the stale-evidence and zero-length-read paths: buffered probe evidence the target stopped waiting for must
/// not be replayed, and a read that returns nothing must spend its budget instead of reporting a timeout.
/// <para>回放过期证据与零长度读路径：目标不再等待的缓冲探测证据不得被回放，返回空内容的读取必须消耗其预算而不是
/// 报告超时。</para>
/// </summary>
public sealed class SaharaStaleEvidenceTests
{
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint ReadDataCommand = EdlConstants.SaharaCmdReadData;
    private const uint EndOfImageCommand = EdlConstants.SaharaCmdEndOfImage;
    private const uint DoneResponseCommand = EdlConstants.SaharaCmdDoneResponse;
    private const uint FirehoseImageId = (uint)SaharaImageId.Firehose;
    private const int HelloPacketBytes = 48;
    private const int ReadDataPacketBytes = 20;
    private const int EndOfImagePacketBytes = 16;
    private const int DoneResponsePacketBytes = 12;
    private const int UploadedRangeBytes = 8;

    [Fact]
    public async Task Probe_BufferedHelloOlderThanTheWindow_IsDroppedInsteadOfReplayed()
    {
        var inner = new HelloOnceTransport();
        var probe = new EdlProbeTransport(inner);
        var state = await probe.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EdlProtocolState.Sahara, state);
        Assert.True(probe.TryReadSaharaHello(out _));

        var readsAfterProbe = inner.BufferedReads;
        probe.BufferedEvidenceMaxAgeMs = 1;
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(probe.TryReadSaharaHello(out _));
        var buffer = new byte[HelloPacketBytes];
        await Assert.ThrowsAsync<TimeoutException>(() => probe.ReadAsync(
            buffer, 0, buffer.Length, 500, TestContext.Current.CancellationToken));
        Assert.True(inner.BufferedReads > readsAfterProbe,
            "stale evidence must be dropped so the read falls through to the target instead of replaying it");
    }

    [Fact]
    public async Task ReadExact_TransportAnswersZeroLengthReads_SpendsTheBudgetInsteadOfReportingTimeout()
    {
        var transport = new ZeroLengthThenHelloTransport();
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport);
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.True(transport.ZeroLengthReads >= 2,
                "a zero-length read must be retried instead of being surfaced as a read timeout");
        }
        finally
        {
            sahara.Dispose();
        }
    }

    [Fact]
    public async Task Handshake_SilentLinkAfterTheFirstRead_WakesTheTargetInsteadOfReportingAProtocolError()
    {
        var transport = new SilentUntilWokenTransport();
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport);
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.True(transport.ZeroLengthReads >= 1);
            Assert.Contains(transport.Writes, write => write.Length == 8
                && BinaryPrimitives.ReadUInt32LittleEndian(write) == EdlConstants.SaharaCmdResetState);
        }
        finally
        {
            sahara.Dispose();
        }
    }

    private static byte[] Hello()
    {
        var packet = new byte[HelloPacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, HelloCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), (uint)SaharaMode.ImageTxPending);
        return packet;
    }

    private static byte[] ReadData(uint offset, uint length)
    {
        var packet = new byte[ReadDataPacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ReadDataCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), FirehoseImageId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), length);
        return packet;
    }

    private static byte[] EndOfImage(uint status)
    {
        var packet = new byte[EndOfImagePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, EndOfImageCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), FirehoseImageId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), status);
        return packet;
    }

    private static byte[] DoneResponse()
    {
        var packet = new byte[DoneResponsePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, DoneResponseCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)SaharaMode.ImageTxComplete);
        return packet;
    }

    /// <summary>
    /// Delivers one HELLO and then stays silent, so buffered evidence is the only thing the probe ever holds.
    /// <para>只交付一次 HELLO 随后保持静默，使探测只可能持有缓冲证据。</para>
    /// </summary>
    private sealed class HelloOnceTransport : ICommTransport
    {
        private byte[]? _carry;
        private bool _delivered;

        public int BufferedReads { get; private set; }

        public bool IsConnected => true;

        public void Connect() { }
        public void Disconnect() { }
        public void Reset() { }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            BufferedReads++;
            if (_carry is { Length: > 0 } carry)
            {
                var carried = Math.Min(length, carry.Length);
                carry.AsSpan(0, carried).CopyTo(buffer.AsSpan(offset));
                _carry = carried < carry.Length ? carry[carried..] : null;
                return Task.FromResult(carried);
            }

            if (!_delivered)
            {
                _delivered = true;
                var hello = Hello();
                var take = Math.Min(length, hello.Length);
                hello.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
                _carry = take < hello.Length ? hello[take..] : null;
                return Task.FromResult(take);
            }

            throw new TimeoutException();
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult((long)length);

        public Stream GetStream() => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Stays silent with zero-length answers until the host writes RESET_STATE_MACHINE, which is what a bootrom whose
    /// one HELLO an earlier probe consumed looks like when the buffered evidence has aged out.
    /// <para>在主机写入 RESET_STATE_MACHINE 之前以零长度应答保持静默，这正是唯一一次 HELLO 已被更早的探测消费、
    /// 且缓冲证据已过期时 bootrom 的样子。</para>
    /// </summary>
    private sealed class SilentUntilWokenTransport : ICommTransport
    {
        private readonly Queue<byte[]> _packets = new();
        private byte[]? _carry;
        private bool _wakeSeen;

        internal SilentUntilWokenTransport()
        {
            _packets.Enqueue(Hello());
            _packets.Enqueue(ReadData(0, UploadedRangeBytes));
            _packets.Enqueue(EndOfImage(0));
            _packets.Enqueue(DoneResponse());
        }

        public int ZeroLengthReads { get; private set; }

        public List<byte[]> Writes { get; } = [];

        public bool IsConnected => true;

        public void Connect() { }
        public void Disconnect() { }
        public void Reset() { }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (_carry is { Length: > 0 } carry)
            {
                return Task.FromResult(Serve(carry, buffer, offset, length));
            }

            if (!_wakeSeen || _packets.Count == 0)
            {
                ZeroLengthReads++;
                return Task.FromResult(0);
            }

            return Task.FromResult(Serve(_packets.Dequeue(), buffer, offset, length));
        }

        private int Serve(byte[] packet, byte[] buffer, int offset, int length)
        {
            var take = Math.Min(length, packet.Length);
            packet.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
            _carry = take < packet.Length ? packet[take..] : null;
            return take;
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            var copy = data.AsSpan(offset, length).ToArray();
            Writes.Add(copy);
            if (copy.Length == 8
                && BinaryPrimitives.ReadUInt32LittleEndian(copy) == EdlConstants.SaharaCmdResetState)
            {
                _wakeSeen = true;
            }

            return Task.FromResult((long)length);
        }

        public Stream GetStream() => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Answers the first reads with zero bytes, which is what a cleared stall looks like on usbfs, and only then
    /// serves the scripted Sahara exchange.
    /// <para>最初几次读取以零字节应答（这正是 usbfs 上已清除 STALL 的样子），之后才提供脚本化的 Sahara 交换。</para>
    /// </summary>
    private sealed class ZeroLengthThenHelloTransport : ICommTransport
    {
        private readonly Queue<byte[]> _packets = new();
        private byte[]? _carry;

        internal ZeroLengthThenHelloTransport()
        {
            _packets.Enqueue(Hello());
            _packets.Enqueue(ReadData(0, UploadedRangeBytes));
            _packets.Enqueue(EndOfImage(0));
            _packets.Enqueue(DoneResponse());
        }

        public int ZeroLengthReads { get; private set; }

        public bool IsConnected => true;

        public void Connect() { }
        public void Disconnect() { }
        public void Reset() { }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (_carry is { Length: > 0 } carry)
            {
                return Task.FromResult(Serve(carry, buffer, offset, length));
            }

            if (_packets.Count == 0)
            {
                throw new TimeoutException();
            }

            var packet = _packets.Peek();
            var isReadData = packet.Length == ReadDataPacketBytes
                && BinaryPrimitives.ReadUInt32LittleEndian(packet) == ReadDataCommand;
            if (isReadData && ZeroLengthReads < 2)
            {
                ZeroLengthReads++;
                return Task.FromResult(0);
            }

            _packets.Dequeue();
            return Task.FromResult(Serve(packet, buffer, offset, length));
        }

        private int Serve(byte[] packet, byte[] buffer, int offset, int length)
        {
            var take = Math.Min(length, packet.Length);
            packet.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
            _carry = take < packet.Length ? packet[take..] : null;
            return take;
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult((long)length);

        public Stream GetStream() => throw new NotSupportedException();
        public void Dispose() { }
    }
}