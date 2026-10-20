using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Node editor UI")]
public sealed class NodeCatalogReductionTests
{
    [Fact]
    public void Descriptors_CurrentBaseline_CountsRegisteredAndToolboxVisibleNodes()
    {
        NodeFactoryRegistrar.RegisterAll();
        var builtIn = NodeFactoryRegistrar.BuiltInTypeKeys;

        Assert.Equal(67, builtIn.Count);
        Assert.Equal(67, NodeFactoryRegistrar.Descriptors
            .Count(descriptor => descriptor.IsToolboxVisible && builtIn.Contains(descriptor.TypeKey)));
    }

    [Fact]
    public void Descriptors_ElementFamilies_RegisterOneNodePerFamily()
    {
        NodeFactoryRegistrar.RegisterAll();

        Assert.Equal(
            [
                "UI.Condition.Equals", "UI.Content.Create", "UI.Dialog", "UI.Field.Create", "UI.Form.Create",
                "UI.Layout.Create", "UI.Notification.Show", "UI.Progress", "UI.Result.Read"
            ],
            NodeFactoryRegistrar.Descriptors
                .Where(descriptor => descriptor.TypeKey.StartsWith("UI.", StringComparison.Ordinal))
                .Select(descriptor => descriptor.TypeKey)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Descriptors_NoDeviceStateQueryOrSlotKeys_RemainRegistered()
    {
        NodeFactoryRegistrar.RegisterAll();

        Assert.Equal(
            [
                "Device.Partition.Flash", "Device.Partition.Read", "Device.Power.Reboot", "Device.Property.Read",
                "Device.Shell.Command", "Device.Table.Read", "Device.Target.Select"
            ],
            NodeFactoryRegistrar.Descriptors
                .Where(descriptor => descriptor.TypeKey.StartsWith("Device.", StringComparison.Ordinal))
                .Select(descriptor => descriptor.TypeKey)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(NodeFactoryRegistrar.Descriptors,
            descriptor => descriptor.TypeKey.StartsWith("Mirroring.", StringComparison.Ordinal));
    }

    [Fact]
    public void TryCreateNode_RetiredFieldAndLayoutKeys_CreateMergedNodesAndKeepEdges()
    {
        var snapshot = new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto
                {
                    InstanceId = "field", TypeKey = "UI.Field.Number", Category = "Interaction",
                    InputInitialValues = new Dictionary<string, string> { ["Label"] = "Capacity", ["Field ID"] = "capacity" }
                },
                new NodeDto { InstanceId = "layout", TypeKey = "UI.Layout.Row", Category = "Interaction" }
            ],
            Connections =
            [
                new ConnectionDto
                {
                    SourceInstanceId = "field", SourceConnectorTitle = "Component",
                    TargetInstanceId = "layout", TargetConnectorTitle = "First"
                }
            ]
        };

        var runtime = WorkflowRuntimeBuilder.Build(snapshot, preserveUnknown: true);

        var field = Assert.Single(runtime.Nodes, node => node.TypeKey == "UI.Field.Create");
        var layout = Assert.Single(runtime.Nodes, node => node.TypeKey == "UI.Layout.Create");
        Assert.Equal("NumberField", field.Input.Single(port => port.Title == "Kind").InitialValue);
        Assert.Equal(NodePortType.Number, field.Input.Single(port => port.Title == "Default").PortType);
        Assert.Equal("Capacity", field.Input.Single(port => port.Title == "Label").InitialValue);
        Assert.Equal("Row", layout.Input.Single(port => port.Title == "Kind").InitialValue);
        var connection = Assert.Single(runtime.ViewConnections);
        Assert.Same(field.Output.Single(port => port.Title == "Component"), connection.Source);
        Assert.Same(layout.Input.Single(port => port.Title == "First"), connection.Target);
    }

    [Fact]
    public void TryCreateNode_RetiredInitRcQuery_RenamesPortOntoMergedConfigNode()
    {
        var snapshot = new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto
                {
                    InstanceId = "rc", TypeKey = "Config.InitRc.Query", Category = "Configuration",
                    InputInitialValues = new Dictionary<string, string>
                    {
                        ["Section Kind"] = "Service", ["Directive"] = "class core", ["Expected Sections"] = "3"
                    }
                }
            ]
        };

