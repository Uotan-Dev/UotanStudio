using System.Buffers.Binary;
using System.Text;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

/// <summary>
/// scrcpy 2.7 control message wire-format conformance: byte layouts are
/// checked against the server's ControlMessageReader.parse* order.
/// <para>scrcpy 2.7 控制消息线格式符合性：字节布局按服务端 ControlMessageReader.parse* 的读取顺序核对。</para>
/// </summary>
public class ScrcpyControlMessageTests
{
    [Fact]
    public void InjectKeyCode_MatchesWireLayout()
    {
        var msg = ScrcpyControlMessageWriter.InjectKeyCode(
            ScrcpyConstants.ActionDown, keyCode: 25, repeat: 2, metaState: 1);

        Assert.Equal(14, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeInjectKeyCode, msg[0]);
        Assert.Equal(ScrcpyConstants.ActionDown, msg[1]);
        Assert.Equal(25, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(2)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(6)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10)));
    }

    [Fact]
    public void InjectText_LengthPrefixAndUtf8Payload()
    {
        var msg = ScrcpyControlMessageWriter.InjectText("你好");

        Assert.Equal(5 + Encoding.UTF8.GetByteCount("你好"), msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeInjectText, msg[0]);
        Assert.Equal(Encoding.UTF8.GetByteCount("你好"), BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(1)));
        Assert.Equal("你好", Encoding.UTF8.GetString(msg, 5, msg.Length - 5));
    }

    [Fact]
    public void InjectText_RejectsOverlongPayload()
    {
        Assert.Throws<ArgumentException>(() => ScrcpyControlMessageWriter.InjectText(new string('a', 301)));
    }

    [Fact]
    public void InjectTouchEvent_Matches32ByteWireLayout()
    {
        var msg = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionDown, ScrcpyConstants.PointerIdMouse,
            x: 100, y: 200, width: 1080, height: 2400,
            pressure: 0xFFFF, actionButton: ScrcpyConstants.ButtonLeft, buttons: ScrcpyConstants.ButtonLeft);

        Assert.Equal(32, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeInjectTouchEvent, msg[0]);
        Assert.Equal(ScrcpyConstants.ActionDown, msg[1]);
        Assert.Equal(unchecked((ulong)ScrcpyConstants.PointerIdMouse), BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(2)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(10)));
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(14)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(18)));
        Assert.Equal(2400, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(20)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(22)));
        Assert.Equal(ScrcpyConstants.ButtonLeft, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(24)));
        Assert.Equal(ScrcpyConstants.ButtonLeft, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(28)));
    }

    [Fact]
    public void InjectTouchEvent_RejectsOutOfBoundsCoordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScrcpyControlMessageWriter.InjectTouchEvent(ScrcpyConstants.ActionDown, 0, x: 5, y: 0, width: 4, height: 4));
    }

    [Fact]
    public void InjectScrollEvent_Matches21ByteWireLayout()
    {
        var msg = ScrcpyControlMessageWriter.InjectScrollEvent(
            x: 10, y: 20, width: 1080, height: 2400,
            hScroll: 0f, vScroll: -1f, buttons: 0);

        Assert.Equal(21, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeInjectScrollEvent, msg[0]);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(1)));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(5)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(9)));
        Assert.Equal(2400, BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(11)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(13)));
        Assert.Equal(unchecked((short)-2048), BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(15)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(17)));
    }

    [Fact]
    public void BackOrScreenOn_TwoBytes()
    {
        var msg = ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionUp);

        Assert.Equal(2, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeBackOrScreenOn, msg[0]);
        Assert.Equal(ScrcpyConstants.ActionUp, msg[1]);
    }

    [Theory]
    [InlineData(ScrcpyConstants.ControlTypeExpandNotificationPanel)]
    [InlineData(ScrcpyConstants.ControlTypeExpandSettingsPanel)]
    [InlineData(ScrcpyConstants.ControlTypeCollapsePanels)]
    public void Simple_SingleByteMessages(byte type)
    {
        var msg = ScrcpyControlMessageWriter.Simple(type);

        Assert.Single(msg);
        Assert.Equal(type, msg[0]);
    }

    [Fact]
    public void Simple_RejectsMultiByteTypes()
    {
        Assert.Throws<ArgumentException>(() => ScrcpyControlMessageWriter.Simple(ScrcpyConstants.ControlTypeInjectKeyCode));
        Assert.Throws<ArgumentException>(() => ScrcpyControlMessageWriter.Simple(ScrcpyConstants.ControlTypeSetDisplayPower));
    }

    [Theory]
    [InlineData(true, ScrcpyConstants.DisplayPowerOn)]
    [InlineData(false, ScrcpyConstants.DisplayPowerOff)]
    public void SetDisplayPower_CarriesBooleanFlagByte(bool on, byte expectedFlag)
    {
        var msg = ScrcpyControlMessageWriter.SetDisplayPower(on);

        Assert.Equal(2, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeSetDisplayPower, msg[0]);
        Assert.Equal(expectedFlag, msg[1]);
    }

    [Fact]
    public void InjectScroll_OneTickIsScaledForTheFiveXReader()
    {
        var msg = ScrcpyControlMessageWriter.InjectScrollEvent(0, 0, 1080, 1920, 1f, -1f);

        Assert.Equal(21, msg.Length);
        Assert.Equal((short)0x0800, BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(13)));
        Assert.Equal((short)(-0x0800), BinaryPrimitives.ReadInt16BigEndian(msg.AsSpan(15)));
    }

    [Fact]
    public void SetClipboard_MatchesWireLayout()
    {
        var msg = ScrcpyControlMessageWriter.SetClipboard("abc", paste: true, sequence: 0x1234);

        Assert.Equal(14 + 3, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeSetClipboard, msg[0]);
        Assert.Equal(0x1234UL, BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(1)));
        Assert.Equal(1, msg[9]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(10)));
        Assert.Equal("abc", Encoding.UTF8.GetString(msg, 14, 3));
    }

    [Fact]
    public void GetClipboard_CarriesCopyKey()
    {
        var msg = ScrcpyControlMessageWriter.GetClipboard(ScrcpyConstants.CopyKeyCut);

        Assert.Equal(2, msg.Length);
        Assert.Equal(ScrcpyConstants.ControlTypeGetClipboard, msg[0]);
        Assert.Equal(ScrcpyConstants.CopyKeyCut, msg[1]);
    }

    [Fact]
    public void ResetVideo_IsOneByteCarryingServerTypeId17()
    {
        var msg = ScrcpyControlMessageWriter.ResetVideo();

        Assert.Single(msg);
        Assert.Equal((byte)17, msg[0]);
        Assert.Equal(ScrcpyConstants.ControlTypeResetVideo, msg[0]);
    }
}