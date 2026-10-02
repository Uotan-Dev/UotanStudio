using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ZstdDictionaryExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zstd-dict-extract-" + Guid.NewGuid().ToString("N"));

    public ZstdDictionaryExtractionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Extract_PayloadBuiltWithExternalDictionary_SuppliedDictionaryRebuildsExactPartition()
    {
        var dictionary = Pattern(64 * 1024);
        var image = new byte[8192];
        Array.Copy(dictionary, 512, image, 0, image.Length);
        RandomNumberGenerator.Fill(image.AsSpan(0, 256));
        var payload = BuildPayload(image, dictionary);
        var output = Path.Combine(_root, "with-dictionary");

        PayloadParser.ExtractWithZstdDictionaries(payload, ["vendor"], output,
            new Dictionary<string, byte[]> { ["vendor"] = dictionary });

        Assert.Equal(image, File.ReadAllBytes(Path.Combine(output, "vendor.img")));
    }

    [Fact]
    public void Extract_PayloadBuiltWithExternalDictionary_DictionaryAboveCeilingFailsBeforeDecoding()
    {
        var dictionary = Pattern(64 * 1024);
        var image = new byte[4096];
        Array.Copy(dictionary, 1024, image, 0, image.Length);
        var payload = BuildPayload(image, dictionary);
        var output = Path.Combine(_root, "ceiling");

        Assert.ThrowsAny<PayloadException>(() => PayloadParser.ExtractWithZstdDictionaries(payload, ["vendor"], output,
            new Dictionary<string, byte[]> { ["vendor"] = dictionary }, 4096));
        Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
    }

    [Fact]
    public void Extract_PayloadBuiltWithExternalDictionary_NoDictionaryFailsWithoutWritingOutput()
    {
        var dictionary = Pattern(64 * 1024);
        var image = new byte[4096];
        Array.Copy(dictionary, 2048, image, 0, image.Length);
        var payload = BuildPayload(image, dictionary);
        var output = Path.Combine(_root, "missing-dictionary");

        Assert.Throws<PartitionExtractionException>(() => PayloadParser.Extract(payload, ["vendor"], output));
        Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
    }

    [Fact]
    public void Parse_ZstdDictGatingKeys_PopulatesBothProperties()
    {
        var withKeys = PayloadPropertiesParser.Parse(
            "FILE_HASH=abc\nFILE_SIZE=10\nmax_zstd_dict_raw_size=67108864\nmax_zstd_dict_trust=100\n");
        var withoutKeys = PayloadPropertiesParser.Parse("FILE_HASH=abc\n");

        Assert.Equal(67108864, withKeys.MaxZstdDictRawSize);
        Assert.Equal("100", withKeys.MaxZstdDictTrust);
        Assert.Equal(0, withoutKeys.MaxZstdDictRawSize);
        Assert.Equal(string.Empty, withoutKeys.MaxZstdDictTrust);
    }

    [Fact]
    public void Parse_ZstdDictGatingKeys_AreCaseInsensitive()
    {
        var properties = PayloadPropertiesParser.Parse("MAX_ZSTD_DICT_RAW_SIZE=2048\nMAX_ZSTD_DICT_TRUST=50\n");

        Assert.Equal(2048, properties.MaxZstdDictRawSize);
        Assert.Equal("50", properties.MaxZstdDictTrust);
    }

    private string BuildPayload(byte[] image, byte[] dictionary)
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".img");
        File.WriteAllBytes(source, image);
        var payload = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        using (var builder = new PayloadBuilder())
        {
            builder.AddPartitionZstd("vendor", source, dictionary);
            builder.Build(payload);
        }

        return payload;
    }

    private static byte[] Pattern(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        return buffer;
    }
}