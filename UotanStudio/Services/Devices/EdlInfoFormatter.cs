using System.Globalization;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Assembles the formatted rows <see cref="EdlInfoSnapshot"/> exposes, so the protocol stages collect
/// facts and this one place decides how they read.
/// <para>汇总 <see cref="EdlInfoSnapshot"/> 对外暴露的格式化行：协议阶段只负责采集事实，
/// 只有这一处决定它们如何呈现。</para>
/// </summary>
internal static class EdlInfoFormatter
{
    /// <summary>The row key of the observed protocol mode.
    /// <para>已观测协议模式的行键。</para></summary>
    internal const string ProtocolKey = "Flash_EdlInfo_Protocol";

    /// <summary>The row key of the Sahara protocol version.
    /// <para>Sahara 协议版本的行键。</para></summary>
    internal const string SaharaVersionKey = "Flash_EdlInfo_SaharaVersion";

    /// <summary>The row key of the Sahara minimum protocol version.
    /// <para>Sahara 最低协议版本的行键。</para></summary>
    internal const string SaharaMinVersionKey = "Flash_EdlInfo_SaharaMinVersion";

    /// <summary>The row key of the Sahara maximum command length.
    /// <para>Sahara 最大命令长度的行键。</para></summary>
    internal const string SaharaMaxCommandLengthKey = "Flash_EdlInfo_SaharaMaxCommandLength";

    /// <summary>The row key of the Sahara operating mode.
    /// <para>Sahara 操作模式的行键。</para></summary>
    internal const string SaharaModeKey = "Flash_EdlInfo_SaharaMode";

    /// <summary>The row key of the bootrom-reported serial number.
    /// <para>bootrom 上报序列号的行键。</para></summary>
    internal const string IdentitySerialKey = "Flash_EdlInfo_IdentitySerial";

    /// <summary>The row key of the bootrom-reported hardware identifier.
    /// <para>bootrom 上报硬件标识的行键。</para></summary>
    internal const string IdentityHardwareKey = "Flash_EdlInfo_IdentityHardware";

    /// <summary>The row key of the bootrom-reported SBL version.
    /// <para>bootrom 上报 SBL 版本的行键。</para></summary>
    internal const string IdentitySblVersionKey = "Flash_EdlInfo_IdentitySblVersion";

    /// <summary>The row key of the bootrom-reported OEM public-key hash.
    /// <para>bootrom 上报 OEM 公钥哈希的行键。</para></summary>
    internal const string IdentityOemKeyHashKey = "Flash_EdlInfo_IdentityOemKeyHash";

    /// <summary>The row key of the version 3 chip identifier.
    /// <para>版本 3 芯片标识的行键。</para></summary>
    internal const string IdentityChipIdentifierKey = "Flash_EdlInfo_IdentityChipIdentifier";

    /// <summary>The row key of the Firehose version string.
    /// <para>Firehose 版本字符串的行键。</para></summary>
    internal const string FirehoseVersionKey = "Flash_EdlInfo_FirehoseVersion";

    /// <summary>The row key of the loaded programmer's file name.
    /// <para>已加载 programmer 文件名的行键。</para></summary>
    internal const string ProgrammerKey = "Flash_EdlInfo_Programmer";

    /// <summary>The row key of the negotiated storage type.
    /// <para>协商得到的存储类型的行键。</para></summary>
    internal const string StorageTypeKey = "Flash_EdlInfo_StorageType";

    /// <summary>The row key of the reported sector size.
    /// <para>上报扇区大小的行键。</para></summary>
    internal const string SectorSizeKey = "Flash_EdlInfo_SectorSize";

    /// <summary>The row key of the reported storage capacity.
    /// <para>上报存储容量的行键。</para></summary>
    internal const string CapacityKey = "Flash_EdlInfo_Capacity";

    /// <summary>The row key of the reported physical sector count.
    /// <para>上报物理扇区数的行键。</para></summary>
    internal const string SectorCountKey = "Flash_EdlInfo_SectorCount";

    /// <summary>The row key of the reported maximum payload size.
    /// <para>上报最大负载大小的行键。</para></summary>
    internal const string MaxPayloadKey = "Flash_EdlInfo_MaxPayload";

    /// <summary>The row key of the storage serial number.
    /// <para>存储序列号的行键。</para></summary>
    internal const string StorageSerialKey = "Flash_EdlInfo_StorageSerial";

    /// <summary>The row key of the storage manufacturer.
    /// <para>存储厂商的行键。</para></summary>
    internal const string StorageManufacturerKey = "Flash_EdlInfo_StorageManufacturer";

    /// <summary>The row key of the storage firmware version.
    /// <para>存储固件版本的行键。</para></summary>
    internal const string StorageFirmwareKey = "Flash_EdlInfo_StorageFirmware";

    /// <summary>The row key of the reported page size.
    /// <para>上报页大小的行键。</para></summary>
    internal const string PageSizeKey = "Flash_EdlInfo_PageSize";

