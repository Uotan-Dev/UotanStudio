using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Delivery;
using UotanStudio.Firmware.OtaDelta.CareMap;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class CareMapDeliveryTests : IDisposable
{
    private const int Block = CareMap.BlockSize;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caremap-delivery-" + Guid.NewGuid().ToString("N"));

    public CareMapDeliveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExportAsync_VerifiedContainerPartition_StoresCareMapBeforeMetadata()
    {
        var source = Path.Combine(_root, "vendor.img");
        File.WriteAllBytes(source, VerityImage(originalBlocks: 3, totalBlocks: 4));
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            OutputDirectory = Path.Combine(_root, "output"),
            Container =
            [
                new() { SourcePath = source, FileName = "vendor.img", Partition = "vendor" },
                new() { SourcePath = Source("boot.img", new byte[8192]), FileName = "boot.img", Partition = "boot" }
            ],
            Files = [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }]
        };

        var zipPath = Path.Combine(plan.OutputDirectory, "ota.zip");
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(
            ["payload.bin", "payload_properties.txt", CareMap.ZipEntryName, "META-INF/com/android/metadata"],
            zip.Entries.Select(entry => entry.FullName));
        Assert.True(IsStoredEntry(zipPath, CareMap.ZipEntryName));
        var entry = zip.GetEntry(CareMap.ZipEntryName)!;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var map = CareMap.FromBytes(buffer.ToArray());

        var vendor = Assert.Single(map.Partitions);
        Assert.Equal("vendor", vendor.Name);
        Assert.Equal("2,0,3", vendor.Ranges);
        Assert.Equal("", vendor.PropertyId);
    }

    [Fact]
    public async Task ExportAsync_NoVerifiedPartition_OmitsCareMapEntry()
    {
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            OutputDirectory = Path.Combine(_root, "unsigned-output"),
            Container = [new() { SourcePath = Source("system.img", new byte[4 * Block]), FileName = "system.img", Partition = "system" }],
            Files = [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }]
        };

        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "ota.zip"));
        Assert.Null(zip.GetEntry(CareMap.ZipEntryName));
    }

    private static bool IsStoredEntry(string zipPath, string name)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var needle = Encoding.UTF8.GetBytes(name);
        for (var position = 0; position + 30 + needle.Length <= bytes.Length; position++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)) != 0x04034b50)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 26)) != needle.Length
                || !bytes.AsSpan(position + 30, needle.Length).SequenceEqual(needle))
            {
                continue;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 8)) == 0;
        }

        return false;
    }

    private string Source(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] VerityImage(int originalBlocks, int totalBlocks)
    {
        var data = new byte[totalBlocks * Block];
        "AVBf"u8.CopyTo(data.AsSpan(data.Length - 64));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(data.Length - 60), 1);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(data.Length - 52), checked((ulong)originalBlocks * Block));
        return data;
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
            => Task.CompletedTask;
    }

    public void Dispose() => Directory.Delete(_root, true);
}