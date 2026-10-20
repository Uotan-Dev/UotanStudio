using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Seed catalog of built-in workflow nodes and the composite-plugin descriptor factory.<para>内置工作流节点与组合插件描述符工厂的种子目录。</para></summary>
internal static class NodeCatalogSeed
{
    /// <summary>Creates the built-in node descriptors in registration order.<para>按注册顺序创建内置节点描述符。</para></summary>
    /// <returns>The ordered list of built-in workflow node descriptors.<para>按顺序排列的内置工作流节点描述符列表。</para></returns>
    internal static IReadOnlyList<NodeDescriptor> CreateDescriptors() =>
    [
        D("Workspace.Document.Resolve", "Node_Workspace_Document_Resolve", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.Resolve")),
        D("Workspace.Document.ReadText", "Node_Workspace_Document_ReadText", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.ReadText")),
        D("Workspace.Document.ReplaceText", "Node_Workspace_Document_ReplaceText", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.ReplaceText")),
        P("Image.Text.Edit", "Node_Image_Text_Edit", "Configuration", () => PatchNodeViewModel.Create("Image.Text.Edit")),
        P("Image.Edit.CollectText", "Node_Image_Edit_CollectText", "Configuration", () => PatchNodeViewModel.Create("Image.Edit.CollectText")),
        P("Image.Edit.Apply", "Node_Image_Edit_Apply", "Configuration", () => PatchNodeViewModel.Create("Image.Edit.Apply")),
        P("Image.File.Attributes", "Node_Image_File_Attributes", "Configuration", () => PatchNodeViewModel.Create("Image.File.Attributes")),
        P("Image.Xattr.Edit", "Node_Image_Xattr_Edit", "Configuration", () => PatchNodeViewModel.Create("Image.Xattr.Edit")),
        P("Config.Edit", "Node_Config_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Edit")),
        P("Binary.Search", "Node_Binary_Search", "Data", () => PatchNodeViewModel.Create("Binary.Search")),
        P("Workspace.Partition.Resolve", "Node_Workspace_Partition_Resolve", "Firmware", () => PatchNodeViewModel.Create("Workspace.Partition.Resolve")),
        P("Flow.Applicability.Gate", "Node_Flow_Applicability_Gate", "Flow", () => PatchNodeViewModel.Create("Flow.Applicability.Gate")),
        P("Resource.Asset.Read", "Node_Resource_Asset_Read", "Inputs", () => PatchNodeViewModel.Create("Resource.Asset.Read")),
        P("Workspace.File", "Node_Workspace_File", "Workspace", () => PatchNodeViewModel.Create("Workspace.File")),
        P("Workspace.Asset.Stage", "Node_Workspace_Asset_Stage", "Workspace", () => PatchNodeViewModel.Create("Workspace.Asset.Stage")),
        P("Workspace.Directory.Open", "Node_Workspace_Directory_Open", "Workspace", () => PatchNodeViewModel.Create("Workspace.Directory.Open")),
        P("File.Text.Read", "Node_File_Text_Read", "Data", () => PatchNodeViewModel.Create("File.Text.Read")),
        P("File.Text.Publish", "Node_File_Text_Publish", "Data", () => PatchNodeViewModel.Create("File.Text.Publish")),
        D("Firmware.Container.Unpack", "Node_Firmware_Container_Unpack", "Firmware", FirmwareNodeViewModel.CreateUnpackNode),
        D("Firmware.Container.Pack", "Node_Firmware_Container_Pack", "Firmware", FirmwareNodeViewModel.CreatePackNode),
        D("Validation.Signature.Verify", "Node_Validation_Signature_Verify", "Validation", FirmwareNodeViewModel.CreateVerifyNode),
        D("Validation.Vbmeta.Sign", "Node_Validation_Vbmeta_Sign", "Validation", VbmetaSignNodeViewModel.CreateSignNode),
        D("Boot.CommandLine.Edit", "Node_Boot_CommandLine_Edit", "Composite", FirmwareNodeViewModel.CreateCmdlineEditNode),
        D("Image.Super.Pack", "Node_Image_Super_Pack", "Firmware", SuperNodeViewModel.CreateSuperPackNode),
        D("Image.Super.Unpack", "Node_Image_Super_Unpack", "Firmware", SuperUnpackNodeViewModel.CreateUnpackNode),
        D("Device.Target.Select", "Node_Device_Target_Select", "Device", SelectDeviceNodeViewModel.CreateSelectDeviceNode),
        D("Device.Property.Read", "Node_Device_Property_Read", "Device", DevicePropertyNodeViewModel.CreateDevicePropertyNode),
        D("Device.Partition.Flash", "Node_Device_Partition_Flash", "Device", FlashNodeViewModel.CreateFlashNode, true),
        D("Device.Power.Reboot", "Node_Device_Power_Reboot", "Device", FlashNodeViewModel.CreateRebootNode, true),
        D("Device.Table.Read", "Node_Device_Table_Read", "Device", DeviceServiceNodeViewModel.CreatePartitionTableReadNode),
        D("Device.Partition.Read", "Node_Device_Partition_Read", "Device", DevicePartitionReadNodeViewModel.CreateReadNode),
        D("Device.Shell.Command", "Node_Device_Shell_Command", "Device", DeviceServiceNodeViewModel.CreateShellCommandNode, true),
        D("File.Binary.Read", "Node_File_Binary_Read", "Data", DataNodeViewModel.CreateFileReadNode),
        D("File.Bytes.Write", "Node_File_Bytes_Write", "Data", FileBytesNodeViewModel.CreateWriteNode),
        D("File.Artifact.Open", "Node_File_Artifact_Open", "Data", () => new ArtifactNodeViewModel()),
        D("Resource.Project.Image", "Node_Resource_Project_Image", "Inputs", ProjectImageNodeViewModel.CreateProjectImageNode),
        D("Data.Cipher.Decrypt", "Node_Data_Cipher_Decrypt", "Data", DecryptNodeViewModel.CreateDecryptNode),
        D("Data.Compression.Decompress", "Node_Data_Compression_Decompress", "Data", DecompressNodeViewModel.CreateDecompressNode),
        D("Data.Value.Convert", "Node_Data_Value_Convert", "Data", DataNodeViewModel.CreateConvertNode),
        D("Validation.Data.Hash", "Node_Validation_Data_Hash", "Data", DataNodeViewModel.CreateChecksumNode),
        D("Data.Value.Compare", "Node_Data_Value_Compare", "Data", DataNodeViewModel.CreateCompareNode),
        D("Data.Bytes.Concat", "Node_Data_Bytes_Concat", "Data", ExtendedDataNodeViewModel.CreateConcatNode),
        D("Data.Bytes.Slice", "Node_Data_Bytes_Slice", "Data", ExtendedDataNodeViewModel.CreateSliceNode),
        D("Data.Cipher.Encrypt", "Node_Data_Cipher_Encrypt", "Data", ExtendedDataNodeViewModel.CreateEncryptNode),
        D("Data.Compression.Compress", "Node_Data_Compression_Compress", "Data", ExtendedDataNodeViewModel.CreateCompressNode),
        D("Data.Bytes.Swap", "Node_Data_Bytes_Swap", "Data", ExtendedDataNodeViewModel.CreateByteSwapNode),
        D("Data.List.FromText", "Node_Data_List_FromText", "Data", DataListNodeViewModel.CreateFromTextNode),
        D("Data.List.ElementAt", "Node_Data_List_ElementAt", "Data", DataListNodeViewModel.CreateElementAtNode),
        D("Data.List.Count", "Node_Data_List_Count", "Data", DataListNodeViewModel.CreateCountNode),
        D("Data.List.Map", "Node_Data_List_Map", "Data", ListMapNodeViewModel.CreateMapNode),
        D("Network.File.Download", "Node_Network_File_Download", "Inputs", () => NetworkNodeViewModel.CreateDownloadNode()),
        D("Flow.Condition.Branch", "Node_Flow_Condition_Branch", "Flow", FlowControlNodeViewModel.CreateBranchNode),
        D("Flow.Collection.Loop", "Node_Flow_Collection_Loop", "Flow", FlowControlNodeViewModel.CreateLoopNode),
        D("Flow.Execution.Delay", "Node_Flow_Execution_Delay", "Flow", FlowControlNodeViewModel.CreateDelayNode),
        D("Flow.Execution.Parallel", "Node_Flow_Execution_Parallel", "Flow", FlowControlNodeViewModel.CreateParallelNode),
        D("Flow.Execution.Terminate", "Node_Flow_Execution_Terminate", "Flow", FlowControlNodeViewModel.CreateTerminateNode),
        D("Feedback.Progress.Report", "Node_Feedback_Progress_Report", "Interaction", OutputNodeViewModel.CreateProgressNode),
        D("File.Artifact.Publish", "Node_File_Artifact_Publish", "Data", OutputNodeViewModel.CreateSaveNode),
        .. InteractionNodeViewModel.TypeKeys.Select(typeKey => D(typeKey, "Node_" + typeKey.Replace('.', '_'), "Interaction",
            () => InteractionNodeViewModel.Create(typeKey)))
    ];

    /// <summary>
    /// Retired or toolbox-withdrawn type keys grouped under the node that serves them, used as toolbox search
    /// terms only. Load-time migration lives in <see cref="Services.Projects.NodeTypeAlias"/>.
    /// <para>已停用或已从工具箱撤下的类型键，按其现任继任节点分组，仅用作工具箱检索词。加载期迁移位于
    /// <see cref="Services.Projects.NodeTypeAlias"/>。</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> MergedTypeKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Image.Text.Edit"] = ["Image.Text.Read", "Image.Text.Stage"],
            ["Image.File.Attributes"] = ["Image.File.ReadAttributes", "Image.File.SetAttributes"],
            ["Image.Xattr.Edit"] = ["Image.Xattr.Read", "Image.Xattr.Set", "Image.Xattr.Delete"],
            ["Resource.Asset.Read"] = ["Resource.Asset.Open", "Resource.Asset.ReadText"],
            ["Binary.Search"] = ["Binary.Pattern.Find", "Binary.Pattern.Replace"],
            ["Workspace.File"] = ["Workspace.File.Find", "Workspace.File.Exists", "Workspace.File.Import",
                "Workspace.File.Delete", "Workspace.Text.Stage"],
            ["UI.Field.Create"] = ["UI.Field.Text", "UI.Field.Number", "UI.Field.Boolean", "UI.Field.Choice",
                "UI.Field.MultiChoice", "UI.Field.File", "UI.Field.Directory", "UI.Field.SavePath"],
            ["UI.Layout.Create"] = ["UI.Layout.Row", "UI.Layout.Column", "UI.Layout.Group", "UI.Layout.Pages"],
            ["UI.Content.Create"] = ["UI.Content.Text", "UI.Content.Divider", "UI.Content.Image"],
            ["Config.Edit"] = ["Config.Text.Edit", "Config.Json.Edit", "Config.Ini.Edit", "Config.Xml.Edit",
                "Config.AndroidProperty.Edit", "Config.Fstab.Edit", "Config.FileContexts.Edit",
                "Config.FsConfig.Edit", "Config.InitRc.Edit", "Config.InitRc.Query", "Config.InitRc.EditCommand",
                "Config.InitRc.EditArgument", "Config.InitRc.EditHeader", "Config.InitRc.AddSection",
                "Config.InitRc.AddCommand", "Config.InitRc.FindReferences"]
        };

    /// <summary>Builds the toolbox descriptor for one composite-node package.<para>为单个组合节点软件包构建工具箱描述符。</para></summary>
    /// <param name="package">Composite package supplying the graph and manifest.<para>提供图与清单的组合软件包。</para></param>
    /// <param name="validateExecution">Hook that returns validation errors before execution.<para>在执行前返回校验错误的回调。</para></param>
    /// <returns>The descriptor naming the node type and its factory.<para>指明节点类型与其工厂的描述符。</para></returns>
    internal static NodeDescriptor CreateCompositePluginDescriptor(VisualPluginPackage package,
        Func<IReadOnlyList<string>>? validateExecution) =>
        CreatePluginDescriptor(package, "Composite",
            () => new CompositeNodeViewModel(package, validateExecution));

    /// <summary>Builds the toolbox descriptor for one script-node package.<para>为单个脚本节点软件包构建工具箱描述符。</para></summary>
    /// <param name="package">Script package supplying the Lua source and manifest.<para>提供 Lua 源码与清单的脚本软件包。</para></param>
    /// <param name="validateExecution">Hook that returns validation errors before execution.<para>在执行前返回校验错误的回调。</para></param>
    /// <returns>The descriptor naming the node type and its factory.<para>指明节点类型与其工厂的描述符。</para></returns>
    internal static NodeDescriptor CreateScriptPluginDescriptor(VisualPluginPackage package,
        Func<IReadOnlyList<string>>? validateExecution) =>
        CreatePluginDescriptor(package, "Script",
            () => new ScriptNodeViewModel(package, validateExecution));

    /// <summary>
    /// Builds a plugin descriptor with the same policy surface as a built-in node: a manifest declaring
    /// <see cref="NodeCapability.MutatesDevice"/> requires the run-time confirmation that built-in mutating nodes
    /// require, and the manifest name doubles as a stable search term so plugins are discoverable in the toolbox.
    /// <para>构建与内置节点具有相同策略面的插件描述符：清单声明 <see cref="NodeCapability.MutatesDevice"/> 时，
    /// 像内置变更节点一样要求运行期确认；清单名称同时作为稳定检索词，使插件在工具箱中可被检索。</para>
    /// </summary>
    /// <param name="package">Plugin package supplying the manifest.<para>提供清单的插件软件包。</para></param>
    /// <param name="category">Presentation category for the toolbox.<para>工具箱呈现分类。</para></param>
    /// <param name="factory">Factory building the node instance.<para>构建节点实例的工厂。</para></param>
    /// <returns>The plugin descriptor.<para>插件描述符。</para></returns>
    private static NodeDescriptor CreatePluginDescriptor(VisualPluginPackage package, string category,
        Func<FlowNodeViewModel> factory)
    {
        var capabilities = ParseCapabilities(package.Manifest.Capabilities);
        return new NodeDescriptor(package.NodeTypeKey,
            string.Empty,
            factory,
            Capabilities: capabilities,
            DisplayName: package.Manifest.Name)
        {
            CategoryPath = category,
            RequiresConfirmation = capabilities.HasFlag(NodeCapability.MutatesDevice),
            RequiredDeviceState = capabilities.HasFlag(NodeCapability.RequiresDevice)
                ? DeviceRequiredState.AnyDevice
                : DeviceRequiredState.None,
            Aliases = string.IsNullOrWhiteSpace(package.Manifest.Name) ? [] : [package.Manifest.Name]
        };
    }

    private static NodeCapability ParseCapabilities(IEnumerable<string> capabilities)
    {
        var result = NodeCapability.None;
        foreach (var capability in capabilities)
        {
            if (Enum.TryParse<NodeCapability>(capability, true, out var parsed)) result |= parsed;
        }
        return result;
    }

    private static NodeDescriptor D(string typeKey, string resourceKey, string category,
        Func<FlowNodeViewModel> factory, bool requiresConfirmation = false)
    {
        var capabilities = NodeCapability.None;
        if (typeKey.StartsWith("Flow.", StringComparison.Ordinal))
        {
            capabilities |= NodeCapability.ControlFlow | NodeCapability.PatchSafe;
        }
        if (typeKey.StartsWith("Device.", StringComparison.Ordinal))
        {
            capabilities |= NodeCapability.RequiresDevice;
        }
        if (typeKey.StartsWith("Network.", StringComparison.Ordinal))
        {
            capabilities |= NodeCapability.RequiresNetwork | NodeCapability.ProducesArtifact;
        }
        if (typeKey.StartsWith("Build.", StringComparison.Ordinal)
            || typeKey.StartsWith("Firmware.", StringComparison.Ordinal)
            || typeKey is "Image.Super.Pack" or "Image.Super.Unpack")
        {
            capabilities |= NodeCapability.ProducesArtifact;
        }
        if (requiresConfirmation)
        {
            capabilities |= NodeCapability.MutatesDevice;
        }
        if (typeKey is "Device.Target.Select" or "Device.Property.Read" or "Device.Table.Read" or "Device.Partition.Read")
        {
            capabilities |= NodeCapability.ReadsDevice;
        }
        if (typeKey is "File.Text.Publish" or "File.Artifact.Publish" or "File.Bytes.Write" or "Validation.Vbmeta.Sign")
        {
            capabilities |= NodeCapability.ProducesArtifact | NodeCapability.PublishesFile;
        }
        if (typeKey is "Resource.Project.Image" or "Resource.Asset.Read" or "Feedback.Progress.Report"
            or "Validation.Data.Hash" or "Data.Value.Compare" or "Data.Value.Convert")
        {
            capabilities |= NodeCapability.PatchSafe;
        }
        if (typeKey.StartsWith("UI.", StringComparison.Ordinal))
        {
            capabilities |= NodeCapability.PatchSafe;
            if (typeKey == "UI.Dialog"
                || typeKey == "UI.Notification.Show"
                || typeKey.StartsWith("UI.Progress", StringComparison.Ordinal))
            {
                capabilities |= NodeCapability.RequiresInteraction;
            }
        }
        return new(typeKey, resourceKey, () => Prepare(factory(), capabilities), capabilities)
        {
            CategoryPath = category,
            RequiresConfirmation = requiresConfirmation,
            RequiredDeviceState = DeviceRequiredStateFor(typeKey),
            DeclaredFamily = typeKey == "Config.Edit" ? "Config" : null,
            Aliases = MergedTypeKeys.TryGetValue(typeKey, out var merged) ? merged : []
        };
    }

    private static DeviceRequiredState DeviceRequiredStateFor(string typeKey) => typeKey switch
    {
        "Device.Target.Select" => DeviceRequiredState.AnyDevice,
        "Device.Property.Read" => DeviceRequiredState.Adb | DeviceRequiredState.Fastboot,
        "Device.Power.Reboot" => DeviceRequiredState.Adb | DeviceRequiredState.Fastboot,
        "Device.Partition.Flash" => DeviceRequiredState.Fastboot,
        "Device.Table.Read" => DeviceRequiredState.Fastboot,
        "Device.Partition.Read" => DeviceRequiredState.Fastboot,
        "Device.Shell.Command" => DeviceRequiredState.Adb,
        _ => DeviceRequiredState.None
    };

    private static NodeDescriptor P(string typeKey, string resourceKey, string category, Func<FlowNodeViewModel> factory)
    {
        var descriptor = D(typeKey, resourceKey, category, factory);
        var capabilities = descriptor.Capabilities | NodeCapability.PatchOperation | NodeCapability.PatchSafe;
        if (typeKey.StartsWith("Config.", StringComparison.Ordinal))
        {
            capabilities &= ~NodeCapability.PatchOperation;
        }
        if (typeKey is "File.Text.Publish" or "Workspace.File" or "Config.Edit"
            or "Workspace.Asset.Stage" or "Binary.Search"
            or "Image.Edit.Apply"
            or "Image.Xattr.Edit" or "Image.Text.Edit" or "Image.File.Attributes")
        {
            capabilities |= NodeCapability.StagedWrite;
        }
        return descriptor with { Capabilities = capabilities };
    }

    private static FlowNodeViewModel Prepare(FlowNodeViewModel node, NodeCapability capabilities)
    {
        if ((capabilities.HasFlag(NodeCapability.MutatesDevice)
            || capabilities.HasFlag(NodeCapability.ProducesArtifact)
            || capabilities.HasFlag(NodeCapability.PublishesFile)
            || node.TypeKey is "Device.Property.Read" or "Device.Table.Read" or "Feedback.Progress.Report")
            && node.Input.All(port => port.PortType != NodePortType.Flow))
        {
            node.Input.Add(new NodeConnectorViewModel
            {
                Title = "Flow",
                PortType = NodePortType.Flow,
                PortId = "flow",
                MaxConnections = 1
            });
            node.Output.Add(new NodeConnectorViewModel
            {
                Title = "Completed",
                PortType = NodePortType.Flow,
                PortId = "completed"
            });
        }
        foreach (var port in node.Input.Concat(node.Output))
        {
            if (string.IsNullOrWhiteSpace(port.PortId))
            {
                port.PortId = NodeConnectorViewModel.CreateStableId(port.Title);
            }
        }
        NodeParameterSchema.Apply(node);
        return node;
    }
}