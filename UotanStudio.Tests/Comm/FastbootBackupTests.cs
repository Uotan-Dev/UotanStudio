using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>
/// Fastboot backup/restore tests: the fetched partition bytes are written to disk verbatim
/// (sparse preserved as-is) and the restore issues the flash sequence.
/// <para>Fastboot 备份/还原测试：fetch 的分区字节原样落盘（sparse 保留），还原走 flash 序列。</para>
/// </summary>
public class FastbootBackupTests
{
    private const long PartitionSize = 0x20000; // 128 KiB

    [Fact]
    public void BackupFastbootPartition_WritesExactDevicePayload()
    {
        var partitionBytes = new byte[PartitionSize];
        Random.Shared.NextBytes(partitionBytes);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAY0x00020000");        // getvar:partition-size:boot (128 KiB)
        transport.Enqueue("OKAYno");                // getvar:has-slot:boot
        transport.Enqueue("OKAY");                  // getvar:max-fetch-size (empty -> -1)
        transport.Enqueue("DATA00020000");          // fetch:boot:0:20000 -> DATA 0x20000 bytes
        transport.Enqueue(partitionBytes);
        transport.Enqueue("OKAY");

        var session = new FastbootSession(transport);
        using var dir = new TempDirectory();
        var outPath = Path.Combine(dir.Path, "boot-backup.img");

        try
        {
            var result = BackupService.BackupFastbootPartition(session, "boot", outPath, progress: null, CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            Assert.Equal(PartitionSize, result.Bytes);

            var written = File.ReadAllBytes(outPath);
            Assert.Equal(partitionBytes, written);
            Assert.Equal(Sha256Hex(partitionBytes), Sha256Hex(written));

            var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
            Assert.Contains("getvar:has-slot:boot", commands);
            Assert.Contains("getvar:partition-size:boot", commands);
            Assert.Contains("getvar:max-fetch-size", commands);
            Assert.Contains("fetch:boot:0:20000", commands);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void RestoreFastbootPartition_IssuesDownloadAndFlash()
    {
        var imageBytes = new byte[16384];
        Random.Shared.NextBytes(imageBytes);

        var transport = new FakeCommTransport();
        // DownloadData: max-download-size query, download command -> DATA, trailing OKAY.
        transport.Enqueue("OKAY");                    // getvar:max-download-size (empty -> fallback)
        transport.Enqueue($"DATA{imageBytes.Length:x8}"); // download:00004000
        transport.Enqueue("OKAY");                    // payload accepted
        transport.Enqueue("OKAYno");                  // getvar:has-slot:boot -> no (no A/B slot)
        transport.Enqueue("OKAY");                    // flash:boot

        var session = new FastbootSession(transport);
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("boot.img", imageBytes);

        try
        {
            var result = BackupService.RestoreFastbootPartition(session, "boot", imagePath, progress: null, CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
            Assert.Contains(commands, c => c.StartsWith("download:", StringComparison.Ordinal));
            Assert.Contains("flash:boot", commands);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void FlashPartition_ResolvesActiveSlot_ForSlotPartition()
    {
        var imageBytes = new byte[4096];
        Random.Shared.NextBytes(imageBytes);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAY");                       // getvar:max-download-size (empty -> fallback)
        transport.Enqueue($"DATA{imageBytes.Length:x8}"); // download:00001000
        transport.Enqueue("OKAY");                       // payload accepted
        transport.Enqueue("OKAYyes");                    // getvar:has-slot:boot -> yes
        transport.Enqueue("OKAYa");                      // getvar:current-slot -> a
        transport.Enqueue("OKAY");                       // flash:boot_a

        using var session = new FastbootSession(transport);
        using var ms = new MemoryStream(imageBytes);
        var response = session.FlashPartition("boot", ms, imageBytes.Length);

        Assert.Equal(FastbootResponseState.Success, response.State);
        var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains("getvar:has-slot:boot", commands);
        Assert.Contains("getvar:current-slot", commands);
        Assert.Contains("flash:boot_a", commands);
    }

    [Fact]
    public void FlashPartition_DoesNotDoubleSuffix_WhenAlreadySlotted()
    {
        var imageBytes = new byte[4096];
        Random.Shared.NextBytes(imageBytes);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAY");                       // getvar:max-download-size
        transport.Enqueue($"DATA{imageBytes.Length:x8}"); // download:00001000
        transport.Enqueue("OKAY");                       // payload accepted
        transport.Enqueue("OKAYyes");                    // getvar:has-slot:boot_a -> yes
        transport.Enqueue("OKAY");                       // flash:boot_a (no re-suffix)

        using var session = new FastbootSession(transport);
        using var ms = new MemoryStream(imageBytes);
        session.FlashPartition("boot_a", ms, imageBytes.Length);

        var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.Contains("getvar:has-slot:boot_a", commands);
        Assert.Contains("flash:boot_a", commands);
        Assert.DoesNotContain(commands, c => c == "flash:boot_a_a");
    }

    [Fact]
    public void FlashPartition_SkipsSlotResolution_WhenDisabled()
    {
        var imageBytes = new byte[4096];
        Random.Shared.NextBytes(imageBytes);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAY");                       // getvar:max-download-size
        transport.Enqueue($"DATA{imageBytes.Length:x8}"); // download:00001000
        transport.Enqueue("OKAY");                       // payload accepted
        transport.Enqueue("OKAY");                       // flash:boot

        using var session = new FastbootSession(transport);
        using var ms = new MemoryStream(imageBytes);
        session.FlashPartition("boot", ms, imageBytes.Length, resolveSlot: false);

        var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
        Assert.DoesNotContain(commands, c => c.StartsWith("getvar:has-slot:", StringComparison.Ordinal));
        Assert.Contains("flash:boot", commands);
    }

    [Fact]
    public void BuildSuperImage_RoundTripsThroughReadMetadata()
    {
        const uint metadataMaxSize = 64 * 1024;
        const uint metadataSlotCount = 2;

        var partitions = new[]
        {
            new SuperBuildPartition("system", "default", 0x20000, attributes: 0x2),
            new SuperBuildPartition("vendor", "default", 0x10000),
            new SuperBuildPartition("odm", "vendor_data", 0x08000),
        };

        var builder = new SuperImageBuilder();
        var image = builder.Build(partitions, deviceSize: 8 * 1024 * 1024, metadataMaxSize, metadataSlotCount);

        Assert.True(builder.IsSuperImage(image));

        var metadata = builder.ReadMetadata(image);
        Assert.NotNull(metadata);
        Assert.Equal(metadataSlotCount, metadata.MetadataSlotCount);
        Assert.Equal(metadataMaxSize, metadata.MetadataMaxSize);
        Assert.Equal(0, metadata.Slot);
        Assert.True(metadata.Geometry.IsChecksumValid);
        Assert.Equal(SuperImageBuilder.GeometryBlockSize, (int)metadata.Geometry.LogicalBlockSize);

        Assert.Equal(partitions.Length, metadata.Partitions.Count);
        for (var i = 0; i < partitions.Length; i++)
        {
            Assert.Equal(partitions[i].Name, metadata.Partitions[i].Name);
            Assert.Equal(partitions[i].SizeInBytes, metadata.Partitions[i].SizeInBytes);
            Assert.Equal(partitions[i].Attributes, metadata.Partitions[i].Attributes);
            Assert.Equal(partitions[i].GroupName, metadata.Partitions[i].GroupName);
        }

        // Every partition maps to exactly one linear extent inside the image data area.
        Assert.Equal(partitions.Length, metadata.Extents.Count);
        var firstLogicalSector = Assert.Single(metadata.BlockDevices).FirstLogicalSector;
        Assert.True(firstLogicalSector > 0);
        for (var i = 0; i < partitions.Length; i++)
        {
            var extent = metadata.Extents[i];
            Assert.Equal((ulong)(partitions[i].SizeInBytes / SuperImageBuilder.LpSectorSize), extent.NumSectors);
            Assert.Equal(SuperImageBuilder.LinearTargetType, extent.TargetType);
            Assert.True(extent.TargetData >= firstLogicalSector);
            var dataStart = (long)(extent.TargetData * SuperImageBuilder.LpSectorSize);
            var dataEnd = dataStart + partitions[i].SizeInBytes;
            Assert.InRange(dataEnd, 1, image.Length);
        }

        // Backup copies: every metadata slot reads back identically.
        for (uint slot = 0; slot < metadataSlotCount; slot++)
        {
            var slotMetadata = builder.ReadMetadata(image, (int)slot);
            Assert.NotNull(slotMetadata);
            Assert.Equal(metadata.MetadataSize, slotMetadata.MetadataSize);
            Assert.Equal(metadata.Partitions.Count, slotMetadata.Partitions.Count);
            Assert.Equal(metadata.Partitions[0].Name, slotMetadata.Partitions[0].Name);
        }
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}