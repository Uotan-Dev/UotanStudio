using UotanStudio.Services;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Project persistence round-trip tests: verify that a full <c>.uotan</c> snapshot (project name,
/// image list, settings, nodes, connections) survives SaveAsync → LoadAsync.
/// <para>工程持久化往返测试：验证完整 <c>.uotan</c> 快照（工程名、镜像清单、设置、节点、连接）
/// 经 SaveAsync → LoadAsync 后保持一致。</para>
/// </summary>
public sealed class ProjectPersistenceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsFullSnapshot()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "proj.uotan");
        var snapshot = new ProjectSnapshot
        {
            FormatVersion = 2,
            ProjectName = "TestProj",
            Images =
            [
                new ImageEntryDto { RelativePath = "images/boot.img", SizeBytes = 123, Format = "boot" }
            ],
            Settings = new ProjectSettingsDto { ImageType = "EXT4", SuperSize = "4096" },
            Nodes =
            [
                new NodeDto { InstanceId = "n1", Title = "N1", Category = "Data", X = 1, Y = 2 }
            ],
            Connections =
            [
                new ConnectionDto { SourceConnectorTitle = "Out", TargetConnectorTitle = "In" }
            ]
        };

        var ct = TestContext.Current.CancellationToken;
        var ok = await ProjectPersistenceService.SaveAsync(path, snapshot, ct);
        Assert.True(ok);

        var loaded = await ProjectPersistenceService.LoadAsync(path, ct);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.FormatVersion);
        Assert.Equal("TestProj", loaded.ProjectName);

        var image = Assert.Single(loaded.Images);
        Assert.Equal("images/boot.img", image.RelativePath);
        Assert.Equal(123, image.SizeBytes);
        Assert.Equal("boot", image.Format);

        Assert.NotNull(loaded.Settings);
        Assert.Equal("EXT4", loaded.Settings.ImageType);
        Assert.Equal("4096", loaded.Settings.SuperSize);

        var node = Assert.Single(loaded.Nodes);
        Assert.Equal("n1", node.InstanceId);
        Assert.Equal("Data", node.Category);
        Assert.Equal(1, node.X);
        Assert.Equal(2, node.Y);

        var connection = Assert.Single(loaded.Connections);
        Assert.Equal("Out", connection.SourceConnectorTitle);
        Assert.Equal("In", connection.TargetConnectorTitle);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var loaded = await ProjectPersistenceService.LoadAsync(Path.Combine(temp.Path, "missing.uotan"), TestContext.Current.CancellationToken);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_EmptyPath_ReturnsFalse()
    {
        var ok = await ProjectPersistenceService.SaveAsync(string.Empty, new ProjectSnapshot(), TestContext.Current.CancellationToken);
        Assert.False(ok);
    }
}