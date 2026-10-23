using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the hash boundary: every checksum and digest computation in production code goes through
/// <c>UotanStudio.Hash</c>, and only that project may reference the BCL hashing package.
/// <para>守护哈希边界：生产代码里的每次校验和与摘要计算都必须经由 <c>UotanStudio.Hash</c>，
/// 且只有该项目可以引用 BCL 哈希包。</para>
/// </summary>
/// <remarks>
/// Scanning is line-oriented text, matching the other architecture gates. A rule never applies to a
/// documentation line, because the XML docs legitimately name the entry points they replaced.
/// The scanned directories are the project folders the checked-out <c>UotanStudio.slnx</c> declares, so a
/// project joins the gate the moment it joins the solution instead of waiting for an edited list here.
/// <c>UotanStudio.Hash</c> and <c>UotanStudio.Tests</c> are outside the scanned set on purpose: the hash
/// library owns the forwarded implementations, and the equivalence pins must call <c>System.IO.Hashing</c>
/// directly to prove that the façade returns the same bytes as the BCL, and a gate that hid that call could
/// not falsify it. <c>UotanStudio.Benchmarks</c> is scanned like any other project.
/// <para>与其它架构门一致，这里按行做文本扫描。文档注释行不受规则约束，因为 XML 注释需要写出被替代的入口名。
/// 扫描目录取自检出的 <c>UotanStudio.slnx</c> 所声明的工程文件夹，因此一个工程加入解决方案时即刻纳入该门，
/// 不需要再回来维护列表。<c>UotanStudio.Hash</c> 与 <c>UotanStudio.Tests</c> 刻意不在扫描集内：哈希库
/// 自己持有被转发的实现，而等价性固定用例必须直接调用 <c>System.IO.Hashing</c> 才能证明门面与 BCL
/// 产出相同字节，把该调用隐藏起来的门无法证伪；<c>UotanStudio.Benchmarks</c> 与其他工程一样纳入扫描。</para>
/// </remarks>
public partial class HashBoundaryGuardTests
{
    private const string HashProjectDirectory = "UotanStudio.Hash";

    private static readonly string[] ProjectDirectoriesOutsideTheScan =
    [
        HashProjectDirectory,
        "UotanStudio.Tests"
    ];

