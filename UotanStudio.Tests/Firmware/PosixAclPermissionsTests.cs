using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PosixAclPermissionsTests
{
    [Theory]
    [InlineData("0200000001000700FFFFFFFF02000600E803000004000700FFFFFFFF10000700FFFFFFFF20000700FFFFFFFF",
        "0200000001000600FFFFFFFF02000600E803000004000700FFFFFFFF10000400FFFFFFFF20000000FFFFFFFF")]
    [InlineData("010000000100070002000600E8030000040007001000070020000700",
        "010000000100060002000600E8030000040007001000040020000000")]
    public void Apply_ExtendedAcl_ChangesMaskAndPreservesNamedPermissions(string original, string expected)
    {
        var input = Convert.FromHexString(original);
        Assert.Equal(Convert.FromHexString(expected), PosixAclPermissions.Apply(input, 0x1A0));
        Assert.Equal(Convert.FromHexString(original), input);
    }

    [Theory]
    [InlineData("0200000001000700FFFFFFFF04000700FFFFFFFF20000700FFFFFFFF",
        "0200000001000600FFFFFFFF04000400FFFFFFFF20000000FFFFFFFF")]
    [InlineData("01000000010007000400070020000700", "01000000010006000400040020000000")]
    public void Apply_MinimalAcl_UpdatesGroupWithoutMask(string original, string expected)
    {
        Assert.Equal(Convert.FromHexString(expected), PosixAclPermissions.Apply(Convert.FromHexString(original), 0x1A0));
    }

    [Theory]
    [InlineData("0200000001000700")]
    [InlineData("03000000")]
    [InlineData("01000000010008000400070020000700")]
    [InlineData("0100000001000700010007000400070020000700")]
    [InlineData("010000000100070002000600E80300000400070020000700")]
    public void Apply_MalformedAcl_Rejects(string value)
    {
        Assert.Throws<InvalidDataException>(() => PosixAclPermissions.Apply(Convert.FromHexString(value), 0x1A0));
    }
}