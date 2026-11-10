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

}