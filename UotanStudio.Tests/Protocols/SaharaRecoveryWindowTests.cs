using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class SaharaRecoveryWindowTests
{
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint ReadDataCommand = EdlConstants.SaharaCmdReadData;
    private const uint EndOfImageCommand = EdlConstants.SaharaCmdEndOfImage;
    private const uint DoneResponseCommand = EdlConstants.SaharaCmdDoneResponse;
    private const uint ResetStateCommand = EdlConstants.SaharaCmdResetState;
    private const uint FirehoseImageId = (uint)SaharaImageId.Firehose;
    private const uint ImageTxCompleteStatus = (uint)SaharaMode.ImageTxComplete;
    private const int HelloPacketBytes = 48;
    private const int ReadDataPacketBytes = 20;
    private const int EndOfImagePacketBytes = 16;
    private const int DoneResponsePacketBytes = 12;
    private const int UploadedRangeBytes = 8;

    [Fact]
    public async Task Handshake_ResetTargetAnnouncesHelloAfterTheFirstRecoveryRead_CompletesTheUpload()
    {
        var transport = new DelayedHelloTransport();
        using var programmer = new MemoryStream(new byte[UploadedRangeBytes]);
        var sahara = new SaharaProtocol(transport);
        try
        {
            var mode = await sahara.HandshakeAsync(programmer,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SaharaMode.ImageTxPending, mode);
            Assert.Contains(transport.Writes, write => write.Length == 8
                && BinaryPrimitives.ReadUInt32LittleEndian(write) == ResetStateCommand);
            Assert.Equal(EdlConstants.SaharaCmdDone, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[^1]));
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
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), ImageTxCompleteStatus);
        return packet;
    }

    private sealed class DelayedHelloTransport : ICommTransport
    {
        private readonly Queue<byte[]> _packets = new();
        private byte[]? _carry;

        internal DelayedHelloTransport()
        {
            _packets.Enqueue(EndOfImage(1));
            _packets.Enqueue(Hello());
            _packets.Enqueue(ReadData(0, UploadedRangeBytes));
            _packets.Enqueue(EndOfImage(0));
            _packets.Enqueue(DoneResponse());
        }

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

            if (_packets.Count == 0)
            {
                throw new TimeoutException();
            }

            if (_resetSeen && !_helloDelaySpent)
            {
                _helloDelaySpent = true;
                throw new TimeoutException();
            }

            return Task.FromResult(Serve(_packets.Dequeue(), buffer, offset, length));
        }

        private bool _resetSeen;
        private bool _helloDelaySpent;

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
            if (copy.Length == 8 && BinaryPrimitives.ReadUInt32LittleEndian(copy) == ResetStateCommand)
            {
                _resetSeen = true;
            }

            return Task.FromResult((long)length);
        }

        public Stream GetStream() => throw new NotSupportedException();
        public void Dispose() { }
    }
}