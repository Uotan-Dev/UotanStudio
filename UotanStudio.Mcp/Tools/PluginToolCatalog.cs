namespace UotanStudio.Mcp.Tools;

/// <summary>
/// The single declared order of every MCP tool this server can expose. <c>BuildTools</c> projects
/// this catalog onto the emitted list, keeping the flash gate as a property of one descriptor rather
/// than a second hard-coded factory call.
/// <para>本服务器可能暴露的全部 MCP 工具的唯一声明顺序。<c>BuildTools</c> 把本目录投影为最终列表，刷写门控
/// 是单个描述符的属性，而不是另一处硬编码的工厂调用。</para>
/// </summary>
internal static class PluginToolCatalog
{
    /// <summary>
    /// Gets every catalogued tool, in firmware, compression, device, delivery, OTA, project, misc,
    /// environment and finally flash-gated order.
    /// <para>获取全部已编目工具，顺序为固件、压缩、设备、交付、OTA、工程、杂项、环境，最后是受门控的刷写工具。</para>
    /// </summary>
    internal static IReadOnlyList<PluginToolDescriptor> All { get; } =
    [
        new("list_formats", "firmware", null, true, false, false,
            static _ => FirmwareToolFactory.CreateListFormats()),
        new("detect_format", "firmware", null, true, false, false,
            static context => FirmwareToolFactory.CreateDetectFormat(context.Sandbox)),
        new("read_metadata", "firmware", null, true, false, false,
            static context => FirmwareToolFactory.CreateReadMetadata(context.Sandbox)),
        new("read_gpt", "firmware", null, true, false, false,
            static context => FirmwareToolFactory.CreateReadGpt(context.Sandbox)),
        new("unpack_firmware", "firmware", "Firmware.Container.Unpack", false, false, false,
            static context => FirmwareToolFactory.CreateUnpackFirmware(context.Sandbox)),
        new("pack_firmware", "firmware", "Firmware.Container.Pack", false, false, false,
            static context => FirmwareToolFactory.CreatePackFirmware(context.Sandbox)),
        new("extract_container_entries", "firmware", null, false, false, false,
            static context => FirmwareToolFactory.CreateExtractContainerEntries(context.Sandbox)),
        new("merge_sparse_chunks", "firmware", null, false, false, false,
            static context => FirmwareToolFactory.CreateMergeSparseChunks(context.Sandbox)),
        new("split_sparse_chunks", "firmware", null, false, false, false,
            static context => FirmwareToolFactory.CreateSplitSparseChunks(context.Sandbox)),

        new("list_compression_algorithms", "compression", null, true, false, false,
            static _ => CompressionToolFactory.CreateListAlgorithms()),
        new("compress_file", "compression", "Data.Compression.Compress", false, false, false,
            static context => CompressionToolFactory.CreateCompressFile(context.Sandbox)),
        new("decompress_file", "compression", "Data.Compression.Decompress", false, false, false,
            static context => CompressionToolFactory.CreateDecompressFile(context.Sandbox)),

        new("list_devices", "device", null, true, false, false,
            static context => DeviceToolFactory.CreateListDevices(context.DeviceManager)),
        new("adb_devices", "device", null, true, false, false,
            static context => DeviceToolFactory.CreateAdbDevices(context.DeviceManager)),
        new("adb_shell", "device", null, false, false, false,
            static context => DeviceToolFactory.CreateAdbShell(context.DeviceManager)),
        new("adb_pull", "device", null, true, false, false,
            static context => DeviceToolFactory.CreateAdbPull(context.Sandbox, context.DeviceManager)),
        new("adb_push", "device", null, false, false, false,
            static context => DeviceToolFactory.CreateAdbPush(context.Sandbox, context.DeviceManager)),
        new("fastboot_variables", "device", null, true, false, false,
            static context => DeviceToolFactory.CreateFastbootVariables(context.DeviceManager)),
        new("mtk_identify", "device", null, true, false, false,
            static context => BootProtocolToolFactory.CreateMtkIdentify(context.DeviceManager)),
        new("mtk_peek", "device", null, true, false, false,
            static context => BootProtocolToolFactory.CreateMtkPeek(context.DeviceManager)),
        new("fastboot_slot", "device", null, false, true, true,
            static context => DeviceToolFactory.CreateFastbootSlot(context.DeviceManager)),
        new("adb_qr_pairing_code", "device", null, true, false, false,
            static context => DeviceToolFactory.CreateAdbQrPairingCode(context.DeviceManager)),
        new("adb_pair_qr", "device", null, false, false, false,
            static context => DeviceToolFactory.CreateAdbPairQr(context.DeviceManager)),

        new("delivery_validate", "delivery", null, true, false, false,
            static context => DeliveryToolFactory.CreateDeliveryValidate(context.Sandbox)),
        new("delivery_list_outputs", "delivery", null, true, false, false,
            static context => DeliveryToolFactory.CreateDeliveryListOutputs(context.Sandbox)),
        new("delivery_build", "delivery", null, false, false, false,
            static context => DeliveryToolFactory.CreateDeliveryBuild(
                context.Sandbox, context.DeliveryTools, context.SigningKeys)),

        new("ota_zip_verify", "ota", null, true, false, false,
            static context => OtaToolFactory.CreateOtaZipVerify(context.Sandbox)),
        new("ota_zip_patch", "ota", null, false, false, false,
            static context => OtaToolFactory.CreateOtaZipPatch(context.Sandbox)),
        new("ota_zip_rebuild", "ota", null, false, false, false,
            static context => OtaToolFactory.CreateOtaZipRebuild(context.Sandbox)),
        new("super_payload_convert", "ota", null, false, false, false,
            static context => OtaToolFactory.CreateSuperPayloadConvert(context.Sandbox)),
        new("delivery_prepare_tools", "ota", null, false, false, false,
            static context => OtaToolFactory.CreateDeliveryPrepareTools(context.Sandbox)),

        new("solution_read", "project", null, true, false, false,
            static context => ProjectToolFactory.CreateSolutionRead(context.Sandbox)),
        new("solution_write", "project", null, false, true, false,
            static context => ProjectToolFactory.CreateSolutionWrite(context.Sandbox)),
        new("firmware_project_read", "project", null, true, false, false,
            static context => ProjectToolFactory.CreateFirmwareProjectRead(context.Sandbox)),

        new("dex_disassemble", "misc", null, true, false, false,
            static context => MiscToolFactory.CreateDexDisassemble(context.Sandbox)),
        new("dex_decompile", "misc", null, true, false, false,
            static context => MiscToolFactory.CreateDexDecompile(context.Sandbox)),
        new("dex_assemble", "misc", null, false, false, false,
            static context => MiscToolFactory.CreateDexAssemble(context.Sandbox)),
        new("dex_info", "misc", null, true, false, false,
            static context => MiscToolFactory.CreateDexInfo(context.Sandbox)),
        new("axml_parse", "misc", null, true, false, false,
            static context => MiscToolFactory.CreateAxmlParse(context.Sandbox)),
        new("arsc_parse", "misc", null, true, false, false,
            static context => MiscToolFactory.CreateArscParse(context.Sandbox)),
        new("apk_strip_signatures", "misc", null, false, false, false,
            static context => MiscToolFactory.CreateApkStripSignatures(context.Sandbox)),
        new("download_file", "misc", null, false, false, false,
            static context => MiscToolFactory.CreateDownloadFile(context.Sandbox)),

        new("get_env", "env", null, true, false, false,
            static context => EnvToolFactory.CreateGetEnv(context.Sandbox, context.AllowFlash)),

        new("adb_reboot", "device", "Device.Power.Reboot", false, true, true,
            static context => DeviceToolFactory.CreateAdbReboot(context.DeviceManager)),
        new("device_shell", "device", null, false, true, true,
            static context => DeviceToolFactory.CreateDeviceShell(context.DeviceManager)),
        new("recovery_clean", "device", null, false, true, true,
            static context => DeviceToolFactory.CreateRecoveryClean(context.DeviceManager)),
        new("fastboot_erase", "device", null, false, true, true,
            static context => DeviceToolFactory.CreateFastbootErase(context.DeviceManager)),
        new("fastboot_flash", "device", "Device.Partition.Flash", false, true, true,
            static context => DeviceToolFactory.CreateFastbootFlash(context.Sandbox, context.DeviceManager))
    ];
}