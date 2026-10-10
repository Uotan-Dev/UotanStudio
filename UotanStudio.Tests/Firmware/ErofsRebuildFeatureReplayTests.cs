using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
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
        uint? profileIncompat = replay.Profile.FeatureIncompat;
        uint? profileCompat = replay.Profile.FeatureCompat;
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
        uint? profileIncompat = replay.Profile.FeatureIncompat;
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
        uint? profileIncompat = replay.Profile.FeatureIncompat;
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
    public void Capture_Restore_SourceWithNlinkOneAdvice_ReplaysDotOmittedEnabledAndKeepsInodeBit()
    {
        using var source = BuildAdviceFixture(false, out var image);
        var census = AdviceBitCensus(image);
        Assert.True(census.Files > 0);
        Assert.Equal(census.Files, census.WithAdviceBit);

        var replay = Replay(image, source.Path);
        Assert.Equal(ErofsTriStateFeature.Enabled.ToString(),
            JsonNode.Parse(ToJson(replay.Profile))!.AsObject()["dotOmitted"]!.GetValue<string>());
        Assert.Equal(ErofsTriStateFeature.Enabled, replay.Restored.DotOmitted);

        var rebuilt = new ErofsBuilder().Build(replay.Extracted, replay.Restored);
        Assert.Equal(census.Files, AdviceBitCensus(rebuilt).WithAdviceBit);
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
        Assert.Equal(census.Files, census.WithAdviceBit);

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
            (int)(rebuiltSuperblock.DevtSlotoff * ErofsDeviceSlot.Size), ErofsDeviceSlot.Size));
        Assert.Equal("other.img", Encoding.UTF8.GetString(slot.Tag).TrimEnd('\0'));
        Assert.Equal((ulong)(ExtraDeviceBytes / BlockSize), slot.Blocks);
        using var parser = new ErofsParser(rebuilt);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(ErofsSuperblock.IncompatChunkedFile, "chunked_file")]
    [InlineData(ErofsSuperblock.IncompatXattrPrefixes, "xattr_prefixes")]
    [InlineData(ErofsSuperblock.Incompat48Bit, "48bit")]
    public void Restore_ProfileWithReadOnlyIncompatBit_ThrowsUnsupportedFeatureNamingUpstream(uint incompat,
        string upstreamName)
    {
        var parameters = new ErofsRebuildParameters
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = "Lz4",
            SuperblockChecksum = true,
            LegacyCompatibility = true,
            FeatureCompat = ErofsSuperblock.CompatSbChecksum,
            FeatureIncompat = incompat,
        };

        var error = Assert.Throws<FileSystemUnsupportedFeatureException>(
            () => ErofsRebuildMetadata.Restore(parameters));

        Assert.Contains(upstreamName, error.Message, StringComparison.Ordinal);
        Assert.Contains("ErofsFeatures=!", error.Message, StringComparison.Ordinal);
        Assert.Equal(upstreamName, ErofsFeatureTable.ReadOnlyIncompatNames(incompat));
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

    private static ErofsFeatureSet Declare(params ErofsFeature[] features)
    {
        var set = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, 0u);
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
            LegacyCompatibility = legacy,
        });
        return source;
    }

    private static (int Files, int WithAdviceBit) AdviceBitCensus(byte[] image)
    {
        var superblock = ErofsSuperblock.FromImage(image);
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
        => JsonSerializer.Deserialize<ErofsRebuildParameters>(json,
            FileSystemPreservationJsonContext.Default.ErofsRebuildParameters)!;

    private sealed record ReplayProfile(string Extracted, ErofsOptions Restored, ErofsRebuildParameters Profile);
}