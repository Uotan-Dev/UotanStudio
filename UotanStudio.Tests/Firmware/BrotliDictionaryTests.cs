
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Compressors;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BrotliDictionaryTests
{
    private static byte[] BuildDictionary()
    {
        var dictionary = new byte[65536];
        var random = new Random(20261001);
        random.NextBytes(dictionary);
        return dictionary;
    }

    private static byte[] SliceFromDictionary(byte[] dictionary, int offset, int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = dictionary[offset + index];
        }
        return data;
    }

    [Fact]
    public void CompressWithDictionary_ByteBacked_RoundTripsThroughMatchingDictionary()
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 1000, 30000);

        var compressed = BrotliCompressor.CompressWithDictionary(payload, dictionary, new CompressionOptions { Level = 9 });
        var decompressed = BrotliCompressor.DecompressWithDictionary(compressed, dictionary);

        Assert.Equal(payload, decompressed);
    }

    [Fact]
    public void CompressWithDictionary_DictionaryBackedContent_YieldsSmallerStreamThanDictionaryFree()
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 0, 40000);

        var withDictionary = BrotliCompressor.CompressWithDictionary(payload, dictionary, new CompressionOptions { Level = 9 });
        var withoutDictionary = BrotliCompressor.Compress(payload, new CompressionOptions { Level = 9 });

        Assert.True(withDictionary.Length * 4 < withoutDictionary.Length,
            $"Dictionary-backed stream ({withDictionary.Length}) should be far smaller than dictionary-free ({withoutDictionary.Length}).");
    }

    [Fact]
    public void DecompressWithDictionary_WrongDictionary_DoesNotRecoverOriginal()
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 1000, 30000);
        var compressed = BrotliCompressor.CompressWithDictionary(payload, dictionary, new CompressionOptions { Level = 9 });
        var wrong = new byte[dictionary.Length];
        new Random(999).NextBytes(wrong);

        byte[]? recovered = null;
        var threw = false;
        try
        {
            recovered = BrotliCompressor.DecompressWithDictionary(compressed, wrong);
        }
        catch (CompressionException)
        {
            threw = true;
        }

        Assert.True(threw || !recovered!.AsSpan().SequenceEqual(payload),
            "A mismatched dictionary must never reproduce the original bytes.");
    }

    [Fact]
    public void CompressWithDictionary_StreamBacked_RoundTripsThroughMatchingDictionary()
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 500, 20000);
        using var source = new MemoryStream(payload, writable: false);
        using var compressed = new MemoryStream();

        BrotliCompressor.CompressWithDictionary(source, compressed, dictionary, new CompressionOptions { Level = 9 });

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        BrotliCompressor.DecompressWithDictionary(compressed, decompressed, dictionary, payload.Length);
        Assert.Equal(payload, decompressed.ToArray());
    }

    [Fact]
    public void DecompressWithDictionary_OutputExceedsLimit_Throws()
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 0, 30000);
        using var source = new MemoryStream(payload, writable: false);
        using var compressed = new MemoryStream();
        BrotliCompressor.CompressWithDictionary(source, compressed, dictionary, new CompressionOptions { Level = 9 });

        compressed.Position = 0;
        using var decompressed = new MemoryStream();
        Assert.Throws<CompressionException>(
            () => BrotliCompressor.DecompressWithDictionary(compressed, decompressed, dictionary, payload.Length - 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4096)]
    public void DecompressWithDictionary_NegativeLimit_Throws(long maxOutputBytes)
    {
        var dictionary = BuildDictionary();
        var payload = SliceFromDictionary(dictionary, 0, 16);
        var compressed = BrotliCompressor.CompressWithDictionary(payload, dictionary, null);
        using var source = new MemoryStream(compressed, writable: false);
        using var decompressed = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BrotliCompressor.DecompressWithDictionary(source, decompressed, dictionary, maxOutputBytes));
    }

    [Fact]
    public void CompressWithDictionary_NullArguments_Throw()
    {
        var dictionary = BuildDictionary();
        Assert.Throws<ArgumentNullException>(() => BrotliCompressor.CompressWithDictionary(null!, dictionary, null));
        Assert.Throws<ArgumentNullException>(() => BrotliCompressor.CompressWithDictionary([1, 2, 3], null!, null));
        Assert.Throws<ArgumentNullException>(() => BrotliCompressor.DecompressWithDictionary(null!, dictionary));
        Assert.Throws<ArgumentNullException>(() => BrotliCompressor.DecompressWithDictionary([1, 2, 3], null!));
    }
}