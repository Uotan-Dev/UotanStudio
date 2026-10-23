using System.Buffers.Binary;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class ScrcpyToOhscrcpyControlAdapterTests
{
    [Fact]
    public void TryTranslate_TouchDown_ReturnsTouchGestureWithinFrame()
    {
        var message = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionDown, 1, 100, 200, 640, 480);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var action));
        Assert.Equal(OhscrcpyControlActionKind.Gesture, action.Kind);
        Assert.Equal("touchDown", action.Gesture);
        Assert.Equal(100, action.X);
        Assert.Equal(200, action.Y);
    }

    [Fact]
    public void TryTranslate_TouchCoordinatesOutsideFrame_ClampsToLastPixel()
    {
        var message = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionMove, 1, 639, 479, 640, 480);
        var outOfRange = new byte[32];
        outOfRange[0] = ScrcpyConstants.ControlTypeInjectTouchEvent;
        outOfRange[1] = ScrcpyConstants.ActionMove;
        BinaryPrimitives.WriteUInt64BigEndian(outOfRange.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(outOfRange.AsSpan(10), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(outOfRange.AsSpan(14), 900);
        BinaryPrimitives.WriteUInt16BigEndian(outOfRange.AsSpan(18), 640);
        BinaryPrimitives.WriteUInt16BigEndian(outOfRange.AsSpan(20), 480);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var inside));
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(outOfRange, out var clamped));
        Assert.Equal((639, 479), (inside.X, inside.Y));
        Assert.Equal((639, 479), (clamped.X, clamped.Y));
        Assert.Equal("touchMove", clamped.Gesture);
    }

    [Theory]
    [InlineData((short)1, OhscrcpyControlActionKind.ScrollUp)]
    [InlineData((short)-1, OhscrcpyControlActionKind.ScrollDown)]
    public void TryTranslate_ScrollSign_ReturnsMatchingAxisStep(short ticks, OhscrcpyControlActionKind expected)
    {
        var message = ScrcpyControlMessageWriter.InjectScrollEvent(10, 20, 640, 480, 0, ticks);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var action));
        Assert.Equal(expected, action.Kind);
        Assert.Equal((10, 20), (action.X, action.Y));
    }

    [Fact]
    public void TryTranslate_ScrollWithoutVerticalDelta_ReturnsFalse()
    {
        var message = ScrcpyControlMessageWriter.InjectScrollEvent(10, 20, 640, 480, 0, 0);

        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out _));
    }

    [Fact]
    public void TryTranslate_DedicatedKey_CollapsesDownAndDropsUp()
    {
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, 4, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, 4, 0, 0);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var press));
        Assert.Equal(OhscrcpyControlActionKind.KeyPress, press.Kind);
        Assert.Equal(OhscrcpyConstants.KeyCodeBack, press.KeyCode);
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out _));
    }

    [Theory]
    [InlineData(3, OhscrcpyConstants.KeyCodeHome)]
    [InlineData(4, OhscrcpyConstants.KeyCodeBack)]
    [InlineData(24, OhscrcpyConstants.KeyCodeVolumeUp)]
    [InlineData(25, OhscrcpyConstants.KeyCodeVolumeDown)]
    [InlineData(26, OhscrcpyConstants.KeyCodePower)]
    public void TryTranslate_HomeBackVolumePower_ReturnOneDedicatedPress(int androidKeyCode, int expected)
    {
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, androidKeyCode, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, androidKeyCode, 0, 0);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var press));
        Assert.Equal(OhscrcpyControlActionKind.KeyPress, press.Kind);
        Assert.Equal(expected, press.KeyCode);
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out _));
    }

    [Fact]
    public void TryTranslate_AppSwitch_CollapsesDownIntoRecentTasksChordAndDropsUp()
    {
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, 187, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, 187, 0, 0);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var chord));
        Assert.Equal(OhscrcpyControlActionKind.KeyChord, chord.Kind);
        Assert.Equal(2076, OhscrcpyConstants.KeyCodeMetaLeft);
        Assert.Equal(2049, OhscrcpyConstants.KeyCodeTab);
        Assert.Equal(OhscrcpyConstants.KeyCodeMetaLeft, chord.KeyCode);
        Assert.Equal(OhscrcpyConstants.KeyCodeTab, chord.SecondaryKeyCode);
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out var released));
        Assert.Equal(OhscrcpyControlActionKind.None, released.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(213)]
    [InlineData(1000)]
    public void TryTranslate_UnknownKeyCode_ProducesNoAction(int androidKeyCode)
    {
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, androidKeyCode, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, androidKeyCode, 0, 0);

        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var pressed));
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out var released));
        Assert.Equal(OhscrcpyControlActionKind.None, pressed.Kind);
        Assert.Equal(OhscrcpyControlActionKind.None, released.Kind);
    }

    [Fact]
    public void TryTranslate_GenericKey_MapsAndroidCodeAndKeepsDownUp()
    {
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, 29, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, 29, 0, 0);

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(down, out var pressed));
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(up, out var released));
        Assert.Equal(OhscrcpyControlActionKind.KeyDown, pressed.Kind);
        Assert.Equal(OhscrcpyControlActionKind.KeyUp, released.Kind);
        Assert.Equal(2017, pressed.KeyCode);
        Assert.Equal(2017, released.KeyCode);
    }

    [Fact]
    public void TryTranslate_Text_ReturnsTextAction()
    {
        var message = ScrcpyControlMessageWriter.InjectText("你好");

        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(message, out var action));
        Assert.Equal(OhscrcpyControlActionKind.Text, action.Kind);
        Assert.Equal("你好", action.Text);
    }

    [Fact]
    public void TryTranslate_ResetVideo_RequestsKeyFrame()
    {
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(
            ScrcpyControlMessageWriter.ResetVideo(), out var action));
        Assert.Equal(OhscrcpyControlActionKind.RequestKeyFrame, action.Kind);
    }

    [Fact]
    public void TryTranslate_BackOrScreenOnAndDisplayPower_ReturnDedicatedPresses()
    {
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(
            ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionDown), out var back));
        Assert.True(ScrcpyToOhscrcpyControlAdapter.TryTranslate(
            ScrcpyControlMessageWriter.SetDisplayPower(false), out var power));
        Assert.Equal(OhscrcpyConstants.KeyCodeBack, back.KeyCode);
        Assert.Equal(OhscrcpyConstants.KeyCodePower, power.KeyCode);
    }

    [Fact]
    public void TryTranslate_ClipboardMessage_HasNoOpenHarmonyEquivalent()
    {
        Assert.False(ScrcpyToOhscrcpyControlAdapter.TryTranslate(
            ScrcpyControlMessageWriter.SetClipboard("text", paste: false), out var action));
        Assert.Equal(OhscrcpyControlActionKind.None, action.Kind);
    }

    [Theory]
    [InlineData(29, 2017)]
    [InlineData(7, 2000)]
    [InlineData(131, 2090)]
    [InlineData(66, 2054)]
    [InlineData(1000, 0)]
    public void TryMapKeyCode_AndroidCode_ReturnsMappedOpenHarmonyCode(
        int androidKeyCode, int expected)
    {
        var mapped = ScrcpyToOhscrcpyControlAdapter.TryMapKeyCode(androidKeyCode, out var ohKeyCode);

        Assert.Equal(expected != 0, mapped);
        Assert.Equal(expected, ohKeyCode);
    }
}