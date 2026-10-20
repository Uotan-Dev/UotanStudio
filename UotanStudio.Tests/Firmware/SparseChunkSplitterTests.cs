using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class SparseChunkSplitterTests
{
    [Fact]
    public async Task SplitRawAsync_MergeAsync_RawPieces_RestoresOriginalBytes()
    {
        using var temp = new TempDirectory();
        var raw = new byte[100_000];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i * 7 + 1);
        }
        var input = temp.WriteBytes("super.raw.img", raw);

        var pieces = await SparseChunkSplitter.SplitRawAsync(input, Path.Combine(temp.Path, "super.img"),
            32_768, TestContext.Current.CancellationToken);

        Assert.Equal(4, pieces.Count);
        Assert.EndsWith(".sparsechunk.00", pieces[0]);
        Assert.EndsWith(".sparsechunk.03", pieces[3]);
        var set = SparseChunkSplitter.Inspect(pieces[1]);
        Assert.Equal(SparseChunkVariant.RawStream, set.Variant);
        Assert.Equal(4, set.Parts.Count);
        Assert.Equal(100_000L, set.TotalInputBytes);

        var merged = Path.Combine(temp.Path, "merged.img");
        await SparseChunkSplitter.MergeAsync(pieces[2], merged, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(raw, File.ReadAllBytes(merged));
    }

    [Fact]
    public async Task MergeAsync_SelfContainedSparsePieces_ProducesReadableSparseImage()
    {
        using var temp = new TempDirectory();
        var raw = new byte[10 * 4096];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i * 11 + 3);
        }
        raw.AsSpan(4096, 4096).Clear();
        var input = temp.WriteBytes("super.raw.img", raw);
        var sparse = Path.Combine(temp.Path, "super.sparse.img");
        SparseImageConverter.ConvertRawToSparse(input, sparse);

        SparseImageConverter.ResparseImage(sparse, Path.Combine(temp.Path, "super.img.sparsechunk.{0}"), 5000);
        var pieces = Directory.GetFiles(temp.Path, "super.img.sparsechunk.*").Order(StringComparer.Ordinal).ToArray();
        Assert.True(pieces.Length > 1);

        var set = SparseChunkSplitter.Inspect(pieces[0]);
        Assert.Equal(SparseChunkVariant.SelfContainedSparse, set.Variant);
        var merged = Path.Combine(temp.Path, "merged.sparse.img");
        await SparseChunkSplitter.MergeAsync(pieces[1], merged, cancellationToken: TestContext.Current.CancellationToken);

        using var mergedFile = SparseReader.FromImageFile(merged, validateCrc: false);
        using var originalFile = SparseReader.FromImageFile(sparse, validateCrc: false);
        Assert.Equal(originalFile.Header.TotalBlocks, mergedFile.Header.TotalBlocks);
        Assert.Equal(originalFile.Header.BlockSize, mergedFile.Header.BlockSize);
        using var mergedRaw = new MemoryStream();
        using var originalRaw = new MemoryStream();
        mergedFile.WriteRawToStream(mergedRaw);
        originalFile.WriteRawToStream(originalRaw);
        Assert.Equal(originalRaw.ToArray(), mergedRaw.ToArray());
    }

    [Fact]
    public void Inspect_MissingPieceNumbering_Throws()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("super.img.sparsechunk.00", [1, 2, 3, 4]);
        var second = temp.WriteBytes("super.img.sparsechunk.02", [5, 6, 7, 8]);

        Assert.Throws<InvalidDataException>(() => SparseChunkSplitter.Inspect(second));
    }

    [Fact]
    public void Inspect_MixedSparseAndRawPieces_Throws()
    {
        using var temp = new TempDirectory();
        var raw = new byte[4096];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i + 1);
        }
        var sparse = Path.Combine(temp.Path, "seed.sparse.img");
        SparseImageConverter.ConvertRawToSparse(temp.WriteBytes("seed.raw.img", raw), sparse);
        temp.WriteBytes("super.img.sparsechunk.00", File.ReadAllBytes(sparse));
        var second = temp.WriteBytes("super.img.sparsechunk.01", [0, 1, 2, 3, 4, 5, 6, 7]);

        Assert.Throws<InvalidDataException>(() => SparseChunkSplitter.Inspect(second));
    }

    [Fact]
    public async Task SplitRawAsync_SparseInput_RejectedWithResparseGuidance()
    {
        using var temp = new TempDirectory();
        var raw = new byte[4096];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i + 1);
        }
        var sparse = Path.Combine(temp.Path, "super.sparse.img");
        SparseImageConverter.ConvertRawToSparse(temp.WriteBytes("super.raw.img", raw), sparse);

        await Assert.ThrowsAsync<InvalidDataException>(() => SparseChunkSplitter.SplitRawAsync(sparse,
            Path.Combine(temp.Path, "cut.img"), 1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SplitRawAsync_PieceCountOverflow_ThrowsBeforeWriting()
    {
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("super.raw.img", new byte[5000]);

        await Assert.ThrowsAsync<InvalidDataException>(() => SparseChunkSplitter.SplitRawAsync(input,
            Path.Combine(temp.Path, "cut.img"), 1, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "cut.img.sparsechunk.*"));
    }
}