        var config = Assert.Single(WorkflowRuntimeBuilder.Build(snapshot, preserveUnknown: true).Nodes);

        Assert.Equal("Config.Edit", config.TypeKey);
        Assert.Equal("InitRc", config.Input.Single(port => port.Title == "Format").InitialValue);
        Assert.Equal("Query", config.Input.Single(port => port.Title == "Operation").InitialValue);
        Assert.Equal(3d, config.Input.Single(port => port.Title == "Expected Matches").InitialValue);
    }

    [Fact]
    public void TryCreateNode_RetiredFileImport_RenamesFileModeAndKeepsMergedOperation()
    {
        var snapshot = new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto
                {
                    InstanceId = "imp", TypeKey = "Workspace.File.Import", Category = "Workspace",
                    InputInitialValues = new Dictionary<string, string>
                    {
                        ["Relative Path"] = "etc/init.rc", ["Source"] = "D:/stage/init.rc", ["Operation"] = "Add"
                    }
                }
            ]
        };

        var workspace = Assert.Single(WorkflowRuntimeBuilder.Build(snapshot, preserveUnknown: true).Nodes);

        Assert.Equal("Workspace.File", workspace.TypeKey);
        Assert.Equal("Import", workspace.Input.Single(port => port.Title == "Operation").InitialValue);
        Assert.Equal("Add", workspace.Input.Single(port => port.Title == "File Mode").InitialValue);
        Assert.Equal("etc/init.rc", workspace.Input.Single(port => port.Title == "Relative Path").InitialValue);
    }

    [Theory]
    [InlineData("Build.Slots.Merge")]
    [InlineData("Config.Fstab.Edit")]
    [InlineData("Config.InitRc.EditArgument")]
    public void TryCreateNode_UnknownOrUnmigratableKey_PreservesAsUnavailable(string typeKey)
    {
        Assert.True(WorkflowReconstruction.TryCreateNode(
            new NodeDto
            {
                InstanceId = "n",
                TypeKey = typeKey,
                Category = "Configuration",
                InputPorts = [new NodePortDto { Title = "Text", PortType = "Text" }]
            },
            out var node, preserveUnknown: true));

        var missing = Assert.IsType<MissingNodeViewModel>(node);
        Assert.True(missing.IsUnavailable);
        Assert.Equal(typeKey, missing.TypeKey);
        Assert.Single(missing.Input, port => port.Title == "Text");
    }

    [Theory]
    [InlineData("Device.Storage.EraseAll")]
    [InlineData("Mirroring.Session.Start")]
    [InlineData("Data.Value.Constant")]
    [InlineData("Validation.Format.Detect")]
    [InlineData("Firmware.Cpio.Unpack")]
    [InlineData("UI.Dialog.Show")]
    [InlineData("UI.Progress.Update")]
    [InlineData("Flow.Execution.Start")]
    [InlineData("Validation.Format.Check")]
    public void TryCreateNode_RefusedTypeKey_RejectsEvenWhenPlaceholdersAllowed(string typeKey)
    {
        Assert.True(NodeTypeAlias.RefusedTypeKeys.Contains(typeKey));
        Assert.False(NodeTypeRegistry.Contains(typeKey));

        Assert.False(WorkflowReconstruction.TryCreateNode(
            new NodeDto { InstanceId = "n", TypeKey = typeKey, Category = "Data" }, out _, preserveUnknown: true));
    }

    [Fact]
    public void Descriptors_EverySeededTypeKey_IsNotRefusedAtLoad()
    {
        NodeFactoryRegistrar.RegisterAll();

        foreach (var typeKey in NodeFactoryRegistrar.BuiltInTypeKeys)
        {
            Assert.False(NodeTypeAlias.RefusedTypeKeys.Contains(typeKey),
                $"'{typeKey}' is seeded in the toolbox yet refused when a saved workflow is loaded.");
        }
    }

    [Fact]
    public void Build_RefusedTypeKey_ReportsTheRemovedTypeAndRefusesTheGraph()
    {
        var runtime = WorkflowRuntimeBuilder.Build(new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "a", TypeKey = "Device.Partition.Erase", Category = "Device" }]
        }, preserveUnknown: true);

        var error = Assert.Single(runtime.Errors);
        Assert.Contains("Device.Partition.Erase", error, StringComparison.Ordinal);
        Assert.Empty(runtime.Nodes);
    }

    [Fact]
    public async Task LoadFromAsync_RefusedTypeKey_ThrowsWithoutTouchingTheCanvas()
    {
        using var editor = new NodeEditorViewModel();
        var before = editor.Nodes.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => editor.LoadFromAsync(new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "g", TypeKey = "Feedback.Log.Write", Category = "Interaction" }]
        }));

        Assert.Equal(before, editor.Nodes.Count);
    }

    [Fact]
    public void Descriptors_AllAliases_TargetARegisteredNodeType()
    {
        NodeFactoryRegistrar.RegisterAll();
        var retired = new[]
        {
            "Image.Text.Read", "Image.Text.Stage", "Image.File.ReadAttributes", "Image.File.SetAttributes",
            "Image.Xattr.Read", "Image.Xattr.Set", "Image.Xattr.Delete", "Resource.Asset.Open", "Resource.Asset.ReadText",
            "Binary.Pattern.Find", "Binary.Pattern.Replace", "Workspace.File.Find", "Workspace.File.Exists",
            "Workspace.File.Import", "Workspace.File.Delete", "Workspace.Text.Stage",
            "Config.Text.Edit", "Config.Json.Edit", "Config.Json.Query", "Config.Ini.Edit", "Config.Ini.Query",
            "Config.Xml.Edit", "Config.Xml.Query", "Config.AndroidProperty.Edit", "Config.AndroidProperty.Query",
            "Config.Fstab.Query", "Config.Fstab.Add", "Config.FileContexts.Edit", "Config.FileContexts.Query",
            "Config.FileContexts.Add", "Config.FileContexts.Delete", "Config.FsConfig.Edit", "Config.FsConfig.Query",
            "Config.FsConfig.Add", "Config.FsConfig.Delete", "Config.InitRc.Query", "Config.InitRc.FindReferences",
            "Config.InitRc.EditHeader", "Config.InitRc.AddSection", "Config.InitRc.AddCommand", "Config.InitRc.EditCommand",
            "UI.Field.Text", "UI.Field.Number", "UI.Field.Boolean", "UI.Field.Choice", "UI.Field.MultiChoice",
            "UI.Field.File", "UI.Field.Directory", "UI.Field.SavePath", "UI.Layout.Row", "UI.Layout.Column",
            "UI.Layout.Group", "UI.Layout.Pages", "UI.Content.Text", "UI.Content.Divider", "UI.Content.Image",
            "Network.GitHub.Download"
        };

        foreach (var typeKey in retired)
        {
            var alias = NodeTypeAlias.Find(typeKey);
            Assert.NotNull(alias);
            Assert.True(NodeTypeRegistry.Contains(alias!.TargetTypeKey), $"'{typeKey}' maps to unregistered '{alias.TargetTypeKey}'.");
            Assert.False(NodeTypeRegistry.Contains(typeKey), $"'{typeKey}' is still a registered node type.");
            Assert.False(NodeTypeAlias.RefusedTypeKeys.Contains(typeKey), $"'{typeKey}' is both aliased and refused.");
            var node = NodeTypeRegistry.Create(alias.TargetTypeKey)!;
            Assert.NotEmpty(node.Input);
            NodeTypeAlias.ApplyDefaults(node, alias);
            foreach (var (title, value) in alias.Defaults)
            {
                Assert.Equal(value, Convert.ToString(node.Input.Single(port => port.Title == title).InitialValue,
                    System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}