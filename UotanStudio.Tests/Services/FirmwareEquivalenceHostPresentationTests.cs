using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Drives a real preserved EXT4 rebuild through the host mapping and presentation layers: the edited image must
/// reach the host with its byte verdict kept separate from the still-equivalent structure and content, a fresh
/// directory build must report nothing as equivalent, and the presenter must render only what the library judged.
/// <para>用真实的受保留 EXT4 重建贯穿宿主的映射与呈现层：编辑后的镜像必须把字节判定与仍然等价的结构/内容
/// 分开抵达宿主；全新目录构建必须什么都不报成等价；呈现器只渲染库确实判定过的内容。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class FirmwareEquivalenceHostPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-equivalence-host-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FirmwareEquivalenceHostPresentationTests()
    {
        FirmwareFormatRegistry.RegisterAll();
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task PackDirectoryAsync_EditedFilesystem_PreservesSeparatedDimensionsInHostResult()
    {
        var extracted = await ExtractEditedExt4Async("edited");

        var host = await RegisteredFirmwareFormats.PackDirectoryAsync("EXT4", extracted,
            Path.Combine(_root, "edited.img"), Token);

        Assert.True(host.Success, host.Summary);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, host.Equivalence.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.ContentEquivalence);
    }

    [Fact]
    public async Task BuildImage_EditedFilesystem_PreservesSeparatedDimensionsInHostResult()
    {
        var extracted = await ExtractEditedExt4Async("packed");
        var output = Path.Combine(_root, "packed.img");

        var host = PackingService.BuildImage(extracted, output);

        Assert.True(host.Success, host.Summary);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, host.Equivalence.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.CapacityEquivalence);
    }

    [Fact]
    public async Task FormatLogLine_EditedFilesystemResult_SeparatesByteVerdictFromEquivalentDimensions()
    {
        var extracted = await ExtractEditedExt4Async("rendered");
        var host = await RegisteredFirmwareFormats.PackDirectoryAsync("EXT4", extracted,
            Path.Combine(_root, "rendered.img"), Token);

        var line = FirmwareEquivalencePresenter.FormatLogLine("rendered.img", host.Equivalence);

        Assert.StartsWith(
            "Equivalence of the built image: rendered.img | Byte identity: Not equivalent | " +
            "Inode structure: Equivalent | File content: Equivalent | Capacity: ",
            line,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void FormatLogLine_HostResultWithoutMetadata_RendersEveryDimensionAsNotEvaluated()
        => Assert.Equal(
            "Equivalence of the built image: system.img | Byte identity: Not evaluated by the library | " +
            "Inode structure: Not evaluated by the library | File content: Not evaluated by the library | " +
            "Capacity: Not evaluated by the library",
            FirmwareEquivalencePresenter.FormatLogLine("system.img",
                new FirmwareOpResult { Success = true }.Equivalence));

    private async Task<string> ExtractEditedExt4Async(string name)
    {
        var source = Path.Combine(_root, name + "-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "original-content");
        var image = Path.Combine(_root, name + ".source.img");
        Ext4Builder.Build(source, image);
        var extracted = Path.Combine(_root, name);
        Assert.True((await new Ext4FormatHandler().UnpackAsync(image, extracted, cancellationToken: Token)).Success);
        await File.WriteAllTextAsync(Path.Combine(extracted, "data"), "changed-content", Token);
        return extracted;
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