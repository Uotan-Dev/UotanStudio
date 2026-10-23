using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the invariant that generic compression is implemented only inside <c>UotanStudio.Compress</c>: no other
/// production project may instantiate the in-box gzip, deflate, zlib or Brotli streams, because the layers above the
/// library must reach the generic codecs through its facade.
/// <para>守护"通用压缩只在 <c>UotanStudio.Compress</c> 中实现"这一不变量：其他生产工程不得直接实例化内置的
/// gzip、deflate、zlib 或 Brotli 流，库之上的层必须经其门面使用通用编解码器。</para>
/// </summary>
public class CompressionImplementationBoundaryTests
{
    private const string CompressProjectDirectory = "UotanStudio.Compress";

    private static readonly Regex GenericCompressionStreamPattern = new(
        @"new\s+(?:(?:System\.)?(?:IO\.)?Compression\.)?(?:GZipStream|GzipStream|DeflateStream|ZLibStream|ZlibStream|BrotliStream)\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.Ordinal)
    {
        "bin",
        "obj",
        "artifacts",
        "reference",
        "UotanStudio.Compress",
        "UotanStudio.Tests",
    };

    private static readonly IReadOnlyDictionary<string, string> RetainedBclCompressionExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UotanStudio.Firmware/DeviceTree/Dt/DtboFiles.cs"] =
            "DTBO zlib members stay on the built-in ZLibStream: the library's zlib codec is a different deflate implementation whose level enum has no smallest-size member, so the packing band would shift. The gzip branch already goes through CompressionService.",
        ["UotanStudio.Firmware/OtaDelta/Diff/ImageDeltaPatch.cs"] =
            "IMGDIFF2 must reproduce the exact compressed bytes recorded in the patch; the built-in DeflateStream cannot, so the recording side uses SharpCompress's DeflateStream deliberately while the applying side keeps the built-in decoder.",
        ["UotanStudio.Firmware/FileSystem/Erofs/ErofsCompression.cs"] =
            "EROFS raw DEFLATE and ZLIB4C pcluster codecs are format-domain compression belonging to the on-disk EROFS layout, not generic compression.",
        ["UotanStudio.Tests/"] =
            "The test project builds fixtures with the in-box streams on purpose so library decoders are checked against an independent encoder; it is excluded from this scan wholesale.",
    };

    private static readonly IReadOnlySet<string> MigratingGenericCompressionCallSites = new HashSet<string>(StringComparer.Ordinal)
    {
        "UotanStudio.Firmware/AndroidMeta/Apk/ApkRepacker.cs",
        "UotanStudio.Firmware/BootSecurity/Patcher/KSudExtractor.cs",
        "UotanStudio.Firmware/VendorFirmware/Oppo/OppoSplashFormat.cs",
        "UotanStudio/Services/Firmware/RemoteRomService.cs",
    };

    [Fact]
    public void ScanProductionSources_GenericCompressionStreamConstruction_StaysInsideTheCompressLibrary()
    {
        var offenders = ScanProductionSources(RepositoryRoot());

        Assert.True(offenders.Count == 0, BuildViolationMessage(offenders));
    }

    [Fact]
    public void ScanProductionSources_CompressLibraryAndExcludedTrees_AreNeverScanned()
    {
        var scanned = EnumerateProductionSources(RepositoryRoot()).Select(entry => entry.Path).ToList();

        Assert.True(scanned.Count > 0, "The production scan found no C# source at all, so the boundary would pass vacuously.");
        Assert.DoesNotContain(scanned, path => path.StartsWith(CompressProjectDirectory + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(scanned, path => path.StartsWith("UotanStudio.Tests/", StringComparison.Ordinal));
        Assert.Contains(scanned, path => path.StartsWith("UotanStudio.Benchmarks/", StringComparison.Ordinal));
        Assert.DoesNotContain(scanned, path => path.StartsWith("reference/", StringComparison.Ordinal));
        Assert.DoesNotContain(scanned, IsBuildOutput);
    }

    [Fact]
    public void ScanProductionSources_RepositoryWithoutSolutionFile_ReturnsEmptyInsteadOfGuessing()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"uotan-arch-missing-{Guid.NewGuid():N}");

        Assert.Empty(ScanProductionSources(missingDirectory));
    }

