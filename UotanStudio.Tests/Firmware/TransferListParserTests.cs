using UotanStudio.Firmware.OtaDelta.Dat;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Transfer list parser tests: verify valid v4 lists (commands, range sets, derived totals), stash-id
/// handling, and malformed-input rejection.
/// <para>Transfer list 解析器测试：校验合法 v4 列表（命令、范围集、派生总量）、stash id 处理以及非法输入拒绝。</para>
/// </summary>
public class TransferListParserTests
{
    [Fact]
    public void Parse_ValidV4List_ParsesCommandsAndRanges()
    {
        using var reader = new StringReader("4\n5\n0\n0\nnew 4,0,2,3,5\nzero 2,2,3\n");
        var list = TransferListParser.Parse(reader);

        Assert.Equal(4, list.Version);
        Assert.Equal(5, list.NewBlocks);
        Assert.Equal(0, list.StashEntries);
        Assert.Equal(0, list.MaxStashedBlocks);
        Assert.Equal(2, list.Commands.Count);

        var newCommand = list.Commands[0];
        Assert.Equal(TransferListCommandKind.New, newCommand.Kind);
        Assert.Equal(new[] { new BlockRange(0, 2), new BlockRange(3, 5) }, newCommand.Ranges);

        var zeroCommand = list.Commands[1];
        Assert.Equal(TransferListCommandKind.Zero, zeroCommand.Kind);
        Assert.Equal(new[] { new BlockRange(2, 3) }, zeroCommand.Ranges);

        Assert.Equal(5, list.TotalBlocks);
        Assert.Equal(4, list.NewDataBlocks);
    }

    [Fact]
    public void Parse_StashCommand_WithId_Parses()
    {
        using var reader = new StringReader("2\n2\n1\n2\nstash abc123 2,0,2\nmove 2,2,4 2 - abc123:2,0,2\n");
        var list = TransferListParser.Parse(reader);

        Assert.Equal(1, list.StashEntries);
        Assert.Equal(2, list.MaxStashedBlocks);
        Assert.Equal(2, list.Commands.Count);

        var stash = list.Commands[0];
        Assert.Equal(TransferListCommandKind.Stash, stash.Kind);
        Assert.Equal("abc123", stash.StashId);
        Assert.Equal(new[] { new BlockRange(0, 2) }, stash.Ranges);

        var diff = list.Commands[1];
        Assert.Equal(TransferListCommandKind.Move, diff.Kind);
        Assert.Equal(new[] { new BlockRange(2, 4) }, diff.Ranges);
    }

    [Fact]
    public void Parse_InvalidVersion_Throws()
    {
        using var reader = new StringReader("0\n5\n");
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(reader));
    }

    [Fact]
    public void Parse_InvalidRangeSet_Throws()
    {
        using var reader = new StringReader("4\n5\n0\n0\nnew 2,0,2,3\n");
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(reader));
    }

    [Fact]
    public void Parse_UnknownCommand_Throws()
    {
        using var reader = new StringReader("4\n5\n0\n0\nbogus 1,0,2\n");
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(reader));
    }

    [Fact]
    public void Parse_TooShort_Throws()
    {
        using var reader = new StringReader("4\n");
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(reader));
    }

    [Fact]
    public void Parse_InvalidNewBlocksCount_Throws()
    {
        using var reader = new StringReader("4\n-1\n0\n0\n");
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(reader));
    }
}