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

    [Fact]
    public void CompressZstdChunk_ReusedContext_MatchesFreshContextFrameBytes()
    {
        var dictionary = Pattern(64 * 1024);
        var chunks = new[]
        {
            ChunkOf(dictionary, 4096, 1),
            ChunkOf(dictionary, 256 * 1024, 2),
            ChunkOf(dictionary, 4096, 1),
            ChunkOf(dictionary, 64 * 1024, 3)
        };
        var destination = new byte[256 * 1024 * 2 + 65536];
        using var reused = PayloadBuilder.CreateZstdFrameContext(dictionary);
        for (var index = 0; index < chunks.Length; index++)
        {
            var written = PayloadBuilder.CompressZstdFrame(reused, chunks[index], destination);
            using var fresh = new MemoryStream();
            PayloadBuilder.CompressZstdChunk(new MemoryStream(chunks[index]), fresh, dictionary);
            Assert.Equal(fresh.ToArray(), destination[..written]);
        }
    }

    [Fact]
    public async Task Build_ZstdReplaceMultiChunkMultiWorker_MatchesSingleWorkerBytes()
    {
        var dictionary = Pattern(64 * 1024);
        const int chunkBytes = 1024 * 1024;
        var chunks = new[]
        {
            ChunkOf(dictionary, chunkBytes, 11),
            ChunkOf(dictionary, chunkBytes, 12),
            ChunkOf(dictionary, chunkBytes, 13),
            ChunkOf(dictionary, 64 * 1024, 14)
        };
        var image = Concatenated(chunks);
        var singleWorker = await BuildZstdPayloadAsync(image, dictionary, workers: 1);
        var multiWorker = await BuildZstdPayloadAsync(image, dictionary, workers: 4);
        Assert.Equal(singleWorker, multiWorker);

        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(multiWorker.AsSpan(12)));
        var metadataSignatureLength = (int)BinaryPrimitives.ReadUInt32BigEndian(multiWorker.AsSpan(20));
        var blobStart = 24 + manifestLength + metadataSignatureLength;
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(multiWorker[24..(24 + manifestLength)]);
        var partition = Assert.Single(manifest.Partitions);
        Assert.Equal(4, partition.Operations.Count);

        using var freshFrames = new MemoryStream();
        var plaintextPosition = 0;
        foreach (var operation in partition.Operations)
        {
            var length = checked((int)operation.DstLength);
            var chunk = image[plaintextPosition..(plaintextPosition + length)];
            PayloadBuilder.CompressZstdChunk(new MemoryStream(chunk), freshFrames, dictionary);
            plaintextPosition += length;
        }

        Assert.Equal(freshFrames.ToArray(), multiWorker[blobStart..]);
        using var rebuilt = new MemoryStream();
        foreach (var operation in partition.Operations)
        {
            var start = blobStart + (int)operation.DataOffset;
            var frame = multiWorker[start..(start + (int)operation.DataLength)];
            Assert.Equal(SHA256.HashData(frame), operation.DataSha256Hash.ToByteArray());
            ZstdReplaceDecoder.Decode(new MemoryStream(frame), rebuilt, dictionary, TestContext.Current.CancellationToken);
        }

        Assert.Equal(image, rebuilt.ToArray());
        Assert.NotNull(partition.NewPartitionInfo);
        Assert.Equal(SHA256.HashData(image), partition.NewPartitionInfo.Hash.ToByteArray());
    }

    [Fact]
    public async Task Build_NoneCompressionMultiWorker_MatchesSingleWorkerBytes()
    {
        const int chunkBytes = 1024 * 1024;
        var image = new byte[3 * chunkBytes + 64 * 1024];
        new Random(7152).NextBytes(image);
        var singleWorker = await BuildPlainPayloadAsync(image, workers: 1);
        var multiWorker = await BuildPlainPayloadAsync(image, workers: 4);
        Assert.Equal(singleWorker, multiWorker);

        var manifestLength = checked((int)BinaryPrimitives.ReadUInt64BigEndian(multiWorker.AsSpan(12)));
        var blobStart = 24 + manifestLength + (int)BinaryPrimitives.ReadUInt32BigEndian(multiWorker.AsSpan(20));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(multiWorker[24..(24 + manifestLength)]);
        var partition = Assert.Single(manifest.Partitions);
        Assert.Equal(4, partition.Operations.Count);
        var plaintextPosition = 0;
        foreach (var operation in partition.Operations)
        {
            var length = checked((int)operation.DstLength);
            var stored = multiWorker[(blobStart + (int)operation.DataOffset)..(blobStart + (int)operation.DataOffset + length)];
            Assert.Equal(image[plaintextPosition..(plaintextPosition + length)], stored);
            Assert.Equal(SHA256.HashData(stored), operation.DataSha256Hash.ToByteArray());
            plaintextPosition += length;
        }

        Assert.Equal(image.Length, plaintextPosition);
        Assert.NotNull(partition.NewPartitionInfo);
        Assert.Equal(SHA256.HashData(image), partition.NewPartitionInfo.Hash.ToByteArray());
    }

    private static async Task<byte[]> BuildZstdPayloadAsync(byte[] image, byte[] dictionary, int workers)
    {
        using var builder = new PayloadBuilder();
        builder.WithCompressionWorkers(workers).AddPartitionZstd("system", new MemoryStream(image), dictionary);
        using var output = new MemoryStream();
        await builder.BuildAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> BuildPlainPayloadAsync(byte[] image, int workers)
    {
        using var builder = new PayloadBuilder();
        builder.WithCompressionWorkers(workers).AddPartitionCompressed("system", new MemoryStream(image), CompressionType.None);
        using var output = new MemoryStream();
        await builder.BuildAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static byte[] Concatenated(byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] ChunkOf(byte[] dictionary, int length, int salt)
    {
        var chunk = new byte[length];
        for (var offset = 0; offset < length; offset += dictionary.Length)
        {
            Array.Copy(dictionary, 0, chunk, offset, Math.Min(dictionary.Length, length - offset));
        }

        for (var index = 0; index < Math.Min(64, length); index++)
        {
            chunk[index] = (byte)(chunk[index] ^ (salt * 31 + index));
        }

        return chunk;
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