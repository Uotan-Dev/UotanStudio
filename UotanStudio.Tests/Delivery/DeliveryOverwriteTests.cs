using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryOverwriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-overwrite-" + Guid.NewGuid().ToString("N"));
    public DeliveryOverwriteTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private DeliveryPlan CopyPlan()
    {
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        return new DeliveryPlan
        {
            OutputDirectory = Path.Combine(_root, "out"),
            Txt = false,
            OrganizeDirectory = true,
            Files = [new DeliveryFile { SourcePath = source, Partition = "boot", FileName = "boot.img" }]
        };
    }

    private static string Destination(DeliveryPlan plan) => Path.Combine(plan.OutputDirectory, "images", "boot.img");

    private static IReadOnlySet<string> Set(params string[] paths)
        => new HashSet<string>(paths, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    [Fact]
    public async Task ExistingOutputOutsideOverwriteSetIsKept()
    {
        var plan = CopyPlan();
        var destination = Destination(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1, 2, 3]);
        var paths = await new DeliveryOutputService(new NoTools()).ExportAsync(plan,
            token: TestContext.Current.CancellationToken, overwrite: Set());
        Assert.DoesNotContain(destination, paths);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ExistingOutputInOverwriteSetIsReplaced()
    {
        var plan = CopyPlan();
        var destination = Destination(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1, 2, 3]);
        var paths = await new DeliveryOutputService(new NoTools()).ExportAsync(plan,
            token: TestContext.Current.CancellationToken, overwrite: Set(destination));
        Assert.Contains(destination, paths);
        Assert.Equal(new byte[8192], File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ExistingOutputWithoutDecisionStillThrows()
    {
        var plan = CopyPlan();
        var destination = Destination(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1]);
        await Assert.ThrowsAsync<IOException>(() => new DeliveryOutputService(new NoTools())
            .ExportAsync(plan, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FindExistingOutputsReturnsOnlyExistingPlannedPaths()
    {
        var plan = CopyPlan();
        var destination = Destination(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1]);
        var existing = DeliveryOutputService.FindExistingOutputs(plan);
        Assert.Equal([destination], existing);
    }

    [Fact]
    public void EstimateExportDemandCountsSourcesAndGeneratedSuper()
    {
        var source = Path.Combine(_root, "partition.img");
        File.WriteAllBytes(source, new byte[4096]);
        var plan = new DeliveryPlan
        {
            OutputDirectory = Path.Combine(_root, "out"),
            PackSuper = true,
            Container = [new DeliveryFile { SourcePath = source, Partition = "boot", FileName = "boot.img" }],
            Super = new() { DeviceSize = 10_000_000, Empty = false }
        };
        Assert.Equal((4096L + 10_000_000L) * DiskSpaceEstimator.SafetyFactor, DiskSpaceEstimator.EstimateExportDemand(plan));
    }

    [Fact]
    public void EstimateExportDemandExcludesDeviceSizeWhenEmptyOnly()
    {
        var plan = new DeliveryPlan
        {
            OutputDirectory = Path.Combine(_root, "out"),
            PackSuper = true,
            Super = new() { DeviceSize = 5_000_000, Empty = true }
        };
        var demand = DiskSpaceEstimator.EstimateExportDemand(plan);
        Assert.InRange(demand, plan.Super.MetadataSize, 1024 * 1024);
        Assert.Equal(demand, DiskSpaceEstimator.EstimateExportDemand(plan with
        {
            Super = plan.Super with { DeviceSize = 50_000_000 },
            Windows = true,
            Linux = true,
            MacOS = true
        }));
    }

    [Fact]
    public void EstimateExportDemandAddsToolAllowancePerPlatform()
    {
        var plan = new DeliveryPlan { OutputDirectory = Path.Combine(_root, "out"), OrganizeDirectory = true };
        Assert.Equal(0, DiskSpaceEstimator.EstimateExportDemand(plan));
        var withTools = plan with { Windows = true, Linux = true };
        Assert.Equal(2L * DiskSpaceEstimator.ToolAllowancePerPlatform * DiskSpaceEstimator.SafetyFactor,
            DiskSpaceEstimator.EstimateExportDemand(withTools));
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }
}