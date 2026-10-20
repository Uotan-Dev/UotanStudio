using System.Buffers.Binary;
using System.Globalization;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Workspace state")]
public sealed class Ext4CapacityHeadroomTests
{
    private const int SuperblockOffset = 1024;
    private const int BlocksCountLowOffset = 0x04;
    private const int ReservedBlocksLowOffset = 0x08;
    private const int FreeBlocksLowOffset = 0x0C;
    private const int LogBlockSizeOffset = 0x18;
    private const int BlocksPerGroupOffset = 0x20;
    private const int DescriptorSizeOffset = 0xFE;
    private const int BlocksCountHighOffset = 0x150;
    private const int ReservedBlocksHighOffset = 0x154;
    private const int FreeBlocksHighOffset = 0x158;
    private const int GroupBlockBitmapLowOffset = 0;
    private const int GroupBlockBitmapHighOffset = 32;
    private const int GroupFreeBlocksLowOffset = 12;
    private const int GroupFreeBlocksHighOffset = 44;

    [Fact]
    public void Build_DefaultCapacityImage_ClampsRootReservationBelowFreeBlocks()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(PopulatedSource(temp, "tight"), new Ext4Options
        {
            CancellationToken = TestContext.Current.CancellationToken
        });
        var freeBlocks = ReadFreeBlocksCount(image);
        var reservedBlocks = ReadReservedBlocksCount(image);

