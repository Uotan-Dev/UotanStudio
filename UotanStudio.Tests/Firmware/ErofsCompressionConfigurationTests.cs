using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Linux EROFS compression configuration records. <para>检查 Linux EROFS 压缩配置记录。</para></summary>
public class ErofsCompressionConfigurationTests
{
    private const int BlockSize = 4096;

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
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x53, 8192).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 1 << 20,
            Lz4MaxDistance = (ushort)lz4MaxDistance
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset)));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset + 2, 2)));
        Assert.Equal((1 << 20) / BlockSize, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset + 4, 2)));
    }

    [Fact]
    public void Build_DefaultPclusterSizeWithLz4_DerivesOnlyZeroPadding()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x53, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        };
        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(BlockSize, options.MaxPclusterSize);
        Assert.False(options.FeaturesAuthoritative);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);

        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        var declared = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset));
        Assert.False(declared is 14 or 6, "a one-block LZ4 build must not carry a compression configuration record.");

        using var parser = new ErofsParser(image);
        Assert.True(parser.MaxCompressedPclusterBytes() <= BlockSize,
            $"a one-block pcluster build must keep every frame inside one block; maxBytes={parser.MaxCompressedPclusterBytes()}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_ExplicitZtailpackingWithoutConfigs_LeavesTheSharedBitClear()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressedTailPacking = true,
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(
            ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatZtailpacking,
            superblock.FeatureIncompat);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_PclusterSizeAboveOneBlock_DeclaresBigPclusterAndConfigRecords()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x63, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 4 * BlockSize,
        });
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster,
            superblock.FeatureIncompat);
        Assert.Equal(1 << (int)ErofsCompressionAlgorithm.Lz4, superblock.AvailableComprAlgs);

        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset)));
        Assert.Equal(ErofsCompression.Lz4DistanceMax,
            BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset + 2, 2)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset + 4, 2)));

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_DedupeOnly_DeclaresTheSharedFragmentBit()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x5A, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Dedup = true,
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(
            ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatFragments,
            superblock.FeatureIncompat);

        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(true, true, 0x00000007u)]
    [InlineData(false, true, 0x00000006u)]
    [InlineData(true, false, 0x00000005u)]
    [InlineData(false, false, 0x00000004u)]
    public void Build_DeclaresXattrFilterCompatBit_AlongsideSourceCompat(bool superblockChecksum, bool mtime, uint expectedCompat)
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x63, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            SuperblockChecksum = superblockChecksum,
            SourceMtimeFeature = mtime,

            SourceXattrFilterFeature = true,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["data.bin"] = [new ErofsXattrEntry { Name = "user.role", Value = "system"u8.ToArray() }],
            },
        });
        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(expectedCompat, superblock.FeatureCompat);
    }

    [Fact]
    public void PreservedMetadata_RoundTrips_XattrFilterFeature()
    {
        var options = new ErofsOptions { SourceXattrFilterFeature = true };
        var parameters = ErofsRebuildMetadata.Capture(options);
        var restored = ErofsRebuildMetadata.Restore(parameters);
        Assert.True(restored.SourceXattrFilterFeature);
    }

    [Fact]
    public void Restore_SourceProfile_ReplaysExactWordsAndCodecGeometry()
    {
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 1 << 20,
            UseCompactedIndexes = true,
            Features = ErofsFeatureSet.FromWords(
                ErofsSuperblock.CompatSbChecksum,
                ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster),
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
        Assert.Equal(1 << 20, codec.MaxPclusterSize);
        Assert.True(restored.UseCompactedIndexes);
        Assert.True(restored.FeaturesAuthoritative);
        Assert.Equal(
            ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster,
            restored.Features.IncompatBits);
        Assert.Equal(ErofsSuperblock.CompatSbChecksum, restored.Features.CompatBits);
        Assert.Equal(ErofsTriStateFeature.Enabled, restored.DotOmitted);
        Assert.True(codec.FeaturesAuthoritative);
        Assert.Equal(restored.Features.IncompatBits, codec.Features.IncompatBits);
        Assert.Equal(restored.Features.CompatBits, codec.Features.CompatBits);
        Assert.Equal(restored.DotOmitted, codec.DotOmitted);
    }

    [Fact]
    public void Build_Legacy_DensePclusters_StayKernelReadable()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", AlternatingCompressionPayload());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            OptimizeCompression = true,
        });
        using var parser = new ErofsParser(image);
        Assert.True(parser.MaxCompressedClusterOfs() > 0, "legacy builds are expected to pack densely.");
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
        Assert.Equal(AlternatingCompressionPayload(), parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_Legacy_PartialTail_KeepsKernelClusterCoverage()
    {
        using var source = new TempDirectory();
        source.WriteBytes("compressible.bin", Enumerable.Repeat((byte)0x42, 11128).ToArray());
        source.WriteBytes("tiny.bin", Enumerable.Repeat((byte)0x5a, 100).ToArray());
        var random = new byte[11128];
        new Random(20260918).NextBytes(random);
        source.WriteBytes("incompressible.bin", random);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            OptimizeCompression = true,
        });
        using var parser = new ErofsParser(image);
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
        Assert.Equal(Enumerable.Repeat((byte)0x42, 11128).ToArray(), parser.ReadFileBytes("compressible.bin"));
        Assert.Equal(Enumerable.Repeat((byte)0x5a, 100).ToArray(), parser.ReadFileBytes("tiny.bin"));
        Assert.Equal(random, parser.ReadFileBytes("incompressible.bin"));
    }

    [Fact]
    public void Build_Legacy_MidClusterTail_StaysPlainForOldKernels()
    {
        const byte plain = 0, nonHead = 2;
        var fixture = new (string Name, int Size)[]
        {
            ("mixed-a.bin", 5 * 4096 + 1234),
            ("mixed-b.bin", 9 * 4096 + 777),
            ("mixed-c.bin", 3 * 4096 + 2049),
            ("mixed-d.bin", 17 * 4096 + 33),
        };
        using var source = new TempDirectory();
        var random = new Random(20260918);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, size) in fixture)
        {
            var payload = new byte[size];
            for (var offset = 0; offset < size; offset += 6144)
            {
                Array.Fill(payload, (byte)0x41, offset, Math.Min(4096, size - offset));
                if (offset + 4096 >= size) continue;
                var length = Math.Min(2048, size - offset - 4096);
                random.NextBytes(payload.AsSpan(offset + 4096, length));
            }
            payloads[name] = payload;
            source.WriteBytes(name, payload);
        }
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            OptimizeCompression = true,
        });
        using var parser = new ErofsParser(image);
        var midClusterTails = 0;
        foreach (var (name, _) in fixture)
        {
            var rows = parser.DumpIndexClusters(name);
            Assert.True(rows.Count > 1, $"{name} needs more than one logical cluster to be meaningful.");
            var tail = rows[..^1].Last(row => row.Type != nonHead);
            Assert.True(tail.Type == plain || tail.ClusterOfs == 0,
                $"{name} stores its final extent as compressed at clusterofs {tail.ClusterOfs}.");
            midClusterTails += tail.ClusterOfs == 0 ? 0 : 1;
            Assert.Equal(payloads[name], parser.ReadFileBytes(name));
        }
        Assert.True(midClusterTails > 0, "the fixture produced no mid-cluster tail, so the rule went untested.");
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
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