    /// <summary>The row key of the active A/B slot.
    /// <para>活动 A/B 槽位的行键。</para></summary>
    internal const string ActiveSlotKey = "Flash_EdlInfo_ActiveSlot";

    /// <summary>The row key of the A/B slot layout.
    /// <para>A/B 槽位布局的行键。</para></summary>
    internal const string SlotLayoutKey = "Flash_EdlInfo_SlotLayout";

    /// <summary>The row value stating the partitioning carries no A/B slots.
    /// <para>表示分区方案没有 A/B 槽位的行值。</para></summary>
    private const string SingleSlotLayoutText = "Single (A-only)";

    /// <summary>The row value stating the partitioning carries A/B slots.
    /// <para>表示分区方案包含 A/B 槽位的行值。</para></summary>
    private const string DualSlotLayoutText = "Dual (A/B)";
    /// <summary>The row key of the QDLoader serial port name.
    /// <para>QDLoader 串口端口名的行键。</para></summary>
    internal const string SerialPortKey = "Flash_EdlInfo_SerialPort";

    /// <summary>
    /// Formats the rows in display order; stage-specific rows appear only when that stage actually
    /// answered, so a Sahara-only device never shows an empty Firehose section.
    /// <para>按显示顺序格式化各行；只有该阶段实际应答过才出现该阶段的行，
    /// 因此仅处于 Sahara 的设备不会显示空的 Firehose 段。</para>
    /// </summary>
    /// <param name="snapshot">The collected facts. <para>已采集的事实。</para></param>
    /// <returns>The formatted rows. <para>格式化后的行。</para></returns>
    internal static IReadOnlyList<EdlInfoRow> Format(EdlInfoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<EdlInfoRow> rows = [];
        if (snapshot.Protocol != EdlProtocolState.Unknown)
        {
            rows.Add(new EdlInfoRow(ProtocolKey, DescribeProtocol(snapshot.Protocol), EdlInfoStage.Unread));
        }

        if (snapshot.SerialPortName is { Length: > 0 } port)
        {
            rows.Add(new EdlInfoRow(SerialPortKey, port, EdlInfoStage.Unread));
        }

        if (snapshot.Sahara is { } sahara)
        {
            rows.Add(new EdlInfoRow(SaharaVersionKey,
                sahara.ProtocolVersion.ToString(CultureInfo.InvariantCulture), EdlInfoStage.Sahara));
            rows.Add(new EdlInfoRow(SaharaMinVersionKey,
                sahara.MinProtocolVersion.ToString(CultureInfo.InvariantCulture), EdlInfoStage.Sahara));
            rows.Add(new EdlInfoRow(SaharaMaxCommandLengthKey,
                $"0x{sahara.MaxCommandLength:X}", EdlInfoStage.Sahara));
            rows.Add(new EdlInfoRow(SaharaModeKey, DescribeSaharaMode(sahara.Mode), EdlInfoStage.Sahara));
        }

        if (snapshot.Identity is { } identity)
        {
            rows.Add(new EdlInfoRow(IdentitySerialKey,
                identity.SerialNumber.ToString(CultureInfo.InvariantCulture), EdlInfoStage.Sahara));
            rows.Add(new EdlInfoRow(IdentityHardwareKey,
                FormatHardwareId(identity.HardwareId), EdlInfoStage.Sahara));
            if (identity.SblVersion != 0)
            {
                rows.Add(new EdlInfoRow(IdentitySblVersionKey, FormatSblVersion(identity.SblVersion),
                    EdlInfoStage.Sahara));
            }

            if (identity.ChipIdentifierV3 != 0)
            {
                rows.Add(new EdlInfoRow(IdentityChipIdentifierKey,
                    $"0x{identity.ChipIdentifierV3:X8}", EdlInfoStage.Sahara));
            }

            if (identity.OemPublicKeyHashHex is { Length: > 0 } hash)
            {
                rows.Add(new EdlInfoRow(IdentityOemKeyHashKey, hash, EdlInfoStage.Sahara));
            }
        }

        if (snapshot.Capabilities is { } capabilities)
        {
            if (capabilities.FirehoseVersion is { Length: > 0 } version)
            {
                rows.Add(new EdlInfoRow(FirehoseVersionKey, version, EdlInfoStage.Firehose));
            }

            if (capabilities.ProgrammerFileName is { Length: > 0 } programmer)
            {
                rows.Add(new EdlInfoRow(ProgrammerKey, programmer, EdlInfoStage.Firehose));
            }

            rows.Add(new EdlInfoRow(StorageTypeKey, DescribeStorageType(capabilities.StorageType),
                EdlInfoStage.Firehose));

            if (capabilities.SectorSizeInBytes is { } sectorSize)
            {
                rows.Add(new EdlInfoRow(SectorSizeKey, $"{sectorSize} " + "B", EdlInfoStage.Firehose));
            }

            if (capabilities.MaxPayloadSizeToTargetInBytes is { } payload)
            {
                rows.Add(new EdlInfoRow(MaxPayloadKey, FormatBytes(payload), EdlInfoStage.Firehose));
            }
        }

        if (snapshot.Geometry is { } geometry)
        {
            rows.Add(new EdlInfoRow(SectorSizeKey, $"{geometry.SectorSize} " + "B", EdlInfoStage.Firehose));
            rows.Add(new EdlInfoRow(SectorCountKey,
                geometry.SectorCount.ToString(CultureInfo.InvariantCulture), EdlInfoStage.Firehose));
            rows.Add(new EdlInfoRow(CapacityKey, FormatBytes(geometry.CapacityBytes), EdlInfoStage.Firehose));
        }

        if (snapshot.Storage is { } storage)
        {
            if (storage.SerialNumber is { Length: > 0 } serial)
            {
                rows.Add(new EdlInfoRow(StorageSerialKey, serial, EdlInfoStage.Firehose));
            }

            if (storage.ManufacturerId is { Length: > 0 } manufacturer)
            {
                rows.Add(new EdlInfoRow(StorageManufacturerKey, manufacturer, EdlInfoStage.Firehose));
            }

            if (storage.FirmwareVersion is { Length: > 0 } firmware)
            {
                rows.Add(new EdlInfoRow(StorageFirmwareKey, firmware, EdlInfoStage.Firehose));
            }

            if (storage.PageSize is { } pageSize)
            {
                rows.Add(new EdlInfoRow(PageSizeKey, $"{pageSize} " + "B", EdlInfoStage.Firehose));
            }
        }

        if (snapshot.SlotLayout == EdlSlotLayout.Single)
        {
            rows.Add(new EdlInfoRow(SlotLayoutKey, SingleSlotLayoutText, EdlInfoStage.Firehose));
        }
        else if (snapshot.ActiveSlot is { Length: > 0 } slot)
        {
            rows.Add(new EdlInfoRow(SlotLayoutKey, DualSlotLayoutText, EdlInfoStage.Firehose));
            rows.Add(new EdlInfoRow(ActiveSlotKey, slot, EdlInfoStage.Firehose));
        }

        return rows;
    }

