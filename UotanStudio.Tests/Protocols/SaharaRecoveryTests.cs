using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Replays the QUD serial recovery path: a target whose bootrom HELLO was already consumed only speaks again
/// after a Sahara RESET_STATE_MACHINE, and an unexpected packet read before that wake must be drained to its
/// full protocol length so the following HELLO read still starts on a packet boundary.
/// <para>回放 QUD 串口恢复路径：bootrom HELLO 已被消费的目标只有收到 Sahara RESET_STATE_MACHINE 才会重新开口，
/// 而唤起之前读到的意外数据包必须按完整协议长度排空，使随后的 HELLO 读取仍然从数据包边界开始。</para>
/// </summary>
public sealed class SaharaRecoveryTests
{
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint HelloResponseCommand = EdlConstants.SaharaCmdHelloResponse;
    private const uint ReadDataCommand = EdlConstants.SaharaCmdReadData;
    private const uint EndOfImageCommand = EdlConstants.SaharaCmdEndOfImage;
    private const uint DoneCommand = EdlConstants.SaharaCmdDone;
    private const uint DoneResponseCommand = EdlConstants.SaharaCmdDoneResponse;
    private const uint ResetStateCommand = EdlConstants.SaharaCmdResetState;
    private const uint FirehoseImageId = (uint)SaharaImageId.Firehose;
    private const uint ImageTxCompleteStatus = (uint)SaharaMode.ImageTxComplete;
    private const int HelloPacketBytes = 48;
    private const int ReadDataPacketBytes = 20;
    private const int EndOfImagePacketBytes = 16;
    private const int DoneResponsePacketBytes = 12;
    private const int ResetStatePacketBytes = 8;
    private const int UploadedRangeBytes = 8;
    private const uint DeviceHelloVersion = 2;
    private const uint DeviceHelloMinVersion = 1;
    private const uint DeviceHelloMaxCommandLength = 0x400;

    [Fact]
    public async Task Handshake_QudSerialHelloConsumed_WakesWithResetStateMachineAndReturnsImageTxPending()
    {
        var transport = new WakeGatedSaharaTransport();
        transport.EnqueueRead(
            Hello(SaharaMode.ImageTxPending),
            ReadData(0, UploadedRangeBytes),
            EndOfImage(FirehoseImageId, 0),
            DoneResponse(ImageTxCompleteStatus));
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport)
        {
            QudHelloRecoveryEnabled = true
        };
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.Equal((uint?)DeviceHelloVersion, sahara.NegotiatedProtocolVersion);
            Assert.False(sahara.FirehoseAlreadyRunning);
            var resets = ResetStateWrites(transport);
            Assert.Single(resets);
            Assert.Equal(ResetStatePacketBytes, resets[0].Length);
            var helloResponses = HelloResponseWrites(transport);
            Assert.Single(helloResponses);
            Assert.Equal(DeviceHelloVersion, BinaryPrimitives.ReadUInt32LittleEndian(helloResponses[0].AsSpan(8)));
            Assert.Equal(DoneCommand, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[^1]));
        }
        finally
        {
            sahara.Dispose();
        }
    }

    [Fact]
    public async Task Handshake_QudRecoveryReadsUnexpectedEndOfImage_DrainsWholePacketBeforeNextWake()
    {
        var transport = new WakeGatedSaharaTransport();
        transport.EnqueueRead(
            EndOfImage(FirehoseImageId, 0),
            Hello(SaharaMode.ImageTxPending),
            ReadData(0, UploadedRangeBytes),
            EndOfImage(FirehoseImageId, 0),
            DoneResponse(ImageTxCompleteStatus));
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport)
        {
            QudHelloRecoveryEnabled = true
        };
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.Equal((uint?)DeviceHelloVersion, sahara.NegotiatedProtocolVersion);
            var resets = ResetStateWrites(transport);
            Assert.Equal(2, resets.Count);
            Assert.All(resets, reset => Assert.Equal(ResetStatePacketBytes, reset.Length));
            Assert.Equal(DoneCommand, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[^1]));
        }
        finally
        {
            sahara.Dispose();
        }
    }

    private static byte[] Hello(SaharaMode mode)
    {
        var packet = new byte[HelloPacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, HelloCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), DeviceHelloVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), DeviceHelloMinVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), DeviceHelloMaxCommandLength);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), (uint)mode);
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

    private static byte[] EndOfImage(uint imageId, uint status)
    {
        var packet = new byte[EndOfImagePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, EndOfImageCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), imageId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), status);
        return packet;
    }

    private static byte[] DoneResponse(uint status)
    {
        var packet = new byte[DoneResponsePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, DoneResponseCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), status);
        return packet;
    }

    private static bool IsResetStateWrite(byte[] write)
        => write.Length == ResetStatePacketBytes
            && BinaryPrimitives.ReadUInt32LittleEndian(write) == ResetStateCommand;

    private static List<byte[]> ResetStateWrites(WakeGatedSaharaTransport transport)
        => [.. transport.Writes.Where(IsResetStateWrite)];

    private static List<byte[]> HelloResponseWrites(WakeGatedSaharaTransport transport)
        => [.. transport.Writes.Where(write => write.Length == HelloPacketBytes
            && BinaryPrimitives.ReadUInt32LittleEndian(write) == HelloResponseCommand)];

    /// <summary>
    /// Models the QUD serial link: every read times out until the host writes a Sahara
    /// RESET_STATE_MACHINE, after which the scripted packets are delivered in order.
    /// <para>模拟 QUD 串口链路：在主机写入 Sahara RESET_STATE_MACHINE 之前每次读取都超时，之后按顺序交付脚本化
    /// 数据包。</para>
    /// </summary>
    private sealed class WakeGatedSaharaTransport : ICommTransport
    {
        private readonly Queue<byte[]> _reads = new();
        private byte[]? _carry;
        private bool _armed;

        public List<byte[]> Writes { get; } = [];

        public bool IsConnected => true;

        public void Connect() { }

        public void Disconnect() { }

        public void Reset() { }

        public void EnqueueRead(params byte[][] chunks)
        {
            foreach (var chunk in chunks)
            {
                _reads.Enqueue(chunk);
            }
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (!_armed)
            {
                throw new TimeoutException();
            }

            if (_carry is { Length: > 0 } carry)
            {
                var carried = Math.Min(length, carry.Length);
                carry.AsSpan(0, carried).CopyTo(buffer.AsSpan(offset));
                _carry = carried < carry.Length ? carry[carried..] : null;
                return Task.FromResult(carried);
            }

            if (_reads.Count == 0)
            {
                throw new TimeoutException();
            }

            var chunk = _reads.Dequeue();
            var copy = Math.Min(length, chunk.Length);
            chunk.AsSpan(0, copy).CopyTo(buffer.AsSpan(offset));
            _carry = copy < chunk.Length ? chunk[copy..] : null;
            return Task.FromResult(copy);
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            var packet = data.AsSpan(offset, length).ToArray();
            Writes.Add(packet);
            if (IsResetStateWrite(packet))
            {
                _armed = true;
            }
            return Task.FromResult((long)length);
        }

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() { }
    }
}