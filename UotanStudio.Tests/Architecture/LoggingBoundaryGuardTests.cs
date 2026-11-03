using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Architecture;

public sealed partial class LoggingBoundaryGuardTests
{
    private static readonly string[] ProducerProjects = ["UotanStudio.Mcp"];

    private static readonly string[] ExcludedDirectories =
    [
        "UotanStudio.Tests",
        "UotanStudio.Benchmarks",
        "UotanStudio.FilesystemFixture"
    ];

    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
        "UotanStudio/Services/Logging/UnifiedLogLoggerFactory.cs"
    };

    [Fact]
    public void MicrosoftExtensionsLogging_ProducerNamesStayInsideTheBridgeAndTheMcpLibrary()
    {
        var offenders = new List<string>();
        foreach (var directory in RepositoryLayout.SolutionProjectDirectories())
        {
            if (ExcludedDirectories.Contains(directory, StringComparer.Ordinal)
                || ProducerProjects.Contains(directory, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var (path, content) in RepositoryLayout.SourceFiles(directory))
            {
                if (AllowedPaths.Contains(path))
                {
                    continue;
                }

                offenders.AddRange(RepositoryLayout.Matches(path, content, ProducerPattern()));
            }
        }

        Assert.True(offenders.Count == 0,
            "Only the unified-log bridge and UotanStudio.Mcp may name Microsoft.Extensions.Logging; application code writes through IUnifiedLog:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Bridge_KeepsTheLoggingImplementationAndOptionsPackagesOutOfTheHost()
    {
        foreach (var (directory, _, content) in RepositoryLayout.ProjectFiles())
        {
            if (directory.Equals("UotanStudio.Mcp", StringComparison.Ordinal))
            {
                continue;
            }

            var offenders = RepositoryLayout.Matches(directory, content, ImplementationPackagePattern());
            Assert.True(offenders.Count == 0,
                "The host bridge consumes Microsoft.Extensions.Logging.Abstractions only; the implementation and options packages stay out of the dependency graph:\n"
                + string.Join("\n", offenders));
        }
    }

    [Fact]
    public void BridgeFile_AloneMatchesTheProducerPattern_SoTheAllowlistIsLoadBearing()
    {
        var bridge = RepositoryLayout.SourceFiles("UotanStudio")
            .Single(source => source.RelativePath.EndsWith("UnifiedLogLoggerFactory.cs", StringComparison.Ordinal));

        Assert.NotEmpty(RepositoryLayout.Matches(bridge.RelativePath, bridge.Content, ProducerPattern()));
    }

    [Fact]
    public void ProducerPattern_Synthetic_ReportsProducerNamesOutsideComments()
    {
        const string content = """
            using Microsoft.Extensions.Logging;
            var level = Microsoft.Extensions.Logging.LogLevel.Debug;
            // using Microsoft.Extensions.Logging;
            """;

        Assert.Equal(2, RepositoryLayout.Matches("sample.cs", content, ProducerPattern()).Count);
    }

    [Fact]
    public void ImplementationPackagePattern_Synthetic_ReportsThePackageReference()
    {
        const string content = """
            <PackageReference Include="Microsoft.Extensions.Logging" />
            <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.14" />
            <PackageReference Include="Microsoft.Extensions.Options" />
            """;

        Assert.Equal(2, RepositoryLayout.Matches("sample.csproj", content, ImplementationPackagePattern()).Count);
    }

    [GeneratedRegex(@"Microsoft\.Extensions\.Logging\b|Microsoft\.Extensions\.LogLevel\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProducerPattern();

    [GeneratedRegex(@"""Microsoft\.Extensions\.Logging""|""Microsoft\.Extensions\.Logging\.Configuration""|""Microsoft\.Extensions\.Options""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImplementationPackagePattern();
}