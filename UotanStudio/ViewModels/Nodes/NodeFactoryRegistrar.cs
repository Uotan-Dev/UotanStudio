using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.</summary>
public static class NodeFactoryRegistrar
{
    private static readonly IReadOnlyList<NodeDescriptor> BuiltInDescriptors =
    [
        D("Workspace.Document.Resolve", "Node_Workspace_Document_Resolve", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.Resolve")),
        D("Workspace.Document.ReadText", "Node_Workspace_Document_ReadText", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.ReadText")),
        D("Workspace.Document.ReplaceText", "Node_Workspace_Document_ReplaceText", "Workspace", () => WorkspaceDocumentNodeViewModel.Create("Workspace.Document.ReplaceText")),
        P("Image.Text.Read", "Node_Image_Text_Read", "Configuration", () => PatchNodeViewModel.Create("Image.Text.Read")),
        P("Image.Text.Stage", "Node_Image_Text_Stage", "Configuration", () => PatchNodeViewModel.Create("Image.Text.Stage")),
        P("Image.Edit.CollectText", "Node_Image_Edit_CollectText", "Configuration", () => PatchNodeViewModel.Create("Image.Edit.CollectText")),
        P("Image.Edit.Apply", "Node_Image_Edit_Apply", "Configuration", () => PatchNodeViewModel.Create("Image.Edit.Apply")),
        P("Image.File.ReadAttributes", "Node_Image_File_ReadAttributes", "Configuration", () => PatchNodeViewModel.Create("Image.File.ReadAttributes")),
        P("Image.Xattr.Read", "Node_Image_Xattr_Read", "Configuration", () => PatchNodeViewModel.Create("Image.Xattr.Read")),
        P("Image.Xattr.Set", "Node_Image_Xattr_Set", "Configuration", () => PatchNodeViewModel.Create("Image.Xattr.Set")),
        P("Image.Xattr.Delete", "Node_Image_Xattr_Delete", "Configuration", () => PatchNodeViewModel.Create("Image.Xattr.Delete")),
        P("Image.File.SetAttributes", "Node_Image_File_SetAttributes", "Configuration", () => PatchNodeViewModel.Create("Image.File.SetAttributes")),
        P("Config.FileContexts.Edit", "Node_Config_FileContexts_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.FileContexts.Edit")),
        P("Config.FsConfig.Edit", "Node_Config_FsConfig_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.FsConfig.Edit")),
        P("Config.InitRc.FindReferences", "Node_Config_InitRc_FindReferences", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.FindReferences")),
        P("Config.InitRc.EditArgument", "Node_Config_InitRc_EditArgument", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.EditArgument")),
        P("Config.InitRc.EditHeader", "Node_Config_InitRc_EditHeader", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.EditHeader")),
        P("Config.InitRc.AddSection", "Node_Config_InitRc_AddSection", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.AddSection")),
        P("Config.InitRc.AddCommand", "Node_Config_InitRc_AddCommand", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.AddCommand")),
        P("Config.InitRc.Query", "Node_Config_InitRc_Query", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.Query")),
        P("Config.InitRc.EditCommand", "Node_Config_InitRc_EditCommand", "Configuration", () => PatchNodeViewModel.Create("Config.InitRc.EditCommand")),
        P("Config.Fstab.Edit", "Node_Config_Fstab_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Fstab.Edit")),
        P("Binary.Pattern.Replace", "Node_Binary_Pattern_Replace", "Data", () => PatchNodeViewModel.Create("Binary.Pattern.Replace")),
        P("Binary.Pattern.Find", "Node_Binary_Pattern_Find", "Data", () => PatchNodeViewModel.Create("Binary.Pattern.Find")),
        P("Workspace.Partition.Resolve", "Node_Workspace_Partition_Resolve", "Firmware", () => PatchNodeViewModel.Create("Workspace.Partition.Resolve")),
        P("Flow.Applicability.Gate", "Node_Flow_Applicability_Gate", "Flow", () => PatchNodeViewModel.Create("Flow.Applicability.Gate")),
        P("Validation.Applicability.Check", "Node_Validation_Applicability_Check", "Validation", () => PatchNodeViewModel.Create("Validation.Applicability.Check")),
        P("Resource.Asset.Open", "Node_Resource_Asset_Open", "Inputs", () => PatchNodeViewModel.Create("Resource.Asset.Open")),
        P("Resource.Asset.ReadText", "Node_Resource_Asset_ReadText", "Inputs", () => PatchNodeViewModel.Create("Resource.Asset.ReadText")),
        P("Workspace.Asset.Stage", "Node_Workspace_Asset_Stage", "Workspace", () => PatchNodeViewModel.Create("Workspace.Asset.Stage")),
        P("Validation.Format.Detect", "Node_Validation_Format_Detect", "Validation", () => PatchNodeViewModel.Create("Validation.Format.Detect")),
        P("Workspace.File.Find", "Node_Workspace_File_Find", "Workspace", () => PatchNodeViewModel.Create("Workspace.File.Find")),
        P("Config.Json.Edit", "Node_Config_Json_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Json.Edit")),
        P("Config.Ini.Edit", "Node_Config_Ini_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Ini.Edit")),
        P("Workspace.Directory.Open", "Node_Workspace_Directory_Open", "Workspace", () => PatchNodeViewModel.Create("Workspace.Directory.Open")),
        P("Workspace.Text.Stage", "Node_Workspace_Text_Stage", "Workspace", () => PatchNodeViewModel.Create("Workspace.Text.Stage")),
        P("Workspace.File.Import", "Node_Workspace_File_Import", "Workspace", () => PatchNodeViewModel.Create("Workspace.File.Import")),
        P("Workspace.File.Delete", "Node_Workspace_File_Delete", "Workspace", () => PatchNodeViewModel.Create("Workspace.File.Delete")),
        P("Workspace.File.Exists", "Node_Workspace_File_Exists", "Workspace", () => PatchNodeViewModel.Create("Workspace.File.Exists")),
        P("Validation.File.Hash", "Node_Validation_File_Hash", "Validation", () => PatchNodeViewModel.Create("Validation.File.Hash")),
        P("Validation.Hash.Verify", "Node_Validation_Hash_Verify", "Validation", () => PatchNodeViewModel.Create("Validation.Hash.Verify")),
        P("Validation.Count.Assert", "Node_Validation_Count_Assert", "Validation", () => PatchNodeViewModel.Create("Validation.Count.Assert")),
        P("Validation.Changes.Verify", "Node_Validation_Changes_Verify", "Validation", () => PatchNodeViewModel.Create("Validation.Changes.Verify")),
        P("File.Text.Read", "Node_File_Text_Read", "Data", () => PatchNodeViewModel.Create("File.Text.Read")),
        P("Validation.Condition.Evaluate", "Node_Validation_Condition_Evaluate", "Validation", () => PatchNodeViewModel.Create("Validation.Condition.Evaluate")),
        P("Validation.Boolean.Assert", "Node_Validation_Boolean_Assert", "Validation", () => PatchNodeViewModel.Create("Validation.Boolean.Assert")),
        P("Config.Text.Edit", "Node_Config_Text_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Text.Edit")),
        P("Config.AndroidProperty.Edit", "Node_Config_AndroidProperty_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.AndroidProperty.Edit")),
        P("Config.Xml.Edit", "Node_Config_Xml_Edit", "Configuration", () => PatchNodeViewModel.Create("Config.Xml.Edit")),
        P("File.Text.Publish", "Node_File_Text_Publish", "Data", () => PatchNodeViewModel.Create("File.Text.Publish")),
        D("Firmware.Container.Unpack", "Node_Firmware_Container_Unpack", "Firmware", () => FirmwareNodeViewModel.CreateUnpackNode()),
        D("Firmware.Container.Pack", "Node_Firmware_Container_Pack", "Firmware", () => FirmwareNodeViewModel.CreatePackNode()),
        D("Validation.Signature.Verify", "Node_Validation_Signature_Verify", "Validation", () => FirmwareNodeViewModel.CreateVerifyNode()),
        D("Boot.CommandLine.Edit", "Node_Boot_CommandLine_Edit", "Composite", () => FirmwareNodeViewModel.CreateCmdlineEditNode()),
        D("Build.Apk.Sign", "Node_Build_Apk_Sign", "Build", () => new ApkSignNodeViewModel()),
        D("Firmware.Container.Extract", "Node_Firmware_Container_Extract", "Firmware", () => FirmwareNodeViewModel.CreateExtractNode()),
        D("Build.Ota.CreateDelta", "Node_Build_Ota_CreateDelta", "Build", () => ExtendedFirmwareNodeViewModel.CreateOtaDeltaNode()),
        D("Build.Signature.Merge", "Node_Build_Signature_Merge", "Build",
            () => ExtendedFirmwareNodeViewModel.CreateMergeSignatureNode()) with { IsToolboxVisible = false },
        D("Build.Slots.Merge", "Node_Build_Slots_Merge", "Build",
            () => ExtendedFirmwareNodeViewModel.CreateMergeDualSlotNode()) with { IsToolboxVisible = false },
        D("Firmware.Cpio.Unpack", "Node_Firmware_Cpio_Unpack", "Firmware", () => CpioNodeViewModel.CreateCpioUnpackNode()),
        D("Firmware.Cpio.Pack", "Node_Firmware_Cpio_Pack", "Firmware", () => CpioNodeViewModel.CreateCpioPackNode()),
        D("Image.Super.Unpack", "Node_Image_Super_Unpack", "Firmware", () => SuperNodeViewModel.CreateSuperUnpackNode()),
        D("Image.Super.Pack", "Node_Image_Super_Pack", "Firmware", () => SuperNodeViewModel.CreateSuperPackNode()),
        D("Image.Sparse.Convert", "Node_Image_Sparse_Convert", "Firmware", () => SparseNodeViewModel.CreateSparseNode()),
        D("Build.Dat.Convert", "Node_Build_Dat_Convert", "Build", () => DatNodeViewModel.CreateDatNode()),
        D("Device.Target.Select", "Node_Device_Target_Select", "Device", () => SelectDeviceNodeViewModel.CreateSelectDeviceNode()),
        D("Device.Property.Read", "Node_Device_Property_Read", "Device", () => DevicePropertyNodeViewModel.CreateDevicePropertyNode()),
        D("Device.Partition.Flash", "Node_Device_Partition_Flash", "Device", () => FlashNodeViewModel.CreateFlashNode(), true),
        D("Device.Partition.Erase", "Node_Device_Partition_Erase", "Device", () => FlashNodeViewModel.CreateEraseNode(), true),
        D("Device.Power.Reboot", "Node_Device_Power_Reboot", "Device", () => FlashNodeViewModel.CreateRebootNode(), true),
        D("Device.Storage.EraseAll", "Node_Device_Storage_EraseAll", "Device", () => ExtendedFlashNodeViewModel.CreateEraseAllNode(), true),
        D("Device.Ota.Sideload", "Node_Device_Ota_Sideload", "Device", () => ExtendedFlashNodeViewModel.CreateOtaSideloadNode(), true),
        D("File.Binary.Read", "Node_File_Binary_Read", "Data", () => DataNodeViewModel.CreateFileReadNode()),
        D("File.Artifact.Open", "Node_File_Artifact_Open", "Data", () => new ArtifactNodeViewModel()),
        D("Data.Value.Constant", "Node_Data_Value_Constant", "Data", () => ConstantNodeViewModel.CreateConstantNode()),
        D("Resource.Project.Image", "Node_Resource_Project_Image", "Inputs", () => ProjectImageNodeViewModel.CreateProjectImageNode()),
        D("Data.Cipher.Decrypt", "Node_Data_Cipher_Decrypt", "Data", () => DecryptNodeViewModel.CreateDecryptNode()),
        D("Data.Compression.Decompress", "Node_Data_Compression_Decompress", "Data", () => DecompressNodeViewModel.CreateDecompressNode()),
        D("Data.Value.Convert", "Node_Data_Value_Convert", "Data", () => DataNodeViewModel.CreateConvertNode()),
        D("Validation.Data.Hash", "Node_Validation_Data_Hash", "Data", () => DataNodeViewModel.CreateChecksumNode()),
        D("Data.Value.Compare", "Node_Data_Value_Compare", "Data", () => DataNodeViewModel.CreateCompareNode()),
        D("Data.Bytes.Concat", "Node_Data_Bytes_Concat", "Data", () => ExtendedDataNodeViewModel.CreateConcatNode()),
        D("Data.Bytes.Slice", "Node_Data_Bytes_Slice", "Data", () => ExtendedDataNodeViewModel.CreateSliceNode()),
        D("Data.Cipher.Encrypt", "Node_Data_Cipher_Encrypt", "Data", () => ExtendedDataNodeViewModel.CreateEncryptNode()),
        D("Data.Compression.Compress", "Node_Data_Compression_Compress", "Data", () => ExtendedDataNodeViewModel.CreateCompressNode()),
        D("Data.Bytes.Swap", "Node_Data_Bytes_Swap", "Data", () => ExtendedDataNodeViewModel.CreateByteSwapNode()),
        D("Network.File.Download", "Node_Network_File_Download", "Inputs", () => NetworkNodeViewModel.CreateDownloadNode()),
        D("Flow.Execution.Start", "Node_Flow_Execution_Start", "Flow", () => FlowControlNodeViewModel.CreateStartNode()),
        D("Flow.Execution.End", "Node_Flow_Execution_End", "Flow", () => FlowControlNodeViewModel.CreateEndNode()),
        D("Flow.Condition.Branch", "Node_Flow_Condition_Branch", "Flow", () => FlowControlNodeViewModel.CreateBranchNode()),
        D("Flow.Collection.Loop", "Node_Flow_Collection_Loop", "Flow", () => FlowControlNodeViewModel.CreateLoopNode()),
        D("Flow.Execution.Delay", "Node_Flow_Execution_Delay", "Flow", () => FlowControlNodeViewModel.CreateDelayNode()),
        D("Flow.Execution.Parallel", "Node_Flow_Execution_Parallel", "Flow", () => FlowControlNodeViewModel.CreateParallelNode()),
        D("Flow.Execution.Terminate", "Node_Flow_Execution_Terminate", "Flow", () => FlowControlNodeViewModel.CreateTerminateNode()),
        D("Feedback.Log.Write", "Node_Feedback_Log_Write", "Interaction", () => OutputNodeViewModel.CreateLogNode()),
        D("Feedback.Progress.Report", "Node_Feedback_Progress_Report", "Interaction", () => OutputNodeViewModel.CreateProgressNode()),
        D("File.Artifact.Publish", "Node_File_Artifact_Publish", "Data", () => OutputNodeViewModel.CreateSaveNode()),
        .. InteractionNodeViewModel.TypeKeys.Select(typeKey => D(typeKey, "Node_" + typeKey.Replace('.', '_'), "Interaction",
            () => InteractionNodeViewModel.Create(typeKey)))
    ];

    private static readonly Lock RegistryLock = new();

    private static readonly Dictionary<string, NodeDescriptor> VisualPluginDescriptors =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, VisualPluginPackage> VisualPluginPackages = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetVisualPluginPackage(string typeKey, out VisualPluginPackage package)
    {
        lock (RegistryLock)
        {
            return VisualPluginPackages.TryGetValue(typeKey, out package!);
        }
    }

    /// <summary>Raised whenever the set of dynamically registered toolbox nodes changes.</summary>
    public static event EventHandler? DescriptorsChanged;

    /// <summary>Built-in and enabled visual-plugin descriptors.</summary>
    public static IReadOnlyList<NodeDescriptor> Descriptors
    {
        get
        {
            lock (RegistryLock)
            {
                return [.. BuiltInDescriptors, .. VisualPluginDescriptors.Values.OrderBy(descriptor => descriptor.DisplayName)];
            }
        }
    }

    /// <summary>Stable type keys provided by the host itself.</summary>
    public static IReadOnlySet<string> BuiltInTypeKeys { get; } =
        BuiltInDescriptors.Select(descriptor => descriptor.TypeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void RegisterAll()
    {
        lock (RegistryLock)
        {
            foreach (var descriptor in Descriptors)
            {
                NodeTypeRegistry.Register(descriptor.TypeKey, descriptor.Factory);
            }
        }
    }

    /// <summary>Registers or replaces one host-rendered composite-node package.</summary>
    public static void RegisterVisualPlugin(VisualPluginPackage package, Func<IReadOnlyList<string>>? validateExecution = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Kind != VisualPluginKind.CompositeNode)
            throw new ArgumentException("Only composite-node packages can be registered in the toolbox.", nameof(package));

        var descriptor = new NodeDescriptor(
            package.NodeTypeKey,
            string.Empty,
            () => new CompositeNodeViewModel(package, validateExecution),
            Capabilities: ParseCapabilities(package.Manifest.Capabilities),
            DisplayName: package.Manifest.Name)
        { CategoryPath = "Composite" };
        lock (RegistryLock)
        {
            VisualPluginDescriptors[package.NodeTypeKey] = descriptor;
            VisualPluginPackages[package.NodeTypeKey] = package;
            NodeTypeRegistry.Register(package.NodeTypeKey, descriptor.Factory);
        }
        DescriptorsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Unregisters one visual-plugin node type.</summary>
    public static void UnregisterVisualPlugin(string typeKey)
    {
        lock (RegistryLock)
        {
            if (!VisualPluginDescriptors.Remove(typeKey))
            {
                return;
            }
            VisualPluginPackages.Remove(typeKey);
            NodeTypeRegistry.Unregister(typeKey);
        }
        DescriptorsChanged?.Invoke(null, EventArgs.Empty);
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
        Func<FlowNodeViewModel> factory, bool destructive = false)
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
            || typeKey is "Image.Super.Pack" or "Image.Super.Unpack" or "Image.Sparse.Convert")
        {
            capabilities |= NodeCapability.ProducesArtifact;
        }
        if (destructive)
        {
            capabilities |= NodeCapability.MutatesDevice;
        }
        if (typeKey is "Device.Target.Select" or "Device.Property.Read")
        {
            capabilities |= NodeCapability.ReadsDevice;
        }
        if (typeKey is "File.Text.Publish" or "File.Artifact.Publish")
        {
            capabilities |= NodeCapability.ProducesArtifact | NodeCapability.PublishesFile;
        }
        if (typeKey is "Data.Value.Constant" or "Resource.Project.Image" or "Feedback.Log.Write" or "Feedback.Progress.Report")
        {
            capabilities |= NodeCapability.PatchSafe;
        }
        if (typeKey.StartsWith("UI.", StringComparison.Ordinal))
        {
            capabilities |= NodeCapability.PatchSafe;
            if (typeKey.StartsWith("UI.Dialog.", StringComparison.Ordinal)
                || typeKey.StartsWith("UI.Notification.", StringComparison.Ordinal)
                || typeKey.StartsWith("UI.Progress.", StringComparison.Ordinal))
            {
                capabilities |= NodeCapability.RequiresInteraction;
            }
        }
        return new(typeKey, resourceKey, () => Prepare(factory(), capabilities), destructive, capabilities)
        {
            CategoryPath = category
        };
    }

