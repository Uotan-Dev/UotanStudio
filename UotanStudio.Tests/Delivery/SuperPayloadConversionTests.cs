using UotanStudio.Delivery;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class SuperPayloadConversionTests
{
    [Fact]
    public async Task ConvertAsync_RawSuper_PreservesPartitionsAndDynamicLayout()
    {
        using var temp = new TempDirectory();
        var system = CreateImage(0x10);
        var vendor = CreateImage(0x40);
        var systemPath = temp.WriteBytes("input/system.img", system);
        var vendorPath = temp.WriteBytes("input/vendor.img", vendor);
        var super = Path.Combine(temp.Path, "super.img");
        await DeliverySuperBuilder.BuildAsync(
            [
                new DeliveryFile { Partition = "system", SourcePath = systemPath },
                new DeliveryFile { Partition = "vendor", SourcePath = vendorPath }
            ],
            new DeliverySuperOptions
            {
                DeviceSize = 16 * 1024 * 1024,
                Group = "test_dynamic_partitions",
                Sparse = false,
                VirtualAb = true,
                Slots = 2
            },
            super,
            token: TestContext.Current.CancellationToken);
        var payload = temp.WriteBytes("output/payload.bin", "old payload"u8.ToArray());
        var properties = temp.WriteBytes("output/payload_properties.txt", "old properties"u8.ToArray());

        var result = await SuperPayloadConversionService.ConvertAsync(super, payload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(payload), result.PayloadPath);
        Assert.Equal(Path.GetFullPath(properties), result.PropertiesPath);
        Assert.True(result.VirtualAb);
        Assert.Equal(["system", "vendor"], result.Partitions);
        using (var payloadStream = File.OpenRead(payload))
        {
            var metadata = PayloadParser.GetPayloadMetadata(payloadStream);
            Assert.True(metadata.Manifest.DynamicPartitionMetadata!.SnapshotEnabled);
            var group = Assert.Single(metadata.Manifest.DynamicPartitionMetadata.Groups);
            Assert.Equal("test_dynamic_partitions", group.Name);
            Assert.Equal(["system", "vendor"], group.PartitionNames);
        }
        var extracted = Path.Combine(temp.Path, "extracted");
        await PayloadParser.ExtractAsync(payload, ["system", "vendor"], extracted,
            TestContext.Current.CancellationToken);
        Assert.Equal(system, await File.ReadAllBytesAsync(Path.Combine(extracted, "system.img"), TestContext.Current.CancellationToken));
        Assert.Equal(vendor, await File.ReadAllBytesAsync(Path.Combine(extracted, "vendor.img"), TestContext.Current.CancellationToken));
        Assert.Contains("FILE_HASH=", await File.ReadAllTextAsync(properties, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertAsync_InvalidSuper_PreservesExistingOutputPair()
    {
        using var temp = new TempDirectory();
        var invalid = temp.WriteBytes("invalid-super.img", "not a super"u8.ToArray());
        var oldPayload = "old payload"u8.ToArray();
        var oldProperties = "old properties"u8.ToArray();
        var payload = temp.WriteBytes("output/payload.bin", oldPayload);
        var properties = temp.WriteBytes("output/payload_properties.txt", oldProperties);

        await Assert.ThrowsAnyAsync<Exception>(() => SuperPayloadConversionService.ConvertAsync(invalid, payload,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(oldPayload, await File.ReadAllBytesAsync(payload, TestContext.Current.CancellationToken));
        Assert.Equal(oldProperties, await File.ReadAllBytesAsync(properties, TestContext.Current.CancellationToken));
    }

    private static byte[] CreateImage(byte seed)
        => Enumerable.Range(0, 4096 * 3).Select(index => (byte)(seed + index)).ToArray();
}