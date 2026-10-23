using UotanStudio.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

/// <summary><para>覆盖交付输出的源镜像目录校验：同级输出属于合法布局应予放行，同时拦截会实际覆盖已选源镜像的输出布局。</para>
/// Covers the source-image directory guard for delivery output: sibling output is accepted, while an output layout that would overwrite a selected source is rejected.</summary>
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

    [Fact]
    public async Task SiblingOutput_WithSourceInImagesFolder_IsAllowed()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        var output = Path.Combine(_root, name, "output");
        await new DeliveryOutputService(new NoTools()).ExportAsync(Plan(file, output), token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(output, "images", "boot.img")));
    }

    [Fact]
    public async Task SolutionRootOutput_WhenPlannedPathsAvoidSources_IsAllowed()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await new DeliveryOutputService(new NoTools()).ExportAsync(Plan(file, _root), token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "images", "boot.img")));
    }

    [Fact]
    public async Task RawCaptureRestore_WithSiblingLayout_SurvivesWithoutFalsePositive()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        var output = Path.Combine(_root, name, "output");
        var original = Plan(file, output);
        var state = DeliveryPlanStore.CaptureElement(_root, original);
        var restored = DeliveryPlanStore.LoadFromState(state, _root)!;
        Assert.Empty(restored.Files);
        Assert.Equal(Path.GetFullPath(output), restored.OutputDirectory);
        await new DeliveryOutputService(new NoTools())
            .ExportAsync(restored with { Files = original.Files }, token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(output, "images", "boot.img")));
    }

    [Fact]
    public async Task OutputInsideSourceImageDirectory_IsRejected()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(file, Path.Combine(_root, name, "images")), token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrganizedLayoutOverwritingSourceImage_IsRejected()
    {
        var name = "NewProject1";
        var file = WriteSource("boot", Path.Combine(name, "images"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(Plan(file, Path.Combine(_root, name)), token: TestContext.Current.CancellationToken));
    }
}