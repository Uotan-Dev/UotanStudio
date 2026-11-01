using UotanStudio.Comm.Protocols.Bootrom;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class RkdlStorageTableTests
{
    [Fact]
    public void WireBits_MatchTheOfficialChangeStorageTable()
    {
        Assert.Equal(
            [0, 1, 2, 3, 9, 8, 6, 7, 10, 11, 12, 13],
            RkdlStorage.WireBits);
        Assert.Equal(12, RkdlStorage.Names.Length);
        Assert.Equal("FLASH", RkdlStorage.Names[0]);
        Assert.Equal("EMMC", RkdlStorage.Names[1]);
        Assert.Equal("SPINOR", RkdlStorage.Names[4]);
        Assert.Equal("RVD", RkdlStorage.Names[^1]);
    }

    [Theory]
    [InlineData(0x0001, 0)]
    [InlineData(0x0002, 1)]
    [InlineData(0x0004, 2)]
    [InlineData(0x0008, 3)]
    [InlineData(0x0200, 4)]
    [InlineData(0x0100, 5)]
    [InlineData(0x0040, 6)]
    [InlineData(0x0080, 7)]
    [InlineData(0x0400, 8)]
    [InlineData(0x0800, 9)]
    [InlineData(0x1000, 10)]
    [InlineData(0x2000, 11)]
    public void IndexOfMask_SingleBitMatch_ReturnsTheOfficialEntry(uint mask, int expected)
    {
        Assert.Equal(expected, RkdlStorage.IndexOfMask(mask));
        Assert.Equal(RkdlStorage.Names[expected], RkdlStorage.DescribeMask(mask));
    }

    [Theory]
    [InlineData(0x0003u)]
    [InlineData(0x0000u)]
    [InlineData(0x0010u)]
    [InlineData(0x80000000u)]
    public void IndexOfMask_MultiBitOrForeignMasks_Unmatched(uint mask)
    {
        Assert.Equal(-1, RkdlStorage.IndexOfMask(mask));
        Assert.Equal($"0x{mask:X8}", RkdlStorage.DescribeMask(mask));
    }

    [Fact]
    public void CapabilityDescribe_ReportsOfficialBitsInTheOfficialOrder()
    {
        var capability = RkCapability.Decode([0x3F, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        var lines = capability.Describe();

        Assert.Equal(
            [
                "DirectLBA=Enable",
                "VendorStorage=Enable",
                "First4M=Enable",
                "ReadLBA=Enable",
                "ReadComLog=Enable",
                "ReadIDBConfig=Disable",
                "ReadSecureMode=Disable",
                "NewIDB=Enable",
                "SwitchStorage=Enable",
                "LBAparity=Enable",
                "ReadOTPchip=Disable",
                "SwitchUsb3=Disable",
                "Bit4=Enable"
            ],
            lines);
    }

    [Fact]
    public void CapabilityDescribe_AllClear_ReportsEveryDisabledBit()
    {
        var capability = RkCapability.Decode(new byte[8]);

        Assert.DoesNotContain(capability.Describe(), line => line.EndsWith("Enable", StringComparison.Ordinal));
        Assert.Equal(12, capability.Describe().Count);
    }
}