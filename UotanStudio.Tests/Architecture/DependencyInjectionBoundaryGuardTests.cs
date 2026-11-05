using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Architecture;

/// <summary>
/// Guards the dependency-injection registration contract that keeps NativeAOT intact: explicit
/// factories, closed implementation types and no reflection-driven discovery. Open generic
/// registrations are called out separately because they compile without a single trim or AOT
/// diagnostic and only fail when a value type closes them at run time.
/// </summary>
public partial class DependencyInjectionBoundaryGuardTests
{
    private static readonly string[] ScannedProjects =
    [
        "UotanStudio.Hash",
        "UotanStudio.Net",
        "UotanStudio.Compress",
        "UotanStudio.Firmware",
        "UotanStudio.Comm",
        "UotanStudio.Mirroring",
        "UotanStudio.Media",
        "UotanStudio.Pipeline",
        "UotanStudio.Mcp",
        "UotanStudio",
        "UotanStudio.Desktop",
        "UotanStudio.RegexInfer",
        "UotanStudio.Firmware.MagicGen",
        "UotanStudio.FilesystemFixture"
    ];

    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void ProductionSources_RegisterWithExplicitFactoriesOnly()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in ProductionSources())
        {
            offenders.AddRange(RepositoryLayout.Matches(path, content, FactorylessRegistrationPattern()));
            offenders.AddRange(RepositoryLayout.Matches(path, content, KeyedTypeMappingPattern()));
            offenders.AddRange(RepositoryLayout.Matches(path, content, ReflectionActivationPattern()));
        }

        Assert.True(offenders.Count == 0,
            "Container registrations must use explicit factories:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ProductionSources_NeverRegisterOpenGenerics()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in ProductionSources())
        {
            offenders.AddRange(RepositoryLayout.Matches(path, content, OpenGenericRegistrationPattern()));
        }

        Assert.True(offenders.Count == 0,
            "Open generic registrations fail at run time under NativeAOT without a build diagnostic:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void ProductionSources_NeverBuildTypesDynamically()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in ProductionSources())
        {
            offenders.AddRange(RepositoryLayout.Matches(path, content, AssemblyDiscoveryPattern()));
        }

        Assert.True(offenders.Count == 0,
            "Assembly discovery and dynamic type construction are not trim or AOT safe:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void RegistrationPatterns_Synthetic_ReportEveryForbiddenForm()
    {
        const string registration = """
            services.AddSingleton<IFoo, Foo>();
            services.AddTransient<Bar>();
            services.AddKeyedSingleton<IBaz, Baz>("k");
            services.AddSingleton<IQux>(sp => new Qux());
            """;
        const string discovery = """
            var types = typeof(Foo).Assembly.GetTypes();
            var closed = typeof(IRepo<>).MakeGenericType(typeof(int));
            var instance = ActivatorUtilities.CreateInstance<Foo>(services);
            """;

        var factoryless = RepositoryLayout.Matches("sample.cs", registration, FactorylessRegistrationPattern());
        Assert.Equal(2, factoryless.Count);
        Assert.Single(RepositoryLayout.Matches("sample.cs", registration, KeyedTypeMappingPattern()));
        Assert.Single(RepositoryLayout.Matches("sample.cs", discovery, ReflectionActivationPattern()));
        Assert.Equal(2, RepositoryLayout.Matches("sample.cs", discovery, AssemblyDiscoveryPattern()).Count);
        Assert.Single(RepositoryLayout.Matches("sample.cs",
            "services.AddSingleton(typeof(IRepo<>), typeof(Repo<>));", OpenGenericRegistrationPattern()));
    }

    private static IEnumerable<(string RelativePath, string Content)> ProductionSources()
    {
        foreach (var project in ScannedProjects)
        {
            foreach (var entry in RepositoryLayout.SourceFiles(project))
            {
                if (!AllowedPaths.Contains(entry.RelativePath))
                {
                    yield return entry;
                }
            }
        }
    }

    [GeneratedRegex(@"\bAdd(?:Singleton|Transient|Scoped)\s*<(?:[^<>]|<[^<>]*>)*>\s*\(\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FactorylessRegistrationPattern();

    [GeneratedRegex(@"\bAddKeyed(?:Singleton|Transient|Scoped)\s*<[^<>]*,[^<>]*>\s*\(\s*[^,()]*\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyedTypeMappingPattern();

    [GeneratedRegex(@"\bActivatorUtilities\s*\.\s*Create", RegexOptions.CultureInvariant)]
    private static partial Regex ReflectionActivationPattern();

    [GeneratedRegex(@"\b(?:Add(?:Keyed)?(?:Singleton|Transient|Scoped)|ServiceDescriptor)\s*\(\s*typeof\s*\([^)]*<\s*>",
        RegexOptions.CultureInvariant)]
    private static partial Regex OpenGenericRegistrationPattern();

    [GeneratedRegex(@"\.\s*(?:GetTypes|MakeGenericType)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyDiscoveryPattern();
}