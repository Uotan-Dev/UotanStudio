using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginScriptPackageTests
{
    private const string Script = "local out = tool.Device.Slot.Query('serial', 'S1')\nreturn out.slot";

    [Fact]
    public void CreateScript_ValidInput_BuildsValidPackage()
    {
        var package = VisualPluginPackageBuilder.CreateScript("Slot reader", "tests.slot-reader", Script, "Tester",
            "2.1.0");

        Assert.Equal(VisualPluginKind.ScriptNode, package.Kind);
        Assert.Equal(Script, package.Script);
        Assert.Equal("VisualPlugin.tests.slot-reader", package.NodeTypeKey);
        Assert.Equal("2.1.0", package.Manifest.Version);
        Assert.Equal("Tester", package.Manifest.Author);
        Assert.Empty(VisualPluginValidation.Validate(package));
    }

    [Fact]
    public void CreateScript_EmptyScript_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VisualPluginPackageBuilder.CreateScript("Empty", "tests.empty-script", "   "));
    }

    [Fact]
    public void CreateScript_TooLongScript_Throws()
    {
        var script = new string('-', VisualPluginValidation.MaximumScriptLength + 1);

        Assert.Throws<InvalidDataException>(() =>
            VisualPluginPackageBuilder.CreateScript("Too long", "tests.too-long-script", script));
    }

    [Fact]
    public void CreateScript_ScriptOnTemplate_Rejected()
    {
        var package = new VisualPluginPackage
        {
            Kind = VisualPluginKind.WorkflowTemplate,
            Script = Script,
            Manifest = new VisualPluginManifest
            {
                Id = "tests.template-script",
                Name = "Template with script",
                RequiredNodeTypes = ["File.Binary.Read"]
            },
            Graph = new WorkflowSnapshot
            {
                Nodes = [new NodeDto { InstanceId = "constant", TypeKey = "File.Binary.Read" }]
            }
        };

        var errors = VisualPluginValidation.Validate(package);

        Assert.Contains(errors, error => error.Contains("Only script-node packages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsScript()
    {
        using var temp = new TempDirectory();
        var package = VisualPluginPackageBuilder.CreateScript("Slot reader", "tests.slot-reader", Script);
        var jsonPath = Path.Combine(temp.Path, "slot-reader.uotanplugin.json");
        var bundlePath = Path.Combine(temp.Path, "slot-reader.uotanplugin");

        await VisualPluginFile.SaveAsync(jsonPath, package, TestContext.Current.CancellationToken);
        await VisualPluginFile.SaveAsync(bundlePath, package, TestContext.Current.CancellationToken);
        var fromJson = await VisualPluginFile.LoadAsync(jsonPath, TestContext.Current.CancellationToken);
        var fromBundle = await VisualPluginFile.LoadAsync(bundlePath, TestContext.Current.CancellationToken);

        Assert.Equal(Script, fromJson.Script);
        Assert.Equal(VisualPluginKind.ScriptNode, fromJson.Kind);
        Assert.Equal(Script, fromBundle.Script);
        Assert.Equal(VisualPluginKind.ScriptNode, fromBundle.Kind);
        Assert.Equal(package.NodeTypeKey, fromBundle.NodeTypeKey);
    }

    [Fact]
    public async Task InstallScriptAsync_ValidLua_RegistersEnabledScriptNode()
    {
        using var temp = new TempDirectory();
        var luaPath = temp.Write("slot-reader.lua", Script);
        var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"));

        var registration = await manager.InstallScriptAsync(luaPath, TestContext.Current.CancellationToken);

        Assert.Equal(VisualPluginKind.ScriptNode, registration.Kind);
        Assert.True(registration.IsEnabled);
        Assert.True(registration.IsAvailable);
        Assert.True(NodeTypeRegistry.Contains(registration.Package.NodeTypeKey));
        await manager.RemoveAsync(registration.Id, TestContext.Current.CancellationToken);
        Assert.False(NodeTypeRegistry.Contains(registration.Package.NodeTypeKey));
    }
}