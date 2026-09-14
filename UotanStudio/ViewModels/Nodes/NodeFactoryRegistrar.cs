using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>
/// <para>节点工厂集中注册器：把全部 TypeKey → 工厂映射登记到 <see cref="NodeTypeRegistry"/>。</para>
/// Central node-factory registrar: registers every TypeKey → factory mapping into <see cref="NodeTypeRegistry"/>.
/// </summary>
/// <remarks>
/// <para>与 UI 无关，可被节点编辑器工具箱初始化与无头 CLI（workflow run）共同调用；重复注册安全
/// （<see cref="NodeTypeRegistry.Register"/> 覆盖同键工厂）。</para>
/// UI-independent; invoked by both the node-editor toolbox initialization and the headless CLI
/// (<c>workflow run</c>). Safe to call repeatedly (<see cref="NodeTypeRegistry.Register"/> overwrites same-key factories).
/// </remarks>
public static class NodeFactoryRegistrar
{
    /// <summary>
    /// <para>注册全部既有节点工厂，确保工程加载与无头执行能按 TypeKey 重建具体节点而非缺失占位。</para>
    /// Registers all existing node factories so project loading and headless execution can rebuild concrete
    /// nodes by TypeKey instead of missing placeholders.
    /// </summary>
    public static void RegisterAll()
    {
        foreach (var operation in HdcNodeViewModel.Operations)
            NodeTypeRegistry.Register("HDC." + operation, () => HdcNodeViewModel.Create(operation));
        NodeTypeRegistry.Register("Firmware.Unpack", () => NodeViewModels.FirmwareNodeViewModel.CreateUnpackNode());
        NodeTypeRegistry.Register("Firmware.Pack", () => NodeViewModels.FirmwareNodeViewModel.CreatePackNode());
        NodeTypeRegistry.Register("Firmware.Verify", () => NodeViewModels.FirmwareNodeViewModel.CreateVerifyNode());
        NodeTypeRegistry.Register("Firmware.Extract", () => NodeViewModels.FirmwareNodeViewModel.CreateExtractNode());
        NodeTypeRegistry.Register("Firmware.OtaDelta", () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateOtaDeltaNode());
        NodeTypeRegistry.Register("Firmware.MergeSignature", () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateMergeSignatureNode());
        NodeTypeRegistry.Register("Firmware.SignApk", () => new NodeViewModels.ApkSignNodeViewModel());
        NodeTypeRegistry.Register("Firmware.MergeDualSlot", () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateMergeDualSlotNode());
        NodeTypeRegistry.Register("Firmware.FlashBoot", () => NodeViewModels.ExtendedFirmwareNodeViewModel.CreateFlashBootNode());
        NodeTypeRegistry.Register("Firmware.CpioUnpack", () => NodeViewModels.CpioNodeViewModel.CreateCpioUnpackNode());
        NodeTypeRegistry.Register("Firmware.CpioPack", () => NodeViewModels.CpioNodeViewModel.CreateCpioPackNode());
        NodeTypeRegistry.Register("Firmware.SuperUnpack", () => NodeViewModels.SuperNodeViewModel.CreateSuperUnpackNode());
        NodeTypeRegistry.Register("Firmware.SuperPack", () => NodeViewModels.SuperNodeViewModel.CreateSuperPackNode());
        NodeTypeRegistry.Register("Firmware.Sparse", () => NodeViewModels.SparseNodeViewModel.CreateSparseNode());
        NodeTypeRegistry.Register("Firmware.Dat", () => NodeViewModels.DatNodeViewModel.CreateDatNode());
        NodeTypeRegistry.Register("Flash.SelectDevice", () => NodeViewModels.SelectDeviceNodeViewModel.CreateSelectDeviceNode());
        NodeTypeRegistry.Register("Flash.GetProp", () => NodeViewModels.DevicePropertyNodeViewModel.CreateDevicePropertyNode());
        NodeTypeRegistry.Register("Flash.Flash", () => NodeViewModels.FlashNodeViewModel.CreateFlashNode());
        NodeTypeRegistry.Register("Flash.Erase", () => NodeViewModels.FlashNodeViewModel.CreateEraseNode());
        NodeTypeRegistry.Register("Flash.Reboot", () => NodeViewModels.FlashNodeViewModel.CreateRebootNode());
        NodeTypeRegistry.Register("Flash.Bootloader", () => NodeViewModels.ExtendedFlashNodeViewModel.CreateFlashBootloaderNode());
        NodeTypeRegistry.Register("Flash.EraseAll", () => NodeViewModels.ExtendedFlashNodeViewModel.CreateEraseAllNode());
        NodeTypeRegistry.Register("Flash.OtaSideload", () => NodeViewModels.ExtendedFlashNodeViewModel.CreateOtaSideloadNode());
        NodeTypeRegistry.Register("Flash.OverwriteFirmware", () => NodeViewModels.ExtendedFlashNodeViewModel.CreateOverwriteFirmwareNode());
        NodeTypeRegistry.Register("Data.ReadFile", () => NodeViewModels.DataNodeViewModel.CreateFileReadNode());
        NodeTypeRegistry.Register("Data.Constant", () => NodeViewModels.ConstantNodeViewModel.CreateConstantNode());
        NodeTypeRegistry.Register("Data.ProjectImage", () => NodeViewModels.ProjectImageNodeViewModel.CreateProjectImageNode());
        NodeTypeRegistry.Register("Data.Decrypt", () => NodeViewModels.DecryptNodeViewModel.CreateDecryptNode());
        NodeTypeRegistry.Register("Data.Decompress", () => NodeViewModels.DecompressNodeViewModel.CreateDecompressNode());
        NodeTypeRegistry.Register("Data.Convert", () => NodeViewModels.DataNodeViewModel.CreateConvertNode());
        NodeTypeRegistry.Register("Data.Checksum", () => NodeViewModels.DataNodeViewModel.CreateChecksumNode());
        NodeTypeRegistry.Register("Data.Compare", () => NodeViewModels.DataNodeViewModel.CreateCompareNode());
        NodeTypeRegistry.Register("Data.Concat", () => NodeViewModels.ExtendedDataNodeViewModel.CreateConcatNode());
        NodeTypeRegistry.Register("Data.Slice", () => NodeViewModels.ExtendedDataNodeViewModel.CreateSliceNode());
        NodeTypeRegistry.Register("Data.Encrypt", () => NodeViewModels.ExtendedDataNodeViewModel.CreateEncryptNode());
        NodeTypeRegistry.Register("Data.Compress", () => NodeViewModels.ExtendedDataNodeViewModel.CreateCompressNode());
        NodeTypeRegistry.Register("Data.ByteSwap", () => NodeViewModels.ExtendedDataNodeViewModel.CreateByteSwapNode());
        NodeTypeRegistry.Register("Flow.Start", () => NodeViewModels.FlowControlNodeViewModel.CreateStartNode());
        NodeTypeRegistry.Register("Flow.End", () => NodeViewModels.FlowControlNodeViewModel.CreateEndNode());
        NodeTypeRegistry.Register("Flow.Branch", () => NodeViewModels.FlowControlNodeViewModel.CreateBranchNode());
        NodeTypeRegistry.Register("Flow.Loop", () => NodeViewModels.FlowControlNodeViewModel.CreateLoopNode());
        NodeTypeRegistry.Register("Flow.Delay", () => NodeViewModels.FlowControlNodeViewModel.CreateDelayNode());
        NodeTypeRegistry.Register("Flow.Parallel", () => NodeViewModels.FlowControlNodeViewModel.CreateParallelNode());
        NodeTypeRegistry.Register("Flow.Terminate", () => NodeViewModels.FlowControlNodeViewModel.CreateTerminateNode());
        NodeTypeRegistry.Register("Output.Log", () => NodeViewModels.OutputNodeViewModel.CreateLogNode());
        NodeTypeRegistry.Register("Output.Progress", () => NodeViewModels.OutputNodeViewModel.CreateProgressNode());
        NodeTypeRegistry.Register("Output.Save", () => NodeViewModels.OutputNodeViewModel.CreateSaveNode());
        NodeTypeRegistry.Register("Output.Notify", () => NodeViewModels.ExtendedOutputNodeViewModel.CreateNotifyNode());
        NodeTypeRegistry.Register("Output.Console", () => NodeViewModels.ExtendedOutputNodeViewModel.CreateConsoleOutputNode());
        NodeTypeRegistry.Register("Output.FileExport", () => NodeViewModels.ExtendedOutputNodeViewModel.CreateFileExportNode());
        NodeTypeRegistry.Register("Dex.Parse", () => NodeViewModels.DexNodeViewModel.CreateParseNode());
        NodeTypeRegistry.Register("Dex.Disassemble", () => NodeViewModels.DexNodeViewModel.CreateDisassembleNode());
        NodeTypeRegistry.Register("Dex.Rebuild", () => NodeViewModels.DexNodeViewModel.CreateRebuildNode());
        NodeTypeRegistry.Register("Dex.Decompile", () => NodeViewModels.DexNodeViewModel.CreateDecompileNode());
    }
}