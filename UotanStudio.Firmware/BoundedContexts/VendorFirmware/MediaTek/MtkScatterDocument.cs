namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Parsed scatter metadata, not an authorized device operation or a resolved file manifest.
/// <para>已解析的 scatter 元数据；不是已授权的设备操作，也不是已解析的文件清单。</para></summary>
/// <param name="ConfigVersion">The declared platform configuration version.
/// <para>声明的平台配置版本。</para></param>
/// <param name="Platform">The declared platform token; not proof of device compatibility.
/// <para>声明的平台标识；不能证明设备兼容性。</para></param>
/// <param name="Project">The declared project name.
/// <para>声明的项目名称。</para></param>
/// <param name="PlatformFields">All platform fields, including uninterpreted extensions.
/// <para>全部平台字段，包含未作解释的扩展字段。</para></param>
/// <param name="Storages">Storage-specific layouts in source order.
/// <para>按源文件顺序排列的各存储布局。</para></param>
public sealed record MtkScatterDocument(string ConfigVersion, string Platform, string Project,
    IReadOnlyDictionary<string, string> PlatformFields, IReadOnlyList<MtkScatterStorage> Storages);

/// <summary>A storage-specific scatter layout, not discovered device geometry.
/// <para>某一存储的 scatter 布局；不是探测到的设备几何信息。</para></summary>
/// <param name="Name">The declared storage type.
/// <para>声明的存储类型。</para></param>
/// <param name="BootChannel">The declared boot channel.
/// <para>声明的启动通道。</para></param>
/// <param name="BlockSize">The scatter block-size hint; never use as the device logical sector size.
/// <para>scatter 的块大小提示；绝不能用作设备逻辑扇区大小。</para></param>
/// <param name="Fields">All storage configuration fields.
/// <para>全部存储配置字段。</para></param>
/// <param name="Partitions">Partition entries, including disabled and reserved entries.
/// <para>分区条目，包含已禁用与保留的条目。</para></param>
public sealed record MtkScatterStorage(string Name, string BootChannel, ulong BlockSize,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<MtkScatterPartition> Partitions);

/// <summary>Untrusted partition declarations. Addresses and file names are not resolved or executable.
/// <para>不可信的分区声明。地址与文件名未经解析，也不具备可执行性。</para></summary>
/// <param name="Index">The exact SYS index token.
/// <para>原样的 SYS 索引标记。</para></param>
/// <param name="Name">The partition name.
/// <para>分区名称。</para></param>
/// <param name="FileName">The literal file name, including the NONE sentinel.
/// <para>字面文件名，包含 NONE 哨兵值。</para></param>
/// <param name="IsDownload">The manifest's download flag, not user confirmation.
/// <para>清单中的下载标志，不代表用户确认。</para></param>
/// <param name="Type">The declared image type, not content detection.
/// <para>声明的镜像类型，不是内容探测结果。</para></param>
/// <param name="LinearStartAddress">The declared linear address.
/// <para>声明的线性起始地址。</para></param>
/// <param name="PhysicalStartAddress">The separate declared physical address.
/// <para>单独声明的物理起始地址。</para></param>
/// <param name="Size">The declared byte size; sentinel values remain uninterpreted.
/// <para>声明的字节大小；哨兵值保持未解释。</para></param>
/// <param name="Region">The literal storage region token.
/// <para>字面的存储区域标记。</para></param>
/// <param name="Storage">The literal hardware-storage token.
/// <para>字面的硬件存储标记。</para></param>
/// <param name="BoundaryCheck">The manifest boundary-check flag, never an instruction to disable host checks.
/// <para>清单的边界检查标志，绝不能作为关闭宿主检查的指令。</para></param>
/// <param name="IsReserved">The reserved-entry flag.
/// <para>保留条目标志。</para></param>
/// <param name="OperationType">The literal operation policy token.
/// <para>字面的操作策略标记。</para></param>
/// <param name="Fields">All fields, including extensions which callers must not silently treat as supported.
/// <para>全部字段，包含调用方不得默认视为受支持的扩展字段。</para></param>
public sealed record MtkScatterPartition(string Index, string Name, string FileName, bool IsDownload, string Type,
    ulong LinearStartAddress, ulong PhysicalStartAddress, ulong Size, string Region, string Storage,
    bool BoundaryCheck, bool IsReserved, string OperationType, IReadOnlyDictionary<string, string> Fields);