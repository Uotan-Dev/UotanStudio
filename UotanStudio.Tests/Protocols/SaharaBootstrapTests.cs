using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Covers the two properties of the Sahara bootstrap that decide whether a Linux usbfs link recovers: the first
/// packet is read into a buffer wide enough for the packet the target actually sends, and a link already known to
/// speak Sahara is never handed a Firehose document before it is woken.
/// <para>覆盖决定 Linux usbfs 链路能否恢复的两项 Sahara 引导性质：首个数据包被读入足以容纳目标实际发送的数据包的
/// 缓冲区，且已知使用 Sahara 的链路在被唤醒之前绝不会收到 Firehose 文档。</para>
/// </summary>
public sealed class SaharaBootstrapTests
{
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint ReadDataCommand = EdlConstants.SaharaCmdReadData;
    private const uint EndOfImageCommand = EdlConstants.SaharaCmdEndOfImage;
    private const uint DoneResponseCommand = EdlConstants.SaharaCmdDoneResponse;
    private const int HelloPacketBytes = 48;
    private const int ReadDataPacketBytes = 20;
    private const int EndOfImagePacketBytes = 16;
    private const int DoneResponsePacketBytes = 12;
    private const int UploadedRangeBytes = 8;

    [Fact]
    public async Task Handshake_FirstRead_AsksForAWholePacketPrefix()
    {
        var transport = new ScriptedSaharaTransport(silentUntilWoken: false);
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport);
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.True(transport.ReadRequestLengths[0] >= HelloPacketBytes,
                "the first read must be able to carry the whole HELLO so one USB round trip serves it");
            Assert.Equal(1, transport.ReadsBeforeFirstWrite(HelloPacketBytes));
        }
        finally
        {
            sahara.Dispose();
        }
    }

    [Fact]
    public async Task Handshake_LinkAlreadyKnownAsSahara_WakesWithoutTheFirehoseProbe()
    {
        var transport = new ScriptedSaharaTransport(silentUntilWoken: true);
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport) { LinkReportsSahara = true };
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.NotEmpty(transport.Writes);
            var firstWrite = transport.Writes[0];
            Assert.Equal(8, firstWrite.Length);
            Assert.Equal(EdlConstants.SaharaCmdResetState, BinaryPrimitives.ReadUInt32LittleEndian(firstWrite));
            Assert.DoesNotContain(transport.Writes, write => write.Length > 0 && write[0] == (byte)'<');
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
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)SaharaImageId.Firehose);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), length);
        return packet;
    }

    private static byte[] EndOfImage(uint status)
    {
        var packet = new byte[EndOfImagePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, EndOfImageCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)SaharaImageId.Firehose);
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
    /// Serves a scripted Sahara exchange one packet at a time and records every read request length, answering a
    /// zero-length read until the host writes RESET_STATE_MACHINE when the link starts silent.
    /// <para>逐包提供脚本化的 Sahara 交换并记录每次读取请求的长度；链路以静默开始时，在主机写入 RESET_STATE_MACHINE
    /// 之前以零长度读应答。</para>
    /// </summary>
    private sealed class ScriptedSaharaTransport : ICommTransport
    {
        private readonly Queue<byte[]> _packets = [];
        private readonly bool _silentUntilWoken;
        private byte[]? _carry;
        private bool _wakeSeen;

        internal ScriptedSaharaTransport(bool silentUntilWoken)
        {
            _silentUntilWoken = silentUntilWoken;
            _packets.Enqueue(Hello());
            _packets.Enqueue(ReadData(0, UploadedRangeBytes));
            _packets.Enqueue(EndOfImage(0));
            _packets.Enqueue(DoneResponse());
        }

        public List<int> ReadRequestLengths { get; } = [];

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
            ReadRequestLengths.Add(length);
            if (_carry is { Length: > 0 } carry)
            {
                return Task.FromResult(Serve(carry, buffer, offset, length));
            }

            if ((_silentUntilWoken && !_wakeSeen) || _packets.Count == 0)
            {
                return Task.FromResult(0);
            }

            return Task.FromResult(Serve(_packets.Dequeue(), buffer, offset, length));
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

        /// <summary>
        /// Counts the read requests issued before <paramref name="packetBytes"/> bytes were served by the first
        /// packet, so a header-sized read that splits the packet shows up as more than one request.
        /// <para>统计首个数据包送出 <paramref name="packetBytes"/> 字节之前发出的读取请求数，使把数据包拆开的
        /// 包头大小读取表现为多于一次请求。</para>
        /// </summary>
        /// <param name="packetBytes">Packet size the first request was expected to carry.
        /// <para>首个请求预期承载的数据包大小。</para></param>
        /// <returns>The number of requests that had to be issued. <para>不得不发出的请求数。</para></returns>
        public int ReadsBeforeFirstWrite(int packetBytes)
        {
            var consumed = 0;
            var requests = 0;
            foreach (var requested in ReadRequestLengths)
            {
                requests++;
                consumed += Math.Min(requested, packetBytes - consumed);
                if (consumed >= packetBytes)
                {
                    return requests;
                }
            }

            return requests;
        }

        private int Serve(byte[] packet, byte[] buffer, int offset, int length)
        {
            var take = Math.Min(length, packet.Length);
            packet.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
            _carry = take < packet.Length ? packet[take..] : null;
            return take;
        }
    }
}