using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ProjectLibraryCompatibilityTests
{
    [Fact]
    public void FormerApplicationTypes_ResolveThroughTheirOriginalAssembly()
    {
        var application = typeof(App).Assembly;
        Assert.Equal("UotanStudio.Projects", typeof(SolutionSnapshot).Assembly.GetName().Name);
        Assert.Same(typeof(SolutionSnapshot), application.GetType(typeof(SolutionSnapshot).FullName!, throwOnError: true));
        Assert.Same(typeof(SolutionPersistenceService), application.GetType(typeof(SolutionPersistenceService).FullName!, throwOnError: true));
        Assert.Same(typeof(NodeDto), application.GetType(typeof(NodeDto).FullName!, throwOnError: true));
        Assert.Same(typeof(HistoryOperationDto), application.GetType(typeof(HistoryOperationDto).FullName!, throwOnError: true));
    }
}
