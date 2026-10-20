using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginDependencyTests
{
    [Fact]
    public async Task Manager_DisabledPackage_RejectsPreviouslyCreatedNode()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var package = Package("test.disabled-existing");
        await SaveAsync(temp.Path, package, token);
        var manager = new VisualPluginManager(temp.Path);
        var node = NodeTypeRegistry.Create(package.NodeTypeKey)!;

        await manager.SetEnabledAsync(package.Manifest.Id, false, token);
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token);

        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("2.0.0", "1.0.0")]
    [InlineData("1.0", "2.0.0")]
    public void Validate_InvalidVersionBounds_RejectsPackage(string minimum, string maximum)
    {
        var package = Package("test.invalid-range");
        package.Manifest.Dependencies.Add(new VisualPluginDependency
        {
            Id = "test.base",
            MinimumVersion = minimum,
            MaximumVersionExclusive = maximum
        });
        Assert.NotEmpty(VisualPluginValidation.Validate(package));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.9.99", true)]
    [InlineData("2.0.0", false)]
    [InlineData("1.2.2", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2", false)]
    public void MatchesVersion_InclusiveMinimumExclusiveMaximum_UsesDocumentedNumericDialect(string version, bool expected)
    {
        var dependency = new VisualPluginDependency { Id = "test.base", MinimumVersion = "1.2.3", MaximumVersionExclusive = "2.0.0" };
        Assert.Equal(expected, dependency.MatchesVersion(version));
    }

    [Fact]
    public async Task Manager_DependencyDisabled_RejectsExistingCanvasComposite()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var dependency = Package("test.runtime-base");
        var dependent = Package("test.runtime-dependent");
        dependent.Manifest.Dependencies.Add(new VisualPluginDependency { Id = dependency.Manifest.Id, MinimumVersion = "1.0.0" });
        await SaveAsync(temp.Path, dependency, token);
        await SaveAsync(temp.Path, dependent, token);
        var manager = new VisualPluginManager(temp.Path);
        Assert.All(manager.Registrations, registration => Assert.True(registration.IsAvailable));
        var node = NodeTypeRegistry.Create(dependent.NodeTypeKey)!;

        await manager.SetEnabledAsync(dependency.Manifest.Id, false, token);
        var result = await new NodeExecutionEngine(new Log()).ExecuteAsync([node], [], token);

        Assert.False(manager.Registrations.Single(registration => registration.Id == dependent.Manifest.Id).IsAvailable);
        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Manager_OneSidedConflict_BlocksBothUntilOneIsDisabled()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var first = Package("test.conflict-first");
        var second = Package("test.conflict-second");
        first.Manifest.Conflicts.Add(second.Manifest.Id);
        await SaveAsync(temp.Path, first, token);
        await SaveAsync(temp.Path, second, token);
        var manager = new VisualPluginManager(temp.Path);
        Assert.All(manager.Registrations, registration => Assert.False(registration.IsAvailable));

        await manager.SetEnabledAsync(second.Manifest.Id, false, token);

        Assert.True(manager.Registrations.Single(registration => registration.Id == first.Manifest.Id).IsAvailable);
    }

    [Fact]
    public async Task Manager_ExplicitDependencyCycle_BlocksActivation()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var first = Package("test.cycle-first");
        var second = Package("test.cycle-second");
        first.Manifest.Dependencies.Add(new VisualPluginDependency { Id = second.Manifest.Id });
        second.Manifest.Dependencies.Add(new VisualPluginDependency { Id = first.Manifest.Id });
        await SaveAsync(temp.Path, first, token);
        await SaveAsync(temp.Path, second, token);

        var manager = new VisualPluginManager(temp.Path);

        Assert.All(manager.Registrations, registration => Assert.False(registration.IsAvailable));
        Assert.All(manager.Registrations, registration => Assert.Contains(registration.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("1.5.0", true)]
    [InlineData("2.0.0", false)]
    [InlineData("legacy-release", false)]
    public async Task Manager_VersionRequirement_ControlsActivation(string version, bool available)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var first = Package("test.version-base");
        first.Manifest.Version = version;
        var second = Package("test.version-dependent");
        second.Manifest.Dependencies.Add(new VisualPluginDependency
        {
            Id = first.Manifest.Id,
            MinimumVersion = "1.0.0",
            MaximumVersionExclusive = "2.0.0"
        });
        await SaveAsync(temp.Path, first, token);
        await SaveAsync(temp.Path, second, token);

        var manager = new VisualPluginManager(temp.Path);

        Assert.Equal(available, manager.Registrations.Single(registration => registration.Id == second.Manifest.Id).IsAvailable);
    }

    [Fact]
    public void Validate_LegacyDependencyDeclaration_RejectsInsteadOfIgnoring()
    {
        var package = Package("test.legacy");
        package.FormatVersion = 1;
        package.Manifest.Dependencies.Add(new VisualPluginDependency { Id = "test.base" });
        Assert.NotEmpty(VisualPluginValidation.Validate(package));
    }

    private static VisualPluginPackage Package(string id) => new()
    {
        Kind = VisualPluginKind.CompositeNode,
        Manifest = new VisualPluginManifest { Id = id, Name = id }
    };

    private static Task SaveAsync(string directory, VisualPluginPackage package, CancellationToken token)
        => VisualPluginFile.SaveAsync(Path.Combine(directory, package.Manifest.Id + ".uotanplugin.json"), package, token);

    private sealed class Log : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}