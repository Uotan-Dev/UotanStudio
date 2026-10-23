using System.Buffers.Binary;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class OhscrcpyClientTests
{
    [Fact]
    public void Connect_ReadsTheEchoedHeartbeatAndDeliversConfigThenFrame()
    {
        var sps = new byte[] { 0x67, 0x42, 0x00, 0x1F };
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var accessUnit = new byte[] { 0, 0, 0, 1, 0x65, 0x88 };
        var stream = new ScriptedDuplexStream();
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoConfig,
            BuildConfig(720, 1280, 10, sps, pps)));
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoFrame,
            BuildFrame(keyFrame: true, timestamp: 123_456, accessUnit)));
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("5XM0225C18001159"));

        client.Connect(2_000);

        Assert.True(client.IsConnected);
        var configuration = client.ReadFrame(2_000);
        Assert.NotNull(configuration);
        Assert.True(configuration.IsCodecConfig);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80 },
            configuration.Data);
        Assert.Equal(720, client.VideoMetadata!.Width);
        Assert.Equal(1280, client.VideoMetadata.Height);
        Assert.Equal(1, client.ConfigEpoch);
        configuration.Dispose();

        var frame = client.ReadFrame(2_000);

        Assert.NotNull(frame);
        Assert.True(frame.IsKeyFrame);
        Assert.Equal(123_456L, frame.PresentationTimestampMicroseconds);
        Assert.Equal(accessUnit, frame.Data);
        frame.Dispose();
    }

    [Fact]
    public void ReadFrame_NonKeyFrame_CarriesNoKeyFrameFlag()
    {
        var stream = new ScriptedDuplexStream();
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoConfig,
            BuildConfig(16, 16, 10, [0x67], [0x68])));
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoFrame,
            BuildFrame(keyFrame: false, timestamp: 42, [0, 0, 0, 1, 0x41])));
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        client.ReadFrame(2_000)!.Dispose();

        var frame = client.ReadFrame(2_000);

        Assert.NotNull(frame);
        Assert.False(frame.IsKeyFrame);
        Assert.Equal(42L, frame.PresentationTimestampMicroseconds);
        frame.Dispose();
    }

    [Fact]
    public void Connect_ServerThatNeverEchoes_ThrowsWhenTheStreamEnds()
    {
        var stream = new ScriptedDuplexStream { EchoHeartbeats = false };
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));

        Assert.Throws<EndOfStreamException>(() => client.Connect(500));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void ReadFrame_JpegConfiguration_ReportsTheUnsupportedCodec()
    {
        var jpegConfig = new byte[13];
        jpegConfig[0] = OhscrcpyConstants.VideoCodecJpeg;
        BinaryPrimitives.WriteInt32BigEndian(jpegConfig.AsSpan(1), 320);
        BinaryPrimitives.WriteInt32BigEndian(jpegConfig.AsSpan(5), 640);
        BinaryPrimitives.WriteInt32BigEndian(jpegConfig.AsSpan(9), 10);
        var stream = new ScriptedDuplexStream();
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoConfig, jpegConfig));
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);

        var error = Assert.Throws<NotSupportedException>(() => client.ReadFrame(2_000));

        Assert.Contains("JPEG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFrame_UnknownPacketType_Throws()
    {
        var stream = new ScriptedDuplexStream();
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        stream.Enqueue(OhscrcpyPayload.Frame(0x7F, []));

        Assert.Throws<InvalidDataException>(() => client.ReadFrame(2_000));
    }

    [Fact]
    public void ReadFrame_LengthAboveTheBound_Throws()
    {
        var stream = new ScriptedDuplexStream();
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, OhscrcpyConstants.PacketTypeVideoFrame);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), OhscrcpyConstants.MaxPacketLength + 1u);
        stream.Enqueue(header);

        var error = Assert.Throws<InvalidDataException>(() => client.ReadFrame(2_000));

        Assert.Contains("bound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendScrcpyControl_Touch_TranslatesAndWritesOneOhscrcpyPacket()
    {
        var stream = new ScriptedDuplexStream();
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        var message = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionDown, ScrcpyConstants.PointerIdGenericFinger, 5, 6, 720, 1280);

        Assert.True(client.SendScrcpyControl(message));

        var written = Assert.Single(stream.WrittenPackets);
        Assert.Equal((uint)OhscrcpyConstants.PacketTypeControl, BinaryPrimitives.ReadUInt32BigEndian(written));
        Assert.Equal(OhscrcpyConstants.ControlTouchDown, written[8]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(9)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(13)));
    }

    [Fact]
    public void TryDequeueDeviceMessage_StatusPacketReadDuringAFrameRead_IsQueued()
    {
        var stream = new ScriptedDuplexStream();
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeVideoConfig,
            BuildConfig(16, 16, 10, [0x67], [0x68])));
        stream.Enqueue(OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeDeviceStatus,
            [OhscrcpyConstants.DeviceStatusAppList, 0, 0]));
        using var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        client.ReadFrame(2_000)!.Dispose();

        Assert.Null(client.ReadFrame(2_000));
        Assert.True(client.TryDequeueDeviceMessage(out var message));
        Assert.Equal(OhscrcpyConstants.DeviceStatusAppList, message!.SubType);
        Assert.Equal(new byte[] { 0, 0 }, message.Payload);
    }

    [Fact]
    public void MirrorSessionFacade_OhscrcpyPath_TranslatesScrcpyTouchControl()
    {
        var stream = new ScriptedDuplexStream();
        var client = new OhscrcpyClient(_ => stream, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        using var session = new ManagedOhscrcpySession(client);
        using var facade = MirrorSessionFacade.FromOhscrcpy(session);
        var message = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionDown, ScrcpyConstants.PointerIdGenericFinger, 1, 2, 720, 1280);

        Assert.True(facade.IsHdc);
        Assert.True(facade.IsControlConnected);
        facade.SendControl(message);

        var written = Assert.Single(stream.WrittenPackets);
        Assert.Equal(OhscrcpyConstants.ControlTouchDown, written[8]);
    }

    private static byte[] BuildConfig(int width, int height, int frameRate, byte[] sps, byte[] pps)
    {
        var payload = new byte[13 + 2 + sps.Length + 2 + pps.Length];
        payload[0] = OhscrcpyConstants.VideoCodecH264;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), width);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5), height);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(9), frameRate);
        var offset = 13;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), (ushort)sps.Length);
        offset += 2;
        sps.CopyTo(payload, offset);
        offset += sps.Length;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), (ushort)pps.Length);
        offset += 2;
        pps.CopyTo(payload, offset);
        return payload;
    }

    private static byte[] BuildFrame(bool keyFrame, long timestamp, byte[] accessUnit)
    {
        var payload = new byte[9 + accessUnit.Length];
        payload[0] = keyFrame ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), timestamp);
        accessUnit.CopyTo(payload, 9);
        return payload;
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly Lock _gate = new();
        private readonly List<byte> _incoming = [];
        private readonly List<byte> _outgoing = [];
        private readonly List<byte[]> _written = [];
        private int _readIndex;

        public bool EchoHeartbeats { get; init; } = true;

        public IReadOnlyList<byte[]> WrittenPackets
        {
            get
            {
                lock (_gate) return [.. _written];
            }
        }

        public void Enqueue(byte[] packet)
        {
            lock (_gate) _outgoing.AddRange(packet);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override bool CanTimeout => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                var available = _outgoing.Count - _readIndex;
                if (available <= 0) return 0;
                var copied = Math.Min(available, count);
                for (var index = 0; index < copied; index++)
                {
                    buffer[offset + index] = _outgoing[_readIndex + index];
                }

                _readIndex += copied;
                return copied;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                for (var index = 0; index < count; index++) _incoming.Add(buffer[offset + index]);
                ParseClientPackets();
            }
        }

        private void ParseClientPackets()
        {
            while (_incoming.Count >= OhscrcpyConstants.HeaderLength)
            {
                var header = _incoming.Take(OhscrcpyConstants.HeaderLength).ToArray();
                var type = BinaryPrimitives.ReadUInt32BigEndian(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (length > int.MaxValue - OhscrcpyConstants.HeaderLength) return;
                var total = OhscrcpyConstants.HeaderLength + (int)length;
                if (_incoming.Count < total) return;
                var packet = _incoming.Take(total).ToArray();
                _incoming.RemoveRange(0, total);
                if (type == OhscrcpyConstants.PacketTypeHeartbeat && EchoHeartbeats)
                {
                    _outgoing.AddRange(packet);
                }
                else
                {
                    _written.Add(packet);
                }
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}