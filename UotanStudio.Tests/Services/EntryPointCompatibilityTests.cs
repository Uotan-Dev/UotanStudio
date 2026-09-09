using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Core;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Messages;
using UotanStudio.Services.Cli;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Characterizes existing adapter policies before consolidating their implementations.</summary>
[Collection("Workspace state")]
public sealed class EntryPointCompatibilityTests
{
    [Theory]
    [InlineData("Saved title.Milliseconds=0", 0)]
    [InlineData("Delay.Milliseconds=0", 2)]
    public async Task WorkflowCli_PreservesSavedTitleForOverrides(string input, int expectedExitCode)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "flow.uotannode");
        var snapshot = new WorkflowSnapshot
        {
            Nodes = [new() { TypeKey = "Flow.Delay", InstanceId = "saved-id", Title = "Saved title",
                InputInitialValues = new() { ["Milliseconds"] = "0" } }]
        };
        await using (var stream = File.Create(path))
            await JsonSerializer.SerializeAsync(stream, snapshot, WorkflowJsonContext.Default.WorkflowSnapshot,
                TestContext.Current.CancellationToken);
        Assert.Equal(expectedExitCode, WorkflowCli.Run(["run", path, "--set", input]));
    }

    [Fact]
    public async Task WorkspaceOpen_PreservesActivationAndLoadedRefreshWithoutDuplicateOnRefocus()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.txt", "hello"u8.ToArray());
        using var workspace = new WorkspaceViewModel(new TestWorkspaceFactory());
        var recipient = new object();
        var documents = new List<FileViewModel?>();
        WeakReferenceMessenger.Default.Register<ActiveDocumentChangedMessage>(recipient, (_, message) => documents.Add(message.Document));
        try
        {
            await workspace.OpenDocumentAsync(path, EditorOpenMode.Text);
            Assert.Equal(2, documents.Count);
            Assert.Same(documents[0], documents[1]);
            Assert.Equal("hello", documents[1]!.Text);
            await workspace.OpenDocumentAsync(path, EditorOpenMode.Text);
            Assert.Equal(2, documents.Count);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task CanvasLoad_PreservesFactoryTitlesLocationsAndIdConnections()
    {
        var editor = new NodeEditorViewModel();
        try
        {
            await editor.LoadFromAsync(new WorkflowSnapshot
            {
                Nodes = [new() { TypeKey = "Flow.Start", InstanceId = "a", Title = "Saved title", X = 12, Y = 34 },
                    new() { TypeKey = "Flow.End", InstanceId = "b" }],
                Connections = [new() { SourceInstanceId = "A", TargetInstanceId = "B", SourceConnectorTitle = "Flow", TargetConnectorTitle = "Flow" }]
            });
            Assert.Equal("Start", editor.Nodes[0].Title);
            Assert.Equal(12, editor.Nodes[0].Location.X);
            Assert.Equal(34, editor.Nodes[0].Location.Y);
            var connection = Assert.Single(editor.Connections);
            Assert.Same(editor.Nodes[0].Output[0], connection.Source);
            Assert.Same(editor.Nodes[1].Input[0], connection.Target);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(editor); }
    }

    [Fact]
    public async Task SparseUnpack_PreservesMcpRejectionAndEditorRawFallback()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "system.img");
        using (var sparse = new SparseFile(4096, 4096))
        {
            sparse.AddFillChunk(0x01010101, 4096);
            using var stream = File.Create(path);
            sparse.WriteToStream(stream, sparse: true);
        }
        using var mcp = JsonDocument.Parse(await new UotanStudioMcpTools().FirmwareUnpack(path,
            outputDir: Path.Combine(temp.Path, "mcp"), ct: TestContext.Current.CancellationToken));
        Assert.False(mcp.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Unpack is not supported for format: Android Sparse Image (simg)", mcp.RootElement.GetProperty("error").GetString());
        var editor = new FirmwareInfoViewModel { FormatName = "Android Sparse Image (simg)" };
        var dispatch = typeof(FirmwareInfoViewModel).GetMethod("DispatchUnpackAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<UotanStudio.Services.Firmware.FirmwareOpResult>)dispatch.Invoke(editor,
            [path, Path.Combine(temp.Path, "editor")])!;
        Assert.True(result.Success, result.Summary);
        var raw = Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "editor"), "*.raw"));
        Assert.Equal(Enumerable.Repeat((byte)1, 4096), File.ReadAllBytes(raw));
    }

    private sealed class TestWorkspaceFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }

    [Theory]
    [InlineData(" ext4 ", "EXT4")]
    [InlineData("boot", null)]
    [InlineData(".ext4", null)]
    public void CliFormatNames_PreserveWhitespaceAndAliasPolicy(string name, string? expected)
    {
        FirmwareFormatRegistry.RegisterAll();
        Assert.Equal(expected, FirmwareCli.ResolveByFormatNameForTest(name)?.FormatName);
    }

    [Theory]
    [InlineData(null, "unknown.img", "cpio")]
    [InlineData(null, "system.erofs", "erofs")]
    [InlineData(" EXT4 ", "unknown.img", "ext4")]
    [InlineData("f2fs", "system.f2fs", null)]
    [InlineData("CPIO/TAR Archive", "ramdisk.cpio", null)]
    public void McpPack_PreservesDefaultAndExplicitFormatPolicy(string? format, string output, string? expected)
    {
        using var temp = new TempDirectory();
        var method = typeof(UotanStudioMcpTools).GetMethod("ResolvePackFormat", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [format, temp.Path, output]));
    }

    [Fact]
    public async Task McpPack_UnknownOutputExtensionStillProducesCpio()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hello.txt"), "compatibility");
        var output = Path.Combine(temp.Path, "result.img");
        var tools = new UotanStudioMcpTools();
        using var result = JsonDocument.Parse(await tools.FirmwarePack(outputPath: output, inputDir: source, inputPath: null,
            ct: TestContext.Current.CancellationToken));
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
        Assert.Equal("cpio", result.RootElement.GetProperty("format").GetString());
        Assert.Equal("CPIO/TAR Archive", FormatDetector.Detect(output)?.FormatName);
    }
}
