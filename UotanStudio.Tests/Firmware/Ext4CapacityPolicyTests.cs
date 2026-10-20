using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Ext4CapacityPolicyTests
{
    [Fact]
    public void Build_ExactTargetSizeAndReadOnly_UpdatesFilesystemGeometryAndFeature()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/system/build.prop", "ro.product.name=uotan\n"u8.ToArray());
        var output = Path.Combine(temp.Path, "system.img");
        const long targetSize = 16 * 1024 * 1024;

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output, new Ext4Options
        {
            TargetSizeBytes = targetSize,
            ReadOnly = true,
            CancellationToken = TestContext.Current.CancellationToken
        });

        Assert.Equal(targetSize, new FileInfo(output).Length);
        using (var parser = new Ext4Parser(output))
        {
            Assert.Equal(targetSize, parser.PartitionSize);
            Assert.Contains(parser.ListFiles(), entry => entry.Path == "system/build.prop");
        }
        using var image = File.OpenRead(output);
        image.Position = 1024 + 0x64;
        Span<byte> feature = stackalloc byte[4];
        image.ReadExactly(feature);
        Assert.NotEqual(0U, BinaryPrimitives.ReadUInt32LittleEndian(feature) & 0x1000U);
    }

    [Fact]
    public void Build_TargetTooSmall_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/large.bin", new byte[2 * 1024 * 1024]);
        var existing = "existing"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                TargetSizeBytes = 4096,
                CancellationToken = TestContext.Current.CancellationToken
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_UndefinedModes_PreserveExistingDestination()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/file", "content"u8.ToArray());
        var existing = "existing"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.Throws<ArgumentOutOfRangeException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                CapacityMode = (Ext4CapacityMode)999,
                CancellationToken = TestContext.Current.CancellationToken
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = (Ext4BlockSharingMode)999,
                CancellationToken = TestContext.Current.CancellationToken
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }
}