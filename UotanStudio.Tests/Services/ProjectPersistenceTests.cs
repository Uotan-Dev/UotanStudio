using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Solution/project persistence round-trip tests: verify that the VS-like layout (solution file, firmware
/// project file, node project file) survives SaveAsync → LoadAsync.
/// <para>解决方案/项目持久化往返测试：验证 VS 式布局（解决方案文件、固件项目文件、节点项目文件）
/// 经 SaveAsync → LoadAsync 后保持一致。</para>
/// </summary>
public sealed class SolutionPersistenceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSolution()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "proj.uotan");
        var solution = new SolutionSnapshot
        {
            FormatVersion = 1,
            SolutionName = "TestProj",
            FirmwareProjectPath = "TestProj/TestProj.uotanfw",
            NodeProjectPath = "TestProj/TestProj.uotannode"
        };

        var ok = await SolutionPersistenceService.SaveSolutionAsync(path, solution, TestContext.Current.CancellationToken);
        Assert.True(ok);

        var loaded = await SolutionPersistenceService.LoadSolutionAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.FormatVersion);
        Assert.Equal("TestProj", loaded.SolutionName);
        Assert.Equal("TestProj/TestProj.uotanfw", loaded.FirmwareProjectPath);
        Assert.Equal("TestProj/TestProj.uotannode", loaded.NodeProjectPath);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsFirmwareProject()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "proj.uotanfw");
        var firmware = new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = "TestProj",
            Images =
            [
                new ImageEntryDto { RelativePath = "images/boot.img", SizeBytes = 123, Format = "boot" }
            ],
            Settings = new ProjectSettingsDto { ImageType = "EXT4", SuperSize = "4096" }
        };

        var ok = await SolutionPersistenceService.SaveFirmwareProjectAsync(path, firmware, TestContext.Current.CancellationToken);
        Assert.True(ok);

        var loaded = await SolutionPersistenceService.LoadFirmwareProjectAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("TestProj", loaded!.ProjectName);

        var image = Assert.Single(loaded.Images);
        Assert.Equal("images/boot.img", image.RelativePath);
        Assert.Equal(123, image.SizeBytes);
        Assert.Equal("boot", image.Format);

        Assert.NotNull(loaded.Settings);
        Assert.Equal("EXT4", loaded.Settings.ImageType);
        Assert.Equal("4096", loaded.Settings.SuperSize);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsNodeProject()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "proj.uotannode");
        var node = new NodeProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = "TestProj",
            Nodes =
            [
                new NodeDto { InstanceId = "n1", Title = "N1", Category = "Data", X = 1, Y = 2 }
            ],
            Connections =
            [
                new ConnectionDto { SourceConnectorTitle = "Out", TargetConnectorTitle = "In" }
            ]
        };

        var ok = await SolutionPersistenceService.SaveNodeProjectAsync(path, node, TestContext.Current.CancellationToken);
        Assert.True(ok);

        var loaded = await SolutionPersistenceService.LoadNodeProjectAsync(path, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("TestProj", loaded!.ProjectName);

        var n = Assert.Single(loaded.Nodes);
        Assert.Equal("n1", n.InstanceId);
        Assert.Equal("Data", n.Category);
        Assert.Equal(1, n.X);
        Assert.Equal(2, n.Y);

        var c = Assert.Single(loaded.Connections);
        Assert.Equal("Out", c.SourceConnectorTitle);
        Assert.Equal("In", c.TargetConnectorTitle);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var loaded = await SolutionPersistenceService.LoadSolutionAsync(
            Path.Combine(temp.Path, "missing.uotan"), TestContext.Current.CancellationToken);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SolutionPersistenceService.SaveSolutionAsync(string.Empty, new SolutionSnapshot(), TestContext.Current.CancellationToken));
    }
}