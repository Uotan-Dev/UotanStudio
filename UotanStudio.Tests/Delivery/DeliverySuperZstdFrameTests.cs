using System.Security.Cryptography;
using UotanStudio.Firmware.Compression.Compressors;
using UotanStudio.Services.Delivery;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliverySuperZstdFrameTests : IDisposable
{
    private const int ChunkSize = 1024 * 1024;
    private const ulong DeviceSize = 8UL * 1024 * 1024;
    private const int PartitionSize = 3 * 1024 * 1024;

    private readonly TempDirectory _temp = new();
    private string Root => _temp.Path;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Export_LargeZstdSuper_DecompressesToSameRawBytes()
    {
        var expected = SHA256.HashData(await BuildRawSuperAsync());
        bool[] layouts = [false, true];
        foreach (var chunked in layouts)
        {
            var compressed = await ExportCompressedSuperAsync(chunked ? "chunked" : "single", chunked);
            using var source = new MemoryStream(compressed);
            using var restored = new MemoryStream();
            ZstdCompressor.Decompress(source, restored);
            Assert.Equal(expected, SHA256.HashData(restored.ToArray()));
        }
    }

    [Fact]
    public async Task Export_LargeZstdSuper_DefaultProducesSingleFrame()
    {
        await BuildRawSuperAsync();
        var compressed = await ExportCompressedSuperAsync("default", chunked: false);

        Assert.Equal(1, CountFrames(compressed));
    }

    [Fact]
    public async Task Export_LargeZstdSuper_ChunkedSwitchProducesMultipleFrames()
    {
        await BuildRawSuperAsync();
        var compressed = await ExportCompressedSuperAsync("parallel", chunked: true);

        var frames = CountFrames(compressed);
        Assert.True(frames > 1, $"ZstdChunkedParallel produced only {frames} frame for a {compressed.Length}-byte container.");
    }

    private async Task<byte[]> BuildRawSuperAsync()
    {
        var path = Path.Combine(Root, "super-raw.img");
        await DeliverySuperBuilder.BuildAsync([Input()], new DeliverySuperOptions { DeviceSize = DeviceSize }, path, token: Token);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 2L * ChunkSize, $"Super image of {bytes.Length} bytes cannot reach the chunking decision.");
        return bytes;
    }

    private async Task<byte[]> ExportCompressedSuperAsync(string folder, bool chunked)
    {
        var output = Path.Combine(Root, folder);
        await new DeliveryOutputService(new NoTools()).ExportAsync(new()
        {
            OrganizeDirectory = false,
            PackSuper = true,
            Super = new DeliverySuperOptions { DeviceSize = DeviceSize, Zstd = true, ZstdChunkedParallel = chunked },
            Container = [Input()],
            OutputDirectory = output
        }, token: Token);
        var compressed = File.ReadAllBytes(Path.Combine(output, "super.zst"));
        Assert.True(compressed.Length > 4, "The compressed Super container is empty.");
        return compressed;
    }

    private DeliveryFile Input()
    {
        var path = Path.Combine(Root, "system.img");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, Enumerable.Range(0, PartitionSize).Select(static index => (byte)(index * 37)).ToArray());
        }

        return new() { SourcePath = path, FileName = "system.img", Partition = "system", SourceLength = PartitionSize };
    }

    private static int CountFrames(byte[] data)
    {
        Span<byte> magic = [0x28, 0xB5, 0x2F, 0xFD];
        var count = 0;
        for (var i = 0; i + magic.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, magic.Length).SequenceEqual(magic))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }
}