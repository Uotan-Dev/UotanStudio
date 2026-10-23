using UotanStudio.Projects;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ProjectLibraryCompatibilityTests
{
    [Fact]
    public void FormerApplicationTypes_ResolveOnlyFromTheLibraryAssembly()
    {
        var application = typeof(App).Assembly;
        Assert.Equal("UotanStudio.Projects", typeof(SolutionSnapshot).Assembly.GetName().Name);
        Assert.Null(application.GetType(typeof(SolutionSnapshot).FullName!));
        Assert.Null(application.GetType(typeof(SolutionPersistenceService).FullName!));
        Assert.Null(application.GetType(typeof(NodeDto).FullName!));
        Assert.Null(application.GetType(typeof(HistoryOperationDto).FullName!));
    }
}