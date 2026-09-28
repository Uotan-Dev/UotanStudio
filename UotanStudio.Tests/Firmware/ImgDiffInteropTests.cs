using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ImgDiffInteropTests
{
    private const int Block = 4096;
    private const int TotalBlocks = 1024;
    private const string OracleFixtureBundle =
        "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";

    [Fact]
    public void OracleV4Artifacts_DatReaderUnpack_RebuildsTargetImage()
    {
        var (list, newData, patchData) = UnpackFixture();
        using var temp = new TempDirectory();
        var source = SourceImage();
        var output = Path.Combine(temp.Path, "rebuilt.img");
        DatReader.Unpack(
            temp.Write("system.transfer.list", list),
            temp.WriteBytes("system.new.dat", newData),
            temp.WriteBytes("system.patch.dat", patchData),
            temp.WriteBytes("system.src.bin", source),
            output);
        Assert.Equal(TargetImage(), File.ReadAllBytes(output));
    }

    [Fact]
    public void OracleV4Artifacts_TransferList_MatchesCommandGrammar()
    {
        var (list, _, _) = UnpackFixture();
        var parsed = TransferListParser.Parse(new StringReader(list));
        Assert.Equal(4, parsed.Version);
        Assert.Equal((long)TotalBlocks, parsed.NewBlocks);
        Assert.Equal(0, parsed.StashEntries);
        Assert.Equal(5L, parsed.MaxStashedBlocks);
        Assert.Equal((long)TotalBlocks, parsed.TotalBlocks);
        Assert.Equal(5L, parsed.NewDataBlocks);
        TransferListCommandKind[] expectedKinds =
        [
            TransferListCommandKind.Diff, TransferListCommandKind.Stash, TransferListCommandKind.Move,
            TransferListCommandKind.Move, TransferListCommandKind.Free, TransferListCommandKind.Stash,
            TransferListCommandKind.Diff, TransferListCommandKind.Zero, TransferListCommandKind.Free,
            TransferListCommandKind.Move, TransferListCommandKind.New,
        ];
        Assert.Equal(expectedKinds, parsed.Commands.Select(command => command.Kind).ToArray());
        Assert.All(
            parsed.Commands.Where(command => command.StashId is not null),
            command => Assert.Equal(command.StashId, command.StashId!.ToLowerInvariant()));
        var swapped = parsed.Commands.First(command => command.Kind == TransferListCommandKind.Move && command.StashReferences.Count > 0);
        Assert.Equal([new BlockRange(600, 603)], swapped.Ranges);
        Assert.Equal(3L, swapped.SourceBlockCount);
        Assert.Empty(swapped.SourceRanges);
        Assert.Single(swapped.StashReferences);
    }

    [Fact]
    public void OracleV4TransferList_SerializeThenReparse_PreservesCommands()
    {
        var (list, _, _) = UnpackFixture();
        var first = TransferListParser.Parse(new StringReader(list));
        var roundTripped = TransferListParser.Parse(new StringReader(TransferListSerializer.Serialize(first)));
        Assert.Equal(first.Commands.Count, roundTripped.Commands.Count);
        for (var i = 0; i < first.Commands.Count; i++)
        {
            AssertCommandsEqual(first.Commands[i], roundTripped.Commands[i]);
        }
    }

    [Fact]
    public void WriteIncremental_BsdiffStyle_OracleCorpus_EmitsBsdiff40CommandsThatDatReaderApplies()
    {
        using var temp = new TempDirectory();
        var paths = WriteUotanIncremental(temp, DatIncrementalCommandStyle.Bsdiff);
        var parsed = TransferListParser.Parse(paths.List);
        Assert.All(parsed.Commands, command => Assert.NotEqual(TransferListCommandKind.Imgdiff, command.Kind));
        var patchCommands = parsed.Commands.Where(command => command.Kind == TransferListCommandKind.Diff).ToArray();
        Assert.Equal(6, patchCommands.Length);
        var patchData = File.ReadAllBytes(paths.Patch);
        Assert.All(patchCommands, command => Assert.Equal("BSDIFF40"u8.ToArray(), patchData.AsSpan((int)command.PatchOffset, 8).ToArray()));
        var output = Path.Combine(temp.Path, "rebuilt.img");
        DatReader.Unpack(paths.List, paths.New, paths.Patch, paths.Source, output);
        Assert.Equal(TargetImage(), File.ReadAllBytes(output));
    }

    [Fact]
    public void WriteIncremental_ImgdiffStyle_OracleCorpus_EmitsRecoveryV2ContainersThatDatReaderApplies()
    {
        using var temp = new TempDirectory();
        var paths = WriteUotanIncremental(temp, DatIncrementalCommandStyle.Imgdiff);
        var parsed = TransferListParser.Parse(paths.List);
        var containers = parsed.Commands.Where(command => command.Kind == TransferListCommandKind.Imgdiff).ToArray();
        Assert.Equal(6, containers.Length);
        var patchData = File.ReadAllBytes(paths.Patch);
        foreach (var command in containers)
        {
            var container = patchData.AsSpan((int)command.PatchOffset, (int)command.PatchLength);
            Assert.Equal("IMGDIFF2"u8.ToArray(), container[..8].ToArray());
            Assert.Equal(1, BitConverter.ToInt32(container[8..12]));
            Assert.Equal(0, BitConverter.ToInt32(container[12..16]));
            Assert.Equal(0L, BitConverter.ToInt64(container[16..24]));
            Assert.Equal(command.SourceBlockCount * Block, BitConverter.ToInt64(container[24..32]));
            Assert.Equal(40L, BitConverter.ToInt64(container[32..40]));
            Assert.Equal("BSDIFF40"u8.ToArray(), container[40..48].ToArray());
        }

        var output = Path.Combine(temp.Path, "rebuilt.img");
        DatReader.Unpack(paths.List, paths.New, paths.Patch, paths.Source, output);
        Assert.Equal(TargetImage(), File.ReadAllBytes(output));
    }

    [Fact(Skip = BlockImgDiffOracle.MissingOracleMessage, SkipUnless = nameof(OracleAvailable))]
    public void OracleRegeneration_PinnedCorpus_ReproducesFixtureBytes()
    {
        using var temp = new TempDirectory();
        var outDir = Path.Combine(temp.Path, "artifacts");
        Directory.CreateDirectory(outDir);
        BlockImgDiffOracle.GenerateArtifacts(outDir, TestContext.Current.CancellationToken);
        var (list, newData, patchData) = UnpackFixture();
        Assert.Equal(list, File.ReadAllText(Path.Combine(outDir, "system.transfer.list")));
        Assert.Equal(newData, File.ReadAllBytes(Path.Combine(outDir, "system.new.dat")));
        Assert.Equal(patchData, File.ReadAllBytes(Path.Combine(outDir, "system.patch.dat")));
        Assert.Equal(SourceImage(), File.ReadAllBytes(Path.Combine(outDir, "system.src.bin")));
        Assert.Equal(TargetImage(), File.ReadAllBytes(Path.Combine(outDir, "system.tgt.bin")));
    }

    [Fact(Skip = BlockImgDiffOracle.MissingOracleMessage, SkipUnless = nameof(OracleAvailable))]
    public void PythonUpdaterSimulation_WriteIncrementalArtifacts_RebuildsTargetForBothStyles()
    {
        using var temp = new TempDirectory();
        var baseDir = Path.Combine(temp.Path, "base");
        Directory.CreateDirectory(baseDir);
        File.WriteAllBytes(Path.Combine(baseDir, "system.src.bin"), SourceImage());
        File.WriteAllBytes(Path.Combine(baseDir, "system.tgt.bin"), TargetImage());
        foreach (var style in new[] { DatIncrementalCommandStyle.Bsdiff, DatIncrementalCommandStyle.Imgdiff })
        {
            var name = "uotan_" + style.ToString().ToLowerInvariant();
            var artDir = Path.Combine(temp.Path, name);
            Directory.CreateDirectory(artDir);
            WriteIncrementalInto(artDir, baseDir, style, selfVerify: false);
            BlockImgDiffOracle.VerifyArtifacts(artDir, baseDir, TestContext.Current.CancellationToken);
        }
    }

    public static bool OracleAvailable => BlockImgDiffOracle.IsAvailable;

    private static (string List, byte[] New, byte[] Patch) UnpackFixture()
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(OracleFixtureBundle));
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        using var bundle = new MemoryStream();
        decoder.CopyTo(bundle);
        var raw = bundle.ToArray();
        var offset = 0;
        var segments = new List<byte[]>(3);
        for (var i = 0; i < 3; i++)
        {
            var length = BitConverter.ToInt32(raw, offset);
            offset += 4;
            segments.Add(raw.AsSpan(offset, length).ToArray());
            offset += length;
        }

        Assert.Equal(raw.Length, offset);
        return (System.Text.Encoding.UTF8.GetString(segments[0]), segments[1], segments[2]);
    }

    private static byte[] Region(byte seed, int count, int origin)
    {
        var data = new byte[count * Block];
        for (var k = 0; k < count; k++)
        {
            var absolute = origin + k;
            for (var i = 0; i < Block; i++)
            {
                data[(k * Block) + i] = (byte)(((seed * 31) + (absolute * 7) + (i * 5)) % 251 + 5);
            }
        }

        return data;
    }

    private static byte[] SourceImage()
    {
        var image = new byte[TotalBlocks * Block];
        Place(image, 0, Region(1, 4, 0));
        Place(image, 100, Region(2, 5, 100));
        Place(image, 200, Region(3, 5, 200));
        Place(image, 500, Region(5, 3, 500));
        Place(image, 600, Region(6, 3, 600));
        Place(image, 700, Region(7, 3, 700));
        return image;
    }

    private static byte[] TargetImage()
    {
        var image = new byte[TotalBlocks * Block];
        var edited = Region(1, 4, 0);
        edited.AsSpan(Block, 64).Fill(200);
        Place(image, 0, edited);
        Place(image, 100, Region(4, 5, 100));
        Place(image, 300, Region(2, 5, 100));
        Place(image, 400, Region(3, 5, 201));
        Place(image, 600, Region(7, 3, 700));
        Place(image, 700, Region(6, 3, 600));
        return image;
    }

    private static void Place(byte[] image, int startBlock, byte[] content) =>
        content.CopyTo(image.AsSpan(startBlock * Block));

    private readonly record struct ArtifactPaths(string List, string New, string Patch, string Source);

    private static ArtifactPaths WriteUotanIncremental(TempDirectory temp, DatIncrementalCommandStyle style)
    {
        var directory = Path.Combine(temp.Path, "uotan_" + style.ToString().ToLowerInvariant());
        Directory.CreateDirectory(directory);
        var baseDir = Path.Combine(temp.Path, "seed");
        Directory.CreateDirectory(baseDir);
        var source = Path.Combine(baseDir, "system.src.bin");
        var target = Path.Combine(baseDir, "system.tgt.bin");
        File.WriteAllBytes(source, SourceImage());
        File.WriteAllBytes(target, TargetImage());
        WriteIncrementalInto(directory, baseDir, style, selfVerify: true);
        return new ArtifactPaths(
            Path.Combine(directory, "system.transfer.list"),
            Path.Combine(directory, "system.new.dat"),
            Path.Combine(directory, "system.patch.dat"),
            source);
    }

    private static void WriteIncrementalInto(string directory, string baseDir, DatIncrementalCommandStyle style, bool selfVerify) =>
        DatWriter.WriteIncremental(
            Path.Combine(baseDir, "system.src.bin"),
            Path.Combine(baseDir, "system.tgt.bin"),
            Path.Combine(directory, "system.new.dat"),
            Path.Combine(directory, "system.patch.dat"),
            Path.Combine(directory, "system.transfer.list"),
            new DatWriteOptions { IncrementalCommandStyle = style, SelfVerifyIncremental = selfVerify },
            cancellationToken: TestContext.Current.CancellationToken);

    private static void AssertCommandsEqual(TransferListCommand expected, TransferListCommand actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Ranges, actual.Ranges);
        Assert.Equal(expected.StashId, actual.StashId);
        Assert.Equal(expected.SourceRanges, actual.SourceRanges);
        Assert.Equal(expected.SourceBlockCount, actual.SourceBlockCount);
        Assert.Equal(expected.PatchOffset, actual.PatchOffset);
        Assert.Equal(expected.PatchLength, actual.PatchLength);
        Assert.Equal(expected.SourceHash, actual.SourceHash);
        Assert.Equal(expected.TargetHash, actual.TargetHash);
        Assert.Equal(expected.StashReferences.Count, actual.StashReferences.Count);
        foreach (var (id, ranges) in expected.StashReferences)
        {
            Assert.True(actual.StashReferences.TryGetValue(id, out var mapped));
            Assert.Equal(ranges, mapped);
        }
    }
}