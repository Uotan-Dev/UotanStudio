using System.Security.Cryptography;
using BsDiff;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DatIncrementalTests
{
    private const int BlockSize = DatReader.BlockSize;

    [Fact]
    public void Parse_V4Patch_SeparatesTargetAndSourceMapping()
    {
        var hash = new string('0', 40);
        using var input = new StringReader($"4\n2\n0\n0\nbsdiff 37 100 {hash} {hash} 4,4,5,6,7 2 4,0,1,2,3\n");
        var list = TransferListParser.Parse(input);
        var command = Assert.Single(list.Commands);
        Assert.Equal(37, command.PatchOffset);
        Assert.Equal(100, command.PatchLength);
        Assert.Equal(2, command.SourceBlockCount);
        Assert.Equal(new[] { new BlockRange(0, 1), new BlockRange(2, 3) }, command.SourceRanges);
        Assert.Equal(new[] { new BlockRange(4, 5), new BlockRange(6, 7) }, command.Ranges);
        Assert.Equal(7, list.TotalBlocks);
        Assert.Equal(2, list.NewBlocks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Move_AllRecoveryVersions_PreserveUntouchedBlocks(int version)
    {
        var original = Pattern(3, 7);
        var moved = original.AsSpan(0, BlockSize).ToArray();
        var command = version switch
        {
            1 => "move 2,0,1 2,2,3",
            2 => "move 2,2,3 1 2,0,1",
            _ => $"move {Hash(moved)} 2,2,3 1 2,0,1"
        };
        var header = version == 1 ? "1\n1\n" : $"{version}\n1\n0\n0\n";
        var expected = original.ToArray(); moved.CopyTo(expected, 2 * BlockSize);
        Assert.Equal(expected, Run(header + command, original));
    }

    [Fact]
    public void Move_SourceAndDestinationMayBeTheSameFile()
    {
        using var temp = new TempDirectory(); var original = Pattern(2, 9);
        var image = temp.WriteBytes("image.img", original);
        var list = temp.Write("list", "1\n1\nmove 2,0,1 2,1,2\n");
        DatReader.Unpack(list, temp.WriteBytes("new.dat", []), null, image, image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(original.AsSpan(0, BlockSize).ToArray().Concat(original.AsSpan(0, BlockSize).ToArray()), File.ReadAllBytes(image));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void Stash_OverwrittenSource_FreeDoesNotEraseImageBlocks()
    {
        var original = Pattern(4, 9); var saved = original.AsSpan(0, BlockSize).ToArray();
        var replacement = Pattern(1, 3); var id = Hash(saved);
        var list = $"4\n2\n1\n1\nstash {id} 2,0,1\nnew 2,0,1\nmove {id} 2,2,3 1 - {id}:2,0,1\nfree {id}\n";
        var expected = original.ToArray(); replacement.CopyTo(expected, 0); saved.CopyTo(expected, 2 * BlockSize);
        Assert.Equal(expected, Run(list, original, replacement));
    }

    [Fact]
    public void Move_CombinesDiskRangesWithStashedBufferLocations()
    {
        var original = Pattern(5, 13); var saved = original.AsSpan(BlockSize, BlockSize).ToArray();
        var id = Hash(saved); var assembled = original.AsSpan(0, 2 * BlockSize).ToArray();
        var list = $"4\n3\n1\n1\nstash {id} 2,1,2\nzero 2,1,2\nmove {Hash(assembled)} 2,3,5 2 2,0,1 2,0,1 {id}:2,1,2\nfree {id}\n";
        var expected = original.ToArray(); Array.Clear(expected, BlockSize, BlockSize); assembled.CopyTo(expected, 3 * BlockSize);
        Assert.Equal(expected, Run(list, original));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Bsdiff_ExplicitOffset_MapsOnePatchAcrossAllRanges(int version)
    {
        var original = Pattern(8, 11); var replacement = Pattern(2, 17);
        var oldData = original.AsSpan(0, BlockSize).ToArray().Concat(original.AsSpan(2 * BlockSize, BlockSize).ToArray()).ToArray();
        var patch = Patch(oldData, replacement); var patchFile = new byte[37].Concat(patch).Concat(new byte[19]).ToArray();
        var list = $"{version}\n2\n0\n0\nbsdiff 37 {patch.Length} {Hash(oldData)} {Hash(replacement)} 4,4,5,6,7 2 4,0,1,2,3\n";
        var expected = original.ToArray(); replacement.AsSpan(0, BlockSize).CopyTo(expected.AsSpan(4 * BlockSize)); replacement.AsSpan(BlockSize).CopyTo(expected.AsSpan(6 * BlockSize));
        Assert.Equal(expected, Run(list, original, patch: patchFile));
    }

    [Fact]
    public void BadTargetHash_PreservesExistingDestination()
    {
        var original = Pattern(1, 1); var changed = Pattern(1, 2); var patch = Patch(original, changed);
        var list = $"4\n1\n0\n0\nbsdiff 0 {patch.Length} {Hash(original)} {Hash(original)} 2,0,1 1 2,0,1\n";
        using var temp = new TempDirectory();
        var output = temp.WriteBytes("out.img", "existing"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => DatReader.Unpack(temp.Write("list", list), temp.WriteBytes("new.dat", []), temp.WriteBytes("patch.dat", patch), temp.WriteBytes("source.img", original), output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void TruncatedNewData_PreservesExistingDestination()
    {
        using var temp = new TempDirectory(); var output = temp.WriteBytes("out.img", "existing"u8.ToArray());
        Assert.Throws<EndOfStreamException>(() => DatReader.Unpack(temp.Write("list", "4\n1\n0\n0\nnew 2,0,1\n"), temp.WriteBytes("new.dat", new byte[BlockSize - 1]), output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public void StashLimit_IsEnforced()
    {
        var original = Pattern(1, 7);
        Assert.Throws<InvalidDataException>(() => Run($"4\n1\n1\n0\nstash {Hash(original)} 2,0,1\n", original));
    }

    [Theory]
    [InlineData("diff 2,0,1")]
    [InlineData("new 1,0,1")]
    [InlineData("move 0000000000000000000000000000000000000000 2,0,1 2 2,0,1")]
    public void NonstandardOrIncompleteCommands_AreRejected(string command)
    {
        using var input = new StringReader("4\n1\n0\n0\n" + command);
        Assert.Throws<InvalidDataException>(() => TransferListParser.Parse(input));
    }

    private static byte[] Run(string list, byte[] original, byte[]? added = null, byte[]? patch = null)
    {
        using var temp = new TempDirectory(); var output = Path.Combine(temp.Path, "out.img");
        DatReader.Unpack(temp.Write("list", list), temp.WriteBytes("new.dat", added ?? []), patch is null ? null : temp.WriteBytes("patch.dat", patch), temp.WriteBytes("source.img", original), output);
        return File.ReadAllBytes(output);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static byte[] Pattern(int blocks, int seed) { var bytes = new byte[blocks * BlockSize]; new Random(seed).NextBytes(bytes); return bytes; }
    private static byte[] Patch(byte[] original, byte[] target) { using var stream = new MemoryStream(); BinaryPatch.Create(original, target, stream); return stream.ToArray(); }
}