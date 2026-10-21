using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>
/// Scheme package persistence: declared-member round trip, bounded reads and writes, string enums and lenient loading.
/// <para>Scheme 软件包持久化：声明成员往返、读写上限、字符串枚举与宽容读取。</para>
/// </summary>
public sealed class SchemeFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsDeclaredMembers()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "demo",
            Name = "Demo scheme",
            Version = "2.1.0",
            Description = "description",
            Author = "author",
            Inputs =
            [
                new SchemeInputDefinition
                {
                    Id = "image",
                    Name = "Image",
                    Type = SchemeInputType.Path,
                    Required = true,
                    Default = "images/a.img",
                    Sensitive = true
                },
                new SchemeInputDefinition
                {
                    Id = "mode",
                    Name = "Mode",
                    Type = SchemeInputType.Choice,
                    Choices = ["fast", "safe"]
                }
            ],
            Assets =
            [
                new SchemeAsset
                {
                    Id = "asset",
                    Name = "Asset",
                    RelativePath = "assets/a.bin",
                    Sha256 = new string('a', 64),
                    Length = 128
                }
            ],
            Stages =
            [
                new SchemeStage
                {
                    Id = "patch",
                    Name = "Patch stage",
                    Kind = SchemeStageKind.Patch,
                    FailurePolicy = SchemeStageFailurePolicy.Continue,
                    Graph = new WorkflowSnapshot
                    {
                        Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo", ExecutionStage = 2 }],
                        Connections = []
                    },
                    Inputs = [new SchemeStageInputBinding { InputId = "image", NodeInstanceId = "n1", PortId = "in" }],
                    Outputs = [new SchemeStageOutputBinding { OutputId = "result", NodeInstanceId = "n1", PortTitle = "Out" }]
                },
                new SchemeStage { Id = "device", Kind = SchemeStageKind.Device, Graph = new WorkflowSnapshot() }
            ],
            Metadata = new Dictionary<string, string> { ["origin"] = "test" }
        };
        var path = Path.Combine(_temp.Path, "demo.uotanscheme");

        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var text = await File.ReadAllTextAsync(path, token);
        Assert.Contains("\"Kind\": \"Patch\"", text, StringComparison.Ordinal);
        var restored = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token));

        Assert.Equal(SchemePackage.CurrentFormatVersion, restored.FormatVersion);
        Assert.Equal("demo", restored.Id);
        Assert.Equal("Demo scheme", restored.Name);
        Assert.Equal("2.1.0", restored.Version);
        Assert.Equal("description", restored.Description);
        Assert.Equal("author", restored.Author);
        Assert.Equal(2, restored.Inputs.Count);
        Assert.Equal(SchemeInputType.Path, restored.Inputs[0].Type);
        Assert.True(restored.Inputs[0].Required);
        Assert.True(restored.Inputs[0].Sensitive);
        Assert.Equal("images/a.img", restored.Inputs[0].Default);
        Assert.Equal(["fast", "safe"], restored.Inputs[1].Choices);
        var asset = Assert.Single(restored.Assets);
        Assert.Equal("assets/a.bin", asset.RelativePath);
        Assert.Equal(new string('a', 64), asset.Sha256);
        Assert.Equal(128L, asset.Length);
        Assert.Equal(2, restored.Stages.Count);
        var openedStage = restored.Stages[0];
        Assert.Equal(SchemeStageKind.Patch, openedStage.Kind);
        Assert.Equal(SchemeStageFailurePolicy.Continue, openedStage.FailurePolicy);
        Assert.Equal("in", Assert.Single(openedStage.Inputs).PortId);
        Assert.Equal("Out", Assert.Single(openedStage.Outputs).PortTitle);
        Assert.Equal(2, Assert.Single(openedStage.Graph.Nodes).ExecutionStage);
        Assert.Equal("test", restored.Metadata!["origin"]);
    }

    [Fact]
    public async Task SaveAsync_PackageLargerThanLimit_ReturnsFalseAndWritesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "too-large",
            Name = "Too large",
            Stages = [new SchemeStage { Id = "only", Graph = new WorkflowSnapshot() }],
            Metadata = new Dictionary<string, string> { ["blob"] = new string('x', SchemeFile.MaximumBytes) }
        };
        var path = Path.Combine(_temp.Path, "too-large.uotanscheme");

        Assert.False(await SchemeFile.SaveAsync(path, package, token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temp.Path, "too-large.uotanscheme.tmp-*"));
    }

    [Fact]
    public async Task LoadAsync_FileLargerThanLimit_ReturnsNull()
    {
        var path = Path.Combine(_temp.Path, "oversized.uotanscheme");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SchemeFile.MaximumBytes + 1);
        }

        Assert.Null(await SchemeFile.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_LenientJson_RepairsMissingMembersAndIgnoresUnknownMembers()
    {
        var path = _temp.Write("lenient.uotanscheme", """
            {
              "Id": "lenient",
              "Name": "Lenient",
              "UnknownTopLevel": { "nested": 1 },
              "Inputs": null,
              "Assets": null,
              "Stages": [
                {
                  "Id": "device",
                  "Kind": "Device",
                  "Graph": { "FormatVersion": 1, "Nodes": [], "Connections": [] },
                  "Inputs": null,
                  "Outputs": null,
                  "UnknownStageMember": true
                }
              ]
            }
            """);

        var restored = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(SchemePackage.CurrentFormatVersion, restored.FormatVersion);
        Assert.Empty(restored.Inputs);
        Assert.Empty(restored.Assets);
        Assert.Null(restored.Metadata);
        var stage = Assert.Single(restored.Stages);
        Assert.Equal(SchemeStageKind.Device, stage.Kind);
        Assert.Empty(stage.Inputs);
        Assert.Empty(stage.Outputs);
        Assert.NotNull(stage.Graph);
    }

    [Fact]
    public async Task LoadAsync_FutureFormatVersion_ReturnsNull()
    {
        var path = _temp.Write("future.uotanscheme", """
            { "FormatVersion": 99, "Id": "future", "Name": "Future", "Stages": [] }
            """);

        Assert.Null(await SchemeFile.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsNull()
    {
        Assert.Null(await SchemeFile.LoadAsync(
            Path.Combine(_temp.Path, "missing.uotanscheme"), TestContext.Current.CancellationToken));
    }
}