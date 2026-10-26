using System.Buffers.Binary;
using UotanStudio.Mirroring;
using UotanStudio.Mirroring.AirPlay;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class AirPlayMirroringDemuxerTests
{
    private static byte[] Packet(long timestamp, params byte[][] nalUnits)
    {
        var body = new List<byte>(256);
        foreach (var nalu in nalUnits)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nalu.Length);
            body.AddRange(length);
            body.AddRange(nalu);
        }

        var packet = new byte[AirPlayConstants.MirrorHeaderLength + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)body.Count);
        packet[4] = AirPlayConstants.VideoPayloadType;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(AirPlayConstants.TimestampOffset), (ulong)timestamp);
        packet[AirPlayConstants.NaluCountOffset] = (byte)nalUnits.Length;
        body.CopyTo(packet, AirPlayConstants.MirrorHeaderLength);
        return packet;
    }

    private static byte[] Nalu(int type, params byte[] payload) => [(byte)(0x60 | type), .. payload];

    [Fact]
    public void Demux_PacketWithSpsPpsAndIdr_EmitsConfigurationThenKeyFrame()
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var pps = Nalu(8, 0xCE, 0x3C, 0x80);
        var idr = Nalu(5, 0x88, 0x84, 0x00, 0x33);
        var packet = Packet(1L << 32, sps, pps, idr);
        var demuxer = new AirPlayMirrorDemuxer();
        var frames = new List<ScrcpyVideoFrame>(2);

        demuxer.Demux(packet, frames);

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].IsCodecConfig);
        Assert.Equal((long)ScrcpyConstants.PacketFlagConfig, frames[0].Pts);
        Assert.Equal([0x00, 0x00, 0x00, 0x01], frames[0].Data[..4]);
        Assert.False(frames[1].IsCodecConfig);
        Assert.True(frames[1].IsKeyFrame);
        Assert.Equal(1_000_000L, frames[1].PresentationTimestampMicroseconds);
        Assert.NotNull(demuxer.Metadata);
        Assert.Equal(1280, demuxer.Metadata!.Width);
        Assert.Equal(720, demuxer.Metadata.Height);
    }

    [Fact]
    public void Demux_SecondPacketWithSameParameters_EmitsOnlyMediaFrame()
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var pps = Nalu(8, 0xCE, 0x3C, 0x80);
        var demuxer = new AirPlayMirrorDemuxer();
        var first = new List<ScrcpyVideoFrame>(2);
        demuxer.Demux(Packet(1L << 32, sps, pps, Nalu(5, 0x11)), first);
        var second = new List<ScrcpyVideoFrame>(2);

        demuxer.Demux(Packet(2L << 32, sps, pps, Nalu(1, 0x22)), second);

        Assert.Single(second);
        Assert.False(second[0].IsKeyFrame);
        Assert.Equal(2_000_000L, second[0].PresentationTimestampMicroseconds);
    }

    [Fact]
    public void Demux_NonIdrSlice_DoesNotSetKeyFrameFlag()
    {
        var demuxer = new AirPlayMirrorDemuxer();
        var frames = new List<ScrcpyVideoFrame>(1);

        demuxer.Demux(Packet(10, Nalu(1, 0x41, 0x9A)), frames);

        var frame = Assert.Single(frames);
        Assert.False(frame.IsKeyFrame);
        Assert.False(frame.IsCodecConfig);
    }

    [Fact]
    public void Demux_AnnouncedLengthBeyondPacket_Throws()
    {
        var packet = Packet(10, Nalu(1, 0x41));
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)packet.Length * 4);
        var demuxer = new AirPlayMirrorDemuxer();

        Assert.Throws<InvalidDataException>(() => demuxer.Demux(packet, new List<ScrcpyVideoFrame>()));
    }

    [Fact]
    public void Demux_NaluLengthBeyondPacket_Throws()
    {
        var packet = Packet(10, Nalu(1, 0x41));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(AirPlayConstants.MirrorHeaderLength), 4096);
        var demuxer = new AirPlayMirrorDemuxer();

        Assert.Throws<InvalidDataException>(() => demuxer.Demux(packet, new List<ScrcpyVideoFrame>()));
    }

    [Fact]
    public void Demux_PacketShorterThanHeader_Throws()
    {
        var demuxer = new AirPlayMirrorDemuxer();

        Assert.Throws<InvalidDataException>(() => demuxer.Demux(new byte[16], new List<ScrcpyVideoFrame>()));
    }

    [Fact]
    public void Demux_RepeatedTimestamp_ProducesIncreasingPresentationTimes()
    {
        var demuxer = new AirPlayMirrorDemuxer();
        var frames = new List<ScrcpyVideoFrame>(1);
        demuxer.Demux(Packet(5_000, Nalu(1, 0x41)), frames);
        demuxer.Demux(Packet(5_000, Nalu(1, 0x42)), frames);

        Assert.Equal(2, frames.Count);
        Assert.True(frames[1].PresentationTimestampMicroseconds > frames[0].PresentationTimestampMicroseconds);
    }
}