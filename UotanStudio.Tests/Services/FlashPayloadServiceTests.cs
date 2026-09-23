using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashPayloadServiceTests
{
    [Fact]
    public async Task ExtractAsync_SelectedPartition_PublishesVerifiedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var operation = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
            var manifest = new DeltaArchiveManifest { BlockSize = 4 };
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "boot" });
            manifest.Partitions[0].Operations.Add(operation);
            var path = Path.Combine(root, "payload.bin");
            File.WriteAllBytes(path, PayloadDeltaTests.BuildPayload(manifest,
                new Dictionary<InstallOperation, byte[]> { [operation] = "boot"u8.ToArray() }));

            var listed = await FlashPayloadService.ListAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(listed, partition => partition.Name == "boot");

            var output = await FlashPayloadService.ExtractAsync(path, ["boot"], root, TestContext.Current.CancellationToken);
            Assert.Equal("boot"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "boot.img")));
            Assert.Empty(Directory.GetDirectories(root, "*.tmp"));

            var package = await FlashPayloadService.BuildPackageAsync(path, ["boot"], root,
                TestContext.Current.CancellationToken);
            Assert.Equal("boot"u8.ToArray(), File.ReadAllBytes(Path.Combine(package, "images", "boot.img")));
            var plan = FlashManifestReader.Read(Path.Combine(package, "flash_fastbootd.txt"));
            Assert.True(plan.RequiresFastbootd);
            Assert.Equal("boot", Assert.Single(plan.Steps).Partition);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
