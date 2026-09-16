using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

/// <summary><para>覆盖交付输出的源镜像目录校验：同级输出不再被误报拒绝，同时防止输出布局实际覆盖已选源镜像。</para>
/// Covers the source-image directory guard for delivery output: sibling output is no longer a false positive, while an output layout that would overwrite a selected source is still rejected.</summary>
public sealed class DeliveryOutputDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-outdir-" + Guid.NewGuid().ToString("N"));
    public DeliveryOutputDirectoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }

    private DeliveryFile WriteSource(string name, string relativePath)
    {
        var source = Path.Combine(_root, relativePath, name + ".img");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, new byte[8192]);
        return new() { SourcePath = source, FileName = name + ".img", Partition = name };
    }

    private DeliveryPlan Plan(DeliveryFile file, string output) => new() { Files = [file], Txt = false, OutputDirectory = output };

    // 新结构下 images 与 output 是同级兄弟目录，输出写入 output/images/*，不得被误报拒绝。
    [Fact]
    public async Task SiblingOutput_WithSourceInImagesFolder_IsAllowed()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        var output = Path.Combine(_root, name, "output");
        await new DeliveryOutputService(new NoTools()).ExportAsync(Plan(file, output), token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(output, "images", "boot.img")));
    }

    // 输出选在解决方案根（源镜像所在子项目的祖先）时，实际写出的 images/boot.img 与源不重合，应当允许。
    [Fact]
    public async Task SolutionRootOutput_WhenPlannedPathsAvoidSources_IsAllowed()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await new DeliveryOutputService(new NoTools()).ExportAsync(Plan(file, _root), token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "images", "boot.img")));
    }

    // 保存/恢复往返（相对路径与绝对路径 rebase）后，同级布局仍不得误报拒绝。
    [Fact]
    public async Task RawCaptureRestore_WithSiblingLayout_SurvivesWithoutFalsePositive()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        var output = Path.Combine(_root, name, "output");
        var original = Plan(file, output);
        var state = DeliveryPlanStore.CaptureElement(_root, original);
        var restored = DeliveryPlanStore.LoadFromState(state, _root)!;
        // 镜像归属不随计划持久化：恢复后输出目录仍正确 rebase，按当前选择重新填入文件后不得误报拒绝。
        // Image membership is not persisted: the output directory still rebases and re-adding the current
        // selection must not trigger the sibling-layout false positive.
        Assert.Empty(restored.Files);
        Assert.Equal(Path.GetFullPath(output), restored.OutputDirectory);
        await new DeliveryOutputService(new NoTools())
            .ExportAsync(restored with { Files = original.Files }, token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(output, "images", "boot.img")));
    }

    // 输出目录落在源镜像所在目录内/相等（会嵌套 images/images 或自写目录）时仍应拒绝。
    [Fact]
    public async Task OutputInsideSourceImageDirectory_IsRejected()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(file, Path.Combine(_root, name, "images")), token: TestContext.Current.CancellationToken));
    }

    // 组织布局下若输出目录是子项目根，output/images/boot.img 会精确覆盖源镜像，必须拒绝。
    [Fact]
    public async Task OrganizedLayoutOverwritingSourceImage_IsRejected()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(file, Path.Combine(_root, name)), token: TestContext.Current.CancellationToken));
    }
}