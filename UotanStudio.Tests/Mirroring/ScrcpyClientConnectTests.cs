using System.Buffers.Binary;
using System.Text;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// scrcpy 5.x handshake sequencing: the server publishes its abstract sockets only after the device
/// runtime boots, so the video socket open must be retried until the readiness byte arrives, and the
/// stream then opens with a 4-byte codec identifier followed by a 12-byte session packet.
/// <para>scrcpy 5.x 握手时序：服务端要等设备运行时启动后才发布 abstract socket，因此视频 socket
/// 必须重试直到就绪字节到达；随后码流以 4 字节编码标识开头，紧跟一个 12 字节会话包。</para>
/// </summary>
public sealed class ScrcpyClientConnectTests
{
    [Fact]
    public void Connect_VideoSocketClosedWhileServerBoots_ReopensUntilDummyByteArrives()
    {
        var options = new ScrcpyServerOptions { Control = false };
        var attempts = 0;
        using var client = new ScrcpyClient(_ => ++attempts == 1
            ? new ClosedSocketStream()
            : ReadyStream("boot-delayed", 1080, 2400), options);

        client.Connect(timeoutMs: 4000);

        Assert.Equal(2, attempts);
        Assert.Equal("boot-delayed", client.DeviceName);
        Assert.Equal(1080, client.VideoMetadata!.Width);
        Assert.Equal(2400, client.VideoMetadata.Height);
    }

    [Fact]
    public void Connect_SocketOpenThrowsWhileServerBoots_ReopensUntilReady()
    {
        var options = new ScrcpyServerOptions { Control = false };
        var attempts = 0;
        using var client = new ScrcpyClient(_ =>
        {
            if (++attempts <= 2)
            {
                throw new IOException("ADB server: closed");
            }

            return ReadyStream("refused-then-ready", 720, 1280);
        }, options);

        client.Connect(timeoutMs: 4000);

        Assert.Equal(3, attempts);
        Assert.Equal("refused-then-ready", client.DeviceName);
        Assert.Equal(720, client.VideoMetadata!.Width);
    }

    [Fact]
    public void Connect_NonZeroDummyByte_FailsWithoutRetrying()
    {
        var options = new ScrcpyServerOptions { Control = false };
        var attempts = 0;
        var payload = ReadyBytes("not-scrcpy", 1, 1);
        payload[0] = 0x42;
        using var client = new ScrcpyClient(_ =>
        {
            attempts++;
            return new MemoryStream(payload);
        }, options);

        var error = Assert.Throws<InvalidDataException>(() => client.Connect(timeoutMs: 4000));

        Assert.Equal(1, attempts);
        Assert.Contains("dummy byte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_SocketNeverReady_ThrowsAfterLaunchBudget()
    {
        var options = new ScrcpyServerOptions { Control = false };
        var attempts = 0;
        using var client = new ScrcpyClient(_ =>
        {
            attempts++;
            return new ClosedSocketStream();
        }, options);

        Assert.Throws<EndOfStreamException>(() => client.Connect(timeoutMs: 250));

        Assert.True(attempts > 1, $"Expected repeated socket attempts, observed {attempts}.");
    }

    [Fact]
    public void Connect_MediaPacketBeforeSessionPacket_FailsInsteadOfGuessingSize()
    {
        var options = new ScrcpyServerOptions { Control = false };
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        var deviceName = new byte[ScrcpyConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes("no-session").AsSpan().CopyTo(deviceName);
        stream.Write(deviceName);
        stream.Write([0x68, 0x32, 0x36, 0x34]);
        var frameHeader = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(frameHeader, 5_000L);
        BinaryPrimitives.WriteInt32BigEndian(frameHeader.AsSpan(8), 0);
        stream.Write(frameHeader);
        stream.Position = 0;

        using var client = new ScrcpyClient(_ => stream, options);
        var error = Assert.Throws<InvalidDataException>(() => client.Connect(timeoutMs: 1000));

        Assert.Contains("session metadata packet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFrame_LaterSessionPacket_ReplacesSizeAndAdvancesEpoch()
    {
        var options = new ScrcpyServerOptions { Control = false };
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        var deviceName = new byte[ScrcpyConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes("resizing").AsSpan().CopyTo(deviceName);
        stream.Write(deviceName);
        stream.Write([0x68, 0x32, 0x36, 0x34]);
        WriteSession(stream, 1080, 2400, clientRequested: false);
        var firstHeader = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(firstHeader, unchecked((long)ScrcpyConstants.PacketFlagConfig) | 1_000_000L);
        BinaryPrimitives.WriteInt32BigEndian(firstHeader.AsSpan(8), 2);
        stream.Write(firstHeader);
        stream.Write([0x01, 0x02]);
        WriteSession(stream, 720, 1600, clientRequested: true);
        var secondHeader = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(secondHeader, 0x2000_0000_0000_1234L);
        BinaryPrimitives.WriteInt32BigEndian(secondHeader.AsSpan(8), 1);
        stream.Write(secondHeader);
        stream.Write([0x03]);
        stream.Position = 0;

        using var client = new ScrcpyClient(_ => stream, options);
        client.Connect(timeoutMs: 1000);
        Assert.Equal(1080, client.VideoMetadata!.Width);
        Assert.Equal(1, client.SessionEpoch);
        Assert.True(client.ReadFrame(1000)!.IsCodecConfig);

        var next = client.ReadFrame(1000);

        Assert.Equal(720, client.VideoMetadata.Width);
        Assert.Equal(1600, client.VideoMetadata.Height);
        Assert.Equal(0x68323634u, client.VideoMetadata.CodecFourCc);
        Assert.Equal(2, client.SessionEpoch);
        Assert.True(client.LastSessionChangeWasClientRequested);
        Assert.True(next!.IsKeyFrame);
        Assert.Equal(0x1234L, next.PresentationTimestampMicroseconds);
    }

    private static void WriteSession(MemoryStream stream, int width, int height, bool clientRequested)
    {
        var session = new byte[12];
        var flags = ScrcpyConstants.PacketFlagSession | (uint)width;
        if (clientRequested) flags |= ScrcpyConstants.SessionFlagClientResize;
        BinaryPrimitives.WriteUInt64BigEndian(session, flags);
        BinaryPrimitives.WriteInt32BigEndian(session.AsSpan(8), height);
        stream.Write(session);
    }

    private static MemoryStream ReadyStream(string name, int width, int height)
        => new(ReadyBytes(name, width, height));

    private static byte[] ReadyBytes(string name, int width, int height)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        var deviceName = new byte[ScrcpyConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes(name.AsSpan(), deviceName);
        stream.Write(deviceName);
        stream.Write([0x68, 0x32, 0x36, 0x34]);
        WriteSession(stream, width, height, clientRequested: false);
        stream.Position = 0;
        return stream.ToArray();
    }

    private sealed class ClosedSocketStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position { get => 0; set { } }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override int Read(Span<byte> destination) => 0;

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}