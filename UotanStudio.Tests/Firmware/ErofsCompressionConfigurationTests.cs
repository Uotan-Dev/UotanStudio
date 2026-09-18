using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Linux EROFS compression configuration records. <para>检查 Linux EROFS 压缩配置记录。</para></summary>
public class ErofsCompressionConfigurationTests
{
    [Theory]
    [InlineData(ErofsCompressionAlgorithm.Lzma, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, false, 0)]
    [InlineData(ErofsCompressionAlgorithm.Lzma, true, 0)]
    [InlineData(ErofsCompressionAlgorithm.Deflate, true, 0)]
    [InlineData(ErofsCompressionAlgorithm.Zstd, true, 0)]
    public void Build_WritesKernelConfigurationBeforeInodes(ErofsCompressionAlgorithm algorithm, bool metabox, int extraDevices)
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x71, 16 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        if (extraDevices > 0)
        {
            var random = new byte[8192];
            new Random(20260907).NextBytes(random);
            source.WriteBytes("random.bin", random);
        }
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = algorithm,
            Metabox = metabox,
            ExtraDeviceCount = extraDevices
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(1 << (int)algorithm, superblock.AvailableComprAlgs);

        // fs/erofs/erofs_fs.h: a le16 length followed by the algorithm-specific packed structure.
        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset));
        Assert.Equal(algorithm == ErofsCompressionAlgorithm.Lzma ? 14 : 6, length);
        var configuration = image.AsSpan(configurationOffset + 2, length);
        if (algorithm == ErofsCompressionAlgorithm.Lzma)
        {
            Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(configuration), 4096u, 8u << 20);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(configuration[4..]));
            Assert.All(configuration[6..].ToArray(), value => Assert.Equal(0, value));
        }
        else
        {
            Assert.Equal(algorithm == ErofsCompressionAlgorithm.Deflate ? 15 : 0, configuration[0]);
            if (algorithm == ErofsCompressionAlgorithm.Zstd) Assert.InRange(configuration[1], (byte)0, (byte)10);
            Assert.All(configuration[2..].ToArray(), value => Assert.Equal(0, value));
        }
        Assert.True(superblock.RootNid * 32 >= configurationOffset + 2 + length);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(0, 65535)]
    [InlineData(4096, 4096)]
    [InlineData(16384, 16384)]
    public void Build_Lz4Record_DeclaresUpstreamDistanceDefault(int lz4MaxDistance, ushort expected)
    {
        // erofs-utils writes sbi->lz4.max_distance (default LZ4_DISTANCE_MAX = 65535) into the
        // z_erofs_lz4_cfgs record; a zero ErofsOptions distance denotes that same default.
        // erofs-utils 将 sbi->lz4.max_distance（默认 LZ4_DISTANCE_MAX = 65535）写入 z_erofs_lz4_cfgs
        // 记录；ErofsOptions 距离为 0 表示同一默认值。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x53, 8192).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Lz4MaxDistance = (ushort)lz4MaxDistance
        });
        var superblock = ErofsSuperblock.FromImage(image);
        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset)));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset + 2, 2)));
    }

    [Fact]
    public void Build_LegacyCompatibility_Lz4_OmitsBigPclusterAndPublishesDistance()
    {
        // Legacy (pre-5.13) images must not advertise the big-pcluster / COMPR_CFGS incompatible
        // feature that Linux 5.4 / 5.10 kernels cannot mount, and must carry the LZ4 max distance
        // inside s_available_compr_algs instead of an algorithm bitmap with no config record.
        // legacy（pre-5.13）镜像不得声明 5.4 / 5.10 内核无法挂载的 big-pcluster / COMPR_CFGS 不兼容
        // 特性，并应把 LZ4 最大匹配距离写入 s_available_compr_algs，同时不写压缩配置记录。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatLz4_0Padding);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);

        // The configuration never follows the superblock in legacy images; instead the inode table
        // begins immediately after it.
        // legacy 镜像中超级块后不跟随压缩配置记录，inode 表紧随其后。
        var afterSuperblock = 1024 + 128 + superblock.SbExtSlots * 16;
        var declared = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(afterSuperblock));
        Assert.False(declared is 14 or 6, "Legacy image unexpectedly carries a compression configuration record.");

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_LegacyCompatibility_StripsPost54IncompatibleFeatures()
    {
        // Linux 5.4 only understands LZ4_0_PADDING; fragments, ztailpacking, dedupe and metabox
        // are newer incompatible bits, so a legacy build must drop them even when requested.
        // Linux 5.4 只认识 LZ4_0_PADDING；fragments、ztailpacking、dedupe 与 metabox 都是更新的
        // incompat 位，legacy 构建必须在用户显式请求时仍然丢弃它们。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x5A, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            Fragments = true,
            Dedup = true,
            Metabox = true,
            CompressedTailPacking = true,
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(0u, superblock.PackedNid);

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(true, true, 0x00000007u)]   // checksum | mtime | xattrfilter
    [InlineData(false, true, 0x00000006u)]  // mtime | xattrfilter
    [InlineData(true, false, 0x00000005u)]  // checksum | xattrfilter
    [InlineData(false, false, 0x00000004u)] // xattrfilter only
    public void Build_DeclaresXattrFilterCompatBit_AlongsideSourceCompat(bool superblockChecksum, bool mtime, uint expectedCompat)
    {
        // The COMPAT_XATTRFILTER bit is carried forward from a source superblock exactly like
        // COMPAT_SB_CHECKSUM and COMPAT_MTIME, keeping a preserved rebuild bit-identical to its
        // source in feature_compat so older kernels introduce no spurious difference.
        // COMPAT_XATTRFILTER 位与 COMPAT_SB_CHECKSUM、COMPAT_MTIME 一样从源超级块继承，
        // 使保留重建在 feature_compat 上与原厂逐位一致，避免给旧内核引入多余的差异。
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x63, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            SuperblockChecksum = superblockChecksum,
            SourceMtimeFeature = mtime,
            SourceXattrFilterFeature = true,
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(expectedCompat, superblock.FeatureCompat);
    }

    [Fact]
    public void PreservedMetadata_RoundTrips_XattrFilterFeature()
    {
        // The persisted rebuild profile must survive the capture/restore round trip so a later
        // pack of the unpacked directory reproduces the source COMPAT_XATTRFILTER bit.
        // 持久化重建档案须在 capture/restore 往返后保留，这样后续对解包目录重新打包才能还原
        // 源镜像的 COMPAT_XATTRFILTER 位。
        var options = new ErofsOptions { SourceXattrFilterFeature = true };
        var parameters = ErofsRebuildMetadata.Capture(options);
        var restored = ErofsRebuildMetadata.Restore(parameters);
        Assert.True(restored.SourceXattrFilterFeature);
    }

    [Fact]
    public void Restore_LegacyProfile_CodecsInheritLegacyConstraints()
    {
        // Per-node planning consumes the codec-specific option objects, so a legacy primary
        // profile must push its constraints onto every codec or recompressed nodes would merge
        // unaligned tails under modern semantics (device-side "failed to decompress ... out[...]").
        // 逐节点规划读取的是逐 codec 的选项对象，因此 legacy 主档案必须把约束下推到每个 codec，
        // 否则被重压的节点会按现代语义把非整簇尾部并入压缩 pcluster（设备端
        // "failed to decompress ... out[...]"）。
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            SourceCompressionOptions = new Dictionary<ErofsCompressionAlgorithm, ErofsOptions>
            {
                [ErofsCompressionAlgorithm.Lz4] = new()
                {
                    Algorithm = ErofsCompressionAlgorithm.Lz4,
                    MaxPclusterSize = 1 << 20,
                    UseCompactedIndexes = true,
                },
            },
        };
        var parameters = ErofsRebuildMetadata.Capture(options);
        var restored = ErofsRebuildMetadata.Restore(parameters);
        restored.Validate();
        var codec = restored.SourceCompressionOptions![ErofsCompressionAlgorithm.Lz4];
        Assert.True(codec.LegacyCompatibility);
        Assert.Equal(codec.BlockSize, codec.MaxPclusterSize);
        Assert.False(codec.UseCompactedIndexes);
    }

    [Fact]
    public void Build_Legacy_CompressedHeads_StayClusterAligned()
    {
        // Linux 5.4 / 5.10 legacy full-index decoders place the LZ4 input at the clusterofs recorded
        // for each head lcluster; a byte-granular (dense) packer records non-zero clusterofs, which
        // those decoders cannot address. Legacy builds must therefore bypass dense packing and keep
        // every compressed head at its cluster start (clusterofs == 0).
        // legacy 全索引解码器把 LZ4 输入放在每个 head lcluster 记录的 clusterofs 处；按字节粒度打包的
        // dense 会记录非零 clusterofs，这是旧内核无法寻址的。故 legacy 构建必须绕过 dense，令每个
        // 压缩 head 位于其逻辑簇起点（clusterofs == 0）。
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", AlternatingCompressionPayload());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            // Route through the dense-by-default optimizer path used by a preserved rebuild so the
            // test genuinely guards the legacy gate inside TryPlanCompressed.
            // 与保留重建一样默认走 dense 优化路径，使该测试真正守护 TryPlanCompressed 内的 legacy 门禁。
            OptimizeCompression = true,
        });
        using var parser = new ErofsParser(image);
        Assert.Equal(0, parser.MaxCompressedClusterOfs());
    }

    [Fact]
    public void Build_Legacy_PartialTail_KeepsKernelClusterCoverage()
    {
        // Pre-big-pcluster kernels (Linux 5.4 / 5.10) demand that a compressed pcluster decodes to
        // exactly its covered whole logical clusters; they never clamp the final extent to i_size.
        // Feeding only the valid tail bytes (e.g. an 11128-byte file) therefore produces
        // "failed to decompress 11128 in[...] out[12288]" on device. The planner must keep every
        // compressed extent on whole clusters and route the unaligned tail through a Plain lcluster.
        // pre-big-pcluster 内核（Linux 5.4 / 5.10）要求压缩 pcluster 恰好解压为覆盖的整逻辑簇，
        // 且不会把最后一个 extent 裁剪到 i_size。若只喂尾部的有效字节（如 11128 字节文件），
        // 设备端就会报 "failed to decompress 11128 in[...] out[12288]"。规划器必须让每个压缩
        // extent 落在整簇上，把非整簇尾部交给 Plain lcluster。
        using var source = new TempDirectory();
        source.WriteBytes("compressible.bin", Enumerable.Repeat((byte)0x42, 11128).ToArray());
        var random = new byte[11128];
        new Random(20260918).NextBytes(random);
        source.WriteBytes("incompressible.bin", random);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            OptimizeCompression = true,
        });
        using var parser = new ErofsParser(image);
        Assert.Equal(0, parser.MaxCompressedClusterOfs());
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
        Assert.Equal(Enumerable.Repeat((byte)0x42, 11128).ToArray(), parser.ReadFileBytes("compressible.bin"));
        Assert.Equal(random, parser.ReadFileBytes("incompressible.bin"));
    }

    private static byte[] AlternatingCompressionPayload()
    {
        var bytes = new byte[2 * 1024 * 1024];
        var random = new Random(20260918);
        for (var offset = 0; offset < bytes.Length; offset += 8192)
        {
            Array.Fill(bytes, (byte)0x41, offset, 4096);
            if (offset + 8192 <= bytes.Length)
            {
                random.NextBytes(bytes.AsSpan(offset + 4096, 4096));
            }
        }

        return bytes;
    }
}