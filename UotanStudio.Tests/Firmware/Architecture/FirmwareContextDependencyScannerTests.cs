using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Negative controls for <see cref="FirmwareContextDependencyScanner"/>: synthetic sources prove that a new context edge and
/// a cycle are actually reported, and that documentation-only mentions are not.
/// <para>针对 <see cref="FirmwareContextDependencyScanner"/> 的反向对照：用合成源码证明新增上下文边与环确实会被报告，
/// 而仅出现在文档注释中的提及不会被计入。</para>
/// </summary>
public class FirmwareContextDependencyScannerTests
{
    private static readonly IReadOnlySet<string> KnownContextNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Archive",
        "BlockImage",
        "BootSecurity",
        "DeviceTree",
        "FileSystem",
        "OtaDelta",
    };

    [Fact]
    public void ScanFile_SyntheticUsingOfUncalledContext_ReportsNewCrossContextEdge()
    {
        const string path = "UotanStudio.Firmware/FileSystem/Squashfs/SquashfsFormatHandler.cs";
        const string content = """
            using UotanStudio.Firmware.Archive;

            namespace UotanStudio.Firmware.FileSystem.Squashfs;

            public sealed partial class SquashfsFormatHandler
            {
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        Assert.Equal("FileSystem", Assert.Single(edges).Source);
        Assert.Equal("Archive", Assert.Single(edges).Target);
    }

    [Fact]
    public void ScanFile_SyntheticFullyQualifiedCallSiteWithoutUsingDirective_ReportsCrossContextEdge()
    {
        const string path = "UotanStudio.Firmware/FileSystem/Erofs/ErofsFormatHandler.cs";
        const string content = """
            namespace UotanStudio.Firmware.FileSystem.Erofs;

            public sealed class ErofsFormatHandler
            {
                public int Inspect(System.IO.Stream stream)
                {
                    return UotanStudio.Firmware.BlockImage.SparseImageReader.CountStreams(stream);
                }
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        var edge = Assert.Single(edges);
        Assert.Equal(("FileSystem", "BlockImage"), (edge.Source, edge.Target));
        Assert.Equal(7, edge.Line);
    }

    [Fact]
    public void ScanFile_SyntheticLayeredPathReferences_ReportsEdgesWithoutKnownContextNames()
    {
        const string path = "UotanStudio.Firmware/BoundedContexts/FileSystem/Squashfs/SquashfsFormatHandler.cs";
        const string content = """
            using UotanStudio.Firmware.BoundedContexts.Archive;

            namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Squashfs;

            public sealed partial class SquashfsFormatHandler
            {
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content);

        var edge = Assert.Single(edges);
        Assert.Equal(("FileSystem", "Archive"), (edge.Source, edge.Target));
    }

    [Fact]
    public void ScanFile_SyntheticMutualReferences_FindContextCycleReportsBothContexts()
    {
        var files = new[]
        {
            ("UotanStudio.Firmware/FileSystem/F2fs/F2fsBuilder.cs", """
                public sealed class F2fsBuilder
                {
                    public void Write() => UotanStudio.Firmware.BlockImage.SparseImageWriter.Flush();
                }
                """),
            ("UotanStudio.Firmware/BlockImage/SparseImageReader.cs", """
                public sealed class SparseImageReader
                {
                    public void Read() => UotanStudio.Firmware.FileSystem.F2fs.F2fsBuilder.Write();
                }
                """),
        };

        var edges = files
            .SelectMany(file => FirmwareContextDependencyScanner.ScanFile(file.Item1, file.Item2, KnownContextNames))
            .ToList();
        var contextEdges = FirmwareContextDependencyScanner.AggregateContextEdges(edges);
        var cycle = FirmwareContextDependencyScanner.FindContextCycle(contextEdges);

        Assert.NotNull(cycle);
        var nodes = cycle!.Split(" -> ", StringSplitOptions.None);
        Assert.Equal(nodes[0], nodes[^1]);
        Assert.Equal(3, nodes.Length);
        Assert.Contains("FileSystem", nodes, StringComparer.Ordinal);
        Assert.Contains("BlockImage", nodes, StringComparer.Ordinal);
    }

    [Fact]
    public void FindContextCycle_SyntheticForwardOnlyChain_ReportsNoCycle()
    {
        var edges = new[]
        {
            new FirmwareContextEdge("FileSystem", "Compression", 5),
            new FirmwareContextEdge("BlockImage", "FileSystem", 4),
            new FirmwareContextEdge("BootSecurity", "BlockImage", 2),
        };

        var cycle = FirmwareContextDependencyScanner.FindContextCycle(edges);

        Assert.Null(cycle);
    }

    [Fact]
    public void FindContextCycle_SyntheticThreeContextRing_ReportsClosedPath()
    {
        var edges = new[]
        {
            new FirmwareContextEdge("Archive", "Compression", 5),
            new FirmwareContextEdge("Compression", "DeviceTree", 1),
            new FirmwareContextEdge("DeviceTree", "Archive", 2),
        };

        var cycle = FirmwareContextDependencyScanner.FindContextCycle(edges);

        Assert.NotNull(cycle);
        var nodes = cycle!.Split(" -> ", StringSplitOptions.None);
        Assert.Equal(nodes[0], nodes[^1]);
        Assert.Equal(4, nodes.Length);
    }

    [Fact]
    public void ScanFile_SyntheticDocumentationAndNamespaceMentions_ReportNoEdges()
    {
        const string path = "UotanStudio.Firmware/DeviceTree/DtoParser.cs";
        const string content = """
            /// <summary>Reads trees; see UotanStudio.Firmware.Archive for container layout.</summary>
            /// <para>参考 UotanStudio.Firmware.FileSystem 与 UotanStudio.Firmware.BlockImage。</para>
            namespace UotanStudio.Firmware.DeviceTree;

            public sealed class DtoParser
            {
                /// <remarks>Layout mirrors UotanStudio.Compress codecs.</remarks>
                public int Parse() => 0;
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        Assert.Empty(edges);
    }

    [Fact]
    public void ScanFile_SyntheticSelfContextQualifiedReference_ReportsNoEdge()
    {
        const string path = "UotanStudio.Firmware/FileSystem/Squashfs/SquashfsDecompression.cs";
        const string content = """
            using UotanStudio.Firmware.FileSystem.Squashfs;

            public sealed class SquashfsDecompression
            {
                public void Run() => UotanStudio.Firmware.FileSystem.Squashfs.SuperBlock.Read();
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        Assert.Empty(edges);
    }

    [Fact]
    public void ScanFile_SyntheticCoreSourceReferencingContext_ReportsEdgeForLayerRule()
    {
        const string path = "UotanStudio.Firmware/Core/Streams/StreamCopy.cs";
        const string content = """
            public static class StreamCopy
            {
                public static void Run() => UotanStudio.Firmware.FileSystem.Squashfs.SquashfsParser.Parse();
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        var edge = Assert.Single(edges);
        Assert.Equal("Core", edge.Source);
        Assert.Equal("FileSystem", edge.Target);
        Assert.True(FirmwareContextDependencyScanner.IsRegionLayerName(edge.Source));
    }

    [Fact]
    public void ScanFile_SyntheticContextSourceReferencingApplicationRegion_ReportsRegionEdge()
    {
        const string path = "UotanStudio.Firmware/BootSecurity/Avb/AvbFooter.cs";
        const string content = """
            public sealed class AvbFooter
            {
                public void Probe() => UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.Resolve();
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        var edge = Assert.Single(edges);
        Assert.Equal(("BootSecurity", "Application"), (edge.Source, edge.Target));
    }

    [Fact]
    public void ScanFile_SyntheticUnknownFlatSegment_ReportsNoEdge()
    {
        const string path = "UotanStudio.Firmware/OtaDelta/Dat/DatFormatHandler.cs";
        const string content = """
            public sealed class DatFormatHandler
            {
                public string Schema() => "UotanStudio.Firmware.Repack";
            }
            """;

        var edges = FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames);

        Assert.Empty(edges);
    }

    [Fact]
    public void OwnerOf_SyntheticPaths_AttributeFilesToOutermostRegion()
    {
        Assert.Equal("FileSystem", FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware\\FileSystem\\Erofs\\Builder\\ErofsBuilder.cs"));
        Assert.Equal("Compression", FirmwareContextDependencyScanner.OwnerOf("C:/repo/UotanStudio.Firmware/Compression/Codecs/Lz4/Lz4Codec.cs"));
        Assert.Equal("Core", FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware/Core/Guard.cs"));
        Assert.Equal("Application", FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware/Application/Formats/FirmwareFormatRegistry.cs"));
        Assert.Null(FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Tests/Firmware/Architecture/SomeTest.cs"));
        Assert.Null(FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware/Properties/AssemblyInfo.cs"));
    }

    [Fact]
    public void OwnerOf_SyntheticLayeredPaths_StillAttributeContextsBehindTheLayerFolder()
    {
        Assert.Equal("FileSystem", FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware\\BoundedContexts\\FileSystem\\Erofs\\Builder\\ErofsBuilder.cs"));
        Assert.Equal("Compression", FirmwareContextDependencyScanner.OwnerOf("C:/repo/UotanStudio.Firmware/BoundedContexts/Compression/Codecs/Lz4/Lz4Codec.cs"));
        Assert.Null(FirmwareContextDependencyScanner.OwnerOf("UotanStudio.Firmware/BoundedContexts/LooseFile.cs"));
    }

    [Fact]
    public void AggregateContextEdges_SyntheticRepeatedReferences_CountsLinesPerEdge()
    {
        const string path = "UotanStudio.Firmware/OtaDelta/PayloadBuilder.cs";
        const string content = """
            public sealed class PayloadBuilder
            {
                public void First() => UotanStudio.Firmware.FileSystem.Squashfs.SquashfsParser.Parse();
                public void Second() => UotanStudio.Firmware.FileSystem.Squashfs.SquashfsParser.Parse();
                public void Third() => UotanStudio.Firmware.BlockImage.Sparse.SparseReader.Read();
            }
            """;

        var edges = FirmwareContextDependencyScanner.AggregateContextEdges(FirmwareContextDependencyScanner.ScanFile(path, content, KnownContextNames));

        Assert.Collection(
            edges,
            edge =>
            {
                Assert.Equal(("OtaDelta", "BlockImage"), (edge.Source, edge.Target));
                Assert.Equal(1, edge.LineCount);
            },
            edge =>
            {
                Assert.Equal(("OtaDelta", "FileSystem"), (edge.Source, edge.Target));
                Assert.Equal(2, edge.LineCount);
            });
    }

    [Fact]
    public void ReadSourceFiles_SyntheticTreeWithBuildOutputs_SkipsIgnoredDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-arch-scan-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "BoundedContexts", "Archive");
        var ignoredDirectory = Path.Combine(root, "obj", "Generated");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(ignoredDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "TarReader.cs"), "public class TarReader { }");
        File.WriteAllText(Path.Combine(ignoredDirectory, "AssemblyInfo.cs"), "public class Generated { }");
        File.WriteAllText(Path.Combine(sourceDirectory, "Notes.md"), "UotanStudio.Firmware.FileSystem");

        try
        {
            var paths = FirmwareContextDependencyScanner.ReadSourceFiles(root).Select(item => item.Path).ToList();

            Assert.Single(paths);
            Assert.EndsWith(Path.Combine("BoundedContexts", "Archive", "TarReader.cs"), paths[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryFindRepositoryRoot_SyntheticStartDirectoryWithoutSolution_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-arch-noroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var found = FirmwareContextDependencyScanner.TryFindRepositoryRoot(root, out var repositoryRoot);

            Assert.False(found);
            Assert.Null(repositoryRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryFindRepositoryRoot_CurrentTestHostOutput_FindsCheckedOutSolution()
    {
        var found = FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot);

        Assert.True(found, $"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'.");
        Assert.NotNull(repositoryRoot);
        Assert.True(File.Exists(Path.Combine(repositoryRoot!, "UotanStudio.slnx")));
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot!, "UotanStudio.Firmware")));
    }
}