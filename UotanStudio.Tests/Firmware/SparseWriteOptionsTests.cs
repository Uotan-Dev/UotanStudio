using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class SparseWriteOptionsTests
{
    [Fact]
    public void ConvertRawToSparse_DefaultOptions_MatchesLegacyBytes()
    {
        var raw = CreateRaw(12 * 1024);
        using var legacy = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), legacy, 4096, raw.Length);
        using var defaulted = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), defaulted, 4096, raw.Length,
            new SparseWriteOptions());

        Assert.Equal(legacy.ToArray(), defaulted.ToArray());
    }

    [Fact]
    public void ConvertRawToSparse_NonDefaultBlockSize_RoundTripsThroughReader()
    {
        var raw = CreateRaw(3 * 1024 + 512);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), output, 1024, raw.Length,
            new SparseWriteOptions { BlockSize = 1024 });

        using var sparse = SparseReader.FromBuffer(output.ToArray(), validateCrc: true);
        Assert.Equal(1024u, sparse.Header.BlockSize);
        Assert.Equal(4u, sparse.Header.TotalBlocks);
        using var expanded = new MemoryStream();
        sparse.WriteRawToStream(expanded);
        Assert.Equal(raw.Concat(new byte[512]).ToArray(), expanded.ToArray());
    }

    [Fact]
    public void ConvertRawToSparse_EmitCrc_AppendsValidatableChecksumChunk()
    {
        var raw = CreateRaw(9 * 1024);
        using var withoutCrc = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), withoutCrc, 1024, raw.Length);
        using var withCrc = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), withCrc, 1024, raw.Length,
            new SparseWriteOptions { BlockSize = 1024, EmitCrc = true });

        Assert.NotEqual(withoutCrc.ToArray(), withCrc.ToArray());
        using var sparse = SparseReader.FromBuffer(withCrc.ToArray(), validateCrc: true);
        using var expanded = new MemoryStream();
        sparse.WriteRawToStream(expanded);
        Assert.Equal(raw, expanded.ToArray());
    }

    [Fact]
    public void ConvertRawToSparse_MaxRawChunkBlocks_SplitsRawChunksWithinCap()
    {
        var raw = CreateRaw(32 * 1024);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), output, 1024, raw.Length,
            new SparseWriteOptions { BlockSize = 1024, MaxRawChunkBlocks = 4 });

        using var sparse = SparseReader.FromBuffer(output.ToArray(), validateCrc: true);
        Assert.All(sparse.Chunks, chunk => Assert.True(chunk.Header.ChunkSize <= 4));
        Assert.True(sparse.Chunks.Count(chunk => (ChunkType)chunk.Header.ChunkType == ChunkType.Raw) >= 8);
        using var expanded = new MemoryStream();
        sparse.WriteRawToStream(expanded);
        Assert.Equal(raw, expanded.ToArray());
    }

    [Fact]
    public void ConvertRawToSparse_FileAlignment_PadsDeclaredBlocksWithDontCare()
    {
        var raw = CreateRaw(3 * 1024);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(new MemoryStream(raw), output, 1024, raw.Length,
            new SparseWriteOptions { BlockSize = 1024, FileAlignment = 4 });

        using var sparse = SparseReader.FromBuffer(output.ToArray(), validateCrc: true);
        Assert.Equal(4u, sparse.Header.TotalBlocks);
        using var expanded = new MemoryStream();
        sparse.WriteRawToStream(expanded);
        Assert.Equal(raw.Concat(new byte[1024]).ToArray(), expanded.ToArray());
    }

    [Fact]
    public void Validate_InvalidDimensions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { BlockSize = 1536 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { BlockSize = 512 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { BlockSize = 131072 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { FileAlignment = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { FileAlignment = SparseWriteOptions.MaximumFileAlignment + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseWriteOptions { MaxRawChunkBlocks = 0 }.Validate());
        new SparseWriteOptions().Validate();
    }

    private static byte[] CreateRaw(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 7 + 1);
        }
        data.AsSpan(0, 1024).Clear();
        return data;
    }
}