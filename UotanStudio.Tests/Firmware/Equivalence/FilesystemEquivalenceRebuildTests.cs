using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware.Equivalence;

/// <summary>
/// Drives real preserved rebuilds for EXT4, EROFS and the sparse container and asserts the emitted
/// equivalence report separates its dimensions: an edited file makes the byte dimension disagree with
/// the still-equivalent structure and content dimensions, an oversize rebuild reports capacity
/// NotEquivalent, and every format handler surfaces the report through its result metadata.
/// <para>对 EXT4、EROFS 与稀疏容器执行真实的受保留重建，断言发出的等价报告分离各维度：编辑文件会让
/// 字节维度与仍然等价的结构/内容维度产生分歧；超容量重建报告容量不等价；每个格式处理器都通过结果
/// 元数据呈现该报告。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class FilesystemEquivalenceRebuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-equivalence-rebuild-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FilesystemEquivalenceRebuildTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task TryRestore_EditedContent_ReportsByteDifferenceWithEquivalentStructureAndContent()
    {
        var (_, _, extracted) = await ExtractExt4Async();
        await File.WriteAllTextAsync(Path.Combine(extracted, "data"), "changed-content", Token);
        var output = Path.Combine(_root, "edited.img");
        IReadOnlyDictionary<string, string>? captured = null;

        Assert.True(FileSystemPreservation.TryRestore(extracted, output, "EXT4", Token, report => captured = report));

        Assert.NotNull(captured);
        var report = FilesystemEquivalenceReport.FromMetadata(captured);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.NotEqual(report.ByteEquivalence, report.ContentEquivalence);
        Assert.Equal(64, report.Details["ProducedImageHash"].Length);
        Assert.Equal(64, report.Details["SourceImageHash"].Length);
    }

    [Fact]
    public async Task PackAsync_PreservedRestore_SurfacesEvaluatedEquivalenceDimensionsInResultMetadata()
    {
        var (handler, _, extracted) = await ExtractExt4Async();
        var output = Path.Combine(_root, "packed.img");

        var result = await handler.PackAsync(extracted, output, Token);

        Assert.True(result.Success, result.Summary);
        var report = FilesystemEquivalenceReport.FromOperationResult(result);
        Assert.NotEqual(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.CapacityEquivalence);
        Assert.True(long.Parse(report.Details["SizeExcess"]) == 0);
    }

    [Fact]
    public async Task TryRestore_OversizedRebuild_ReportsCapacityNotEquivalent()
    {
        var source = Path.Combine(_root, "erofs-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "original-content");
        var image = Path.Combine(_root, "erofs.img");
        new ErofsBuilder().Build(source, image);
        var handler = new ErofsFormatHandler();
        var extracted = Path.Combine(_root, "erofs");
        Assert.True((await handler.UnpackAsync(image, extracted, cancellationToken: Token)).Success, "EROFS extraction failed.");
        await using (var filler = new FileStream(Path.Combine(extracted, "large.bin"), FileMode.CreateNew, FileAccess.Write))
        {
            var block = new byte[65536];
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = (byte)((i * 137 + (i >> 3)) & 0xFF);
            }

            for (var round = 0; round < 48; round++)
            {
                await filler.WriteAsync(block, Token);
            }
        }

        var output = Path.Combine(_root, "oversized.img");
        IReadOnlyDictionary<string, string>? captured = null;
        Assert.True(FileSystemPreservation.TryRestore(extracted, output, "EROFS", Token, report => captured = report));

        Assert.NotNull(captured);
        var report = FilesystemEquivalenceReport.FromMetadata(captured);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.CapacityEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.ByteEquivalence);
        Assert.True(long.Parse(report.Details["SizeExcess"]) > 0);
        Assert.True(long.Parse(report.Details["RebuiltSize"]) > long.Parse(report.Details["SourceFilesystemSize"]));
    }

    [Fact]
    public async Task SparsePackAsync_PreservedRestore_SurfacesEquivalenceMetadataWithLogicalCapacity()
    {
        FirmwareFormatRegistry.RegisterAll();
        var source = Path.Combine(_root, "sparse-source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "data"), "original-content");
        var raw = Path.Combine(_root, "inner.ext4.img");
        Ext4Builder.Build(source, raw);
        var sparse = Path.Combine(_root, "system.sparse.img");
        SparseImageConverter.ConvertRawToSparse(raw, sparse, 4096);
        var handler = new SparseFormatHandler();
        var extracted = Path.Combine(_root, "sparse");
        var unpacked = await handler.UnpackAsync(sparse, extracted, null, Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        await File.WriteAllTextAsync(Path.Combine(extracted, "data"), "changed-content", Token);
        var output = Path.Combine(_root, "rebuilt.sparse.img");

        var result = await handler.PackAsync(extracted, output, Token);

        Assert.True(result.Success, result.Summary);
        var report = FilesystemEquivalenceReport.FromOperationResult(result);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.NotEqual(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
        Assert.True(long.Parse(report.Details["RebuiltLogicalSize"]) > 0);
    }

    private async Task<(Ext4FormatHandler Handler, string Image, string Extracted)> ExtractExt4Async()
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "data"), "original-content");
        var image = Path.Combine(_root, "system-" + Guid.NewGuid().ToString("N") + ".img");
        Ext4Builder.Build(source, image);
        var handler = new Ext4FormatHandler();
        var extracted = Path.Combine(_root, "system-" + Guid.NewGuid().ToString("N"));
        var result = await handler.UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(result.Success, result.Summary);
        return (handler, image, extracted);
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}