    /// <summary>
    /// Maps an observed protocol state to its readable name.
    /// <para>把已观测协议状态映射为可读名称。</para>
    /// </summary>
    /// <param name="state">The verified protocol. <para>已验证协议。</para></param>
    /// <returns>The readable protocol name. <para>可读协议名。</para></returns>
    internal static string DescribeProtocol(EdlProtocolState state) => state switch
    {
        EdlProtocolState.Sahara => "Sahara",
        EdlProtocolState.SaharaMemoryDebug => "Sahara · memory debug",
        EdlProtocolState.Firehose => "Firehose",
        EdlProtocolState.Streaming => "Streaming DLOAD",
        _ => string.Empty,
    };

    private static string DescribeSaharaMode(SaharaMode mode) => mode switch
    {
        SaharaMode.ImageTxPending => "ImageTxPending",
        SaharaMode.ImageTxComplete => "ImageTxComplete",
        SaharaMode.MemoryDebug => "MemoryDebug",
        SaharaMode.Command => "Command",
        _ => $"0x{(uint)mode:X}",
    };

    private static string DescribeStorageType(FirehoseStorageType type) => type switch
    {
        FirehoseStorageType.Ufs => "UFS",
        FirehoseStorageType.Emmc => "eMMC",
        FirehoseStorageType.Nand => "NAND",
        FirehoseStorageType.Nvme => "NVMe",
        FirehoseStorageType.Spinor => "SPI NOR",
        _ => type.ToString(),
    };

    private static string FormatHardwareId(ulong hardwareId)
    {
        var msmId = (uint)(hardwareId >> 32);
        var oemId = (ushort)((hardwareId >> 16) & 0xFFFF);
        var modelId = (ushort)(hardwareId & 0xFFFF);
        return msmId != 0
            ? string.Create(CultureInfo.InvariantCulture, $"0x{hardwareId:X16} (MSM 0x{msmId:X8}, OEM 0x{oemId:X4}, model 0x{modelId:X4})")
            : string.Create(CultureInfo.InvariantCulture, $"0x{hardwareId:X16}");
    }

    private static string FormatSblVersion(ulong sblVersion) =>
        sblVersion <= uint.MaxValue ? $"0x{sblVersion:X8}" : $"0x{sblVersion:X16}";

    /// <summary>
    /// Formats a byte count using binary units so capacities read the same way the rest of the UI shows sizes.
    /// <para>按二进制单位格式化字节数，使容量与界面其他位置的尺寸显示方式一致。</para>
    /// </summary>
    /// <param name="bytes">The byte count. <para>字节数。</para></param>
    /// <returns>The formatted size with its unit. <para>带单位的格式化尺寸。</para></returns>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} " + "B";
        }

        var value = (double)bytes;
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}