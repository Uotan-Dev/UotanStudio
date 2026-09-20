using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Single source of truth for workflow node identity, toolbox metadata and factories.</summary>
public static class NodeFactoryRegistrar
{
    public static IReadOnlyList<NodeDescriptor> Descriptors { get; } =
    [
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

    public static void RegisterAll()
    {
        foreach (var descriptor in Descriptors)
            NodeTypeRegistry.Register(descriptor.TypeKey, descriptor.Factory);
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
            _ => NodeCapability.None
        };

        if (destructive)
            capabilities |= NodeCapability.MutatesDevice;
        if (typeKey is "Flash.SelectDevice" or "Flash.GetProp")
            capabilities |= NodeCapability.ReadsDevice;

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
    Flow,
    Output
}

public sealed record NodeDescriptor(
    string TypeKey,
    string NameResourceKey,
    NodeToolboxGroup Group,
    Func<FlowNodeViewModel> Factory,
    bool IsDestructive = false,
    NodeCapability Capabilities = NodeCapability.None);

[Flags]
public enum NodeCapability
{
    None = 0,
    ControlFlow = 1 << 0,
    RequiresDevice = 1 << 1,
    ReadsDevice = 1 << 2,
    MutatesDevice = 1 << 3,
    ProducesArtifact = 1 << 4
}