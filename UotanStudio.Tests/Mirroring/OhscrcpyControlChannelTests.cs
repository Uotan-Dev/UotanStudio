using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class OhscrcpyControlChannelTests
{
    [Fact]
    public void BuildKeyChordCommand_RecentTasksChord_HoldsBothKeysInOneUinputCall()
    {
        var command = OhscrcpyControlChannel.BuildKeyChordCommand(
            OhscrcpyConstants.KeyCodeMetaLeft, OhscrcpyConstants.KeyCodeTab);

        Assert.Equal("uinput -K -d 2076 -d 2049 -u 2076 -u 2049", command);
    }
}