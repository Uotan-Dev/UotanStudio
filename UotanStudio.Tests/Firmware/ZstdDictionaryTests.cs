using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ZstdDictionaryTests
{
    [Fact]
    public async Task AddPartitionZstd_NoDictionary_RoundTripsThroughManagedExtractor()
    {
        var image = Pattern(3 * 4096);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("fstab.img", image);
        var payload = Path.Combine(temp.Path, "payload.bin");
        var output = Path.Combine(temp.Path, "out");
        using (var builder = new PayloadBuilder())
        {
            builder.AddPartitionZstd("vendor_fstab", imagePath);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        await PayloadParser.ExtractAsync(payload, ["vendor_fstab"], output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(output, "vendor_fstab.img")));
    }

    [Fact]
    public void CompressZstdChunk_WithExternalDictionary_DecodesByteExactWithSameDictionary()
    {
        var dictionary = Pattern(64 * 1024);
        var plain = new byte[4096];
        Array.Copy(dictionary, 1000, plain, 0, plain.Length);
        RandomNumberGenerator.Fill(plain.AsSpan(0, 128));
        using var frame = new MemoryStream();
        PayloadBuilder.CompressZstdChunk(new MemoryStream(plain), frame, dictionary);
        Assert.Equal([0x28, 0xB5, 0x2F, 0xFD], frame.ToArray()[..4]);
        using var bare = new MemoryStream();
        PayloadBuilder.CompressZstdChunk(new MemoryStream(plain), bare, ReadOnlyMemory<byte>.Empty);
        Assert.True(frame.Length < bare.Length);
        using var output = new MemoryStream();
        frame.Position = 0;
        ZstdReplaceDecoder.Decode(frame, output, dictionary, TestContext.Current.CancellationToken);
        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public void Decode_DictionaryFrameWithoutDictionary_ThrowsPartitionExtraction()
    {
        var dictionary = Pattern(32 * 1024);
        var plain = new byte[4096];
        Array.Copy(dictionary, 500, plain, 0, plain.Length);
        using var frame = new MemoryStream();
        PayloadBuilder.CompressZstdChunk(new MemoryStream(plain), frame, dictionary);
        frame.Position = 0;
        using var output = new MemoryStream();
        Assert.Throws<PartitionExtractionException>(() =>
            ZstdReplaceDecoder.Decode(frame, output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Decode_DictionaryFrameWithWrongDictionary_ThrowsPartitionExtraction()
    {
        var dictionary = Pattern(32 * 1024);
        var wrong = Pattern(32 * 1024);
        wrong[^1] ^= 0xFF;
        var plain = new byte[4096];
        Array.Copy(dictionary, 500, plain, 0, plain.Length);
        using var frame = new MemoryStream();
        PayloadBuilder.CompressZstdChunk(new MemoryStream(plain), frame, dictionary);
        frame.Position = 0;
        using var output = new MemoryStream();
        Assert.Throws<PartitionExtractionException>(() =>
            ZstdReplaceDecoder.Decode(frame, output, wrong, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddPartitionZstd_WithDictionary_EmittedFramesDecodeThroughDictionaryAwareDecoder()
    {
        var dictionary = Pattern(64 * 1024);
        var image = new byte[2 * 4096];
        Array.Copy(dictionary, 2000, image, 0, image.Length);
        RandomNumberGenerator.Fill(image.AsSpan(0, 64));
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder())
        {
            builder.AddPartitionZstd("system", new MemoryStream(image), dictionary);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var bytes = File.ReadAllBytes(payload);
        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var metadataSignatureLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var blobStart = 24 + manifestLength + metadataSignatureLength;
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..(24 + manifestLength)]);
        var partition = Assert.Single(manifest.Partitions);
        Assert.All(partition.Operations, operation => Assert.Equal(InstallOperation.Types.Type.ReplaceZstd, operation.Type));
        using var rebuilt = new MemoryStream();
        foreach (var op in partition.Operations)
        {
            var start = blobStart + (int)op.DataOffset;
            var frame = bytes[start..(start + (int)op.DataLength)];
            Assert.Equal(SHA256.HashData(frame), op.DataSha256Hash.ToByteArray());
            ZstdReplaceDecoder.Decode(new MemoryStream(frame), rebuilt, dictionary, TestContext.Current.CancellationToken);
        }

        Assert.Equal(image, rebuilt.ToArray());
    }

    [Fact]
    public void AddPartitionZstd_EmptyImageFileWithDictionary_UsesPlainFrames()
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("empty.img", []);
        using var builder = new PayloadBuilder();
        var info = builder.AddPartitionZstd("odm", imagePath, []);
        using var output = new MemoryStream();
        info.Build(output);
        Assert.NotEmpty(output.ToArray());
    }

    [Fact]
    public async Task AddPartitionZstd_MultiChunkSingleWorker_ReusesDictionaryAndHashesEveryFrame()
    {
        var dictionary = Pattern(64 * 1024);
        const int chunkBytes = 1024 * 1024;
        var image = new byte[3 * chunkBytes];
        for (var offset = 0; offset < image.Length; offset += dictionary.Length)
        {
            Array.Copy(dictionary, 0, image, offset, Math.Min(dictionary.Length, image.Length - offset));
        }
        RandomNumberGenerator.Fill(image.AsSpan(0, 64));
        RandomNumberGenerator.Fill(image.AsSpan(chunkBytes, 64));
        RandomNumberGenerator.Fill(image.AsSpan(2 * chunkBytes, 64));
        using var temp = new TempDirectory();
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new PayloadBuilder())
        {
            builder.WithCompressionWorkers(1).AddPartitionZstd("system", new MemoryStream(image), dictionary);
            await builder.BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var bytes = File.ReadAllBytes(payload);
        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var metadataSignatureLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var blobStart = 24 + manifestLength + metadataSignatureLength;
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..(24 + manifestLength)]);
        var partition = Assert.Single(manifest.Partitions);
        Assert.Equal(3, partition.Operations.Count);
        using var rebuilt = new MemoryStream();
        foreach (var op in partition.Operations)
        {
            var start = blobStart + (int)op.DataOffset;
            var frame = bytes[start..(start + (int)op.DataLength)];
            Assert.Equal(SHA256.HashData(frame), op.DataSha256Hash.ToByteArray());
            Assert.Equal([0x28, 0xB5, 0x2F, 0xFD], frame[..4]);
            ZstdReplaceDecoder.Decode(new MemoryStream(frame), rebuilt, dictionary, TestContext.Current.CancellationToken);
        }

        Assert.Equal(image, rebuilt.ToArray());
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7 + 3);
        }

        return data;
    }
}