using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DatWriterDiffModeTests
{
    private const int Block = 4096;

    [Theory]
    [InlineData(DatIncrementalCommandStyle.Imgdiff)]
    [InlineData(DatIncrementalCommandStyle.Bsdiff)]
    [InlineData(DatIncrementalCommandStyle.ShortestWins)]
    public void WriteIncremental_EveryCommandStyle_RebuildsTargetByteExact(DatIncrementalCommandStyle style)
    {
        var source = Concat(BlockBytes(0xAA), BlockBytes(0xBB), new byte[Block], BlockBytes(0xCC));
        var target = Concat(BlockBytes(0xAA), RandomBlock(1), new byte[Block], new byte[Block], RandomBlock(2), BlockBytes(0xDD));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions { IncrementalCommandStyle = style });
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_BsdiffStyle_EmitsBsdiffCommandsAndNoImgdiff()
    {
        var source = Concat(BlockBytes(0x11), BlockBytes(0x22));
        var target = Concat(BlockBytes(0x11), BlockBytes(0x33));
        using var temp = new TempDirectory();
        var (list, _, _) = Run(temp, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.Bsdiff });
        var text = File.ReadAllText(list);
        Assert.Contains("bsdiff ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("imgdiff ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIncremental_ShortestWins_PrefersBareBsdiffForBlockAlignedRuns()
    {
        var source = Concat(BlockBytes(0x44), BlockBytes(0x55));
        var target = Concat(BlockBytes(0x44), BlockBytes(0x66));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.ShortestWins });
        var text = File.ReadAllText(list);
        Assert.Contains("bsdiff ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("imgdiff ", text, StringComparison.Ordinal);
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_AllowRawNewCommands_EmitsNewForRunsBiggerThanTheirPatch()
    {
        var source = Concat(BlockBytes(0x11), BlockBytes(0x11), RandomBlock(5));
        var target = Concat(BlockBytes(0x12), BlockBytes(0x12), RandomBlock(5), RandomBlock(6));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions { AllowRawNewCommands = true });
        var text = File.ReadAllText(list);
        Assert.Contains("new ", text, StringComparison.Ordinal);
        Assert.Contains("imgdiff ", text, StringComparison.Ordinal);
        Assert.True(new FileInfo(dat).Length > 0);
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_AllowRawNewCommands_WithoutRemainingDiffsFallsBackToFullImage()
    {
        var source = Concat(RandomBlock(5), BlockBytes(0x77));
        var target = Concat(RandomBlock(5), BlockBytes(0x77), RandomBlock(6));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions { AllowRawNewCommands = true });
        var text = File.ReadAllText(list);
        Assert.Contains("new ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("imgdiff ", text, StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(patch).Length);
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_RawNewDisabled_KeepsLegacyImgdiffOnlyOutput()
    {
        var source = Concat(RandomBlock(5), BlockBytes(0x77));
        var target = Concat(RandomBlock(5), BlockBytes(0x77), RandomBlock(6));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions());
        var text = File.ReadAllText(list);
        Assert.Contains("imgdiff ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new ", text, StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(dat).Length);
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_CancellationRequested_ThrowsBeforeCreatingOutputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("src.img", RandomBlock(1));
        var targetPath = temp.WriteBytes("tgt.img", RandomBlock(2));
        var dat = Path.Combine(temp.Path, "t.new.dat");
        var patch = Path.Combine(temp.Path, "t.patch.dat");
        var list = Path.Combine(temp.Path, "t.transfer.list");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DatWriter.WriteIncremental(sourcePath, targetPath, dat, patch, list, new DatWriteOptions(), null, cancellation.Token));
        Assert.False(File.Exists(list));
        Assert.False(File.Exists(dat));
    }

    [Fact]
    public void WriteIncremental_OptionsDisableSelfVerify_StillProducesApplicableArtifacts()
    {
        var source = Concat(BlockBytes(0x12), BlockBytes(0x34));
        var target = Concat(BlockBytes(0x12), BlockBytes(0x56));
        using var temp = new TempDirectory();
        var (list, dat, patch) = Run(temp, source, target, new DatWriteOptions { SelfVerifyIncremental = false });
        AssertRebuilds(temp, source, target, list, dat, patch);
    }

    [Fact]
    public void WriteIncremental_ShortestWins_EqualLengthImagesMatchBsdiffStyleByteForByte()
    {
        var source = Concat(BlockBytes(0xA1), BlockBytes(0xB2), new byte[Block], BlockBytes(0xC3), RandomBlock(7), BlockBytes(0xD4));
        var target = Concat(BlockBytes(0xA1), RandomBlock(1), new byte[Block], RandomBlock(2), RandomBlock(8), BlockBytes(0xD4));
        using var shortest = new TempDirectory();
        using var bsdiff = new TempDirectory();
        var shortestArtifacts = Run(shortest, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.ShortestWins });
        var bsdiffArtifacts = Run(bsdiff, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.Bsdiff });
        AssertArtifactsEqual(shortestArtifacts, bsdiffArtifacts);
        AssertRebuilds(shortest, source, target, shortestArtifacts.List, shortestArtifacts.Dat, shortestArtifacts.Patch);
    }

    [Fact]
    public void WriteIncremental_ShortestWins_TargetLongerThanSourceMatchesBsdiffStyleByteForByte()
    {
        var source = Concat(BlockBytes(0x31), BlockBytes(0x42), RandomBlock(3));
        var target = Concat(BlockBytes(0x31), RandomBlock(4), RandomBlock(5), BlockBytes(0x99), BlockBytes(0xAA));
        using var shortest = new TempDirectory();
        using var bsdiff = new TempDirectory();
        var shortestArtifacts = Run(shortest, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.ShortestWins });
        var bsdiffArtifacts = Run(bsdiff, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.Bsdiff });
        AssertArtifactsEqual(shortestArtifacts, bsdiffArtifacts);
        AssertRebuilds(shortest, source, target, shortestArtifacts.List, shortestArtifacts.Dat, shortestArtifacts.Patch);
    }

    [Fact]
    public void WriteIncremental_ImgdiffStyle_PrependsOnlyTheContainerHeaderToTheBarePatch()
    {
        var source = Concat(BlockBytes(0x21), BlockBytes(0x31), BlockBytes(0x41));
        var target = Concat(BlockBytes(0x21), BlockBytes(0x39), BlockBytes(0x41));
        using var imgdiff = new TempDirectory();
        using var bsdiff = new TempDirectory();
        var imgdiffArtifacts = Run(imgdiff, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.Imgdiff });
        var bsdiffArtifacts = Run(bsdiff, source, target, new DatWriteOptions { IncrementalCommandStyle = DatIncrementalCommandStyle.Bsdiff });
        var container = File.ReadAllBytes(imgdiffArtifacts.Patch);
        var bare = File.ReadAllBytes(bsdiffArtifacts.Patch);
        Assert.Equal("IMGDIFF2"u8.ToArray(), container[..8]);
        Assert.Equal(bare, container[40..]);
        Assert.Equal(bare.Length + 40, container.Length);
    }

    [Fact]
    public void WriteIncremental_InterleavedRuns_EmitsCommandsInBlockOrder()
    {
        var source = Concat(BlockBytes(0x51), BlockBytes(0x52), BlockBytes(0x53), BlockBytes(0x54), BlockBytes(0x55), BlockBytes(0x56));
        var target = Concat(BlockBytes(0x51), BlockBytes(0x62), new byte[Block], BlockBytes(0x64), BlockBytes(0x55), BlockBytes(0x66));
        using var temp = new TempDirectory();
        var artifacts = Run(temp, source, target, new DatWriteOptions());
        var lines = File.ReadAllLines(artifacts.List);
        Assert.Equal(new[] { "4", "6", "0", "0" }, lines[..4]);
        Assert.Equal(4, lines.Length - 4);
        Assert.StartsWith("imgdiff ", lines[4], StringComparison.Ordinal);
        Assert.Equal("zero 2,2,3", lines[5]);
        Assert.StartsWith("imgdiff ", lines[6], StringComparison.Ordinal);
        Assert.StartsWith("imgdiff ", lines[7], StringComparison.Ordinal);
        AssertRebuilds(temp, source, target, artifacts.List, artifacts.Dat, artifacts.Patch);
    }

    private static void AssertArtifactsEqual((string List, string Dat, string Patch) left, (string List, string Dat, string Patch) right)
    {
        Assert.Equal(File.ReadAllText(right.List), File.ReadAllText(left.List));
        Assert.Equal(File.ReadAllBytes(right.Dat), File.ReadAllBytes(left.Dat));
        Assert.Equal(File.ReadAllBytes(right.Patch), File.ReadAllBytes(left.Patch));
    }

    private static (string List, string Dat, string Patch) Run(TempDirectory temp, byte[] source, byte[] target, DatWriteOptions options)
    {
        var sourcePath = temp.WriteBytes("src.img", source);
        var targetPath = temp.WriteBytes("tgt.img", target);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        var patch = Path.Combine(temp.Path, "system.patch.dat");
        var list = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.WriteIncremental(sourcePath, targetPath, dat, patch, list, options);
        return (list, dat, patch);
    }

    private static void AssertRebuilds(TempDirectory temp, byte[] source, byte[] target, string listPath, string datPath, string patchPath)
    {
        var output = Path.Combine(temp.Path, "rebuilt-" + Guid.NewGuid().ToString("N") + ".img");
        DatReader.Unpack(listPath, datPath, patchPath, temp.WriteBytes("old-" + Guid.NewGuid().ToString("N") + ".img", source), output);
        var rebuilt = File.ReadAllBytes(output);
        Assert.Equal(target, rebuilt[..target.Length]);
        Assert.All(rebuilt[target.Length..], value => Assert.Equal((byte)0, value));
    }

    private static byte[] BlockBytes(byte fill)
    {
        var data = new byte[Block];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(fill ^ i);
        }

        return data;
    }

    private static byte[] RandomBlock(int seed)
    {
        var data = new byte[Block];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}