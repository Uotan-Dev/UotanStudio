using UotanStudio.Firmware.CompoundFile;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class CompoundFileTests
{
    private static byte[] Pattern(int length, byte seed) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 7) + seed)).ToArray();

    private static CompoundFileBuilder CreateBuilder()
    {
        var builder = new CompoundFileBuilder();
        builder.AddStream("File_Version", [0x02, 0x00, 0x00, 0x00, 0x00, 0x00]);
        builder.AddStream("00000000/default/NV_ITEM_ARRAY", Pattern(10 * 1024, 0x11));
        builder.AddStream("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", Pattern(300, 0x22));
        builder.AddStream("00000000/default/Mobile_Property_Info", Pattern(64, 0x33));
        builder.AddStream("empty", []);
        return builder;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    [Fact]
    public async Task OpenAsync_NestedStreams_RoundTripsContentByPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, CreateBuilder().Build(), TestContext.Current.CancellationToken);
        try
        {
            using var reader = await CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(512u, reader.SectorSize);

            var version = reader.TryGetEntryByPath("File_Version");
            Assert.NotNull(version);
            Assert.Equal(CompoundFileEntryType.Stream, version!.Type);
            Assert.Equal(6, version.StreamSize);

            var root = reader.TryGetEntryByPath(string.Empty);
            Assert.NotNull(root);
            Assert.Equal(CompoundFileEntryType.Root, root!.Type);

            var storage = reader.TryGetEntryByPath("00000000/default");
            Assert.NotNull(storage);
            Assert.Equal(CompoundFileEntryType.Storage, storage!.Type);

            var regular = reader.TryGetEntryByPath("00000000/default/NV_ITEM_ARRAY");
            Assert.NotNull(regular);
            using (var stream = reader.OpenStream(regular!))
            {
                Assert.Equal(10 * 1024, stream.Length);
                Assert.Equal(Pattern(10 * 1024, 0x11), await ReadAllAsync(stream, TestContext.Current.CancellationToken));
            }

            var mini = reader.TryGetEntryByPath("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY");
            Assert.NotNull(mini);
            using (var stream = reader.OpenStream(mini!))
            {
                Assert.Equal(300, stream.Length);
                Assert.Equal(Pattern(300, 0x22), await ReadAllAsync(stream, TestContext.Current.CancellationToken));
            }

            var empty = reader.TryGetEntryByPath("empty");
            Assert.NotNull(empty);
            using (var stream = reader.OpenStream(empty!))
            {
                Assert.Equal(0, stream.Length);
                Assert.Empty(await ReadAllAsync(stream, TestContext.Current.CancellationToken));
            }

            Assert.Null(reader.TryGetEntryByPath("missing/stream"));
            Assert.Contains(reader.Entries, entry => entry.FullPath == "00000000/default/NV_ITEM_ARRAY" && entry.Type == CompoundFileEntryType.Stream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_BlockLayout_MatchesStreamBytes()
    {
        var bytes = CreateBuilder().Build();
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            using var reader = await CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken);
            var entry = reader.TryGetEntryByPath("00000000/default/NV_ITEM_ARRAY");
            Assert.NotNull(entry);
            var layout = reader.GetStreamBlockLayout(entry!);
            Assert.Equal(512, layout.BlockSize);
            Assert.Equal((10 * 1024 + 511) / 512, layout.BlockOffsets.Length);

            Span<byte> head = stackalloc byte[8];
            using (var stream = reader.OpenStream(entry!))
            {
                _ = stream.Read(head);
            }

            Assert.True(head.SequenceEqual(bytes.AsSpan((int)layout.BlockOffsets[0], 8)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_TruncatedContainer_ThrowsBoundedException()
    {
        var bytes = CreateBuilder().Build();
        var shortPath = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        var truncatedPath = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(shortPath, bytes[..100], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(truncatedPath, bytes[..600], TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidCompoundFileMagicException>(() => CompoundFileReader.OpenAsync(shortPath, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CompoundFileCorruptedException>(() => CompoundFileReader.OpenAsync(truncatedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(shortPath);
            File.Delete(truncatedPath);
        }
    }

    [Fact]
    public async Task OpenAsync_WrongMagic_ThrowsInvalidMagic()
    {
        var bytes = CreateBuilder().Build();
        bytes[0] = 0x12;
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidCompoundFileMagicException>(() => CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_UnsupportedMajorVersion_ThrowsUnsupportedVersion()
    {
        var bytes = CreateBuilder().Build();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 5);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<UnsupportedCompoundFileVersionException>(() => CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_FatChainCycle_ThrowsCorrupted()
    {
        var bytes = CreateBuilder().Build();
        var totalSectors = (bytes.Length / 512) - 1;
        for (var sector = 2; sector < totalSectors; sector++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((1 + (sector / 128)) * 512 + ((sector % 128) * 4)), (uint)sector);
        }

        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<CompoundFileCorruptedException>(() => CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_LargeFileWithDifatChain_RoundTripsStream()
    {
        var builder = new CompoundFileBuilder();
        var payload = Pattern(7 * 1024 * 1024 + 4096, 0x5A);
        builder.AddStream("00000000/default/NV_ITEM_ARRAY", payload);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        try
        {
            using var reader = await CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken);
            var entry = reader.TryGetEntryByPath("00000000/default/NV_ITEM_ARRAY");
            Assert.NotNull(entry);
            using var stream = reader.OpenStream(entry!);
            Assert.Equal(payload.Length, stream.Length);
            Assert.Equal(payload, await ReadAllAsync(stream, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}