    private static readonly IReadOnlyDictionary<string, string> RetainedHashOracleFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UotanStudio.Benchmarks/Firmware/ChecksumSliceBenchmarks.cs"] =
            "The slice benchmark measures the software CRC-32 table directly against the hardware path; both polynomial tables are the measurement input, not production hashing.",
    };

    [Fact]
    public void ProductionSources_NoHashComputationOutsideTheHashLibrary()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var offenders = new List<string>();
        var inspected = 0;
        foreach (var projectDirectory in ScannedProjectDirectories(repositoryRoot))
        {
            var absoluteDirectory = Path.Combine(repositoryRoot, projectDirectory);
            Assert.True(Directory.Exists(absoluteDirectory), $"The scanned project directory {projectDirectory} is missing.");
            foreach (var (path, content) in FirmwareContextDependencyScanner.ReadSourceFiles(absoluteDirectory))
            {
                inspected++;
                var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
                if (RetainedHashOracleFiles.ContainsKey(relative))
                {
                    continue;
                }

                offenders.AddRange(ScanContent(repositoryRoot, path, content));
            }
        }

        Assert.True(inspected > 0, "The scan read no source files, so it never reached the production tree.");
        Assert.True(
            offenders.Count == 0,
            "Production code must compute hashes through UotanStudio.Hash; only that project may use the BCL "
            + "hashing package directly. Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ProductionProjectFiles_OnlyTheHashLibraryReferencesTheHashingPackage()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var offenders = new List<string>();
        var declarations = 0;
        foreach (var projectDirectory in ScannedProjectDirectories(repositoryRoot).Append(HashProjectDirectory))
        {
            foreach (var projectFile in Directory.EnumerateFiles(Path.Combine(repositoryRoot, projectDirectory), "*.csproj",
                SearchOption.AllDirectories))
            {
                var declaresPackage = File.ReadLines(projectFile).Any(HashingPackageReferencePattern().IsMatch);
                if (!declaresPackage)
                {
                    continue;
                }

                declarations++;
                if (!projectFile.Contains($"{Path.DirectorySeparatorChar}{HashProjectDirectory}{Path.DirectorySeparatorChar}"))
                {
                    offenders.Add($"{Path.GetRelativePath(repositoryRoot, projectFile)}: declares PackageReference System.IO.Hashing");
                }
            }
        }

        Assert.True(declarations > 0, "No project declared the hashing package, so the rule never observed a positive case.");
        Assert.True(offenders.Count == 0, "Only UotanStudio.Hash may reference System.IO.Hashing. Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ScanContent_DeliberateBoundaryViolations_ReportsEveryRuleOnItsOwnLine()
    {
        var offenders = ScanContent(
            LocateRepositoryRoot(),
            Path.Combine(LocateRepositoryRoot(), "Sample.cs"),
            string.Join('\n',
                "using System.IO.Hashing;",
                "var header = System.IO.Hashing.Crc32.HashToUInt32(span);",
                "var digest = SHA256.HashData(span);",
                "using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);",
                "var table = InitTable(0xEDB88320u);",
                "return Aes.Create();"));

        Assert.Equal(
        [
            "Sample.cs:1: using System.IO.Hashing;",
            "Sample.cs:2: System.IO.Hashing.Crc32.HashToUInt32",
            "Sample.cs:3: SHA256.HashData(",
            "Sample.cs:4: IncrementalHash.CreateHash(",
            "Sample.cs:5: 0xEDB88320"
        ], offenders);
    }

    [Fact]
    public void ScanContent_FaçadeOnlyText_ReportsNothing()
    {
        var offenders = ScanContent(
            LocateRepositoryRoot(),
            Path.Combine(LocateRepositoryRoot(), "Sample.cs"),
            string.Join('\n',
                "using UotanStudio.Hash.Crc32Ieee;",
                "using UotanStudio.Hash.Digests;",
                "var header = Crc32Ieee.Compute(span);",
                "var seeded = UotanStudio.Hash.Crc32Register.Crc32Register.Compute(span, F2fsSuperblock.Magic);",
                "var digest = Sha256.ComputeHash(span);",
                "using var accumulator = Sha256.CreateAccumulator();",
                "accumulator.Append(span);"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void ScanContent_DocumentationLine_NamingTheReplacedEntryPoint_IsIgnored()
    {
        var offenders = ScanContent(
            LocateRepositoryRoot(),
            Path.Combine(LocateRepositoryRoot(), "Sample.cs"),
            """
            /// <summary>Replaces <c>SHA256.HashData</c> for stored digests.</summary>
            /// A local CRC table over 0xEDB88320 no longer lives here.
            """);

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> ScanContent(string repositoryRoot, string path, string content)
    {
        var lineNumber = 0;
        foreach (var line in content.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var match in MatchedTexts(line))
            {
                yield return $"{Path.GetRelativePath(repositoryRoot, path)}:{lineNumber}: {match}";
            }
        }
    }

    private static IEnumerable<string> MatchedTexts(string line)
    {
        foreach (var pattern in new[]
        {
            BclHashingImportPattern(),
            BclHashingQualifiedPattern(),
            BclDigestEntryPointPattern(),
            LocalCrcPolynomialPattern()
        })
        {
            foreach (Match match in pattern.Matches(line))
            {
                yield return match.Value;
            }
        }
    }

    private static string[] ScannedProjectDirectories(string repositoryRoot)
    {
        var directories = File.ReadLines(Path.Combine(repositoryRoot, "UotanStudio.slnx"))
            .Select(line => SolutionProjectPathPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => Path.GetDirectoryName(match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))!)
            .Where(directory => !ProjectDirectoriesOutsideTheScan.Contains(directory, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            directories.Length > 0,
            "The solution declared no production project directory to scan, so the rule never reached the production tree.");
        return directories;
    }

    private static string LocateRepositoryRoot()
    {
        Assert.True(
            FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot),
            "This gate must run from a checkout of UotanStudio.slnx: a test host launched with a build-output "
            + "directory outside the repository would find no sources and report a vacuous pass.");
        return repositoryRoot!;
    }

    [GeneratedRegex(@"^\s*using\s+System\.IO\.Hashing\s*;")]
    private static partial Regex BclHashingImportPattern();

    [GeneratedRegex(@"System\.IO\.Hashing\.[A-Za-z0-9_.]+")]
    private static partial Regex BclHashingQualifiedPattern();

    [GeneratedRegex(@"\b(?:MD5|SHA(?:1|256|384|512))\.(?:HashData|Create)\(|\bIncrementalHash\.CreateHash\(")]
    private static partial Regex BclDigestEntryPointPattern();

    [GeneratedRegex(@"0x(?:EDB88320|82F63B78|04C11DB7|C96C5795D7870F42|A001|8408)(?![0-9A-Fa-f])", RegexOptions.IgnoreCase)]
    private static partial Regex LocalCrcPolynomialPattern();

    [GeneratedRegex(@"PackageReference\s+Include=""System\.IO\.Hashing""")]
    private static partial Regex HashingPackageReferencePattern();

    [GeneratedRegex(@"<Project\s+Path=""(?<path>[^""]+\.csproj)""")]
    private static partial Regex SolutionProjectPathPattern();
}