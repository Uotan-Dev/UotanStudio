using UotanStudio.Firmware.OtaDelta.Dat;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Transfer-list serialization tests: the serializer is exercised as the exact inverse of
/// <see cref="TransferListParser"/> through golden-text emission and parse/serialize/reparse round-trips.
/// <para>传输列表序列化测试：通过规范文本发射与 解析/序列化/再解析 往返，将序列化器作为
/// <see cref="TransferListParser"/> 的精确逆运算进行验证。</para>
/// </summary>
public class TransferListSerializerTests
{
    private const string H = "0123456789abcdef0123456789abcdef01234567";
    private const string T = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("4\n2\n0\n0\nnew 4,0,1,2,3\n", "4\n2\n0\n0\nnew 4,0,1,2,3\n")]
    [InlineData("4\n2\n0\n0\nzero 2,1,2\n", "4\n2\n0\n0\nzero 2,1,2\n")]
    [InlineData("4\n2\n0\n0\nerase 2,1,2\n", "4\n2\n0\n0\nerase 2,1,2\n")]
    [InlineData("4\n1\n1\n1\nstash " + H + " 2,0,1\n", "4\n1\n1\n1\nstash " + H + " 2,0,1\n")]
    [InlineData("4\n1\n1\n1\nstash " + H + " 2,0,1\nfree " + H + "\n", "4\n1\n1\n1\nstash " + H + " 2,0,1\nfree " + H + "\n")]
    public void Serialize_EmitsGoldenText(string input, string expected)
    {
        Assert.Equal(expected, TransferListSerializer.Serialize(TransferListParser.Parse(new StringReader(input))));
    }

    [Fact]
    public void Serialize_Move_EmitsSingleMirroredHash()
    {
        var list = TransferListParser.Parse(new StringReader($"3\n1\n0\n0\nmove {H} 2,2,3 1 2,0,1 2,0,1\n"));
        Assert.Equal($"3\n1\n0\n0\nmove {H} 2,2,3 1 2,0,1 2,0,1\n", TransferListSerializer.Serialize(list));
    }

    [Fact]
    public void Serialize_MoveWithDistinctHashes_Throws()
    {
        var command = new TransferListCommand(TransferListCommandKind.Move, [new BlockRange(2, 3)], sourceRanges: [new BlockRange(0, 1)])
        {
            SourceBlockCount = 1,
            SourceLocations = [new BlockRange(0, 1)],
            SourceHash = H,
            TargetHash = T,
        };
        var list = new TransferList(3, 1, 0, 0, [command]);
        Assert.Throws<InvalidDataException>(() => TransferListSerializer.Serialize(list));
    }

    [Fact]
    public void Serialize_Bsdiff_EmitsOffsetLengthAndSeparateTargetHash()
    {
        var list = TransferListParser.Parse(new StringReader($"4\n2\n0\n0\nbsdiff 37 100 {H} {T} 4,4,5,6,7 2 4,0,1,2,3\n"));
        Assert.Equal($"4\n2\n0\n0\nbsdiff 37 100 {H} {T} 4,4,5,6,7 2 4,0,1,2,3 2,0,2\n", TransferListSerializer.Serialize(list));
    }

    [Fact]
    public void Serialize_ImgdiffWithStash_EmitsStashReferenceAndFree()
    {
        var stash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var input = $"4\n3\n1\n1\nstash {stash} 2,1,2\nimgdiff 0 20 {H} {T} 2,3,5 2 2,0,1 2,0,1 {stash}:2,1,2\nfree {stash}\n";
        var list = TransferListParser.Parse(new StringReader(input));
        Assert.Equal(input, TransferListSerializer.Serialize(list));
    }

    [Theory]
    [InlineData("1\n1\nnew 4,0,1,2,3\n")]
    [InlineData("1\n1\nmove 2,0,1 2,1,2\n")]
    [InlineData("2\n1\n0\n0\nmove 2,2,3 1 2,0,1\n")]
    [InlineData($"3\n1\n0\n0\nmove {H} 2,2,3 1 2,0,1 2,0,1\n")]
    [InlineData($"4\n1\n0\n0\nmove {H} 2,2,3 1 2,0,1\n")]
    [InlineData("4\n2\n0\n0\nnew 4,0,1,2,3\n")]
    [InlineData("4\n2\n0\n0\nzero 2,1,2\n")]
    [InlineData($"4\n1\n1\n1\nstash {H} 2,0,1\n")]
    [InlineData($"4\n1\n1\n1\nstash {H} 2,0,1\nfree {H}\n")]
    [InlineData($"4\n2\n0\n0\nbsdiff 37 100 {H} {T} 4,4,5,6,7 2 4,0,1,2,3\n")]
    [InlineData($"4\n2\n0\n0\nimgdiff 37 100 {H} {T} 4,4,5,6,7 2 4,0,1,2,3\n")]
    public void Serialize_ThenReparse_PreservesCommandShape(string input)
    {
        var first = TransferListParser.Parse(new StringReader(input));
        var roundTripped = TransferListParser.Parse(new StringReader(TransferListSerializer.Serialize(first)));

        Assert.Equal(first.Version, roundTripped.Version);
        Assert.Equal(first.NewBlocks, roundTripped.NewBlocks);
        Assert.Equal(first.StashEntries, roundTripped.StashEntries);
        Assert.Equal(first.MaxStashedBlocks, roundTripped.MaxStashedBlocks);
        Assert.Equal(first.TotalBlocks, roundTripped.TotalBlocks);
        Assert.Equal(first.NewDataBlocks, roundTripped.NewDataBlocks);
        Assert.Equal(first.Commands.Count, roundTripped.Commands.Count);
        for (var i = 0; i < first.Commands.Count; i++)
        {
            AssertCommandsEqual(first.Commands[i], roundTripped.Commands[i]);
        }
    }

    [Fact]
    public void Serialize_InvalidVersion_Throws()
    {
        var commands = new[] { new TransferListCommand(TransferListCommandKind.New, [new BlockRange(0, 1)]) };
        var bad = new TransferList(5, 1, 0, 0, commands);
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferListSerializer.Serialize(bad));
    }

    private static void AssertCommandsEqual(TransferListCommand expected, TransferListCommand actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Ranges, actual.Ranges);
        Assert.Equal(expected.StashId, actual.StashId);
        Assert.Equal(expected.SourceRanges, actual.SourceRanges);
        Assert.Equal(expected.SourceLocations, actual.SourceLocations);
        Assert.Equal(expected.SourceBlockCount, actual.SourceBlockCount);
        Assert.Equal(expected.PatchOffset, actual.PatchOffset);
        Assert.Equal(expected.PatchLength, actual.PatchLength);
        Assert.Equal(expected.SourceHash, actual.SourceHash);
        Assert.Equal(expected.TargetHash, actual.TargetHash);
        Assert.Equal(expected.StashReferences.Count, actual.StashReferences.Count);
        foreach (var (id, ranges) in expected.StashReferences)
        {
            Assert.True(actual.StashReferences.TryGetValue(id, out var mapped));
            Assert.Equal(ranges, mapped);
        }
    }
}