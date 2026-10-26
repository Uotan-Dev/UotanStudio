using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Apple;
using UotanStudio.Mirroring;
using UotanStudio.Mirroring.AirPlay;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class AirPlayReceiverSessionTests
{
    private sealed record RtspReply(int Status, string Headers, byte[] Body);

    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    private static RtspReply Send(NetworkStream stream, string method, string uri, byte[]? body, int cseq)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(cseq.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\r\n");
        if (body is not null)
        {
            builder.Append("Content-Type: application/x-apple-binary-plist\r\n");
            builder.Append("Content-Length: ")
                .Append(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }

        builder.Append("\r\n");
        var header = Encoding.Latin1.GetBytes(builder.ToString());
        stream.Write(header);
        if (body is not null)
        {
            stream.Write(body);
        }

        stream.Flush();

        var received = new List<byte>(256);
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new IOException("The receiver closed the RTSP connection.");
            }

            received.Add((byte)value);
            var count = received.Count;
            if (count >= 4 && received[count - 4] == (byte)'\r' && received[count - 3] == (byte)'\n' &&
                received[count - 2] == (byte)'\r' && received[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        var text = Encoding.Latin1.GetString(received.ToArray(), 0, received.Count);
        var status = int.Parse(text.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var length = 0;
        foreach (var line in text.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var payload = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = stream.Read(payload, offset, length - offset);
            if (count == 0)
            {
                throw new IOException("The receiver closed the RTSP body.");
            }

            offset += count;
        }

        return new RtspReply(status, text, payload);
    }

    private static byte[] VideoPacket(long timestamp, params byte[][] nalUnits)
    {
        var body = new List<byte>(512);
        foreach (var nalu in nalUnits)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nalu.Length);
            body.AddRange(length);
            body.AddRange(nalu);
        }

        var rtp = new byte[12 + AirPlayConstants.MirrorHeaderLength + body.Count];
        rtp[0] = 0x80;
        rtp[1] = AirPlayConstants.VideoPayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), 1);
        var mirror = rtp.AsSpan(12);
        BinaryPrimitives.WriteUInt32BigEndian(mirror, (uint)body.Count);
        BinaryPrimitives.WriteUInt64BigEndian(mirror[AirPlayConstants.TimestampOffset..], (ulong)timestamp);
        mirror[AirPlayConstants.NaluCountOffset] = (byte)nalUnits.Length;
        body.CopyTo(rtp, 12 + AirPlayConstants.MirrorHeaderLength);
        return rtp;
    }

    private static void SendInterleaved(NetworkStream stream, int channel, byte[] payload)
    {
        var header = new byte[4];
        header[0] = (byte)'$';
        header[1] = (byte)channel;
        header[2] = (byte)(payload.Length >> 8);
        header[3] = (byte)payload.Length;
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static byte[] SetupBody(int streamType) => ApplePlist.ToBinary(new Dictionary<string, object>
    {
        ["streams"] = new List<object>
        {
            new Dictionary<string, object> { ["type"] = (long)streamType, ["controlPort"] = 0L },
        },
    });

    private static byte[] AnnounceBody(byte[] ekey) => ApplePlist.ToBinary(new Dictionary<string, object>
    {
        ["ekey"] = ekey,
        ["sdk"] = "310.10",
    });

    private static MirrorSessionFacade StartFacade(List<string> log)
        => MirrorSessionFacade.FromAirPlay(MirrorSessionFacade.StartAirPlayReceiver(
            new AirPlayOptions { RtspPort = 0, Advertise = false }, text => log.Add(text)));

    private static TcpClient Connect(MirrorSessionFacade facade)
    {
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, facade.RtspPort);
        client.NoDelay = true;
        client.ReceiveTimeout = 5000;
        return client;
    }

    [Fact]
    public void StartAirPlayReceiver_EphemeralPort_ReportsAirPlayAndBoundPort()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { RtspPort = 0, Advertise = false }));

        facade.Connect();

        Assert.True(facade.IsAirPlay);
        Assert.False(facade.IsHdc);
        Assert.False(facade.IsControlConnected);
        Assert.Equal("UotanStudio", facade.DeviceName);
        Assert.True(facade.RtspPort > 0);
        Assert.NotNull(facade.AudioMetadata);
        Assert.Null(facade.VideoMetadata);
    }

    [Fact]
    public void Handshake_OptionsAnnounceSetupRecord_AcceptsAndReportsDataPort()
    {
        var log = new List<string>();
        using var facade = StartFacade(log);
        facade.Connect();
        using var client = Connect(facade);
        using var stream = client.GetStream();

        var options = Send(stream, "OPTIONS", "*", null, 1);
        var announce = Send(stream, "ANNOUNCE", "rtsp://127.0.0.1/1", AnnounceBody([]), 2);
        var setup = Send(stream, "SETUP", "rtsp://127.0.0.1/1", SetupBody(AirPlayConstants.VideoPayloadType), 3);
        var record = Send(stream, "RECORD", "rtsp://127.0.0.1/1", null, 4);

        Assert.Equal(200, options.Status);
        Assert.Contains("ANNOUNCE", options.Headers, StringComparison.Ordinal);
        Assert.True(announce.Status == 200, string.Join(" | ", log));
        Assert.True(setup.Status == 200, string.Join(" | ", log));
        Assert.Equal(200, record.Status);
        var reply = Assert.IsType<Dictionary<string, object>>(ApplePlist.Parse(setup.Body));
        var streams = Assert.IsType<List<object>>(reply["streams"]);
        var streamReply = Assert.IsType<Dictionary<string, object>>(Assert.Single(streams));
        var dataPort = Assert.IsType<long>(streamReply["dataPort"]);
        Assert.InRange(dataPort, 1, 65535);
    }

    [Fact]
    public void Handshake_AnnounceWithEkeyWithoutKeyMaterial_ReturnsForbidden()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { Advertise = false }));
        facade.Connect();
        using var client = Connect(facade);
        using var stream = client.GetStream();

        var announce = Send(stream, "ANNOUNCE", "rtsp://127.0.0.1/1", AnnounceBody(new byte[164]), 2);

        Assert.Equal(403, announce.Status);
    }

    [Fact]
    public void InterleavedVideo_AfterHandshake_ReadFrameReturnsConfigurationAndKeyFrame()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { Advertise = false }));
        facade.Connect();
        using var client = Connect(facade);
        using var stream = client.GetStream();
        _ = Send(stream, "ANNOUNCE", "rtsp://127.0.0.1/1", AnnounceBody([]), 1);
        _ = Send(stream, "SETUP", "rtsp://127.0.0.1/1", SetupBody(AirPlayConstants.VideoPayloadType), 2);
        _ = Send(stream, "RECORD", "rtsp://127.0.0.1/1", null, 3);
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var idr = new byte[] { 0x65, 0x88, 0x84, 0x00, 0x33 };

        SendInterleaved(stream, AirPlayConstants.VideoPayloadType, VideoPacket(1_000, sps, pps, idr));

        using var configuration = facade.ReadFrame(5000);
        using var frame = facade.ReadFrame(5000);
        Assert.NotNull(configuration);
        Assert.True(configuration!.IsCodecConfig);
        Assert.NotNull(frame);
        Assert.True(frame!.IsKeyFrame);
        Assert.Equal(StartCode, frame.Data[..4]);
        Assert.NotNull(facade.VideoMetadata);
        Assert.Equal(1280, facade.VideoMetadata!.Width);
    }

    [Fact]
    public void ReadFrame_NoMediaArrived_ReturnsNullAfterTimeout()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { Advertise = false }));
        facade.Connect();

        var frame = facade.ReadFrame(50);

        Assert.Null(frame);
    }

    [Fact]
    public void Handshake_PostPairVerify_ReturnsNotImplemented()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { Advertise = false }));
        facade.Connect();
        using var client = Connect(facade);
        using var stream = client.GetStream();

        var reply = Send(stream, "POST", "/pair-verify", [0x01, 0x00, 0x00, 0x00], 1);

        Assert.Equal(501, reply.Status);
    }

    [Fact]
    public void Handshake_UnknownMethod_ReturnsMethodNotAllowed()
    {
        using var facade = MirrorSessionFacade.FromAirPlay(
            MirrorSessionFacade.StartAirPlayReceiver(new AirPlayOptions { Advertise = false }));
        facade.Connect();
        using var client = Connect(facade);
        using var stream = client.GetStream();

        var reply = Send(stream, "JUMP", "rtsp://127.0.0.1/1", null, 1);

        Assert.Equal(405, reply.Status);
    }
}