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
        D("Device.State.Query", "Node_Device_State_Query", "Device", () => DeviceServiceNodeViewModel.CreateStateQueryNode()),
        D("Device.Table.Read", "Node_Device_Table_Read", "Device", () => DeviceServiceNodeViewModel.CreatePartitionTableReadNode()),
        D("Device.Slot.Query", "Node_Device_Slot_Query", "Device", () => DeviceServiceNodeViewModel.CreateSlotQueryNode()),
        D("Device.Slot.Switch", "Node_Device_Slot_Switch", "Device", () => DeviceServiceNodeViewModel.CreateSlotSwitchNode(), true),
        D("Device.Recovery.Clean", "Node_Device_Recovery_Clean", "Device", () => DeviceServiceNodeViewModel.CreateRecoveryCleanNode(), true),
        D("Device.Shell.Command", "Node_Device_Shell_Command", "Device", () => DeviceServiceNodeViewModel.CreateShellCommandNode(), true),
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
        D("Mirroring.Session.Start", "Node_Mirroring_Session_Start", "Mirroring", () => MirroringNodeViewModel.CreateStartNode()),
        D("Mirroring.Session.Stop", "Node_Mirroring_Session_Stop", "Mirroring", () => MirroringNodeViewModel.CreateStopNode()),
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

    /// <summary>Builds the toolbox descriptor for one composite-node package.<para>为单个组合节点软件包构建工具箱描述符。</para></summary>
    /// <param name="package">Composite package supplying the graph and manifest.<para>提供图与清单的组合软件包。</para></param>
    /// <param name="validateExecution">Hook that returns validation errors before execution.<para>在执行前返回校验错误的回调。</para></param>
    /// <returns>The descriptor naming the node type and its factory.<para>指明节点类型与其工厂的描述符。</para></returns>
    internal static NodeDescriptor CreateCompositePluginDescriptor(VisualPluginPackage package,
        Func<IReadOnlyList<string>>? validateExecution) =>
        new(package.NodeTypeKey,
            string.Empty,
            () => new CompositeNodeViewModel(package, validateExecution),
            Capabilities: ParseCapabilities(package.Manifest.Capabilities),
            DisplayName: package.Manifest.Name)
        { CategoryPath = "Composite" };

    /// <summary>Builds the toolbox descriptor for one script-node package.<para>为单个脚本节点软件包构建工具箱描述符。</para></summary>
    /// <param name="package">Script package supplying the Lua source and manifest.<para>提供 Lua 源码与清单的脚本软件包。</para></param>
    /// <param name="validateExecution">Hook that returns validation errors before execution.<para>在执行前返回校验错误的回调。</para></param>
    /// <returns>The descriptor naming the node type and its factory.<para>指明节点类型与其工厂的描述符。</para></returns>
    internal static NodeDescriptor CreateScriptPluginDescriptor(VisualPluginPackage package,
        Func<IReadOnlyList<string>>? validateExecution) =>
        new(package.NodeTypeKey,
            string.Empty,
            () => new ScriptNodeViewModel(package, validateExecution),
            Capabilities: ParseCapabilities(package.Manifest.Capabilities),
            DisplayName: package.Manifest.Name)
        { CategoryPath = "Script" };

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
        if (typeKey.StartsWith("Device.", StringComparison.Ordinal)
            || typeKey.StartsWith("Mirroring.", StringComparison.Ordinal))
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
        if (requiresConfirmation)
        {
            capabilities |= NodeCapability.MutatesDevice;
        }
        if (typeKey is "Device.Target.Select" or "Device.Property.Read" or "Device.State.Query"
            or "Device.Table.Read" or "Device.Slot.Query")
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
        return new(typeKey, resourceKey, () => Prepare(factory(), capabilities), capabilities)
        {
            CategoryPath = category,
            RequiresConfirmation = requiresConfirmation,
            RequiredDeviceState = DeviceRequiredStateFor(typeKey)
        };
    }

    private static DeviceRequiredState DeviceRequiredStateFor(string typeKey) => typeKey switch
    {
        "Device.Target.Select" => DeviceRequiredState.AnyDevice,
        "Device.Property.Read" => DeviceRequiredState.Adb | DeviceRequiredState.Fastboot,
        "Device.Power.Reboot" => DeviceRequiredState.Adb | DeviceRequiredState.Fastboot,
        "Device.Ota.Sideload" => DeviceRequiredState.Adb,
        "Device.Partition.Flash" => DeviceRequiredState.Fastboot,
        "Device.Partition.Erase" => DeviceRequiredState.Fastboot,
        "Device.Storage.EraseAll" => DeviceRequiredState.Fastboot,
        "Device.State.Query" => DeviceRequiredState.AnyDevice,
        "Device.Table.Read" => DeviceRequiredState.Fastboot,
        "Device.Slot.Query" => DeviceRequiredState.Fastboot,
        "Device.Slot.Switch" => DeviceRequiredState.Fastboot,
        "Device.Recovery.Clean" => DeviceRequiredState.Adb,
        "Device.Shell.Command" => DeviceRequiredState.Adb,
        "Mirroring.Session.Start" => DeviceRequiredState.Adb,
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
            || node.TypeKey is "Device.Property.Read" or "Device.State.Query" or "Device.Table.Read"
                or "Device.Slot.Query" or "Mirroring.Session.Start" or "Mirroring.Session.Stop"
                or "Feedback.Log.Write" or "Feedback.Progress.Report")
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