    [Fact]
    public void FindGenericCompressionConstructors_SyntheticProductionSource_ReportsEveryOffendingLine()
    {
        const string path = "UotanStudio.Firmware/Synthetic/Repacker.cs";
        const string content = """
            using System.IO.Compression;

            namespace UotanStudio.Firmware.Synthetic;

            public static class Repacker
            {
                public static Stream Encode(Stream output)
                    => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);

                public static Stream Wrap(Stream output)
                    => new System.IO.Compression.DeflateStream(output, CompressionMode.Compress, true);

                public static Stream Read(Stream input)
                    => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);

                public static Stream Passthrough(Stream input) => input;
            }
            """;

        var offenders = FindGenericCompressionConstructors([(path, content)]);

        Assert.Equal(3, offenders.Count);
        Assert.Equal([8, 11, 14], offenders.Select(offender => offender.Line));
        Assert.All(offenders, offender => Assert.Equal(path, offender.Path));
    }

    [Fact]
    public void FindGenericCompressionConstructors_TypeMentionWithoutConstruction_ReportsNothing()
    {
        const string path = "UotanStudio.Firmware/Synthetic/Codec.cs";
        const string content = """
            using System.IO.Compression;

            namespace UotanStudio.Firmware.Synthetic;

            public static class Codec
            {
                public static Stream Create(Stream destination, GZipStream? template)
                    => CompressionService.CreateCompressionStream(destination, CompressionFormat.Gzip, new CompressionOptions());

                public static Stream Reopen(Stream destination)
                    => CompressionService.CreateCompressionStream(destination, CompressionFormat.Zlib, new CompressionOptions());
            }
            """;

        Assert.Empty(FindGenericCompressionConstructors([(path, content)]));
    }

    [Fact]
    public void FindGenericCompressionConstructors_AllowListedPath_IsReportedAsRetainedRatherThanViolating()
    {
        const string allowListedPath = "UotanStudio.Firmware/FileSystem/Erofs/ErofsCompression.cs";
        const string content = "var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);";

        var offenders = FindGenericCompressionConstructors([(allowListedPath, content)]);

        Assert.Single(offenders);
        Assert.True(IsRetainedException(offenders[0].Path), $"'{allowListedPath}' must stay on the documented allow-list.");
    }

