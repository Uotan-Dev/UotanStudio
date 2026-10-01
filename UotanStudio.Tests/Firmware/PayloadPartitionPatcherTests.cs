using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadPartitionPatcherTests : IDisposable
{
    private const int Block = 4096;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-payload-patch-test-" + Guid.NewGuid().ToString("N"));

    public PayloadPartitionPatcherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PatchAsync_AddDeleteReplace_RebuildsManifestAndPreservesImages()
    {
        var token = TestContext.Current.CancellationToken;
        var sourcePath = WriteImage("system", 3, 0x11);
        var vendorPath = WriteImage("vendor", 2, 0x22);
        var bootPath = WriteImage("boot", 2, 0x33);
        var newVendorPath = WriteImage("vendor-new", 2, 0x44);
        var addedPath = WriteImage("odm", 1, 0x55);

        var basePayload = Path.Combine(_root, "base.img");
        using (var builder = new PayloadBuilder().WithBlockSize(Block)
                   .WithDynamicPartitions(Group("qti_dynamic_partitions", ["system", "vendor"])))
        {
            _ = builder.AddPartition("system", sourcePath).AddPartition("vendor", vendorPath);
            builder.Build(basePayload);
        }

        var output = Path.Combine(_root, "patched.img");
        var patch = new PayloadPartitionPatch
        {
            DeletePartitionNames = ["boot"],
            ReplaceImagePaths = new Dictionary<string, string>(StringComparer.Ordinal) { ["vendor"] = newVendorPath },
            AddedPartitions = [("odm", addedPath)]
        };

        await using (var input = File.OpenRead(basePayload))
        await using (var destination = File.Create(output))
        {
            await PayloadPartitionPatcher.PatchAsync(input, destination, patch, cancellationToken: token);
        }

        await using var resultStream = File.OpenRead(output);
        var metadata = PayloadParser.GetPayloadMetadata(resultStream);
        Assert.Equal(["odm", "system", "vendor"], metadata.PartitionNames.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Block, (int)metadata.BlockSize);
        Assert.DoesNotContain("boot", metadata.PartitionNames);
        Assert.Equal(["system", "vendor"], metadata.DynamicPartitionMetadata.Groups[0].PartitionNames.ToArray());

        foreach (var partition in metadata.Manifest.Partitions)
        {
            var extracted = Path.Combine(_root, "out-" + partition.PartitionName + ".img");
            resultStream.Position = 0;
            await new PayloadExtractor().ExtractPartitionAsync(partition, extracted, resultStream, metadata.BaseOffset,
                metadata.BlockSize, token);
            var expected = partition.PartitionName switch
            {
                "system" => ReadImage(sourcePath),
                "vendor" => ReadImage(newVendorPath),
                "odm" => ReadImage(addedPath),
                _ => throw new InvalidOperationException($"unexpected partition {partition.PartitionName}")
            };
            Assert.Equal(expected, await File.ReadAllBytesAsync(extracted, token));
        }
    }

    [Fact]
    public async Task PatchAsync_KeepsBaseImageAndSigning_ProducesVerifiableSignedPayload()
    {
        var token = TestContext.Current.CancellationToken;
        var systemPath = WriteImage("system", 2, 0x11);
        var vendorPath = WriteImage("vendor", 2, 0x22);
        var basePayload = Path.Combine(_root, "signed-base.img");
        using (var rsa = RSA.Create(2048))
        {
            using (var builder = new PayloadBuilder().WithBlockSize(Block).WithSigningKey(rsa))
            {
                _ = builder.AddPartition("system", systemPath);
                builder.Build(basePayload);
            }

            var output = Path.Combine(_root, "signed-patched.img");
            var patch = new PayloadPartitionPatch
            {
                ReplaceImagePaths = new Dictionary<string, string>(StringComparer.Ordinal),
                SigningKey = rsa
            };
            await using (var input = File.OpenRead(basePayload))
            await using (var destination = File.Create(output))
            {
                await PayloadPartitionPatcher.PatchAsync(input, destination, patch, cancellationToken: token);
            }

            await using var resultStream = File.OpenRead(output);
            var metadata = PayloadParser.GetPayloadMetadata(resultStream);
            Assert.Equal(["system"], metadata.PartitionNames);
        }
    }

    [Fact]
    public async Task PatchAsync_ReplacesAbsentPartition_Throws()
    {
        var token = TestContext.Current.CancellationToken;
        var systemPath = WriteImage("system", 2, 0x11);
        var missingPath = WriteImage("missing-src", 1, 0x99);
        var basePayload = Path.Combine(_root, "absent-base.img");
        using (var builder = new PayloadBuilder().WithBlockSize(Block))
        {
            _ = builder.AddPartition("system", systemPath);
            builder.Build(basePayload);
        }

        var patch = new PayloadPartitionPatch
        {
            ReplaceImagePaths = new Dictionary<string, string>(StringComparer.Ordinal) { ["ghost"] = missingPath }
        };
        await using var input = File.OpenRead(basePayload);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<PayloadGenerationException>(
            () => PayloadPartitionPatcher.PatchAsync(input, destination, patch, cancellationToken: token));
    }

    private static DynamicPartitionMetadata Group(string name, string[] partitions)
    {
        var metadata = new DynamicPartitionMetadata();
        var group = new DynamicPartitionGroup { Name = name, Size = 1024UL * 1024 * 1024 };
        foreach (var partition in partitions)
        {
            group.PartitionNames.Add(partition);
        }
        metadata.Groups.Add(group);
        return metadata;
    }

    private string WriteImage(string prefix, int blocks, byte fill)
    {
        var path = Path.Combine(_root, prefix + ".img");
        var data = new byte[blocks * Block];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(fill ^ (index % 251));
        }
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] ReadImage(string path) => File.ReadAllBytes(path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}