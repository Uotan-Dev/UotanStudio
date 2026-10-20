using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsMetadataReplayTests
{
    private static readonly ulong Timestamp = (ulong)DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
    private const uint TimestampNanoseconds = 123_456_789;
    private const int BlockSize = 4096;

    [Fact]
    public void Build_ParsedEntryMetadata_MatchesTheAttributesTheBuildWasGiven()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, OptionsFor(SourceAttributes(), FileXattrs()));

        using var parser = new ErofsParser(image);
        var entries = parser.EnumerateEntries().ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        Assert.Equal(
            [".", "heavy.bin", "meta", "meta/data.bin", "meta/tiny.cfg"],
            entries.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var (path, expected) in SourceAttributes())
        {
            var actual = entries[path];
            Assert.Equal(expected.Mode, actual.Mode);
            Assert.Equal(expected.Uid, actual.Uid);
            Assert.Equal(expected.Gid, actual.Gid);
            Assert.Equal(Timestamp, actual.MTime);
        }

        Assert.Equal(3u, entries["."].LinkCount);
        Assert.Equal(2u, entries["meta"].LinkCount);
        Assert.Equal(1u, entries["meta/data.bin"].LinkCount);
        Assert.Equal(
            "u:object_r:vendor_file:s0",
            Encoding.UTF8.GetString(entries["meta/data.bin"].Xattrs!.Single().Value));
        Assert.Null(entries["heavy.bin"].Xattrs);
    }

    [Fact]
    public void Build_WideOwnershipForcesTheExtendedInodeAndSurvivesTheParse()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, OptionsFor(SourceAttributes(), FileXattrs()));

        using var parser = new ErofsParser(image);
        var wide = parser.EnumerateEntries().Single(entry => entry.Path == "meta/data.bin");
        var narrow = parser.EnumerateEntries().Single(entry => entry.Path == "meta/tiny.cfg");

        Assert.Equal(70_000u, wide.Uid);
        Assert.Equal(70_000u, wide.Gid);
        Assert.Equal(0u, narrow.Uid);
        Assert.True(ErofsSuperblock.FromImage(image).Inos > 0);
    }

    [Fact]
    public void ReadRebuildOptions_CarriesEveryPerInodeCompressionDecision()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, OptionsFor(SourceAttributes(), FileXattrs()));

        using var parser = new ErofsParser(image);
        var options = parser.ReadRebuildOptions();

        Assert.True(options.FeaturesAuthoritative);
        Assert.False(options.Features.IsAuto);
        Assert.NotNull(options.SourceInodesByPath);
        var byPath = options.SourceInodesByPath!;
        Assert.True(byPath["heavy.bin"].SourceCompressed);
        Assert.Equal(Timestamp, byPath["heavy.bin"].CTime);
        Assert.Equal(TimestampNanoseconds, byPath["heavy.bin"].CTimeNsec);
        Assert.Equal(BlockSize, byPath["heavy.bin"].LogicalClusterSize);
        Assert.False(byPath["meta/tiny.cfg"].SourceCompressed);
    }

    [Fact]
    public void Restore_ProfileWithoutInodeRecords_LeavesNoPerPathStrategy()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, OptionsFor(SourceAttributes(), FileXattrs()));

        ErofsOptions captured;
        using (var parser = new ErofsParser(image))
        {
            captured = parser.ReadRebuildOptions();
        }

        var document = new ErofsRebuildParameters
        {
            BlockSize = captured.BlockSize,
            Compress = captured.Compress,
            Algorithm = captured.Algorithm.ToString(),
            CompressionLevel = captured.CompressionLevel,
            TailPacking = captured.TailPacking,
            CompressedTailPacking = captured.CompressedTailPacking,
            UseCompactedIndexes = captured.UseCompactedIndexes,
            Fragments = captured.Fragments,
            Dedup = captured.Dedup,
            Metabox = captured.Metabox,
            SuperblockChecksum = captured.SuperblockChecksum,
            BuildTime = captured.BuildTime,
            BuildTimeNanoseconds = captured.BuildTimeNanoseconds,
            BuildTimeOffset = captured.BuildTimeOffset,
            FeatureCompat = captured.Features.CompatBits,
            FeatureIncompat = captured.Features.IncompatBits,
            DotOmitted = captured.DotOmitted.ToString(),
            DictionarySize = captured.DictionarySize,
            WindowBits = captured.WindowBits,
            Lz4MaxDistance = captured.Lz4MaxDistance,
            MaxPclusterSize = captured.MaxPclusterSize,
            LegacyCompatibility = captured.LegacyCompatibility,
        };
        foreach (var codec in captured.SourceCompressionOptions!)
        {
            document.Codecs.Add(new ErofsRebuildCodec
            {
                Algorithm = codec.Key.ToString(),
                BlockSize = codec.Value.BlockSize,
                MaxPclusterSize = codec.Value.MaxPclusterSize,
                DictionarySize = codec.Value.DictionarySize,
                WindowBits = codec.Value.WindowBits,
                Lz4MaxDistance = codec.Value.Lz4MaxDistance,
                CompressionLevel = codec.Value.CompressionLevel,
            });
        }

        var restored = ErofsRebuildMetadata.Restore(document);

        Assert.Null(restored.SourceInodes);
        Assert.Null(restored.SourceInodesByPath);
        Assert.Equal(captured.Features.CompatBits, restored.Features.CompatBits);
        Assert.Equal(captured.Features.IncompatBits, restored.Features.IncompatBits);
        Assert.True(restored.FeaturesAuthoritative);
        Assert.True(restored.Compress);
        Assert.Equal(captured.Algorithm, restored.Algorithm);
        Assert.Equal((ulong?)Timestamp, restored.BuildTime);
        Assert.Equal(TimestampNanoseconds, restored.BuildTimeNanoseconds);
    }

    [Fact]
    public async Task FormatHandlerUnpackPack_ReplaysOwnershipModesXattrsTimesAndSuperblockDeclarations()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var imagePath = Path.Combine(temp.Path, "source.img");
        await File.WriteAllBytesAsync(imagePath, new ErofsBuilder().Build(temp.Path, OptionsFor(SourceAttributes(), FileXattrs())),
            TestContext.Current.CancellationToken);

        var extracted = Path.Combine(temp.Path, "extracted");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.True(File.Exists(Path.Combine(extracted, "metadata.json")));

        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);
        Assert.NotNull(packed.Metadata);
        Assert.Equal("True", packed.Metadata!["EquivalentStructure"]);
        Assert.Equal("True", packed.Metadata["EquivalentContent"]);

        var source = ReadEntries(imagePath);
        var rebuilt = ReadEntries(rebuiltPath);
        Assert.Equal(
            source.Keys.OrderBy(key => key, StringComparer.Ordinal),
            rebuilt.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (path, expected) in source)
        {
            var actual = rebuilt[path];
            Assert.Equal(expected.Mode, actual.Mode);
            Assert.Equal(expected.Uid, actual.Uid);
            Assert.Equal(expected.Gid, actual.Gid);
            Assert.Equal(expected.LinkCount, actual.LinkCount);
            Assert.Equal(expected.MTime, actual.MTime);
            Assert.Equal(expected.MTimeNanoseconds, actual.MTimeNanoseconds);
            Assert.Equal(
                expected.Xattrs?.Select(xattr => (xattr.Name, Encoding.UTF8.GetString(xattr.Value))).OrderBy(pair => pair.Name),
                actual.Xattrs?.Select(xattr => (xattr.Name, Encoding.UTF8.GetString(xattr.Value))).OrderBy(pair => pair.Name));
        }

        var sourceSuperblock = SuperblockOf(imagePath);
        var rebuiltSuperblock = SuperblockOf(rebuiltPath);
        Assert.Equal(sourceSuperblock.FeatureCompat, rebuiltSuperblock.FeatureCompat);
        Assert.Equal(sourceSuperblock.FeatureIncompat, rebuiltSuperblock.FeatureIncompat);
        Assert.Equal(sourceSuperblock.BlockSizeBits, rebuiltSuperblock.BlockSizeBits);
        Assert.Equal(sourceSuperblock.VolumeName, rebuiltSuperblock.VolumeName);
        Assert.Equal(sourceSuperblock.BuildTime, rebuiltSuperblock.BuildTime);
    }

    [Fact]
    public async Task FormatHandlerUnpackPack_ReplaysRecordedInodeNumbersThroughTheMetadataDocument()
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var imagePath = Path.Combine(temp.Path, "source.img");
        var sourceOptions = OptionsFor(SourceAttributes(), FileXattrs());
        sourceOptions.SuperblockChecksum = false;
        var image = new ErofsBuilder().Build(temp.Path, sourceOptions);
        var sourceNumbers = ErofsInodeNumberFixture.Stamp(image);
        Assert.Equal(5, sourceNumbers.Count);
        await File.WriteAllBytesAsync(imagePath, image, TestContext.Current.CancellationToken);

        var extracted = Path.Combine(temp.Path, "extracted");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(extracted, RepackMetadata.FileName), TestContext.Current.CancellationToken))!.AsObject();
        var records = document["fileSystemPreservation"]!["rebuild"]!["erofs"]!["inodes"]!.AsArray();
        foreach (var (path, expected) in sourceNumbers)
        {
            Assert.Contains(records, record => (string?)record!["path"] == path
                && record["ino"] is { } recorded && recorded.GetValue<uint>() == expected);
        }

        File.Delete(imagePath);
        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        Assert.Equal(sourceNumbers, ErofsInodeNumberFixture.Read(
            await File.ReadAllBytesAsync(rebuiltPath, TestContext.Current.CancellationToken)));
    }

    private static ErofsSuperblock SuperblockOf(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        using var parser = new ErofsParser(stream);
        return parser.Superblock;
    }

    private static Dictionary<string, ErofsFileEntry> ReadEntries(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        using var parser = new ErofsParser(stream);
        return parser.EnumerateEntries().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
    }

    private static void WriteTree(TempDirectory temp)
    {
        temp.WriteBytes("heavy.bin", [.. Enumerable.Repeat((byte)0x5A, 40 * 1024)]);
        temp.WriteBytes("meta/data.bin", [.. Enumerable.Repeat((byte)0x11, 2 * 1024)]);
        temp.WriteBytes("meta/tiny.cfg", "ro.secure=1\n"u8.ToArray());
    }

    private static Dictionary<string, FsConfigEntry> SourceAttributes() => new(StringComparer.Ordinal)
    {
        ["."] = new FsConfigEntry(0, 0, 0x41ED),
        ["meta"] = new FsConfigEntry(2_000, 2_000, 0x41C0),
        ["meta/data.bin"] = new FsConfigEntry(70_000, 70_000, 0x81A4),
        ["meta/tiny.cfg"] = new FsConfigEntry(0, 0, 0x81B4),
        ["heavy.bin"] = new FsConfigEntry(0, 0, 0x89ED),
    };

    private static Dictionary<string, IReadOnlyList<ErofsXattrEntry>> FileXattrs() => new(StringComparer.Ordinal)
    {
        ["meta/data.bin"] = [new ErofsXattrEntry
        {
            Name = "security.selinux",
            Value = "u:object_r:vendor_file:s0"u8.ToArray(),
        }],
    };

    private static ErofsOptions OptionsFor(
        IReadOnlyDictionary<string, FsConfigEntry> attributes,
        IReadOnlyDictionary<string, IReadOnlyList<ErofsXattrEntry>> xattrs) => new()
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            BuildTime = Timestamp,
            BuildTimeNanoseconds = TimestampNanoseconds,
            VolumeName = "vendor",
            FsConfig = attributes,
            FileXattrs = xattrs,
        };
}