    [Fact]
    public void RetainedBclCompressionExceptions_EveryEntry_ExplainsWhyTheInBoxStreamIsKept()
    {
        Assert.NotEmpty(RetainedBclCompressionExceptions);
        Assert.Contains(RetainedBclCompressionExceptions.Keys, key => key.Contains("DtboFiles.cs", StringComparison.Ordinal));
        Assert.Contains(RetainedBclCompressionExceptions.Keys, key => key.Contains("ImageDeltaPatch.cs", StringComparison.Ordinal));
        Assert.Contains(RetainedBclCompressionExceptions.Keys, key => key.Contains("ErofsCompression.cs", StringComparison.Ordinal));
        Assert.Contains(RetainedBclCompressionExceptions.Keys, key => string.Equals(key, "UotanStudio.Tests/", StringComparison.Ordinal));
        Assert.All(
            RetainedBclCompressionExceptions,
            entry =>
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Key), "An allow-list entry needs the repository-relative path it exempts.");
                Assert.True(entry.Value.Length > 40, $"Allow-list entry '{entry.Key}' needs a reason explaining why the in-box stream is retained.");
            });
    }

    private static string RepositoryRoot()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot) || repositoryRoot is null)
        {
            Assert.Skip($"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'; the layered source tree is unavailable to this test host.");
        }

        return repositoryRoot!;
    }

    private static IReadOnlyList<string> ScanProductionSources(string startDirectory)
    {
        return FindGenericCompressionConstructors(EnumerateProductionSources(startDirectory))
            .Select(Describe)
            .ToList();
    }

    private static IReadOnlyList<(string Path, string Content)> EnumerateProductionSources(string startDirectory)
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(startDirectory, out var repositoryRoot) || repositoryRoot is null)
        {
            return [];
        }

        var compressDirectory = Path.Combine(repositoryRoot, CompressProjectDirectory);
        if (!Directory.Exists(compressDirectory))
        {
            Assert.Skip($"Compression source directory '{compressDirectory}' is missing; the library was not checked out next to the solution.");
        }

        return FirmwareContextDependencyScanner
            .ReadSourceFiles(repositoryRoot)
            .Select(entry => (
                Path: Path.GetRelativePath(repositoryRoot, entry.Path).Replace('\\', '/'),
                entry.Content))
            .Where(entry => !IsExcludedFromScan(entry.Path))
            .ToList();
    }

    private static IReadOnlyList<CompressionConstructorSite> FindGenericCompressionConstructors(
        IEnumerable<(string Path, string Content)> sources)
    {
        var sites = new List<CompressionConstructorSite>();
        foreach (var (path, content) in sources)
        {
            var line = 1;
            foreach (var text in content.Split('\n'))
            {
                if (GenericCompressionStreamPattern.IsMatch(text))
                {
                    sites.Add(new CompressionConstructorSite(path, line, text.Trim()));
                }

                line++;
            }
        }

        return sites;
    }

    private static bool IsExcludedFromScan(string repositoryRelativePath)
    {
        if (IsBuildOutput(repositoryRelativePath))
        {
            return true;
        }

        if (IsRetainedException(repositoryRelativePath))
        {
            return true;
        }

        var segments = repositoryRelativePath.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (ExcludedDirectoryNames.Contains(segments[index]) || segments[index].StartsWith('.'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRetainedException(string repositoryRelativePath)
    {
        foreach (var exception in RetainedBclCompressionExceptions.Keys)
        {
            if (exception.EndsWith('/'))
            {
                if (repositoryRelativePath.StartsWith(exception, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(repositoryRelativePath, exception, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBuildOutput(string repositoryRelativePath)
    {
        var segments = repositoryRelativePath.Split('/');
        return segments.Any(segment => segment is "bin" or "obj" or "artifacts");
    }

    private static string BuildViolationMessage(IReadOnlyList<string> offenders)
    {
        var migrating = offenders
            .Select(offender => offender[..offender.IndexOf(':', StringComparison.Ordinal)].Trim())
            .Distinct(StringComparer.Ordinal)
            .Where(MigratingGenericCompressionCallSites.Contains)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{path} (a migration onto the CompressionService facade is in flight)")
            .ToList();

        var message = $"Generic compression escapes {CompressProjectDirectory}: {string.Join(", ", offenders)}. "
            + "Route these through UotanStudio.Compress.CompressionService so the library stays the single generic codec implementation. "
            + "Format-domain compression (SquashFS/EROFS/F2FS block clusters, boot-image component packaging, Payload compression operations and .cpio.gz/.tar.xz combinations) is legitimate, but it must not build generic streams by hand. "
            + $"The retained exceptions are declared with their reasons in the {nameof(RetainedBclCompressionExceptions)} allow-list; extend it only after an architect review.";

        if (migrating.Count > 0)
        {
            message += $" Still un-migrated call sites: {string.Join(", ", migrating)}.";
        }

        return message;
    }

    private static string Describe(CompressionConstructorSite site)
    {
        return $"{site.Path}:{site.Line} -> {site.Text}";
    }

    private sealed record CompressionConstructorSite(string Path, int Line, string Text);
}