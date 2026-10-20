using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Freezes the bounded-context dependency baseline of <c>UotanStudio.Firmware</c>: no new context-to-context edge and no
/// cycle in the context graph.
/// <para>冻结 <c>UotanStudio.Firmware</c> 的限界上下文依赖基线：不允许新增上下文间依赖，上下文图中不允许出现环。</para>
/// </summary>
public class FirmwareArchitectureGuardTests
{
    private const int MinimumExpectedEdgeCount = 10;

    private static readonly HashSet<string> RevokedCycleEdges =
    [
        "FileSystem -> BlockImage",
        "BootSecurity -> Patching",
        "Patching -> BootSecurity",
    ];

    private static readonly HashSet<string> AllowedCrossContextEdges =
    [
        "AndroidMeta -> Executable",
        "Archive -> FileSystem",
        "BootSecurity -> Archive",
        "BootSecurity -> BlockImage",
        "BootSecurity -> DeviceTree",
        "OtaDelta -> BlockImage",
        "Patching -> FileSystem",
        "Radio -> CompoundFile",
        "VendorFirmware -> BlockImage",
        "VendorFirmware -> FileSystem",
        "VendorFirmware -> OtaDelta",
        "VendorFirmware -> Patching",
    ];

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_DetectsMoreThanTenCrossContextEdges()
    {
        var edges = FirmwareContextDependencyScanner.AggregateContextEdges(ScanCurrentTree());

        Assert.True(edges.Count > MinimumExpectedEdgeCount, $"Detected only {edges.Count} cross-context edges, so the scanner is likely matching nothing: {Describe(edges)}.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_DetectsOnlyFrozenCrossContextEdges()
    {
        var edges = FirmwareContextDependencyScanner.AggregateContextEdges(ScanCurrentTree());

        var unexpected = edges.Where(edge => !AllowedCrossContextEdges.Contains(edge.Key)).ToList();

        Assert.True(unexpected.Count == 0, $"New cross-context dependencies appeared in UotanStudio.Firmware: {Describe(unexpected)}. Remove the dependency or review and re-freeze the baseline with the architect.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_ContextGraphHasNoCycle()
    {
        var edges = FirmwareContextDependencyScanner.AggregateContextEdges(ScanCurrentTree());

        var cycle = FirmwareContextDependencyScanner.FindContextCycle(edges);

        Assert.True(cycle is null, $"Circular bounded-context dependency detected in UotanStudio.Firmware: {cycle}. Known cycle-prone pairs are FileSystem/BlockImage and BootSecurity/Patching.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_FrozenEdgesAreAllStillPresent()
    {
        var edges = FirmwareContextDependencyScanner.AggregateContextEdges(ScanCurrentTree());

        var present = edges.Select(edge => edge.Key).ToHashSet(StringComparer.Ordinal);

        var disappeared = AllowedCrossContextEdges.Where(key => !present.Contains(key)).ToList();

        Assert.True(disappeared.Count == 0, $"Frozen cross-context dependencies are no longer detected: {string.Join(", ", disappeared)}. Shrink the baseline deliberately instead of leaving stale entries.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_CoreDependsOnNoBoundedContext()
    {
        var edges = FirmwareContextDependencyScanner.ScanDirectory(FirmwareProjectDirectory());

        var offenders = edges
            .Where(edge => string.Equals(edge.Source, "Core", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"Core references bounded contexts and can no longer be reused below them: {Describe(offenders)}.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_BoundedContextsDependOnNoUpperRegion()
    {
        var edges = FirmwareContextDependencyScanner.ScanDirectory(FirmwareProjectDirectory());

        var offenders = edges
            .Where(edge => !FirmwareContextDependencyScanner.IsRegionLayerName(edge.Source)
                && (string.Equals(edge.Target, "Application", StringComparison.Ordinal) || string.Equals(edge.Target, "Infrastructure", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0, $"Bounded contexts reach back into the composition or host regions: {Describe(offenders)}.");
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_ApplicationDoesNotDependOnInfrastructure()
    {
        var edges = FirmwareContextDependencyScanner.ScanDirectory(FirmwareProjectDirectory());

        var offenders = edges
            .Where(edge => string.Equals(edge.Source, "Application", StringComparison.Ordinal)
                && string.Equals(edge.Target, "Infrastructure", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"The composition region reaches into host infrastructure and must go through Core contracts instead: {Describe(offenders)}.");
    }

    [Fact]
    public void DiscoverContextNames_LayeredTree_ReturnsBoundedContextChildren()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-arch-contexts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "BoundedContexts", "FileSystem"));
        Directory.CreateDirectory(Path.Combine(root, "BoundedContexts", "BlockImage"));
        Directory.CreateDirectory(Path.Combine(root, "Core"));

        try
        {
            var contextNames = FirmwareContextDependencyScanner.DiscoverContextNames(root);

            Assert.Equal(2, contextNames.Count);
            Assert.Contains("FileSystem", contextNames);
            Assert.Contains("BlockImage", contextNames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanDirectory_SyntheticFlatTree_AttributesFlatContextReferencesOnly()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "UotanStudio.Firmware");
        var fileSystemDirectory = Path.Combine(root, "FileSystem");
        var blockImageDirectory = Path.Combine(root, "BlockImage");
        Directory.CreateDirectory(fileSystemDirectory);
        Directory.CreateDirectory(blockImageDirectory);
        Directory.CreateDirectory(Path.Combine(root, "Core"));
        File.WriteAllText(Path.Combine(blockImageDirectory, "SparseImageWriter.cs"), "public sealed class SparseImageWriter { }");
        File.WriteAllText(
            Path.Combine(fileSystemDirectory, "ErofsBuilder.cs"),
            """
            namespace UotanStudio.Firmware.FileSystem.Erofs;

            public sealed class ErofsBuilder
            {
                public void Write() => UotanStudio.Firmware.BlockImage.SparseImageWriter.Flush();
                public string Schema() => "UotanStudio.Firmware.Repack";
            }
            """);

        var contextEdges = FirmwareContextDependencyScanner.AggregateContextEdges(FirmwareContextDependencyScanner.ScanDirectory(root));

        var edge = Assert.Single(contextEdges);
        Assert.Equal("FileSystem -> BlockImage", edge.Key);
    }

    [Fact]
    public void AllowedCrossContextEdges_RevokedCyclePairs_AreNotPartOfFrozenBaseline()
    {
        Assert.All(RevokedCycleEdges, key => Assert.False(AllowedCrossContextEdges.Contains(key), $"{key} would recreate a bidirectional context cycle."));
    }

    [Fact]
    public void ScanDirectory_CurrentFirmwareSource_EveryContextIsAttributedFromItsDirectory()
    {
        var firmwareDirectory = FirmwareProjectDirectory();

        var contextNames = FirmwareContextDependencyScanner.DiscoverContextNames(firmwareDirectory);

        Assert.True(contextNames.Count >= 10, $"Only {contextNames.Count} bounded contexts discovered from '{firmwareDirectory}'; the context folders drifted from the scanner's expectations.");
        foreach (var name in contextNames)
        {
            var probe = Path.Combine(firmwareDirectory, name, "Probe.cs");
            var owner = FirmwareContextDependencyScanner.OwnerOf(probe);

            Assert.Equal(name, owner);
        }
    }

    [Fact]
    public void ScanDirectory_SyntheticTreeWithRevokedEdge_ReportsBaselineViolationAndCycle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-arch-violation-{Guid.NewGuid():N}");
        var fileSystemDirectory = Path.Combine(root, "BoundedContexts", "FileSystem", "Squashfs");
        var blockImageDirectory = Path.Combine(root, "BoundedContexts", "BlockImage");
        Directory.CreateDirectory(fileSystemDirectory);
        Directory.CreateDirectory(blockImageDirectory);
        File.WriteAllText(
            Path.Combine(fileSystemDirectory, "SquashfsBuilder.cs"),
            """
            public sealed class SquashfsBuilder
            {
                public void Write() => UotanStudio.Firmware.BlockImage.SparseImageWriter.Flush();
            }
            """);
        File.WriteAllText(
            Path.Combine(blockImageDirectory, "SparseImageWriter.cs"),
            """
            /// <summary>Mentions UotanStudio.Compress only in documentation.</summary>
            public sealed class SparseImageWriter
            {
                public void Flush() => UotanStudio.Firmware.FileSystem.Squashfs.SquashfsBuilder.Write();
            }
            """);

        try
        {
            var contextEdges = FirmwareContextDependencyScanner.AggregateContextEdges(FirmwareContextDependencyScanner.ScanDirectory(root));
            var violations = contextEdges.Where(edge => !AllowedCrossContextEdges.Contains(edge.Key)).ToList();
            var cycle = FirmwareContextDependencyScanner.FindContextCycle(contextEdges);

            Assert.Equal(2, contextEdges.Count);
            Assert.Equal(2, violations.Count);
            Assert.Contains("FileSystem -> BlockImage (1 lines)", violations.Select(violation => violation.ToString()));
            Assert.Contains("BlockImage -> FileSystem (1 lines)", violations.Select(violation => violation.ToString()));
            Assert.NotNull(cycle);
            Assert.Contains("BlockImage -> FileSystem", contextEdges.Select(edge => edge.Key));
            var nodes = cycle!.Split(" -> ", StringSplitOptions.None);
            Assert.Equal(nodes[0], nodes[^1]);
            Assert.DoesNotContain("Compression", contextEdges.Select(edge => edge.Target), StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<FirmwareDependencyEdge> ScanCurrentTree()
    {
        return FirmwareContextDependencyScanner.ScanDirectory(FirmwareProjectDirectory());
    }

    private static string FirmwareProjectDirectory()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot))
        {
            Assert.Skip($"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'; the firmware source tree is unavailable to this test host.");
        }

        var firmwareDirectory = Path.Combine(repositoryRoot!, "UotanStudio.Firmware");
        if (!Directory.Exists(firmwareDirectory))
        {
            Assert.Skip($"Firmware source directory '{firmwareDirectory}' is missing; the library was not checked out next to the solution.");
        }

        return firmwareDirectory;
    }

    private static string Describe(IEnumerable<FirmwareDependencyEdge> edges)
    {
        return string.Join(", ", edges.Select(edge => $"{edge.Source} -> {edge.Target} at line {edge.Line}"));
    }

    private static string Describe(IEnumerable<FirmwareContextEdge> edges)
    {
        return string.Join(", ", edges.Select(edge => edge.ToString()));
    }
}