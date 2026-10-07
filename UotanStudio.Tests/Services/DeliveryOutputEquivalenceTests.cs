using System.Globalization;
using System.Security.Cryptography;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Covers the equivalence gate delivery runs on staged images. The byte dimension of a pure copy is evaluated for
/// real against the selected source and must abort the delivery when it disagrees; structure and content become
/// required only when the image carries preserved-rebuild metadata whose recorded source identity still matches the
/// source on disk, and a dimension the delivery path never computed stays unevaluated — it must neither fail the
/// build nor be presented as verified.
/// <para>覆盖交付在暂存镜像上执行的等价门禁：纯拷贝的字节维度按所选源真实评估，不一致时必须中止交付；结构与内容
/// 仅当镜像附带受保留重建元数据、且其记录的源身份与磁盘上的源仍然一致时才成为要求项；交付路径从未计算的维度保持
/// 不评估，既不得让构建失败，也不得被呈现为已校验。</para>
/// </summary>
public sealed class DeliveryOutputEquivalenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-delivery-equivalence-" + Guid.NewGuid().ToString("N"));

    public DeliveryOutputEquivalenceTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string WriteFile(string relativePath, int length, byte fill)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        return path;
    }

    private static (string Hash, long Length) IdentityOf(string path)
    {
        using var stream = File.OpenRead(path);
        return (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), new FileInfo(path).Length);
    }

    private DeliveryFile SourceImage(string partition, byte fill, Dictionary<string, string>? rebuildEquivalence = null,
        int length = 8192)
        => new()
        {
            SourcePath = WriteFile(Path.Combine("source", partition + ".img"), length, fill),
            FileName = partition + ".img",
            Partition = partition,
            RebuildEquivalence = rebuildEquivalence,
        };

    private Dictionary<string, string> RebuildMetadata(string sourcePath, bool structure = true, bool content = true,
        bool capacity = true, bool withSourceIdentity = true)
    {
        var (hash, length) = IdentityOf(sourcePath);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = structure ? "True" : "False",
            [FilesystemEquivalenceReport.ContentEquivalenceKey] = content ? "True" : "False",
            [FilesystemEquivalenceReport.CapacityEquivalenceKey] = capacity ? "True" : "False",
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "False",
        };
        if (withSourceIdentity)
        {
            metadata[DeliveryOutputService.RecordedSourceHashKey] = hash;
            metadata[DeliveryOutputService.RecordedSourceLengthKey] = length.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private string StageDirectory(string name, string fileName, int length, byte fill, bool nestedImages = true)
    {
        var stage = Path.Combine(_root, name);
        var path = nestedImages ? Path.Combine(stage, "images", fileName) : Path.Combine(stage, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, length).ToArray());
        return stage;
    }

    private static DeliveryPlan Plan(string output, params ReadOnlySpan<DeliveryFile> files)
        => new() { Files = [.. files], Txt = false, OutputDirectory = output };

    private string OutputPath(string name) => Path.Combine(_root, name, "output");

    private async Task<DeliveryStagedEquivalence> VerifySingleRowAsync(DeliveryFile file, string stage, DeliveryPlan? plan = null)
        => (await DeliveryOutputService.VerifyStagedEquivalenceAsync(plan ?? Plan(OutputPath("unused"), file), stage, null, Token)).Single();

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_IdenticalCopy_RequiresByteAndLeavesOtherDimensionsUnevaluated()
    {
        var file = SourceImage("system", 0xA1);
        var stage = StageDirectory("stage-identical", "system.img", 8192, 0xA1);

        var row = await VerifySingleRowAsync(file, stage);

        Assert.Equal(FirmwareEquivalenceState.Equivalent, row.Report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.CapacityEquivalence);
        Assert.Equal([DeliveryEquivalenceDimension.Byte], row.RequiredDimensions);
        Assert.Null(row.RequiredFailure);
        Assert.Equal("byte=equivalent(required); structure=not_evaluated; content=not_evaluated; capacity=not_evaluated",
            row.Format());
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_StagedCopyDifferingFromSource_AbortsBeforePublishing()
    {
        var file = SourceImage("system", 0xA1);
        var stage = StageDirectory("stage-drift", "system.img", 8192, 0xA2);

        var error = await Assert.ThrowsAsync<IOException>(
            () => DeliveryOutputService.VerifyStagedEquivalenceAsync(Plan(OutputPath("out-drift"), file), stage, null, Token));

        Assert.Contains("byte", error.Message, StringComparison.Ordinal);
        Assert.Contains("system.img", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_StagedCopyWithDifferentLength_AbortsBeforePublishing()
    {
        var file = SourceImage("system", 0xA1);
        var stage = StageDirectory("stage-truncated", "system.img", 2048, 0xA1);

        var error = await Assert.ThrowsAsync<IOException>(
            () => DeliveryOutputService.VerifyStagedEquivalenceAsync(Plan(OutputPath("out-truncated"), file), stage, null, Token));

        Assert.Contains("byte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_MissingStagedCopy_AbortsBeforePublishing()
    {
        var file = SourceImage("vendor", 0xC1);
        var stage = Path.Combine(_root, "stage-missing");
        Directory.CreateDirectory(Path.Combine(stage, "images"));

        await Assert.ThrowsAsync<IOException>(
            () => DeliveryOutputService.VerifyStagedEquivalenceAsync(Plan(OutputPath("out-missing"), file), stage, null, Token));
    }

    [Theory]
    [InlineData(FilesystemEquivalenceReport.StructureEquivalenceKey, "structure")]
    [InlineData(FilesystemEquivalenceReport.ContentEquivalenceKey, "content")]
    public async Task VerifyStagedEquivalenceAsync_BoundRebuildMetadataReportedInequivalent_AbortsBeforePublishing(
        string failingKey, string expectedDimension)
    {
        var metadata = RebuildMetadata(WriteFile("source/rebind.img", 8192, 0xC1));
        metadata[failingKey] = "False";
        var file = new DeliveryFile
        {
            SourcePath = Path.Combine(_root, "source", "rebind.img"),
            FileName = "rebind.img",
            Partition = "rebind",
            RebuildEquivalence = metadata,
        };
        var stage = StageDirectory("stage-bound-" + failingKey, "rebind.img", 8192, 0xC1);

        var error = await Assert.ThrowsAsync<IOException>(
            () => DeliveryOutputService.VerifyStagedEquivalenceAsync(Plan(OutputPath("out-bound"), file), stage, null, Token));

        Assert.Contains(expectedDimension, error.Message, StringComparison.Ordinal);
        Assert.Contains("rebind.img", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_BoundRebuildMetadataReportedEquivalent_RequiresStructureAndContent()
    {
        var source = WriteFile("source/boundgood.img", 8192, 0xC2);
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "boundgood.img",
            Partition = "boundgood",
            RebuildEquivalence = RebuildMetadata(source),
        };
        var stage = StageDirectory("stage-bound-good", "boundgood.img", 8192, 0xC2);

        var row = await VerifySingleRowAsync(file, stage);

        Assert.Equal([DeliveryEquivalenceDimension.Byte, DeliveryEquivalenceDimension.Structure,
            DeliveryEquivalenceDimension.Content], row.RequiredDimensions);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, row.Report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, row.Report.ContentEquivalence);
        Assert.Null(row.RequiredFailure);
        Assert.Contains("byte=equivalent(required)", row.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_RebuildMetadataWithoutRecordedSourceIdentity_StaysUnevaluatedAndPasses()
    {
        var source = WriteFile("source/unbound.img", 8192, 0xE1);
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "unbound.img",
            Partition = "unbound",
            RebuildEquivalence = RebuildMetadata(source, structure: false, withSourceIdentity: false),
        };
        var stage = StageDirectory("stage-unbound", "unbound.img", 8192, 0xE1);

        var row = await VerifySingleRowAsync(file, stage);

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.CapacityEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, row.Report.ByteEquivalence);
        Assert.Equal([DeliveryEquivalenceDimension.Byte], row.RequiredDimensions);
        Assert.Null(row.RequiredFailure);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_RebuildMetadataWithUnknownValues_IsReportedAsNotEvaluatedNotPass()
    {
        var source = WriteFile("source/unknownkey.img", 8192, 0xF1);
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "unknownkey.img",
            Partition = "unknownkey",
            RebuildEquivalence = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FilesystemEquivalenceReport.StructureEquivalenceKey] = "maybe",
                ["SomeFutureKey"] = "True",
            },
        };
        var stage = StageDirectory("stage-unknownkey", "unknownkey.img", 8192, 0xF1);

        var row = await VerifySingleRowAsync(file, stage);

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, row.Report.StructureEquivalence);
        Assert.DoesNotContain("structure=equivalent", row.Format(), StringComparison.Ordinal);
        Assert.Contains("structure=not_evaluated", row.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_StaleRecordedSourceIdentity_AbortsWithoutDowngrading()
    {
        var source = WriteFile("source/stale.img", 8192, 0x22);
        var metadata = RebuildMetadata(WriteFile("source/other.img", 8192, 0x33));
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "stale.img",
            Partition = "stale",
            RebuildEquivalence = metadata,
        };
        var stage = StageDirectory("stage-stale", "stale.img", 8192, 0x22);

        var error = await Assert.ThrowsAsync<IOException>(
            () => DeliveryOutputService.VerifyStagedEquivalenceAsync(Plan(OutputPath("out-stale"), file), stage, null, Token));

        Assert.Contains("no longer matches", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_BoundRebuildMetadataWithInequivalentCapacity_RecordsWithoutRequiring()
    {
        var source = WriteFile("source/capacity.img", 8192, 0x11);
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "capacity.img",
            Partition = "capacity",
            RebuildEquivalence = RebuildMetadata(source, capacity: false),
        };
        var stage = StageDirectory("stage-capacity", "capacity.img", 8192, 0x11);

        var row = await VerifySingleRowAsync(file, stage);

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, row.Report.CapacityEquivalence);
        Assert.DoesNotContain(DeliveryEquivalenceDimension.Capacity, row.RequiredDimensions);
        Assert.Null(row.RequiredFailure);
        Assert.Contains("capacity=not_equivalent", row.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_SuperPackingLayout_ReportsEveryDimensionUnevaluatedAndPasses()
    {
        var file = SourceImage("superpart", 0x33);
        var stage = StageDirectory("stage-super", "superpart.img", 8192, 0x44, nestedImages: false);
        var plan = Plan(OutputPath("out-super"), file) with
        {
            PackSuper = true,
            Super = new DeliverySuperOptions { VirtualAb = true },
        };

        var row = await VerifySingleRowAsync(file, stage, plan);

        Assert.Equal("byte=not_evaluated; structure=not_evaluated; content=not_evaluated; capacity=not_evaluated",
            row.Format());
        Assert.Empty(row.RequiredDimensions);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_GeneratedEntry_IsSkippedWithoutReadingAFile()
    {
        var stage = StageDirectory("stage-generated", "unrelated.img", 16, 0x55);
        var generated = new DeliveryFile
        {
            Generated = true,
            SourcePath = string.Empty,
            FileName = "payload.bin",
            Partition = "payload",
        };
        var plan = Plan(OutputPath("out-generated"), generated) with { PackPayload = true };

        var rows = await DeliveryOutputService.VerifyStagedEquivalenceAsync(plan, stage, null, Token);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task VerifyStagedEquivalenceAsync_FlatOutputMode_HasNoStagedImageCopies()
    {
        var file = SourceImage("flat", 0x77);
        var plan = Plan(OutputPath("out-flat"), file) with { OrganizeDirectory = false, PackSuper = true };

        var rows = await DeliveryOutputService.VerifyStagedEquivalenceAsync(
            plan, Path.Combine(_root, "stage-flat"), null, Token);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ExportAsync_RequiredDimensionFailed_KeepsPreExistingOutputIntact()
    {
        var exportRoot = Path.Combine(_root, "export-failing");
        var output = Path.Combine(exportRoot, "output");
        var existing = Path.Combine(output, "images", "aptp.img");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "PREEXISTING");
        var source = WriteFile("source/aptp.img", 8192, 0x66);
        var file = new DeliveryFile
        {
            SourcePath = source,
            FileName = "aptp.img",
            Partition = "aptp",
            RebuildEquivalence = RebuildMetadata(source, structure: false),
        };

        var error = await Assert.ThrowsAsync<IOException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(output, file), null, Token,
                new HashSet<string>(StringComparer.Ordinal) { existing }));

        Assert.Contains("structure", error.Message, StringComparison.Ordinal);
        Assert.Equal("PREEXISTING", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(output, "checksums.sha256")));
        Assert.Empty(Directory.GetDirectories(exportRoot, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task ExportAsync_CopiedSourceImageWithMatchingBytes_PublishesAndReportsUnevaluatedDimensionsHonestly()
    {
        var output = OutputPath("happy");
        var file = SourceImage("boot", 0xE7);
        var progress = new CollectProgress();

        var published = await new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(output, file), progress, Token);

        var destination = Path.Combine(output, "images", "boot.img");
        Assert.Contains(destination, published, StringComparer.Ordinal);
        Assert.Equal(IdentityOf(file.SourcePath).Hash, IdentityOf(destination).Hash);
        var summary = progress.Snapshots.LastOrDefault(value => value.Metadata is not null)?.Metadata;
        Assert.NotNull(summary);
        Assert.Contains("byte=equivalent(required)", summary![file.FileName], StringComparison.Ordinal);
        Assert.Contains("structure=not_evaluated", summary[file.FileName], StringComparison.Ordinal);
        Assert.Contains("not_evaluated", summary[DeliveryOutputService.GeneratedContainerEquivalenceKey], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
            => Task.CompletedTask;
    }

    private sealed class CollectProgress : IProgress<DeliveryProgress>
    {
        public List<DeliveryProgress> Snapshots { get; } = [];

        public void Report(DeliveryProgress value) => Snapshots.Add(value);
    }
}