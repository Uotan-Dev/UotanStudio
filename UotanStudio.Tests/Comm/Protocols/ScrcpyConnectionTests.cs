using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Scrcpy;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class ScrcpyConnectionTests
{
    [Fact]
    public void Connect_OpensControlBeforeReadingDeviceMetadata_AndPreservesJoinedPackets()
    {
        using var data = new MemoryStream();
        data.WriteByte(0);
        byte[] name = new byte[64]; Encoding.UTF8.GetBytes("Emulator").CopyTo(name, 0); data.Write(name);
        byte[] meta = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(meta, 0x68323634);
        BinaryPrimitives.WriteInt32BigEndian(meta.AsSpan(4), 1024);
        BinaryPrimitives.WriteInt32BigEndian(meta.AsSpan(8), 576); data.Write(meta);
        WritePacket(data, long.MinValue, [0, 0, 1, 0x67]);
        WritePacket(data, 42, [1, 2, 3, 4, 5]);
        bool controlOpened = false;
        using var video = new GatedStream(data.ToArray(), () => controlOpened);
        int opens = 0;
        using var client = new ScrcpyClient(_ => ++opens == 1 ? video : OpenControl(), new ScrcpyServerOptions());
        Stream OpenControl() { controlOpened = true; return new MemoryStream(); }
        client.Connect();
        Assert.Equal("Emulator", client.DeviceName);
        Assert.Equal(1024, client.VideoMetadata!.Width);
        Assert.True(client.ReadFrame(1000)!.IsCodecConfig);
        var frame = client.ReadFrame(1000)!;
        Assert.Equal(42, frame.PresentationTimestampMicroseconds);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, frame.Data);
        Assert.Throws<EndOfStreamException>(() => client.ReadFrame(1000));
        client.Dispose(); client.Dispose();
    }

    [Fact]
    public void ServerArguments_UseUpstreamForwardTunnelAndSignedScid()
    {
        for (int i = 0; i < 100; i++)
        {
            var options = new ScrcpyServerOptions();
            Assert.InRange(options.Scid, 0u, (uint)int.MaxValue);
            Assert.Contains("tunnel_forward=true", options.BuildArguments());
            Assert.DoesNotContain(options.BuildArguments(), item => item.StartsWith("socket_name=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Connect_WithoutMetadata_DoesNotWaitForAudioCodecHeader()
    {
        int opens = 0;
        using var client = new ScrcpyClient(_ => { opens++; return new MemoryStream(); },
            new ScrcpyServerOptions { Audio = true, SendDummyByte = false, SendDeviceMeta = false, SendCodecMeta = false });
        client.Connect();
        Assert.Equal(3, opens);
        Assert.Null(client.VideoMetadata);
    }

    [Fact]
    public void Connect_FailedHandshake_ReleasesEveryOpenedStream()
    {
        var video = new MemoryStream(new byte[] { 0 });
        var control = new MemoryStream();
        int opens = 0;
        using var client = new ScrcpyClient(_ => ++opens == 1 ? video : control, new ScrcpyServerOptions());
        Assert.Throws<EndOfStreamException>(() => client.Connect());
        Assert.False(video.CanRead);
        Assert.False(control.CanRead);
        Assert.False(client.IsControlConnected);
    }

    private static void WritePacket(Stream stream, long pts, byte[] bytes)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt64BigEndian(header, pts);
        BinaryPrimitives.WriteInt32BigEndian(header[8..], bytes.Length);
        stream.Write(header); stream.Write(bytes);
    }
    private sealed class GatedStream(byte[] bytes, Func<bool> controlOpened) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= 1 && !controlOpened()) throw new InvalidOperationException("Server waits for control connection before metadata.");
            return base.Read(buffer, offset, Math.Min(count, 3));
        }
    }
}
