using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Cases for the parameter-table special cases the vendor tools implement: grow entries, bare
/// <c>mtdparts</c> lines, PARM-wrapped text, name matching with grow suffixes, and the
/// <c>parameter</c> target classification.
/// <para>厂商工具实现的分区表特判用例：grow 条目、裸 <c>mtdparts</c> 行、PARM 封装文本、带 grow 后缀的
/// 名称匹配，以及 <c>parameter</c> 目标的分类。</para>
/// </summary>
public sealed class RockchipParameterSpecialCaseTests
{
    [Fact]
    public void ParseText_GrowSizeEntry_ReportsVendorSentinel()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),-@0x00006000(userdata:grow)");

        var grow = Assert.Single(partitions, p => p.Name == "userdata:grow");
        Assert.Equal(RockchipParameterParser.GrowSectorCount, grow.SizeSectors);
        Assert.True(grow.IsGrow);
        Assert.Equal("userdata", grow.BareName);
    }

    [Fact]
    public void ParseText_BareMtdpartsLine_ParsesWithoutCmdlinePrefix()
    {
        var partitions = RockchipParameterParser.ParseText(
            "mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)\n# a comment\nuuid:rootfs=614e0000-0000-4b53-8000-1d28000054a9\n");

        var entry = Assert.Single(partitions);
        Assert.Equal("uboot", entry.Name);
        Assert.Equal(0x4000, entry.OffsetSectors);
        Assert.Equal(0x2000, entry.SizeSectors);
    }

    [Fact]
    public void ParseBytes_ParmWrappedText_IsUnwrappedBeforeParsing()
    {
        var payload = "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)"u8.ToArray();
        var wrapped = RockchipParmContainer.Wrap(payload);

        var partitions = RockchipParameterParser.ParseBytes(wrapped);

        var entry = Assert.Single(partitions);
        Assert.Equal("uboot", entry.Name);
        Assert.Equal(0x4000, entry.OffsetSectors);
    }

    [Fact]
    public void ParseText_TrailingColonSuffix_StopsBeforeTheSuffix()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot):grow");

        var entry = Assert.Single(partitions);
        Assert.Equal("uboot", entry.Name);
    }

    [Fact]
    public void ParseText_MissingMarker_IsRejected()
    {
        Assert.Throws<InvalidDataException>(
            () => RockchipParameterParser.ParseText("FIRMWARE_VER: 8.1\nMACHINE_MODEL: RK3288\n"));
    }

    [Fact]
    public void ParseText_EmptySegmentsBetweenCommas_AreSkipped()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),,0x00002000@0x00006000(misc)");

        Assert.Equal(2, partitions.Count);
        Assert.Equal(["uboot", "misc"], partitions.Select(p => p.Name));
    }

    [Fact]
    public void IsParameterTarget_NameAndFileVariants_MatchTheVendorSpecialCase()
    {
        Assert.True(FlashService.IsParameterTarget("parameter", "boot.img", "boot"));
        Assert.True(FlashService.IsParameterTarget("boot", "parameter", "parameter"));
        Assert.True(FlashService.IsParameterTarget("boot", "parameter.txt", "parameter"));
        Assert.False(FlashService.IsParameterTarget("ParameterX", "boot.img", "boot"));
        Assert.False(FlashService.IsParameterTarget("boot", "parameter.xml", "parameter.xml"));
    }

    [Fact]
    public void ParseText_KSizeSuffix_ConvertsToSectors()
    {
        var partitions = RockchipParameterParser.ParseText(
            "CMDLINE: mtdparts=rk29xxnand:1M@0x4000(boot),256K@0x8000(misc)");

        Assert.Equal(0x4000, partitions[0].OffsetSectors);
        Assert.Equal(2048, partitions[0].SizeSectors);
        Assert.Equal(512, partitions[1].SizeSectors);
    }
}