using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the write-side rule for a metabox container whose payload is encoded. A metabox container is an
/// ordinary compressed inode, so an encoded container is written by the same planner, index writer and frame
/// writer that serve a regular file; what these tests guard is that the writer only takes that path when the
/// source recorded a container codec this build can re-encode, and that it otherwise keeps failing loudly
/// instead of publishing an uncompressed container under the recorded feature.
/// <para>钉住 metabox 容器负载被编码时的写入侧规则。metabox 容器就是普通压缩 inode，因此编码后的容器由与常规
/// 文件相同的规划器、索引写入方与帧写入方产出；这些测试守护的是：只有源记录了本次构建能重新编码的容器编解码器
/// 时写入方才走该路径，否则继续保持显式失败，而不是在已记录特性下发布未压缩容器。</para>
/// </summary>
public sealed class ErofsMetaboxContainerCompressionTests
{
    private const int BlockSize = 4096;

    private const int ResidentSize = 512;

    private const int ResidentCount = 600;

    private const ulong PinnedBuildTime = 1_700_000_000UL;

    private static string[] WriteIncompressibleResidents(TempDirectory source)
    {
        var random = new Random(20261012);
        var names = new string[ResidentCount];
        for (var i = 0; i < ResidentCount; i++)
        {
            names[i] = $"resident-{i:D4}.bin";
            var payload = new byte[ResidentSize];
            random.NextBytes(payload);
            source.WriteBytes(names[i], payload);
        }

        return names;
    }

    [Fact]
    public void Build_RecordedContainerAlgorithm_WritesACompressedContainerThatReparsesAsCompressed()
    {
        using var source = new TempDirectory();
        var residents = WriteIncompressibleResidents(source);
        var options = PreservedContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.True(parser.MetaboxContainer?.Compressed,
            "the container inode has to carry a compressed data layout once the build encodes its payload.");
        Assert.Equal(ErofsCompressionAlgorithm.Lz4, parser.MetaboxContainer?.Algorithm);
        foreach (var name in residents)
        {
            Assert.Equal(ResidentSize, parser.ReadFileBytes(name).Length);
        }
    }

    [Fact]
    public void Build_RecordedContainerAlgorithm_ShrinksTheImageAgainstTheVerbatimContainer()
    {
        using var source = new TempDirectory();
        WriteIncompressibleResidents(source);
        var verbatim = PreservedContainersOptions();
        var encoded = PreservedContainersOptions();
        encoded.SourceMetaboxCompressed = true;
        encoded.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;

        var verbatimImage = new ErofsBuilder().Build(source.Path, verbatim);
        var encodedImage = new ErofsBuilder().Build(source.Path, encoded);

        Assert.True(encodedImage.LongLength < verbatimImage.LongLength,
            $"an encoded container has to occupy fewer bytes than the verbatim one, but {encodedImage.LongLength} "
            + $"is not below {verbatimImage.LongLength}.");
    }

    [Fact]
    public void Build_RecordedContainerAlgorithm_KeepsTheContainerLogicalLength()
    {
        using var source = new TempDirectory();
        WriteIncompressibleResidents(source);
        var options = PreservedContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var containerNid = parser.Superblock.MetaBoxNid;
        Assert.NotEqual(0UL, containerNid);
        var containerOffset = parser.Superblock.GetInodeOffset(containerNid);
        Assert.True(containerOffset > 0 && containerOffset < image.LongLength,
            $"the container inode at {containerOffset} has to sit inside the {image.LongLength}-byte image.");
    }

    [Fact]
    public void Build_RecordedContainerAlgorithm_StillResolvesEveryMetaboxResident()
    {
        using var source = new TempDirectory();
        var residents = WriteIncompressibleResidents(source);
        var options = PreservedContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        var entries = parser.EnumerateEntries().ToList();
        foreach (var name in residents)
        {
            var resident = entries.Single(entry => entry.Path == name);
            Assert.NotEqual(0UL, resident.Nid);
            Assert.Equal(ResidentSize, parser.ReadFileBytes(name).Length);
        }
    }

    [Fact]
    public void Build_VerbatimContainer_DoesNotCompressTheContainer()
    {
        using var source = new TempDirectory();
        WriteIncompressibleResidents(source);
        var options = PreservedContainersOptions();

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.False(parser.MetaboxContainer?.Compressed,
            "a source with no recorded container codec has to keep the container verbatim.");
    }

    [Fact]
    public void Build_CompressedSourceContainerWithoutARecoverableAlgorithm_StillFailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = ContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = null;
        AttachFlatSourceRecord(options, "data.bin", BlockSize);

        var error = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("metabox container is compressed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CompressedSourceContainerWithAnUndeclarableAlgorithm_StillFailsLoudly()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = ContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lzma;
        AttachFlatSourceRecord(options, "data.bin", BlockSize);

        var error = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("metabox container is compressed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoSourceInodeRecords_StoresTheContainerVerbatim()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", new byte[BlockSize]);
        var options = ContainersOptions();
        options.SourceMetaboxCompressed = true;
        options.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;

        var image = new ErofsBuilder().Build(source.Path, options);

        using var parser = new ErofsParser(image);
        Assert.False(parser.ReadRebuildOptions().SourceMetaboxCompressed,
            "a fresh build has no recorded container codec to replay, so the container stays verbatim.");
    }

    [Fact]
    public void MetaboxContainerCompressed_RequiresMetaboxCompressionAndARecoverableCodec()
    {
        var fresh = ContainersOptions();
        Assert.False(fresh.MetaboxContainerCompressed);

        var preserved = ContainersOptions();
        AttachFlatSourceRecord(preserved, "data.bin", BlockSize);
        Assert.False(preserved.MetaboxContainerCompressed);

        var recorded = ContainersOptions();
        AttachFlatSourceRecord(recorded, "data.bin", BlockSize);
        recorded.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;
        Assert.True(recorded.MetaboxContainerCompressed);

        var uncompressed = ContainersOptions();
        AttachFlatSourceRecord(uncompressed, "data.bin", BlockSize);
        uncompressed.Compress = false;
        uncompressed.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4;
        Assert.False(uncompressed.MetaboxContainerCompressed);

        var foreignCodec = ContainersOptions();
        AttachFlatSourceRecord(foreignCodec, "data.bin", BlockSize);
        foreignCodec.SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lzma;
        Assert.False(foreignCodec.MetaboxContainerCompressed,
            "a codec with no configuration record behind it cannot be declared by this build.");

        var noContainer = new ErofsOptions { SourceMetaboxAlgorithm = ErofsCompressionAlgorithm.Lz4 };
        Assert.False(noContainer.MetaboxContainerCompressed);
    }

    private static ErofsOptions PreservedContainersOptions()
    {
        var options = ContainersOptions();
        AttachFlatSourceRecord(options, "preserved.bin", BlockSize);
        return options;
    }

    private static ErofsOptions ContainersOptions()
    {
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            SuperblockChecksum = false,
            Uuid = new byte[16],
            BuildTime = PinnedBuildTime,
            Metabox = true,
            MetaboxRequired = true,
        };
        options.Features = ErofsFeatureSet.FromWords(0, ErofsSuperblock.IncompatMetabox);
        return options;
    }

    private static void AttachFlatSourceRecord(ErofsOptions options, string path, int size)
    {
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.Flat,
            Idata = new ErofsFlatIdata { BlockAddress = 0 },
            Size = (ulong)size,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            SourcePath = path,
            SourceCompressed = false,
            IFormat = 0,
        };
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue - 1] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            [path] = record,
        };
    }
}