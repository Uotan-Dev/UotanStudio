using System.Buffers.Binary;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class OhscrcpyProtocolTests
{
    [Fact]
    public void ParseVideoConfig_H264_ReadsSizeAndNalUnitsWithoutStartCodes()
    {
        var sps = new byte[] { 0x67, 0x42, 0x00, 0x1F };
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var payload = BuildConfig(OhscrcpyConstants.VideoCodecH264, 720, 1280, 10, sps, pps);

        var config = OhscrcpyPayload.ParseVideoConfig(payload);

        Assert.True(config.IsH264);
        Assert.Equal(720, config.Width);
        Assert.Equal(1280, config.Height);
        Assert.Equal(10, config.FrameRate);
        Assert.Equal("H264", config.CodecName);
        Assert.Equal(sps, config.Sps);
        Assert.Equal(pps, config.Pps);
    }

    [Fact]
    public void BuildAnnexBConfiguration_PrefixesBothNalUnitsWithFourByteStartCodes()
    {
        var sps = new byte[] { 0x67, 0x01 };
        var pps = new byte[] { 0x68, 0x02 };
        var config = new OhscrcpyVideoConfig(OhscrcpyConstants.VideoCodecH264, 16, 16, 10, sps, pps);

        var annexB = config.BuildAnnexBConfiguration();

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x01, 0, 0, 0, 1, 0x68, 0x02 }, annexB);
    }

    [Fact]
    public void ParseVideoConfig_Jpeg_ReportsTheCodecAndNoNalUnits()
    {
        var payload = new byte[13];
        payload[0] = OhscrcpyConstants.VideoCodecJpeg;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), 320);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(5), 640);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(9), 10);

        var config = OhscrcpyPayload.ParseVideoConfig(payload);

        Assert.False(config.IsH264);
        Assert.Equal("JPEG", config.CodecName);
        Assert.Empty(config.Sps);
        Assert.Empty(config.Pps);
        Assert.Empty(config.BuildAnnexBConfiguration());
    }

    [Fact]
    public void ParseVideoConfig_SpsLengthOverrunsPayload_Throws()
    {
        var payload = BuildConfig(OhscrcpyConstants.VideoCodecH264, 16, 16, 10, [0x67], [0x68]);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), 0xFFFF);

        Assert.Throws<InvalidDataException>(() => OhscrcpyPayload.ParseVideoConfig(payload));
    }

    [Fact]
    public void ParseVideoConfig_ZeroWidth_Throws()
    {
        var payload = BuildConfig(OhscrcpyConstants.VideoCodecH264, 0, 1280, 10, [0x67], [0x68]);

        Assert.Throws<InvalidDataException>(() => OhscrcpyPayload.ParseVideoConfig(payload));
    }

    [Fact]
    public void ParseVideoConfig_ShortPayload_Throws()
        => Assert.Throws<InvalidDataException>(() => OhscrcpyPayload.ParseVideoConfig(new byte[12]));

    [Theory]
    [InlineData(true, 123_456L)]
    [InlineData(false, 0L)]
    public void ParseVideoFrameHeader_ReadsTheKeyFrameFlagAndTimestamp(bool keyFrame, long timestamp)
    {
        var payload = new byte[13];
        payload[0] = keyFrame ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1), (ulong)timestamp);

        var (parsedKeyFrame, parsedTimestamp) = OhscrcpyPayload.ParseVideoFrameHeader(payload);

        Assert.Equal(keyFrame, parsedKeyFrame);
        Assert.Equal(timestamp, parsedTimestamp);
    }

    [Fact]
    public void ParseVideoFrameHeader_EightBytePayload_Throws()
        => Assert.Throws<InvalidDataException>(() => OhscrcpyPayload.ParseVideoFrameHeader(new byte[8]));

    [Fact]
    public void Frame_WritesBigEndianTypeAndLength()
    {
        var packet = OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeControl, [0x15, 0x41]);

        Assert.Equal(10, packet.Length);
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32BigEndian(packet));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(new byte[] { 0x15, 0x41 }, packet[8..]);
    }

    [Fact]
    public void Touch_EncodesSubTypeCoordinatesAndPointerId()
    {
        var packet = OhscrcpyControlMessageWriter.Touch(OhscrcpyConstants.ControlTouchDown, 100, 200, 7);

        Assert.Equal((uint)OhscrcpyConstants.PacketTypeControl, BinaryPrimitives.ReadUInt32BigEndian(packet));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(OhscrcpyConstants.ControlTouchDown, packet[8]);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(9)));
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(13)));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(17)));
    }

    [Fact]
    public void Text_OverTheByteBound_Throws()
        => Assert.Throws<ArgumentException>(() =>
            OhscrcpyControlMessageWriter.Text(new string('a', OhscrcpyControlMessageWriter.MaxTextLength + 1)));

    [Fact]
    public void ParseAppList_ReadsLengthPrefixedEntries()
    {
        var payload = new byte[] { 0, 2, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0, 1, (byte)'X', 0, 2, (byte)'d', (byte)'e', 0, 0 };

        var entries = OhscrcpyPayload.ParseAppList(payload);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new OhscrcpyAppEntry("abc", "X"), entries[0]);
        Assert.Equal(new OhscrcpyAppEntry("de", string.Empty), entries[1]);
    }

    [Fact]
    public void ParseAppList_TruncatedEntry_StopsWithoutThrowing()
    {
        var payload = new byte[] { 0, 2, 0, 5, (byte)'a' };

        var entries = OhscrcpyPayload.ParseAppList(payload);

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseDisplayInfo_NineBytePayload_ReadsGeometry()
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(payload, 1260);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), 2720);
        payload[8] = 1;

        var info = OhscrcpyPayload.ParseDisplayInfo(payload);

        Assert.NotNull(info);
        Assert.Equal(1260, info.Width);
        Assert.Equal(2720, info.Height);
        Assert.Equal(1, info.Rotation);
    }

    [Fact]
    public void Adapter_ScrcpyTouch_BecomesAnOhscrcpyTouchPacket()
    {
        var message = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionMove, ScrcpyConstants.PointerIdGenericFinger, 12, 34, 720, 1280);

        var translated = ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet);

        Assert.True(translated);
        Assert.Equal((uint)OhscrcpyConstants.PacketTypeControl, BinaryPrimitives.ReadUInt32BigEndian(packet));
        Assert.Equal(OhscrcpyConstants.ControlTouchMove, packet[8]);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(9)));
        Assert.Equal(34u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(13)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(17)));
    }

    [Fact]
    public void Adapter_ScrcpyBackPair_CollapsesToOnePress()
    {
        var down = ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionDown);
        var up = ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionUp);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var press));
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out _));
        Assert.Equal(OhscrcpyConstants.ControlBackKey, press[8]);
    }

    [Fact]
    public void Adapter_ScrcpyText_BecomesUtf8TextInput()
    {
        var message = ScrcpyControlMessageWriter.InjectText("hi");

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet));

        Assert.Equal(OhscrcpyConstants.ControlTextInput, packet[8]);
        Assert.Equal("hi", System.Text.Encoding.UTF8.GetString(packet, 9, packet.Length - 9));
    }

    [Fact]
    public void Adapter_ScrcpyKeyCode_MapsToAnOpenHarmonyKeyEvent()
    {
        var message = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, 29, 0, 0);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet));

        Assert.Equal(OhscrcpyConstants.ControlKeyEvent, packet[8]);
        Assert.Equal((byte)1, packet[9]);
        Assert.Equal(2017, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(10)));
    }

    [Fact]
    public void Adapter_ScrcpyScroll_ConvertsFixedPointTicksToTheFloatAxis()
    {
        var message = ScrcpyControlMessageWriter.InjectScrollEvent(10, 20, 720, 1280, 0, 1);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet));

        var axis = BinaryPrimitives.ReadSingleBigEndian(packet.AsSpan(packet.Length - 4));
        Assert.Equal(OhscrcpyConstants.ControlMouseEvent, packet[8]);
        Assert.Equal(1.0f, axis, 2);
    }

    [Fact]
    public void Adapter_ScrcpyClipboard_HasNoOhscrcpyEquivalent()
    {
        var message = ScrcpyControlMessageWriter.SetClipboard("text", paste: false);

        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var packet));
        Assert.Empty(packet);
    }

    [Theory]
    [InlineData(29, 2017)]
    [InlineData(54, 2042)]
    [InlineData(7, 2000)]
    [InlineData(16, 2009)]
    [InlineData(66, 2054)]
    [InlineData(131, 2090)]
    public void TryMapKeyCode_KnownAndroidCodes_MapToOpenHarmonyKeyCodes(int android, int expected)
    {
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryMapKeyCode(android, out var mapped));
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void TryMapKeyCode_UnknownAndroidCode_IsRejected()
        => Assert.False(ScrcpyToOhscrcpyControlAdapter.TryMapKeyCode(1000, out _));

    private static byte[] BuildConfig(byte codec, int width, int height, int frameRate, byte[] sps, byte[] pps)
    {
        var payload = new byte[13 + 2 + sps.Length + 2 + pps.Length];
        payload[0] = codec;
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
}