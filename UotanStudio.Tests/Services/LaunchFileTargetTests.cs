using UotanStudio.Services.Launch;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public class LaunchFileTargetTests
{
    [Fact]
    public void Classify_SolutionPath_OpensAsSolution()
    {
        using var temp = new TempDirectory();
        var solution = temp.Write("Demo.uotan", "{}");

        var target = LaunchFileTarget.Classify(solution);

        Assert.Equal(LaunchFileKind.Solution, target.Kind);
        Assert.Equal(Path.GetFullPath(solution), target.Path);
        Assert.Equal(Path.GetFullPath(solution), target.OwningSolution);
        Assert.Null(target.ProjectName);
    }

    [Fact]
    public void Classify_ImagePath_OpensAsDocument()
    {
        using var temp = new TempDirectory();
        var image = temp.WriteBytes("boot.img", [0x41, 0x42]);

        var target = LaunchFileTarget.Classify(image);

        Assert.Equal(LaunchFileKind.Document, target.Kind);
        Assert.Null(target.OwningSolution);
        Assert.Null(target.ProjectName);
    }

    [Fact]
    public void Classify_FirmwareProjectBesideSolution_OpensOwningSolution()
    {
        using var temp = new TempDirectory();
        var solution = temp.Write("Demo.uotan", "{}");
        var firmware = temp.Write(Path.Combine("System", "System.uotanfw"), "{}");

        var target = LaunchFileTarget.Classify(firmware);

        Assert.Equal(LaunchFileKind.FirmwareProject, target.Kind);
        Assert.Equal(Path.GetFullPath(solution), target.OwningSolution);
        Assert.Equal("System", target.ProjectName);
    }

    [Fact]
    public void Classify_NodeProjectUnderSolutionRoot_OpensOwningSolution()
    {
        using var temp = new TempDirectory();
        var solution = temp.Write("Demo.uotan", "{}");
        var node = temp.Write(Path.Combine("nodes", "Setup.uotannode"), "{}");

        var target = LaunchFileTarget.Classify(node);

        Assert.Equal(LaunchFileKind.NodeProject, target.Kind);
        Assert.Equal(Path.GetFullPath(solution), target.OwningSolution);
        Assert.Equal("Setup", target.ProjectName);
    }

    [Fact]
    public void Classify_ProjectFileWithAmbiguousSolution_FallsBackToDocument()
    {
        using var temp = new TempDirectory();
        temp.Write("First.uotan", "{}");
        temp.Write("Second.uotan", "{}");
        var firmware = temp.Write(Path.Combine("Loose", "Loose.uotanfw"), "{}");

        var target = LaunchFileTarget.Classify(firmware);

        Assert.Equal(LaunchFileKind.Document, target.Kind);
        Assert.Null(target.OwningSolution);
    }

    [Fact]
    public void FindOwningSolution_AmbiguousAncestorWithTwoSolutions_ReturnsNull()
    {
        using var temp = new TempDirectory();
        temp.Write("First.uotan", "{}");
        temp.Write("Second.uotan", "{}");
        var node = temp.Write(Path.Combine("nodes", "Setup.uotannode"), "{}");

        var solution = LaunchFileTarget.FindOwningSolution(node);

        Assert.Null(solution);
    }
}