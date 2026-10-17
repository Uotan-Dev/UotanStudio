using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Drives the EROFS inherit path end to end inside the repository: capture the verified source profile,
/// persist it as the structured metadata document, restore it, rebuild the extracted tree and re-parse the
/// output, so the rebuilt superblock words are compared against the source words instead of against a
/// freshly derived layout.
/// <para>在仓库内贯通驱动 EROFS 继承链路：采集已校验源档案、以结构化元数据文档持久化、还原、重建解包目录树并
/// 重新解析输出，使重建后的超级块特性字与源特性字比对，而不是与重新推导的布局比对。</para>
/// </summary>
public sealed class ErofsRebuildFeatureReplayTests
{
    private const int BlockSize = 4096;
    private const uint ModernIncompat = ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster;
    private const uint FragmentedLegacyIncompat =
        ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatFragments;
    private const long ExtraDeviceBytes = 1L << 20;

    [Fact]
    public void Capture_Restore_ModernSourceProfile_ReplaysSourceWordAndCodecPclusterSize()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x53, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 4 * BlockSize,
            Features = Declare(ErofsFeature.Lz4ZeroPadding, ErofsFeature.CompressionConfigs),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ModernIncompat, sourceSuperblock.FeatureIncompat);

        var replay = Replay(image, source.Path);
        var profileIncompat = replay.Profile.FeatureIncompat;
        var profileCompat = replay.Profile.FeatureCompat;
        Assert.Equal(sourceSuperblock.FeatureIncompat, profileIncompat);
        Assert.Equal(sourceSuperblock.FeatureCompat, profileCompat);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(sourceSuperblock.FeatureIncompat, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal(sourceSuperblock.FeatureCompat, rebuiltSuperblock.FeatureCompat);
        Assert.Equal((ushort)4, ReadPclusterBlocks(rebuilt, rebuiltSuperblock));
        Assert.Equal(4 * BlockSize,
            replay.Restored.SourceCompressionOptions![ErofsCompressionAlgorithm.Lz4].MaxPclusterSize);
        Assert.True(replay.Restored.Features.Contains(ErofsFeature.CompressionConfigs));
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Pack_PreservedModernDefaultErofsOnlyLz4ZeroPadding_RebuildKeepsDotOmittedAndModernLayouts()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x74, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            Features = Declare(ErofsFeature.Lz4ZeroPadding),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, sourceSuperblock.FeatureIncompat);
        Assert.False(sourceSuperblock.HasBigPcluster);
        Assert.False(sourceSuperblock.HasCompressionConfigs);

        var replay = Replay(image, source.Path);

        Assert.False(replay.Restored.LegacyCompatibility);
        Assert.Equal(ErofsTriStateFeature.Disabled, replay.Restored.DotOmitted);
        Assert.Equal(
            ErofsSuperblock.IncompatLz4_0Padding, replay.Profile.FeatureIncompat);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, rebuiltSuperblock.FeatureIncompat);
        Assert.False(rebuiltSuperblock.HasBigPcluster);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Capture_Restore_LegacySourceProfileForGki510_ReplaysLz4ZeroPaddingWithoutBigPcluster()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            Features = Declare(ErofsFeature.Lz4ZeroPadding),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, sourceSuperblock.FeatureIncompat);

        var replay = Replay(image, source.Path);
        var profileIncompat = replay.Profile.FeatureIncompat;
        Assert.Equal(sourceSuperblock.FeatureIncompat, profileIncompat);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal(0u, rebuiltSuperblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, rebuiltSuperblock.AvailableComprAlgs);
        Assert.False(ReadConfigurationLength(rebuilt, rebuiltSuperblock) is 14 or 6,
            "a replayed legacy profile must not emit a compression configuration record.");
        Assert.True(replay.Restored.LegacyCompatibility);
        Assert.Equal(5, replay.Restored.Features.MinimumKernelMajor);
        Assert.Equal(4, replay.Restored.Features.MinimumKernelMinor);
        Assert.False(replay.Restored.Features.Contains(ErofsFeature.BigPcluster));
        Assert.False(replay.Restored.Features.Contains(ErofsFeature.CompressionConfigs));
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Capture_Restore_FragmentsSourceWithoutBigPcluster_ReplaysSourceWordAndKeepsFramesInOneBlock()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x5A, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("tail.bin", Enumerable.Repeat((byte)0x41, 1000).ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Fragments = true,
            MaxPclusterSize = BlockSize,
            UseCompactedIndexes = false,
            Features = Declare(ErofsFeature.Lz4ZeroPadding, ErofsFeature.Fragments),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(FragmentedLegacyIncompat, sourceSuperblock.FeatureIncompat);

        var replay = Replay(image, source.Path);
        var profileIncompat = replay.Profile.FeatureIncompat;
        Assert.Equal(sourceSuperblock.FeatureIncompat, profileIncompat);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(FragmentedLegacyIncompat, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal(0u, rebuiltSuperblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, rebuiltSuperblock.AvailableComprAlgs);
        Assert.True(replay.Restored.Features.Contains(ErofsFeature.Dedupe));
        Assert.Equal(6, replay.Restored.Features.MinimumKernelMajor);
        Assert.Equal(1, replay.Restored.Features.MinimumKernelMinor);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        Assert.Equal(1000, parser.ReadFileBytes("tail.bin").Length);
        Assert.True(parser.MaxCompressedPclusterBytes() <= BlockSize,
            $"a declaration without big_pcluster must keep every frame inside one block; saw {parser.MaxCompressedPclusterBytes()}.");
    }

    [Fact]
    public void Restore_InheritedDeclarationWithoutModernLayout_KeepsDeclaredBitVerbatim()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x37, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            UseCompactedIndexes = false,
            Features = Declare(ErofsFeature.Lz4ZeroPadding, ErofsFeature.CompressionConfigs),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ModernIncompat, sourceSuperblock.FeatureIncompat);
        using (var probe = new ErofsParser(image))
        {
            Assert.True(probe.MaxCompressedPclusterBytes() <= BlockSize,
                $"the declared configuration record must not license a multi-block frame while the pcluster cap is one block; saw {probe.MaxCompressedPclusterBytes()}.");
        }

        var replay = Replay(image, source.Path);
        var profileIncompat = replay.Profile.FeatureIncompat;
        Assert.Equal(sourceSuperblock.FeatureIncompat, profileIncompat);
        Assert.True(replay.Restored.FeaturesAuthoritative);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(ModernIncompat, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal(1 << (int)ErofsCompressionAlgorithm.Lz4, rebuiltSuperblock.AvailableComprAlgs);
        Assert.Equal(14, ReadConfigurationLength(rebuilt, rebuiltSuperblock));
        Assert.Equal((ushort)1, ReadPclusterBlocks(rebuilt, rebuiltSuperblock));
        using var parser = new ErofsParser(rebuilt);
        Assert.True(parser.MaxCompressedPclusterBytes() <= BlockSize,
            $"an inherited declaration without a modern layout must still keep every frame inside one block; saw {parser.MaxCompressedPclusterBytes()}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Capture_Restore_SourceWithoutTheAdviseBit_ReplaysDotOmittedDisabledAndKeepsInodeBitClear()
    {
        using var source = BuildAdviceFixture(false, out var image);
        var census = AdviceBitCensus(image);
        Assert.True(census.Files > 0);
        Assert.Equal(0, census.WithAdviceBit);

        var replay = Replay(image, source.Path);
        Assert.Equal(ErofsTriStateFeature.Disabled.ToString(),
            JsonNode.Parse(ToJson(replay.Profile))!.AsObject()["dotOmitted"]!.GetValue<string>());
        Assert.Equal(ErofsTriStateFeature.Disabled, replay.Restored.DotOmitted);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        Assert.Equal(0, AdviceBitCensus(rebuilt).WithAdviceBit);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(
            1UL,
            parser.EnumerateEntries().Where(candidate => !candidate.IsDirectory).ToArray()[0].LinkCount);
    }

    [Fact]
    public void Build_TargetDeclaredToDefineTheAdviseBit_WritesItOnEverySingleLinkedFile()
    {
        using var source = BuildAdviceFixture(false, out _);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 4 * BlockSize,
            NlinkOneAdviseSupported = true,
        });

        var census = AdviceBitCensus(image, allowUnreadableFormat: true);
        Assert.True(census.Files > 0);
        Assert.Equal(census.Files, census.WithAdviceBit);
    }

    [Fact]
    public void Capture_Restore_SourceWithoutNlinkOneAdvice_ReplaysDotOmittedDisabledAndKeepsInodeBitClear()
    {
        using var source = BuildAdviceFixture(true, out var image);
        var census = AdviceBitCensus(image);
        Assert.True(census.Files > 0);
        Assert.Equal(0, census.WithAdviceBit);

        var replay = Replay(image, source.Path);
        Assert.Equal(ErofsTriStateFeature.Disabled.ToString(),
            JsonNode.Parse(ToJson(replay.Profile))!.AsObject()["dotOmitted"]!.GetValue<string>());
        Assert.Equal(ErofsTriStateFeature.Disabled, replay.Restored.DotOmitted);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        Assert.Equal(0, AdviceBitCensus(rebuilt).WithAdviceBit);
    }

    [Fact]
    public void Capture_Restore_DisabledAdviceDeclarationOnModernProfile_KeepsInodeBitClear()
    {
        using var source = BuildAdviceFixture(false, out var image);
        var census = AdviceBitCensus(image);
        Assert.Equal(0, census.WithAdviceBit);

        var replay = Replay(image, source.Path);
        replay.Restored.DotOmitted = ErofsTriStateFeature.Disabled;

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        Assert.False(replay.Restored.LegacyCompatibility);
        Assert.Equal(0, AdviceBitCensus(rebuilt).WithAdviceBit);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(64 * 1024, parser.ReadFileBytes("data.bin").Length);
    }

    [Fact]
    public void Capture_Restore_DeviceTableSource_ReplaysExtraDevicesAndKeepsIncompatBit()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x71, 16 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            ExtraDeviceCount = 1,
            ExtraDeviceImages = [new ErofsDeviceSpec("other.img", ExtraDeviceBytes)],
            Features = Declare(ErofsFeature.Lz4ZeroPadding, ErofsFeature.BigPcluster, ErofsFeature.DeviceTable),
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(ModernIncompat | ErofsSuperblock.IncompatDeviceTable, sourceSuperblock.FeatureIncompat);

        var replay = Replay(image, source.Path);
        Assert.Equal(1, replay.Profile.ExtraDeviceCount);
        var device = Assert.Single(replay.Profile.SourceDevices);
        Assert.Equal("other.img", Encoding.UTF8.GetString(device.Tag).TrimEnd('\0'));
        Assert.Equal((ulong)(ExtraDeviceBytes / BlockSize), device.Blocks);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);
        Assert.Equal(sourceSuperblock.FeatureIncompat, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal((ushort)1, rebuiltSuperblock.ExtraDevices);
        var slot = ErofsDeviceSlot.FromBytes(rebuilt.AsSpan(
            (int)(rebuiltSuperblock.DevtSlotoff * ErofsDeviceSlot.Size), ErofsDeviceSlot.Size),
            rebuiltSuperblock.Has48Bit);
        Assert.Equal("other.img", Encoding.UTF8.GetString(slot.Tag).TrimEnd('\0'));
        Assert.Equal((ulong)(ExtraDeviceBytes / BlockSize), slot.Blocks);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(ErofsSuperblock.IncompatChunkedFile, "chunked_file")]
    [InlineData(ErofsSuperblock.IncompatXattrPrefixes, "xattr_prefixes")]
    [InlineData(ErofsSuperblock.Incompat48Bit, "48bit")]
    public void Restore_ProfileWithInheritedIncompatBit_KeepsTheDeclaredBit(uint incompat, string upstreamName)
    {
        var parameters = new ErofsRebuildParameters
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = "Lz4",
            SuperblockChecksum = true,
            LegacyCompatibility = false,
            FeatureCompat = ErofsSuperblock.CompatSbChecksum,
            FeatureIncompat = incompat,
        };

        var options = ErofsRebuildMetadata.Restore(parameters);

        Assert.Equal(incompat, options.Features.IncompatBits & incompat);
        Assert.Contains(upstreamName, ErofsSuperblock.DescribeIncompat(incompat), StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_InodeAlgorithmWithoutConfigurationRecord_ThrowsCorruptionNamingAlgorithmAndPath()
    {
        var parameters = new ErofsRebuildParameters
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = "Lz4",
            LegacyCompatibility = true,
        };
        parameters.Codecs.Add(new ErofsRebuildCodec
        {
            Algorithm = "Lz4",
            BlockSize = BlockSize,
            MaxPclusterSize = BlockSize,
            Lz4MaxDistance = 65535,
        });
        parameters.Inodes.Add(new ErofsRebuildInode
        {
            Nid = 9,
            Path = "packed.bin",
            Compressed = true,
            Algorithm = "Zstd",
            LogicalClusterSize = BlockSize,
        });

        var error = Assert.Throws<FileSystemCorruptedException>(
            () => ErofsRebuildMetadata.Restore(parameters));

        Assert.Contains("Zstd", error.Message, StringComparison.Ordinal);
        Assert.Contains("packed.bin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_InodeAlgorithmMatchingConfigurationRecord_KeepsRecordAndCodecTogether()
    {
        var parameters = new ErofsRebuildParameters
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = "Lz4",
            LegacyCompatibility = true,
        };
        parameters.Codecs.Add(new ErofsRebuildCodec
        {
            Algorithm = "Lz4",
            BlockSize = BlockSize,
            MaxPclusterSize = BlockSize,
            Lz4MaxDistance = 65535,
        });
        parameters.Inodes.Add(new ErofsRebuildInode
        {
            Nid = 9,
            Path = "packed.bin",
            Compressed = true,
            Algorithm = "Lz4",
            LogicalClusterSize = BlockSize,
        });

        var restored = ErofsRebuildMetadata.Restore(parameters);

        Assert.True(restored.SourceInodes!.ContainsKey(9));
        Assert.Equal(ErofsCompressionAlgorithm.Lz4, restored.SourceInodes[9].SourceAlgorithm);
        Assert.NotNull(restored.SourceCompressionOptions);
    }

    [Fact]
    public void Capture_Restore_OldSnapshotWithoutFeatureWords_KeepsImageIdentical()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x77, 20 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("small.txt", "tiny"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
        });
        var extracted = Path.Combine(source.Path, "extracted");
        ErofsRebuildParameters captured;
        using (var sourceParser = new ErofsParser(image))
        {
            sourceParser.ExtractToDirectory(extracted);
            captured = ErofsRebuildMetadata.Capture(sourceParser.ReadRebuildOptions());
        }
        var json = ToJson(captured);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, ErofsSuperblock.FromImage(image).FeatureIncompat);

        var legacyShape = FromJson(json);
        legacyShape.FeatureCompat = null;
        legacyShape.FeatureIncompat = null;
        legacyShape.DotOmitted = null;
        legacyShape.DroppedIncompatBits = null;
        var pinnedShape = FromJson(json);
        pinnedShape.FeatureCompat = ErofsSuperblock.CompatSbChecksum;
        pinnedShape.FeatureIncompat = ErofsSuperblock.IncompatLz4_0Padding;
        pinnedShape.DotOmitted = ErofsTriStateFeature.Disabled.ToString();
        pinnedShape.DroppedIncompatBits = 0u;

        var legacy = ErofsRebuildMetadata.Restore(legacyShape);
        var pinned = ErofsRebuildMetadata.Restore(pinnedShape);
        legacy.Uuid = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        pinned.Uuid = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        legacy.BuildTime = 1_700_000_000UL;
        pinned.BuildTime = 1_700_000_000UL;

        var legacyImage = new ErofsBuilder().Build(extracted, legacy);
        var pinnedImage = new ErofsBuilder().Build(extracted, pinned);

        Assert.Equal(legacyImage, pinnedImage);
        var legacySuperblock = ErofsSuperblock.FromImage(legacyImage);
        var pinnedSuperblock = ErofsSuperblock.FromImage(pinnedImage);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, legacySuperblock.FeatureIncompat);
        Assert.Equal(legacySuperblock.FeatureIncompat, pinnedSuperblock.FeatureIncompat);
        Assert.Equal(legacySuperblock.FeatureCompat, pinnedSuperblock.FeatureCompat);
        Assert.Equal(ErofsSuperblock.CompatSbChecksum, pinnedSuperblock.FeatureCompat);
        using var parser = new ErofsParser(pinnedImage);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Capture_Restore_NonZeroSourceIno_ReplaysEveryInodeNumber()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x6D, 20 * 1024).ToArray());
        source.WriteBytes("meta/small.txt", "tiny"u8.ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            Features = DeclareWithoutSuperblockChecksum(ErofsFeature.Lz4ZeroPadding),
        });
        var sourceNumbers = ErofsInodeNumberFixture.Stamp(image);
        Assert.Equal(
            new[] { ".", "data.bin", "meta", "meta/small.txt" },
            sourceNumbers.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(sourceNumbers.Values, number => Assert.True(number > 0));

        var replay = Replay(image, source.Path);

        foreach (var (path, expected) in sourceNumbers)
        {
            var record = Assert.Single(replay.Profile.Inodes, inode => inode.Path == path);
            Assert.Equal((uint?)expected, record.Ino);
        }

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltNumbers = ErofsInodeNumberFixture.Read(rebuilt);

        Assert.Equal(sourceNumbers, rebuiltNumbers);
        Assert.Equal(0x1000u, sourceNumbers["."]);
        Assert.Equal(0x1000u, rebuiltNumbers["."]);
    }

    [Fact]
    public void Restore_SnapshotWithoutRecordedIno_LeavesRebuiltInodeNumbersAtZero()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x2E, 8 * 1024).ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            Features = DeclareWithoutSuperblockChecksum(ErofsFeature.Lz4ZeroPadding),
        });
        var sourceNumbers = ErofsInodeNumberFixture.Stamp(image);
        Assert.All(sourceNumbers.Values, number => Assert.True(number > 0));

        var extracted = Path.Combine(source.Path, "extracted");
        ErofsRebuildParameters captured;
        using (var parser = new ErofsParser(image))
        {
            parser.ExtractToDirectory(extracted);
            captured = ErofsRebuildMetadata.Capture(parser.ReadRebuildOptions());
        }

        var document = JsonNode.Parse(ToJson(captured))!.AsObject();
        var records = document["inodes"]!.AsArray();
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.True(record!.AsObject().Remove("ino")));

        var legacy = FromJson(document.ToJsonString());
        Assert.All(legacy.Inodes, record => Assert.Null(record.Ino));

        var rebuilt = new ErofsBuilder().Build(extracted, ErofsRebuildMetadata.Restore(legacy));
        var rebuiltNumbers = ErofsInodeNumberFixture.Read(rebuilt);

        Assert.Equal(
            sourceNumbers.Keys.OrderBy(path => path, StringComparer.Ordinal),
            rebuiltNumbers.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(rebuiltNumbers.Values, number => Assert.Equal(0u, number));
        Assert.Equal(0u, rebuiltNumbers["."]);
    }

    private static ErofsFeatureSet Declare(params ErofsFeature[] features)
    {
        var set = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, 0u);
        foreach (var feature in features)
        {
            set = set.With(feature);
        }

        return set;
    }

    private static ErofsFeatureSet DeclareWithoutSuperblockChecksum(params ErofsFeature[] features)
    {
        var set = ErofsFeatureSet.FromWords(0u, 0u);
        foreach (var feature in features)
        {
            set = set.With(feature);
        }

        return set;
    }

    private static TempDirectory BuildAdviceFixture(bool legacy, out byte[] image)
    {
        var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x53, 64 * 1024).ToArray());
        source.WriteBytes("small.txt", "advice"u8.ToArray());
        image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = legacy ? BlockSize : 4 * BlockSize,
            LegacyCompatibility = legacy,
        });
        return source;
    }

    private static (int Files, int WithAdviceBit) AdviceBitCensus(byte[] image, bool allowUnreadableFormat = false)
    {
        var superblock = ErofsSuperblock.FromImage(image);
        if (allowUnreadableFormat)
        {
            var rawFiles = 0;
            var rawMarked = 0;
            var limit = image.Length - superblock.MetadataByteOffset;
            for (long nid = 0; nid * ErofsSuperblock.CompactInodeSize + 32 <= limit; nid++)
            {
                var slot = (int)(superblock.MetadataByteOffset + nid * ErofsSuperblock.CompactInodeSize);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(slot));
                var mode = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(slot + 4));
                if (format == 0 || (mode & 0xF000) == 0)
                {
                    continue;
                }

                var isDirectory = (mode & 0xF000) == 0x4000;
                if (isDirectory)
                {
                    continue;
                }

                var wideNlink = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(slot + 6));
                var nlinkIsOne = (format & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0 && wideNlink == 0;
                if (!nlinkIsOne && wideNlink != 1)
                {
                    continue;
                }

                rawFiles++;
                if (nlinkIsOne)
                {
                    rawMarked++;
                }
            }

            return (rawFiles, rawMarked);
        }

        using var parser = new ErofsParser(image);
        var files = 0;
        var marked = 0;
        foreach (var entry in parser.EnumerateEntries())
        {
            if (entry.IsDirectory || entry.LinkCount != 1)
            {
                continue;
            }

            files++;
            var iFormat = BinaryPrimitives.ReadUInt16LittleEndian(
                image.AsSpan((int)superblock.GetInodeOffset(entry.Nid), 2));
            if ((iFormat & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0)
            {
                marked++;
            }
        }

        return (files, marked);
    }

    [Fact]
    public void Capture_Restore_XattrFilterAndSharedEaSource_RecomputesBothDimensions()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x2C, 2048).ToArray();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        IReadOnlyList<ErofsXattrEntry> shared =
        [
            new ErofsXattrEntry { Name = "user.role", Value = "system"u8.ToArray() },
        ];
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            XattrNameFilter = true,
            IshareXattrs = true,
            Metabox = true,
            MetaboxRequired = true,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["a.bin"] = shared,
                ["b.bin"] = shared,
            },
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.True((sourceSuperblock.FeatureCompat & ErofsSuperblock.CompatXattrFilter) != 0);
        Assert.True((sourceSuperblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.True((sourceSuperblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox) != 0);

        var replay = Replay(image, source.Path);
        Assert.True(replay.Restored.XattrNameFilter);
        Assert.True(replay.Restored.IshareXattrs);

        replay.Restored.FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
        {
            ["a.bin"] = shared,
            ["b.bin"] = shared,
        };

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        var rebuiltSuperblock = ErofsSuperblock.FromImage(rebuilt);

        Assert.True((rebuiltSuperblock.FeatureCompat & ErofsSuperblock.CompatXattrFilter) != 0);
        Assert.True((rebuiltSuperblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.True((rebuiltSuperblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox) != 0);

        using var parser = new ErofsParser(rebuilt);
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith(name, StringComparison.Ordinal));
            Assert.Contains(file.Xattrs ?? [], x => x.Name == "user.role");
        }
    }

    [Fact]
    public void Capture_Restore_IshareFingerprintPrefix_ReRegistersItAndRecomputesTheDigest()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x5D, 4096).ToArray();
        source.WriteBytes("data.bin", payload);
        const string prefix = "erofs.fingerprint.v1";
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            IshareXattrs = true,
            IshareXattrPrefix = prefix,
        });
        var sourceSuperblock = ErofsSuperblock.FromImage(image);
        Assert.True((sourceSuperblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.Equal(1, sourceSuperblock.XattrPrefixCount);
        Assert.True(sourceSuperblock.IshareXattrPrefixId < sourceSuperblock.XattrPrefixCount);
        Assert.Equal(0, sourceSuperblock.IshareXattrPrefixId & ErofsXattr.LongPrefixFlag);

        var replay = Replay(image, source.Path);
        Assert.Equal(prefix, replay.Restored.IshareXattrPrefix);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        using var parser = new ErofsParser(rebuilt);
        var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith("data.bin", StringComparison.Ordinal));
        var fingerprint = Assert.Single(file.Xattrs ?? [], x => x.Name == prefix);
        var expected = new byte[7 + 32];
        "sha256:"u8.CopyTo(expected);
        System.Security.Cryptography.SHA256.HashData(payload).CopyTo(expected, 7);
        Assert.Equal(expected, fingerprint.Value);
    }

    private static ushort ReadConfigurationLength(byte[] image, ErofsSuperblock superblock)
        => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ConfigurationOffset(superblock)));

    private static ushort ReadPclusterBlocks(byte[] image, ErofsSuperblock superblock)
        => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ConfigurationOffset(superblock) + 4, 2));

    private static int ConfigurationOffset(ErofsSuperblock superblock)
        => ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize
            + superblock.SbExtSlots * ErofsSuperblock.ExtSlotSize;

    private static ReplayProfile Replay(byte[] image, string directory)
    {
        var extracted = Path.Combine(directory, "extracted");
        ErofsRebuildParameters profile;
        using (var parser = new ErofsParser(image))
        {
            parser.ExtractToDirectory(extracted);
            profile = ErofsRebuildMetadata.Capture(parser.ReadRebuildOptions());
        }

        return new ReplayProfile(extracted, ErofsRebuildMetadata.Restore(FromJson(ToJson(profile))), profile);
    }

    private static string ToJson(ErofsRebuildParameters parameters)
        => JsonSerializer.Serialize(parameters, FileSystemPreservationJsonContext.Default.ErofsRebuildParameters);

    private static ErofsRebuildParameters FromJson(string json)
        => JsonSerializer.Deserialize(json,
            FileSystemPreservationJsonContext.Default.ErofsRebuildParameters)!;

    private sealed record ReplayProfile(string Extracted, ErofsOptions Restored, ErofsRebuildParameters Profile);
}