        Assert.True(freeBlocks > 0);
        Assert.True(freeBlocks * 20UL < ReadTotalBlocksCount(image));
        Assert.Equal(freeBlocks - 1, reservedBlocks);
    }

    [Fact]
    public void Build_DefaultCapacityImage_SuperblockFreeMatchesDescriptorsAndBitmaps()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(PopulatedSource(temp, "accounting"), new Ext4Options
        {
            CancellationToken = TestContext.Current.CancellationToken
        });
        var freeBlocks = ReadFreeBlocksCount(image);

        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupDescriptors(image));
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupBitmaps(image));
        for (var group = 0; group < GroupCount(image); group++)
        {
            Assert.Equal(FreeBlocksFromGroupDescriptor(image, group), FreeBlocksFromGroupBitmap(image, group));
        }
    }

    [Fact]
    public void Build_ExplicitCapacityImage_KeepsFivePercentRootReservation()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(PopulatedSource(temp, "slack"), new Ext4Options
        {
            TargetSizeBytes = 8L * 1024 * 1024,
            CancellationToken = TestContext.Current.CancellationToken
        });
        var totalBlocks = ReadTotalBlocksCount(image);
        var freeBlocks = ReadFreeBlocksCount(image);

        Assert.Equal(2048UL, totalBlocks);
        Assert.True(freeBlocks > totalBlocks / 20UL);
        Assert.Equal(totalBlocks / 20UL, ReadReservedBlocksCount(image));
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupBitmaps(image));
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupDescriptors(image));
    }

    [Fact]
    public async Task PackAsync_DefaultCapacityImage_ReportsLowWriteHeadroomBlocks()
    {
        using var temp = new TempDirectory();
        var source = PopulatedSource(temp, "packtight");
        var output = Path.Combine(temp.Path, "packtight.img");

        var result = await new Ext4FormatHandler().PackAsync(source, output, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var image = File.ReadAllBytes(output);
        var freeBlocks = ReadFreeBlocksCount(image);
        Assert.True(freeBlocks > 0);
        Assert.True(freeBlocks < ReadTotalBlocksCount(image) / 20UL);
        Assert.True(result.Metadata.TryGetValue("LowWriteHeadroomBlocks", out var reported));
        Assert.Equal(freeBlocks.ToString(CultureInfo.InvariantCulture), reported);
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupBitmaps(image));
        Assert.Equal(freeBlocks, ReadReservedBlocksCount(image) + 1);
    }

    [Fact]
    public async Task PackAsync_ExplicitCapacityImage_OmitsLowWriteHeadroomMetadata()
    {
        using var temp = new TempDirectory();
        var source = PopulatedSource(temp, "packslack");
        await new RepackMetadata
        {
            FormatName = "EROFS",
            SourceFileName = "system.img",
            SourceLength = 8L * 1024 * 1024,
            CanPackDirectory = true,
            PackingOptions =
            {
                ["BlockSize"] = "4096",
                ["CapacityPolicy"] = "Explicit",
                ["TargetSizeBytes"] = (8L * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            },
        }.SaveAsync(source, TestContext.Current.CancellationToken);
        var output = Path.Combine(temp.Path, "packslack.img");

        FirmwareOpResult result;
        using (RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory))
        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CapacityPolicy"] = "Explicit",
            ["TargetSizeBytes"] = (8L * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
        }))
        {
            result = await new Ext4FormatHandler().PackAsync(source, output, TestContext.Current.CancellationToken);
        }

        Assert.True(result.Success, result.Summary);
        var image = File.ReadAllBytes(output);
        var freeBlocks = ReadFreeBlocksCount(image);
        Assert.Equal(2048UL, ReadTotalBlocksCount(image));
        Assert.True(freeBlocks > ReadReservedBlocksCount(image));
        Assert.False(result.Metadata.ContainsKey("LowWriteHeadroomBlocks"));
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupBitmaps(image));
    }

    [Fact]
    public void Grow_AppendedGroups_KeepFreeCountersAndReservationConsistent()
    {
        using var temp = new TempDirectory();
        var source = PopulatedSource(temp, "growslack");
        var imagePath = Path.Combine(temp.Path, "growslack.img");
        Ext4Builder.Build(source, imagePath, new Ext4Options
        {
            TargetSizeBytes = 8L * 1024 * 1024,
            CancellationToken = TestContext.Current.CancellationToken,
        });
        var blocksBefore = ReadTotalBlocksCount(File.ReadAllBytes(imagePath));

        Ext4Builder.Grow(imagePath, (long)(blocksBefore * 2));
        var image = File.ReadAllBytes(imagePath);
        var freeBlocks = ReadFreeBlocksCount(image);

        Assert.True(ReadTotalBlocksCount(image) > blocksBefore);
        Assert.True(freeBlocks > ReadTotalBlocksCount(image) / 20UL);
        Assert.True(ReadReservedBlocksCount(image) <= freeBlocks);
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupDescriptors(image));
        Assert.Equal(freeBlocks, SumFreeBlocksFromGroupBitmaps(image));
        for (var group = 0; group < GroupCount(image); group++)
        {
            Assert.Equal(FreeBlocksFromGroupDescriptor(image, group), FreeBlocksFromGroupBitmap(image, group));
        }
    }

    private static string PopulatedSource(TempDirectory temp, string name)
    {
        temp.Write($"{name}/system/build.prop", "ro.product.name=uotan\n");
        temp.Write($"{name}/system/设备.cfg", "headroom 内容\n");
        temp.Write($"{name}/nested/notes.txt", "alpha beta gamma delta epsilon\n");
        temp.WriteBytes($"{name}/nested/payload.bin", new byte[4096]);
        return Path.Combine(temp.Path, name);
    }

    private static byte[] BuildImage(string sourceDirectory, Ext4Options options)
    {
        var imagePath = Path.Combine(Path.GetDirectoryName(sourceDirectory)!, Path.GetFileName(sourceDirectory) + ".img");
        Ext4Builder.Build(sourceDirectory, imagePath, options);
        return File.ReadAllBytes(imagePath);
    }

    private static ulong ReadTotalBlocksCount(byte[] image)
        => ReadSuperblockValue(image, BlocksCountLowOffset, BlocksCountHighOffset);

    private static ulong ReadFreeBlocksCount(byte[] image)
        => ReadSuperblockValue(image, FreeBlocksLowOffset, FreeBlocksHighOffset);

    private static ulong ReadReservedBlocksCount(byte[] image)
        => ReadSuperblockValue(image, ReservedBlocksLowOffset, ReservedBlocksHighOffset);

    private static ulong ReadSuperblockValue(byte[] image, int lowOffset, int highOffset)
        => ((ulong)ReadUInt32(image, SuperblockOffset + highOffset) << 32) | ReadUInt32(image, SuperblockOffset + lowOffset);

    private static uint ReadUInt32(byte[] image, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] image, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));

    private static int BlockSize(byte[] image)
        => 1024 << (int)ReadUInt32(image, SuperblockOffset + LogBlockSizeOffset);

    private static int GroupCount(byte[] image)
    {
        var blocksPerGroup = (ulong)ReadUInt32(image, SuperblockOffset + BlocksPerGroupOffset);
        return (int)((ReadTotalBlocksCount(image) + blocksPerGroup - 1) / blocksPerGroup);
    }

    private static int GroupDescriptorOffset(byte[] image, int group)
        => BlockSize(image) + group * (int)ReadUInt16(image, SuperblockOffset + DescriptorSizeOffset);

    private static ulong SumFreeBlocksFromGroupDescriptors(byte[] image)
    {
        var free = 0UL;
        for (var group = 0; group < GroupCount(image); group++)
        {
            free += FreeBlocksFromGroupDescriptor(image, group);
        }

        return free;
    }

    private static ulong FreeBlocksFromGroupDescriptor(byte[] image, int group)
    {
        var descriptor = GroupDescriptorOffset(image, group);
        return ((ulong)ReadUInt16(image, descriptor + GroupFreeBlocksHighOffset) << 16)
            | ReadUInt16(image, descriptor + GroupFreeBlocksLowOffset);
    }

    private static ulong SumFreeBlocksFromGroupBitmaps(byte[] image)
    {
        var free = 0UL;
        for (var group = 0; group < GroupCount(image); group++)
        {
            free += FreeBlocksFromGroupBitmap(image, group);
        }

        return free;
    }

    private static ulong FreeBlocksFromGroupBitmap(byte[] image, int group)
    {
        var blockSize = (ulong)BlockSize(image);
        var blocksPerGroup = (int)ReadUInt32(image, SuperblockOffset + BlocksPerGroupOffset);
        var totalBlocks = (int)ReadTotalBlocksCount(image);
        var descriptor = GroupDescriptorOffset(image, group);
        var bitmapBlock = ((ulong)ReadUInt32(image, descriptor + GroupBlockBitmapHighOffset) << 32)
            | ReadUInt32(image, descriptor + GroupBlockBitmapLowOffset);
        var bitmapOffset = (int)(bitmapBlock * blockSize);
        var blocksInGroup = Math.Min(blocksPerGroup, totalBlocks - group * blocksPerGroup);
        var free = 0UL;
        for (var index = 0; index < blocksInGroup; index++)
        {
            if ((image[bitmapOffset + (index >> 3)] & (1 << (index & 7))) == 0)
            {
                free++;
            }
        }

        return free;
    }
}