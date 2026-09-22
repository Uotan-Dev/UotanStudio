using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.</summary>
public static class NodeFactoryRegistrar
{
    private static readonly IReadOnlyList<NodeDescriptor> BuiltInDescriptors =
    [
        D("Patch.ImageReadText", "Node_Patch_ImageReadText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageReadText")),
        D("Patch.ImageWriteText", "Node_Patch_ImageWriteText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageWriteText")),
        D("Patch.ImageCollectText", "Node_Patch_ImageCollectText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageCollectText")),
        D("Patch.ImageApplyTexts", "Node_Patch_ImageApplyTexts", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageApplyTexts")),
        D("Patch.ImageReadAttributes", "Node_Patch_ImageReadAttributes", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageReadAttributes")),
        D("Patch.ImageReadXattr", "Node_Patch_ImageReadXattr", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageReadXattr")),
        D("Patch.ImageSetXattr", "Node_Patch_ImageSetXattr", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageSetXattr")),
        D("Patch.ImageDeleteXattr", "Node_Patch_ImageDeleteXattr", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageDeleteXattr")),
        D("Patch.ImageSetAttributes", "Node_Patch_ImageSetAttributes", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImageSetAttributes")),
        D("Patch.FileContextAdd", "Node_Patch_FileContextAdd", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FileContextAdd")),
        D("Patch.FileContextDelete", "Node_Patch_FileContextDelete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FileContextDelete")),
        D("Patch.FileContextQuery", "Node_Patch_FileContextQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FileContextQuery")),
        D("Patch.FileContextEdit", "Node_Patch_FileContextEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FileContextEdit")),
        D("Patch.FsConfigAdd", "Node_Patch_FsConfigAdd", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FsConfigAdd")),
        D("Patch.FsConfigDelete", "Node_Patch_FsConfigDelete", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FsConfigDelete")),
        D("Patch.FsConfigQuery", "Node_Patch_FsConfigQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FsConfigQuery")),
        D("Patch.FsConfigEdit", "Node_Patch_FsConfigEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FsConfigEdit")),
        D("Patch.InitRcReferences", "Node_Patch_InitRcReferences", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcReferences")),
        D("Patch.InitRcArgumentEdit", "Node_Patch_InitRcArgumentEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcArgumentEdit")),
        D("Patch.InitRcHeaderEdit", "Node_Patch_InitRcHeaderEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcHeaderEdit")),
        D("Patch.InitRcAddSection", "Node_Patch_InitRcAddSection", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcAddSection")),
        D("Patch.InitRcAdd", "Node_Patch_InitRcAdd", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcAdd")),
        D("Patch.InitRcQuery", "Node_Patch_InitRcQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcQuery")),
        D("Patch.InitRcEdit", "Node_Patch_InitRcEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.InitRcEdit")),
        D("Patch.FstabAdd", "Node_Patch_FstabAdd", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FstabAdd")),
        D("Patch.FstabQuery", "Node_Patch_FstabQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FstabQuery")),
        D("Patch.FstabEdit", "Node_Patch_FstabEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FstabEdit")),
        D("Patch.BinaryReplace", "Node_Patch_BinaryReplace", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.BinaryReplace")),
        D("Patch.BinaryFind", "Node_Patch_BinaryFind", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.BinaryFind")),
        D("Patch.PartitionPath", "Node_Patch_PartitionPath", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.PartitionPath")),
        D("Patch.ApplyGate", "Node_Patch_ApplyGate", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ApplyGate")),
        D("Patch.Applicability", "Node_Patch_Applicability", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Applicability")),
        D("Patch.Asset", "Node_Patch_Asset", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Asset")),
        D("Patch.AssetText", "Node_Patch_AssetText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.AssetText")),
        D("Patch.ImportAsset", "Node_Patch_ImportAsset", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImportAsset")),
        D("Patch.DetectFormat", "Node_Patch_DetectFormat", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.DetectFormat")),
        D("Patch.CheckFormat", "Node_Patch_CheckFormat", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.CheckFormat")),
        D("Patch.WorkspaceFind", "Node_Patch_WorkspaceFind", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.WorkspaceFind")),
        D("Patch.JsonQuery", "Node_Patch_JsonQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.JsonQuery")),
        D("Patch.JsonEdit", "Node_Patch_JsonEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.JsonEdit")),
        D("Patch.IniQuery", "Node_Patch_IniQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.IniQuery")),
        D("Patch.IniEdit", "Node_Patch_IniEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.IniEdit")),
        D("Patch.Workspace", "Node_Patch_Workspace", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Workspace")),
        D("Patch.WorkspaceRead", "Node_Patch_WorkspaceRead", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.WorkspaceRead")),
        D("Patch.StageText", "Node_Patch_StageText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.StageText")),
        D("Patch.ImportFile", "Node_Patch_ImportFile", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ImportFile")),
        D("Patch.CopyFile", "Node_Patch_CopyFile", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.CopyFile")),
        D("Patch.DeleteFile", "Node_Patch_DeleteFile", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.DeleteFile")),
        D("Patch.FileExists", "Node_Patch_FileExists", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FileExists")),
        D("Patch.HashFile", "Node_Patch_HashFile", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.HashFile")),
        D("Patch.VerifyHash", "Node_Patch_VerifyHash", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.VerifyHash")),
        D("Patch.AssertCount", "Node_Patch_AssertCount", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.AssertCount")),
        D("Patch.VerifyChanges", "Node_Patch_VerifyChanges", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.VerifyChanges")),
        D("Patch.FindFiles", "Node_Patch_FindFiles", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.FindFiles")),
        D("Patch.ReadText", "Node_Patch_ReadText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.ReadText")),
        D("Patch.Condition", "Node_Patch_Condition", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Condition")),
        D("Patch.Assert", "Node_Patch_Assert", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Assert")),
        D("Patch.Text", "Node_Patch_Text", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.Text")),
        D("Patch.PropertyRead", "Node_Patch_PropertyRead", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.PropertyRead")),
        D("Patch.PropertyEdit", "Node_Patch_PropertyEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.PropertyEdit")),
        D("Patch.XmlQuery", "Node_Patch_XmlQuery", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.XmlQuery")),
        D("Patch.XmlEdit", "Node_Patch_XmlEdit", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.XmlEdit")),
        D("Patch.WriteText", "Node_Patch_WriteText", NodeToolboxGroup.Patch, () => PatchNodeViewModel.Create("Patch.WriteText")),
        D("Firmware.Unpack", "Node_Firmware_Unpack", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreateUnpackNode()),
        D("Firmware.Pack", "Node_Firmware_Pack", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreatePackNode()),
        D("Firmware.Verify", "Node_Firmware_Verify", NodeToolboxGroup.Android, () => FirmwareNodeViewModel.CreateVerifyNode()),
        D("Firmware.SignApk", "Node_Firmware_SignApk", NodeToolboxGroup.Android, () => new ApkSignNodeViewModel()),
        D("Firmware.Extract", "Node_Firmware_Extract", NodeToolboxGroup.FirmwareContainer, () => FirmwareNodeViewModel.CreateExtractNode()),
        D("Firmware.OtaDelta", "Node_Firmware_OtaDelta", NodeToolboxGroup.Delivery, () => ExtendedFirmwareNodeViewModel.CreateOtaDeltaNode()),
        D("Firmware.MergeSignature", "Node_Firmware_MergeSignature", NodeToolboxGroup.Android, () => ExtendedFirmwareNodeViewModel.CreateMergeSignatureNode()),
        D("Firmware.MergeDualSlot", "Node_Firmware_MergeDualSlot", NodeToolboxGroup.Delivery, () => ExtendedFirmwareNodeViewModel.CreateMergeDualSlotNode()),
        D("Firmware.FlashBoot", "Node_Firmware_FlashBoot", NodeToolboxGroup.FirmwareImage, () => ExtendedFirmwareNodeViewModel.CreateFlashBootNode()),
        D("Firmware.CpioUnpack", "Node_Firmware_CpioUnpack", NodeToolboxGroup.FirmwareContainer, () => CpioNodeViewModel.CreateCpioUnpackNode()),
        D("Firmware.CpioPack", "Node_Firmware_CpioPack", NodeToolboxGroup.FirmwareContainer, () => CpioNodeViewModel.CreateCpioPackNode()),
        D("Firmware.SuperUnpack", "Node_Firmware_SuperUnpack", NodeToolboxGroup.FirmwareImage, () => SuperNodeViewModel.CreateSuperUnpackNode()),
        D("Firmware.SuperPack", "Node_Firmware_SuperPack", NodeToolboxGroup.FirmwareImage, () => SuperNodeViewModel.CreateSuperPackNode()),
        D("Firmware.Sparse", "Node_Firmware_Sparse", NodeToolboxGroup.FirmwareImage, () => SparseNodeViewModel.CreateSparseNode()),
        D("Firmware.Dat", "Node_Firmware_Dat", NodeToolboxGroup.Delivery, () => DatNodeViewModel.CreateDatNode()),
        D("Flash.SelectDevice", "Node_Flash_SelectDevice", NodeToolboxGroup.Flash, () => SelectDeviceNodeViewModel.CreateSelectDeviceNode()),
        D("Flash.GetProp", "Node_Flash_GetProp", NodeToolboxGroup.Flash, () => DevicePropertyNodeViewModel.CreateDevicePropertyNode()),
        D("Flash.Flash", "Node_Flash_Flash", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateFlashNode(), true),
        D("Flash.Erase", "Node_Flash_Erase", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateEraseNode(), true),
        D("Flash.Reboot", "Node_Flash_Reboot", NodeToolboxGroup.Flash, () => FlashNodeViewModel.CreateRebootNode(), true),
        D("Flash.Bootloader", "Node_Flash_Bootloader", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateFlashBootloaderNode(), true),
        D("Flash.EraseAll", "Node_Flash_EraseAll", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateEraseAllNode(), true),
        D("Flash.OtaSideload", "Node_Flash_OtaSideload", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateOtaSideloadNode(), true),
        D("Flash.OverwriteFirmware", "Node_Flash_OverwriteFirmware", NodeToolboxGroup.Flash, () => ExtendedFlashNodeViewModel.CreateOverwriteFirmwareNode(), true),
        D("Data.ReadFile", "Node_Data_ReadFile", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateFileReadNode()),
        D("Data.Constant", "Node_Data_Constant", NodeToolboxGroup.Data, () => ConstantNodeViewModel.CreateConstantNode()),
        D("Data.ProjectImage", "Node_Data_ProjectImage", NodeToolboxGroup.Data, () => ProjectImageNodeViewModel.CreateProjectImageNode()),
        D("Data.Decrypt", "Node_Data_Decrypt", NodeToolboxGroup.Data, () => DecryptNodeViewModel.CreateDecryptNode()),
        D("Data.Decompress", "Node_Data_Decompress", NodeToolboxGroup.Data, () => DecompressNodeViewModel.CreateDecompressNode()),
        D("Data.Convert", "Node_Data_Convert", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateConvertNode()),
        D("Data.Checksum", "Node_Data_Checksum", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateChecksumNode()),
        D("Data.Compare", "Node_Data_Compare", NodeToolboxGroup.Data, () => DataNodeViewModel.CreateCompareNode()),
        D("Data.Concat", "Node_Data_Concat", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateConcatNode()),
        D("Data.Slice", "Node_Data_Slice", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateSliceNode()),
        D("Data.Encrypt", "Node_Data_Encrypt", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateEncryptNode()),
        D("Data.Compress", "Node_Data_Compress", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateCompressNode()),
        D("Data.ByteSwap", "Node_Data_ByteSwap", NodeToolboxGroup.Data, () => ExtendedDataNodeViewModel.CreateByteSwapNode()),
        D("Network.Download", "Node_Network_Download", NodeToolboxGroup.Network, () => NetworkNodeViewModel.CreateDownloadNode()),
        D("Network.GitHubDownload", "Node_Network_GitHubDownload", NodeToolboxGroup.Network, () => NetworkNodeViewModel.CreateGitHubDownloadNode()),
        D("Flow.Start", "Node_Flow_Start", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateStartNode()),
        D("Flow.End", "Node_Flow_End", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateEndNode()),
        D("Flow.Branch", "Node_Flow_Branch", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateBranchNode()),
        D("Flow.Loop", "Node_Flow_Loop", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateLoopNode()),
        D("Flow.Delay", "Node_Flow_Delay", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateDelayNode()),
        D("Flow.Parallel", "Node_Flow_Parallel", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateParallelNode()),
        D("Flow.Terminate", "Node_Flow_Terminate", NodeToolboxGroup.Flow, () => FlowControlNodeViewModel.CreateTerminateNode()),
        D("Output.Log", "Node_Output_Log", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateLogNode()),
        D("Output.Progress", "Node_Output_Progress", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateProgressNode()),
        D("Output.Save", "Node_Output_Save", NodeToolboxGroup.Output, () => OutputNodeViewModel.CreateSaveNode()),
        D("Output.Notify", "Node_Output_Notify", NodeToolboxGroup.Output, () => ExtendedOutputNodeViewModel.CreateNotifyNode()),
        D("Output.Console", "Node_Output_Console", NodeToolboxGroup.Output, () => ExtendedOutputNodeViewModel.CreateConsoleOutputNode()),
        D("Output.FileExport", "Node_Output_FileExport", NodeToolboxGroup.Output, () => ExtendedOutputNodeViewModel.CreateFileExportNode())
    ];

    private static readonly Dictionary<string, NodeDescriptor> VisualPluginDescriptors =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, VisualPluginPackage> VisualPluginPackages = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetVisualPluginPackage(string typeKey, out VisualPluginPackage package)
        => VisualPluginPackages.TryGetValue(typeKey, out package!);

    /// <summary>Raised whenever the set of dynamically registered toolbox nodes changes.</summary>
    public static event EventHandler? DescriptorsChanged;

    /// <summary>Built-in and enabled visual-plugin descriptors.</summary>
    public static IReadOnlyList<NodeDescriptor> Descriptors =>
        [.. BuiltInDescriptors, .. VisualPluginDescriptors.Values.OrderBy(descriptor => descriptor.DisplayName)];

    /// <summary>Stable type keys provided by the host itself.</summary>
    public static IReadOnlySet<string> BuiltInTypeKeys { get; } =
        BuiltInDescriptors.Select(descriptor => descriptor.TypeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void RegisterAll()
    {
        foreach (var descriptor in Descriptors)
            NodeTypeRegistry.Register(descriptor.TypeKey, descriptor.Factory);
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
        VisualPluginDescriptors[package.NodeTypeKey] = descriptor;
        VisualPluginPackages[package.NodeTypeKey] = package;
        NodeTypeRegistry.Register(package.NodeTypeKey, descriptor.Factory);
        DescriptorsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Unregisters one visual-plugin node type.</summary>
    public static void UnregisterVisualPlugin(string typeKey)
    {
        if (!VisualPluginDescriptors.Remove(typeKey)) return;
        VisualPluginPackages.Remove(typeKey);
        NodeTypeRegistry.Unregister(typeKey);
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
        var capabilities = group switch
        {
            NodeToolboxGroup.Flow => NodeCapability.ControlFlow,
            NodeToolboxGroup.FirmwareContainer or NodeToolboxGroup.FirmwareImage or NodeToolboxGroup.Android or NodeToolboxGroup.Delivery
                => NodeCapability.ProducesArtifact,
            NodeToolboxGroup.Flash => NodeCapability.RequiresDevice,
            NodeToolboxGroup.Network => NodeCapability.RequiresNetwork | NodeCapability.ProducesArtifact,
            _ => NodeCapability.None
        };

        if (destructive)
            capabilities |= NodeCapability.MutatesDevice;
        if (typeKey is "Flash.SelectDevice" or "Flash.GetProp")
            capabilities |= NodeCapability.ReadsDevice;
        if (typeKey == "Patch.WriteText")
        {
            capabilities |= NodeCapability.ProducesArtifact;
        }
        if (typeKey == "Patch.ApplyGate")
        {
            capabilities |= NodeCapability.ControlFlow;
        }

        return new(typeKey, resourceKey, group, factory, destructive, capabilities);
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
    string? DisplayName = null);

[Flags]
public enum NodeCapability
{
    None = 0,
    ControlFlow = 1 << 0,
    RequiresDevice = 1 << 1,
    ReadsDevice = 1 << 2,
    MutatesDevice = 1 << 3,
    ProducesArtifact = 1 << 4,
    RequiresNetwork = 1 << 5
}
