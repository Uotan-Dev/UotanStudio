using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
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