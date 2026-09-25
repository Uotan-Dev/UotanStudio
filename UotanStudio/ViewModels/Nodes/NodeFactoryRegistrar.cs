using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.</summary>
public static class NodeFactoryRegistrar
{
    private static readonly IReadOnlyList<NodeDescriptor> BuiltInDescriptors =
    [
        P("Image.Text.Read", "Node_Image_Text_Read", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Text.Read")),
        P("Image.Text.Stage", "Node_Image_Text_Stage", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Text.Stage")),
        P("Image.Edit.CollectText", "Node_Image_Edit_CollectText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Edit.CollectText")),
        P("Image.Edit.Apply", "Node_Image_Edit_Apply", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Edit.Apply")),
        P("Image.File.ReadAttributes", "Node_Image_File_ReadAttributes", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.File.ReadAttributes")),
        P("Image.Xattr.Read", "Node_Image_Xattr_Read", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Xattr.Read")),
        P("Image.Xattr.Set", "Node_Image_Xattr_Set", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Xattr.Set")),
        P("Image.Xattr.Delete", "Node_Image_Xattr_Delete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.Xattr.Delete")),
        P("Image.File.SetAttributes", "Node_Image_File_SetAttributes", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Image.File.SetAttributes")),
        P("Config.FileContexts.Add", "Node_Config_FileContexts_Add", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FileContexts.Add")),
        P("Config.FileContexts.Delete", "Node_Config_FileContexts_Delete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FileContexts.Delete")),
        P("Config.FileContexts.Query", "Node_Config_FileContexts_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FileContexts.Query")),
        P("Config.FileContexts.Edit", "Node_Config_FileContexts_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FileContexts.Edit")),
        P("Config.FsConfig.Add", "Node_Config_FsConfig_Add", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FsConfig.Add")),
        P("Config.FsConfig.Delete", "Node_Config_FsConfig_Delete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FsConfig.Delete")),
        P("Config.FsConfig.Query", "Node_Config_FsConfig_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FsConfig.Query")),
        P("Config.FsConfig.Edit", "Node_Config_FsConfig_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.FsConfig.Edit")),
        P("Config.InitRc.FindReferences", "Node_Config_InitRc_FindReferences", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.FindReferences")),
        P("Config.InitRc.EditArgument", "Node_Config_InitRc_EditArgument", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.EditArgument")),
        P("Config.InitRc.EditHeader", "Node_Config_InitRc_EditHeader", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.EditHeader")),
        P("Config.InitRc.AddSection", "Node_Config_InitRc_AddSection", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.AddSection")),
        P("Config.InitRc.AddCommand", "Node_Config_InitRc_AddCommand", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.AddCommand")),
        P("Config.InitRc.Query", "Node_Config_InitRc_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.Query")),
        P("Config.InitRc.EditCommand", "Node_Config_InitRc_EditCommand", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.InitRc.EditCommand")),
        P("Config.Fstab.Add", "Node_Config_Fstab_Add", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Fstab.Add")),
        P("Config.Fstab.Query", "Node_Config_Fstab_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Fstab.Query")),
        P("Config.Fstab.Edit", "Node_Config_Fstab_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Fstab.Edit")),
        P("Binary.Pattern.Replace", "Node_Binary_Pattern_Replace", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Binary.Pattern.Replace")),
        P("Binary.Pattern.Find", "Node_Binary_Pattern_Find", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Binary.Pattern.Find")),
        P("Workspace.Partition.Resolve", "Node_Workspace_Partition_Resolve", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.Partition.Resolve")),
        P("Flow.Applicability.Gate", "Node_Flow_Applicability_Gate", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Flow.Applicability.Gate")),
        P("Validation.Applicability.Check", "Node_Validation_Applicability_Check", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Applicability.Check")),
        P("Resource.Asset.Open", "Node_Resource_Asset_Open", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Resource.Asset.Open")),
        P("Resource.Asset.ReadText", "Node_Resource_Asset_ReadText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Resource.Asset.ReadText")),
        P("Workspace.Asset.Stage", "Node_Workspace_Asset_Stage", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.Asset.Stage")),
        P("Validation.Format.Detect", "Node_Validation_Format_Detect", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Format.Detect")),
        P("Validation.Format.Check", "Node_Validation_Format_Check", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Format.Check")),
        P("Workspace.File.Find", "Node_Workspace_File_Find", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.File.Find")),
        P("Config.Json.Query", "Node_Config_Json_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Json.Query")),
        P("Config.Json.Edit", "Node_Config_Json_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Json.Edit")),
        P("Config.Ini.Query", "Node_Config_Ini_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Ini.Query")),
        P("Config.Ini.Edit", "Node_Config_Ini_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Ini.Edit")),
        P("Workspace.Directory.Open", "Node_Workspace_Directory_Open", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.Directory.Open")),
        P("Workspace.Text.Stage", "Node_Workspace_Text_Stage", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.Text.Stage")),
        P("Workspace.File.Import", "Node_Workspace_File_Import", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.File.Import")),
        P("Workspace.File.Delete", "Node_Workspace_File_Delete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.File.Delete")),
        P("Workspace.File.Exists", "Node_Workspace_File_Exists", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Workspace.File.Exists")),
        P("Validation.File.Hash", "Node_Validation_File_Hash", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.File.Hash")),
        P("Validation.Hash.Verify", "Node_Validation_Hash_Verify", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Hash.Verify")),
        P("Validation.Count.Assert", "Node_Validation_Count_Assert", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Count.Assert")),
        P("Validation.Changes.Verify", "Node_Validation_Changes_Verify", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Changes.Verify")),
        P("File.Text.Read", "Node_File_Text_Read", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("File.Text.Read")),
        P("Validation.Condition.Evaluate", "Node_Validation_Condition_Evaluate", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Condition.Evaluate")),
        P("Validation.Boolean.Assert", "Node_Validation_Boolean_Assert", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Validation.Boolean.Assert")),
        P("Config.Text.Edit", "Node_Config_Text_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Text.Edit")),
        P("Config.AndroidProperty.Query", "Node_Config_AndroidProperty_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.AndroidProperty.Query")),
        P("Config.AndroidProperty.Edit", "Node_Config_AndroidProperty_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.AndroidProperty.Edit")),
        P("Config.Xml.Query", "Node_Config_Xml_Query", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Xml.Query")),
        P("Config.Xml.Edit", "Node_Config_Xml_Edit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Config.Xml.Edit")),
        P("File.Text.Publish", "Node_File_Text_Publish", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("File.Text.Publish")),
        D("Firmware.Container.Unpack", "Node_Firmware_Container_Unpack", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreateUnpackNode()),
        D("Firmware.Container.Pack", "Node_Firmware_Container_Pack", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreatePackNode()),
        D("Validation.Signature.Verify", "Node_Validation_Signature_Verify", NodeToolboxGroup.Android, () => FirmwareNodeViewModel.CreateVerifyNode()),
        D("Build.Apk.Sign", "Node_Build_Apk_Sign", NodeToolboxGroup.Android, () => new ApkSignNodeViewModel()),
        D("Firmware.Container.Extract", "Node_Firmware_Container_Extract", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreateExtractNode()),
        D("Build.Ota.CreateDelta", "Node_Build_Ota_CreateDelta", NodeToolboxGroup.Delivery, () => ExtendedFirmwareNodeViewModel.CreateOtaDeltaNode()),
        D("Build.Signature.Merge", "Node_Build_Signature_Merge", NodeToolboxGroup.Android, () => ExtendedFirmwareNodeViewModel.CreateMergeSignatureNode()),
        D("Build.Slots.Merge", "Node_Build_Slots_Merge", NodeToolboxGroup.Delivery, () => ExtendedFirmwareNodeViewModel.CreateMergeDualSlotNode()),
        D("Firmware.Cpio.Unpack", "Node_Firmware_Cpio_Unpack", NodeToolboxGroup.FirmwareContainer, () => CpioNodeViewModel.CreateCpioUnpackNode()),
        D("Firmware.Cpio.Pack", "Node_Firmware_Cpio_Pack", NodeToolboxGroup.FirmwareContainer, () => CpioNodeViewModel.CreateCpioPackNode()),
        D("Image.Super.Unpack", "Node_Image_Super_Unpack", NodeToolboxGroup.FirmwareImage, () => SuperNodeViewModel.CreateSuperUnpackNode()),
        D("Image.Super.Pack", "Node_Image_Super_Pack", NodeToolboxGroup.FirmwareImage, () => SuperNodeViewModel.CreateSuperPackNode()),
        D("Image.Sparse.Convert", "Node_Image_Sparse_Convert", NodeToolboxGroup.FirmwareImage, () => SparseNodeViewModel.CreateSparseNode()),
        D("Build.Dat.Convert", "Node_Build_Dat_Convert", NodeToolboxGroup.Delivery, () => DatNodeViewModel.CreateDatNode()),
        D("Device.Target.Select", "Node_Device_Target_Select", NodeToolboxGroup.Flash, () => SelectDeviceNodeViewModel.CreateSelectDeviceNode()),
        D("Device.Property.Read", "Node_Device_Property_Read", NodeToolboxGroup.Flash, () => DevicePropertyNodeViewModel.CreateDevicePropertyNode()),
        D("Device.Partition.Flash", "Node_Device_Partition_Flash", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateFlashNode(), true),
        D("Device.Partition.Erase", "Node_Device_Partition_Erase", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateEraseNode(), true),
        D("Device.Power.Reboot", "Node_Device_Power_Reboot", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateRebootNode(), true),
        D("Device.Storage.EraseAll", "Node_Device_Storage_EraseAll", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateEraseAllNode(), true),
        D("Device.Ota.Sideload", "Node_Device_Ota_Sideload", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateOtaSideloadNode(), true),
        D("File.Binary.Read", "Node_File_Binary_Read", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateFileReadNode()),
        D("File.Artifact.Open", "Node_File_Artifact_Open", NodeToolboxGroup.Data, () => new ArtifactNodeViewModel()),
        D("Data.Value.Constant", "Node_Data_Value_Constant", NodeToolboxGroup.Data, () => ConstantNodeViewModel.CreateConstantNode()),
        D("Resource.Project.Image", "Node_Resource_Project_Image", NodeToolboxGroup.Data, () => ProjectImageNodeViewModel.CreateProjectImageNode()),
        D("Data.Cipher.Decrypt", "Node_Data_Cipher_Decrypt", NodeToolboxGroup.Data, () => DecryptNodeViewModel.CreateDecryptNode()),
        D("Data.Compression.Decompress", "Node_Data_Compression_Decompress", NodeToolboxGroup.Data, () => DecompressNodeViewModel.CreateDecompressNode()),
        D("Data.Value.Convert", "Node_Data_Value_Convert", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateConvertNode()),
        D("Validation.Data.Hash", "Node_Validation_Data_Hash", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateChecksumNode()),
        D("Data.Value.Compare", "Node_Data_Value_Compare", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateCompareNode()),
        D("Data.Bytes.Concat", "Node_Data_Bytes_Concat", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateConcatNode()),
        D("Data.Bytes.Slice", "Node_Data_Bytes_Slice", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateSliceNode()),
        D("Data.Cipher.Encrypt", "Node_Data_Cipher_Encrypt", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateEncryptNode()),
        D("Data.Compression.Compress", "Node_Data_Compression_Compress", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateCompressNode()),
        D("Data.Bytes.Swap", "Node_Data_Bytes_Swap", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateByteSwapNode()),
        D("Network.File.Download", "Node_Network_File_Download", NodeToolboxGroup.Network, () => NetworkNodeViewModel.CreateDownloadNode()),
        D("Network.GitHub.Download", "Node_Network_GitHub_Download", NodeToolboxGroup.Network, () => NetworkNodeViewModel.CreateGitHubDownloadNode()),
        D("Flow.Execution.Start", "Node_Flow_Execution_Start", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateStartNode()),
        D("Flow.Execution.End", "Node_Flow_Execution_End", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateEndNode()),
        D("Flow.Condition.Branch", "Node_Flow_Condition_Branch", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateBranchNode()),
        D("Flow.Collection.Loop", "Node_Flow_Collection_Loop", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateLoopNode()),
        D("Flow.Execution.Delay", "Node_Flow_Execution_Delay", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateDelayNode()),
        D("Flow.Execution.Parallel", "Node_Flow_Execution_Parallel", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateParallelNode()),
        D("Flow.Execution.Terminate", "Node_Flow_Execution_Terminate", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateTerminateNode()),
        D("Feedback.Log.Write", "Node_Feedback_Log_Write", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateLogNode()),
        D("Feedback.Progress.Report", "Node_Feedback_Progress_Report", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateProgressNode()),
        D("File.Artifact.Publish", "Node_File_Artifact_Publish", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateSaveNode()),
        .. InteractionNodeViewModel.TypeKeys.Select(typeKey => D(typeKey, "Node_" + typeKey.Replace('.', '_'),
            NodeToolboxGroup.Output, () => InteractionNodeViewModel.Create(typeKey)))
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
            NodeToolboxGroup.VisualPlugins,
            () => new CompositeNodeViewModel(package, validateExecution),
            Capabilities: ParseCapabilities(package.Manifest.Capabilities),
            DisplayName: package.Manifest.Name);
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

    private static NodeDescriptor D(string typeKey, string resourceKey, NodeToolboxGroup group,
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
        return new(typeKey, resourceKey, group, () => Prepare(factory()), destructive, capabilities);
    }

    private static NodeDescriptor P(string typeKey, string resourceKey, NodeToolboxGroup group, Func<FlowNodeViewModel> factory)
    {
        var descriptor = D(typeKey, resourceKey, group, factory);
        var capabilities = descriptor.Capabilities | NodeCapability.PatchOperation | NodeCapability.PatchSafe;
        if (typeKey is "File.Text.Publish" or "Workspace.Text.Stage" or "Workspace.File.Import"
            or "Workspace.File.Delete" or "Workspace.Asset.Stage" or "Binary.Pattern.Replace" or "Image.Text.Stage"
            or "Image.Edit.Apply" or "Image.File.SetAttributes" or "Image.Xattr.Set" or "Image.Xattr.Delete")
        {
            capabilities |= NodeCapability.StagedWrite;
        }
        return descriptor with { Capabilities = capabilities };
    }

    private static FlowNodeViewModel Prepare(FlowNodeViewModel node)
    {
        foreach (var port in node.Input.Concat(node.Output))
        {
            if (string.IsNullOrWhiteSpace(port.PortId))
            {
                port.PortId = NodeConnectorViewModel.CreateStableId(port.Title);
            }
        }
        foreach (var input in node.Input)
        {
            if (input.InitialValue is null && input.Title is "Device" or "Image" or "Path" or "Data" or "Workspace"
                or "Asset" or "Text" or "Source" or "Input" or "File" or "Partition")
            {
                input.IsRequired = true;
            }
        }
        NodeParameterSchema.Apply(node);
        return node;
    }
}

public enum NodeToolboxGroup
{
    FirmwareContainer,
    FirmwareImage,
    Android,
    Delivery,
    Flash,
    Data,
    Network,
    Flow,
    Output,
    Patch,
    VisualPlugins
}

public sealed record NodeDescriptor(
    string TypeKey,
    string NameResourceKey,
    NodeToolboxGroup Group,
    Func<FlowNodeViewModel> Factory,
    bool IsDestructive = false,
    NodeCapability Capabilities = NodeCapability.None,
    string? DisplayName = null)
{
    /// <summary>Stable presentation category independent of execution capabilities.</summary>
    public string CategoryPath => TypeKey.Split('.')[0] switch
    {
        "Resource" or "Network" or "Workspace" => "Inputs",
        "File" or "Data" or "Binary" => "Data",
        "Validation" => "Validation",
        "Config" => "Configuration",
        "Firmware" or "Image" => "Firmware",
        "Build" => "Build",
        "Device" => "Device",
        "Flow" => "Flow",
        "UI" or "Feedback" => "Interaction",
        _ => "Composite"
    };

    /// <summary>Object family used for the second toolbox level.</summary>
    public string Family => TypeKey.Split('.') is [_, var family, ..] ? family : TypeKey;

    /// <summary>Stable technical search terms augment localized display labels.</summary>
    public IReadOnlyList<string> SearchKeywords => TypeKey switch
    {
        "Binary.Pattern.Replace" => [TypeKey, CategoryPath, Family, "hexpatch", "hex", "binary"],
        "Device.Partition.Flash" => [TypeKey, CategoryPath, Family, "boot", "bootloader", "super", "fastboot"],
        "Config.AndroidProperty.Edit" or "Config.AndroidProperty.Query" => [TypeKey, CategoryPath, Family, "build.prop", "property"],
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