    private static NodeDescriptor P(string typeKey, string resourceKey, string category, Func<FlowNodeViewModel> factory)
    {
        var descriptor = D(typeKey, resourceKey, category, factory);
        var capabilities = descriptor.Capabilities | NodeCapability.PatchOperation | NodeCapability.PatchSafe;
        if (typeKey.StartsWith("Config.", StringComparison.Ordinal))
        {
            capabilities &= ~NodeCapability.PatchOperation;
        }
        if (typeKey is "File.Text.Publish" or "Workspace.Text.Stage" or "Workspace.File.Import"
            or "Workspace.File.Delete" or "Workspace.Asset.Stage" or "Binary.Pattern.Replace" or "Image.Text.Stage"
            or "Image.Edit.Apply" or "Image.File.SetAttributes" or "Image.Xattr.Set" or "Image.Xattr.Delete")
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
            || node.TypeKey is "Device.Property.Read" or "Feedback.Log.Write" or "Feedback.Progress.Report")
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

public sealed record NodeDescriptor(
    string TypeKey,
    string NameResourceKey,
    Func<FlowNodeViewModel> Factory,
    bool IsDestructive = false,
    NodeCapability Capabilities = NodeCapability.None,
    string? DisplayName = null)
{
    /// <summary>Whether users may create new instances from the toolbox; hidden types remain loadable.</summary>
    public bool IsToolboxVisible { get; init; } = true;

    /// <summary>Stable presentation category declared per registration; independent of execution capabilities.</summary>
    public required string CategoryPath { get; init; }

    /// <summary>Object family used for the second toolbox level.</summary>
    public string Family => TypeKey.Split('.') is [_, var family, ..] ? family : TypeKey;

    /// <summary>Stable technical search terms augment localized display labels.</summary>
    public IReadOnlyList<string> SearchKeywords => TypeKey switch
    {
        "Binary.Pattern.Replace" => [TypeKey, CategoryPath, Family, "hexpatch", "hex", "binary"],
        "Device.Partition.Flash" => [TypeKey, CategoryPath, Family, "boot", "bootloader", "super", "fastboot"],
        "Config.AndroidProperty.Edit" => [TypeKey, CategoryPath, Family, "build.prop", "property"],
        _ => [TypeKey, CategoryPath, Family]
    };
}

[Flags]
public enum NodeCapability
{
    None = 0,
    ControlFlow = 1 << 0,
    RequiresDevice = 1 << 1,
    ReadsDevice = 1 << 2,
    MutatesDevice = 1 << 3,
    ProducesArtifact = 1 << 4,
    RequiresNetwork = 1 << 5,
    PatchOperation = 1 << 6,
    PatchSafe = 1 << 7,
    StagedWrite = 1 << 8,
    PublishesFile = 1 << 9,
    RequiresInteraction = 1 << 10
}