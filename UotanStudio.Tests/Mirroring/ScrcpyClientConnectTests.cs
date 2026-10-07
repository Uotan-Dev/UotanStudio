using System.Buffers.Binary;
using System.Text;
using UotanStudio.Services.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// scrcpy 2.7 handshake sequencing: the server publishes its abstract sockets only after the device
/// runtime boots, so the video socket open must be retried until the readiness byte arrives.
/// <para>scrcpy 2.7 握手时序：服务端要等设备运行时启动后才发布 abstract socket，因此视频 socket
/// 必须重试直到就绪字节到达。</para>
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

    private static MemoryStream ReadyStream(string name, int width, int height)
        => new(ReadyBytes(name, width, height));

    private static byte[] ReadyBytes(string name, int width, int height)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        var deviceName = new byte[ScrcpyConstants.DeviceNameLength];
        Encoding.UTF8.GetBytes(name.AsSpan(), deviceName);
        stream.Write(deviceName);
        var metadata = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(metadata, 0x68323634);
        BinaryPrimitives.WriteInt32BigEndian(metadata.AsSpan(4), width);
        BinaryPrimitives.WriteInt32BigEndian(metadata.AsSpan(8), height);
        stream.Write(metadata);
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