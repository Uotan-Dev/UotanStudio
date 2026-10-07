using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DatWriterIncrementalTests
{
    [Fact]
    public void WriteIncremental_ChangedBlocksRebuildTargetViaImgdiff()
    {
        var source = Concat(Block(0xAA), Block(0xBB), Block(0xCC), Block(0xDD));
        var target = Concat(Block(0xAA), Block(0x11), new byte[4096], Block(0xDD));
        using var temp = new TempDirectory();
        var (listPath, datPath, patchPath) = Run(temp, source, target);
        Assert.Contains("imgdiff ", File.ReadAllText(listPath), StringComparison.Ordinal);
        Assert.True(new FileInfo(patchPath).Length > 0);
        AssertRebuilds(temp, source, target, listPath, datPath, patchPath);
    }

    [Fact]
    public void WriteIncremental_IdenticalImagesFallBackToFullNew()
    {
        var image = Concat(Block(0x5A), Block(0x6A));
        using var temp = new TempDirectory();
        var (listPath, datPath, patchPath) = Run(temp, image, image);
        var list = File.ReadAllText(listPath);
        Assert.Contains("new ", list, StringComparison.Ordinal);
        Assert.DoesNotContain("imgdiff ", list, StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(patchPath).Length);
        AssertRebuilds(temp, image, image, listPath, datPath, patchPath);
    }

    [Fact]
    public void WriteIncremental_TargetLargerThanSourceAppendsChangedTail()
    {
        var source = Block(0x33);
        var target = Concat(Block(0x33), Block(0x44), Block(0x55));
        using var temp = new TempDirectory();
        var (listPath, datPath, patchPath) = Run(temp, source, target);
        Assert.Contains("imgdiff ", File.ReadAllText(listPath), StringComparison.Ordinal);
        AssertRebuilds(temp, source, target, listPath, datPath, patchPath);
    }

    [Fact]
    public void WriteIncremental_EmptySourceAndTarget_RebuildsEmpty()
    {
        using var temp = new TempDirectory();
        var (listPath, datPath, patchPath) = Run(temp, [], []);
        AssertRebuilds(temp, [], [], listPath, datPath, patchPath);
    }

    private static void AssertRebuilds(TempDirectory temp, byte[] source, byte[] target, string listPath, string datPath, string patchPath)
    {
        var output = Path.Combine(temp.Path, "out.img");
        DatReader.Unpack(listPath, datPath, patchPath, temp.WriteBytes("old.img", source), output);
        var rebuilt = File.ReadAllBytes(output);
        Assert.Equal(target, rebuilt[..target.Length]);
        Assert.Empty(rebuilt[target.Length..]);
    }

    private static (string List, string Dat, string Patch) Run(TempDirectory temp, byte[] source, byte[] target)
    {
        var sourcePath = temp.WriteBytes("src.img", source);
        var targetPath = temp.WriteBytes("tgt.img", target);
        var datPath = Path.Combine(temp.Path, "t.new.dat");
        var patchPath = Path.Combine(temp.Path, "t.patch.dat");
        var listPath = Path.Combine(temp.Path, "t.list");
        DatWriter.WriteIncremental(sourcePath, targetPath, datPath, patchPath, listPath, new DatWriteOptions());
        return (listPath, datPath, patchPath);
    }

    private static byte[] Block(byte fill)
    {
        var data = new byte[4096];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(fill ^ i);
        return data;
    }

    private static byte[] Concat(params ReadOnlySpan<byte[]> parts)
    {
        var buffer = new List<byte>();
        foreach (var part in parts) buffer.AddRange(part);
        return [.. buffer];
    }
}