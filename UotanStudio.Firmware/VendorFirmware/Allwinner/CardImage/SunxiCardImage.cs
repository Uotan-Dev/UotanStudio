namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// One partition of an Allwinner SD-card image, taken from the validated GPT entry array together with the
/// role its payload actually starts with.
/// <para>全志 SD 卡镜像的一个分区，取自已校验的 GPT 条目数组，并附带其负载实际起始处的角色。</para>
/// </summary>
/// <param name="Index">Zero-based slot position in the GPT entry array. <para>GPT 条目数组中从零开始的槽位位置。</para></param>
/// <param name="Name">GPT partition name, which the vendor boot flow resolves by name. <para>GPT 分区名；厂商引导流程按名称解析。</para></param>
/// <param name="FirstLba">First logical block of the partition. <para>分区的起始逻辑块。</para></param>
/// <param name="LastLba">Last logical block, inclusive. <para>结束逻辑块（闭区间）。</para></param>
/// <param name="Attributes">Raw 64-bit GPT attribute flags. <para>原始 64 位 GPT 属性标志。</para></param>
/// <param name="TypeId">Partition type GUID. <para>分区类型 GUID。</para></param>
/// <param name="Id">Unique partition GUID. <para>唯一分区 GUID。</para></param>
/// <param name="ByteOffset">Absolute byte offset of the partition inside the image. <para>分区在镜像内的绝对字节偏移。</para></param>
/// <param name="ByteLength">Byte length covering the whole partition slot. <para>覆盖整个分区槽位的字节长度。</para></param>
/// <param name="ContentLength">Byte length of the significant payload: the slot minus the trailing NUL fill the vendor
/// flasher never writes. <para>有效负载的字节长度：即槽位长度减去厂商烧录流程从不写入的尾部 NUL 填充。</para></param>
/// <param name="Payload">Role observed in the leading bytes of the payload. <para>由负载起始字节观察到的角色。</para></param>
/// <param name="ExportName">File name the partition is exported under, mirroring the vendor <c>.fex</c> name.
/// <para>分区导出时使用的文件名，沿用厂商 <c>.fex</c> 命名。</para></param>
public sealed record SunxiCardPartition(int Index, string Name, ulong FirstLba, ulong LastLba, ulong Attributes,
    Guid TypeId, Guid Id, long ByteOffset, long ByteLength, long ContentLength, SunxiCardPayload Payload,
    string ExportName);

/// <summary>
/// Kind of content detected at the start of a card-image partition payload.
/// <para>卡镜像分区负载起始处识别出的内容种类。</para>
/// </summary>
public enum SunxiCardPayload
{
    /// <summary>Nothing was recognized. <para>未识别出内容。</para></summary>
    Unknown,

    /// <summary>FAT file system with a <c>FAT16</c> or <c>FAT32</c> label, used for <c>boot-resource</c>.
    /// <para>带 <c>FAT16</c> 或 <c>FAT32</c> 标签的 FAT 文件系统，用于 <c>boot-resource</c>。</para></summary>
    FatVolume,

    /// <summary>U-Boot environment block: CRC word, flags byte, then NUL-separated <c>key=value</c> text.
    /// <para>U-Boot 环境块：CRC 字、标志字节，随后是以 NUL 分隔的 <c>key=value</c> 文本。</para></summary>
    Environment,

    /// <summary>Android boot image whose header the vendor kernel path reuses. <para>Android 启动镜像；厂商内核路径复用了该头部。</para></summary>
    AndroidBootImage,

    /// <summary>SquashFS read-only root file system. <para>SquashFS 只读根文件系统。</para></summary>
    SquashFs,

    /// <summary>ELF executable image. <para>ELF 可执行镜像。</para></summary>
    ElfImage,

    /// <summary>eGON first-stage loader, recognized by its <c>eGON.BT0</c> stamp.
    /// <para>eGON 一级引导程序，由其 <c>eGON.BT0</c> 戳记识别。</para></summary>
    EgonBootloader,

    /// <summary>Linux <c>Image</c> carrying the RISC-V boot protocol magic.
    /// <para>携带 RISC-V 引导协议魔数的 Linux <c>Image</c>。</para></summary>
    LinuxImage,

    /// <summary>OpenSBI RISC-V firmware, recognized by its embedded <c>OpenSBI</c> banner.
    /// <para>OpenSBI RISC-V 固件，由内嵌的 <c>OpenSBI</c> 横幅识别。</para></summary>
    OpenSbiFirmware,

    /// <summary>Das U-Boot binary, recognized by its embedded <c>U-Boot</c> version banner.
    /// <para>Das U-Boot 二进制，由内嵌的 <c>U-Boot</c> 版本横幅识别。</para></summary>
    UBootBinary,

    /// <summary>Flattened device tree blob. <para>扁平设备树 blob。</para></summary>
    FlattenedDeviceTree,

    /// <summary>ASCII placeholder naming another file, which the vendor packer writes for an empty partition.
    /// <para>纯 ASCII 占位文本，指向另一个文件；厂商打包器为不存在的分区写入该内容。</para></summary>
    Placeholder,

    /// <summary>All-zero slot. <para>全零槽位。</para></summary>
    Empty
}

/// <summary>
/// Header of the region that precedes the first usable block: the vendor MBR, both GPT copies, the eGON
/// first-stage loader and the packed boot package.
/// <para>首个可用块之前区域的头部：厂商 MBR、两份 GPT、eGON 一级引导程序与打包启动包。</para>
/// </summary>
/// <param name="Boot0Offset">Byte offset of the eGON first-stage loader. <para>eGON 一级引导程序的字节偏移。</para></param>
/// <param name="Boot0">Parsed eGON header, or <see langword="null"/> when that offset carries no loader.
/// <para>解析出的 eGON 头部；该偏移没有引导程序时为 <see langword="null"/>。</para></param>
/// <param name="BootPackageOffset">Byte offset of the packed boot package, or <see langword="null"/> when absent.
/// <para>打包启动包的字节偏移；不存在时为 <see langword="null"/>。</para></param>
/// <param name="BootPackage">Parsed pack header, or <see langword="null"/> when absent. <para>解析出的包头部；不存在时为 <see langword="null"/>。</para></param>
/// <param name="BootPackageLength">Byte length of the boot package region. <para>启动包区域的字节长度。</para></param>
public sealed record SunxiCardBootRegion(long Boot0Offset, SunxiEgonHeader? Boot0, long? BootPackageOffset,
    SunxiPackageHeader? BootPackage, long BootPackageLength);

/// <summary>
/// The 56-byte <c>boot_file_head</c> of an eGON first-stage loader, in the version-0 layout the D1 boot ROM accepts.
/// <para>eGON 一级引导程序的 56 字节 <c>boot_file_head</c>，采用 D1 引导 ROM 接受的版本 0 布局。</para>
/// </summary>
/// <param name="Instruction">First executed instruction word; <c>0x3C80006F</c> is the RISC-V <c>j</c> to the loader entry.
/// <para>首条执行指令字；<c>0x3C80006F</c> 为跳转到引导程序入口的 RISC-V <c>j</c> 指令。</para></param>
/// <param name="Stamp">The eight <c>eGON.BT0</c> stamp characters at offset 4. <para>偏移 4 处的八个 <c>eGON.BT0</c> 戳记字符。</para></param>
/// <param name="ChecksumWord">Word at offset 0x0C. The vendor flow does not document its coverage — a plain 32-bit
/// word sum over the loader matches neither with nor without the field zeroed — so it is reported uninterpreted.
/// <para>偏移 0x0C 的字。厂商流程未说明其覆盖范围——对引导程序做 32 位字求和，无论是否将该字段清零都不匹配——
/// 因此原样报告。</para></param>
/// <param name="Version">Header layout version word, normally <c>0x10000</c>. <para>头部布局版本字，通常为 <c>0x10000</c>。</para></param>
/// <param name="Platform">Platform tag word. <para>平台标记字。</para></param>
/// <param name="PlatformText">Platform tag read as four big-endian characters, or an empty string when it is not printable.
/// <para>按四个大端字符读出的平台标记；不可打印时为空字符串。</para></param>
/// <param name="DeviceTreeWord">Word at offset 0x20, observed as <c>0x20000</c>; reported uninterpreted.
/// <para>偏移 0x20 的字，实测为 <c>0x20000</c>；原样报告。</para></param>
/// <param name="VersionText">Version string the vendor tooling writes at offset 0x30, for example <c>4.0</c>.
/// <para>厂商工具在偏移 0x30 写入的版本字符串，例如 <c>4.0</c>。</para></param>
public sealed record SunxiEgonHeader(uint Instruction, string Stamp, uint ChecksumWord, uint Version, uint Platform,
    string PlatformText, uint DeviceTreeWord, string VersionText)
{
    /// <summary>Constant bytes every eGON first-stage loader carries at offset 4.
    /// <para>每个 eGON 一级引导程序在偏移 4 携带的常量字节。</para></summary>
    public static ReadOnlySpan<byte> Magic => "eGON.BT0"u8;
}

/// <summary>
/// One item record of a <c>sunxi-package</c> boot package: a fixed 1024-byte record carrying a name, a type, the
/// stored length and the absolute offset of the payload inside the package.
/// <para><c>sunxi-package</c> 启动包的一条条目记录：固定 1024 字节，携带名称、类型、存储长度与负载在包内的绝对偏移。</para>
/// </summary>
/// <param name="Index">Zero-based record position. <para>从零开始的记录序号。</para></param>
/// <param name="Name">Item name, for example <c>opensbi</c> or <c>u-boot</c>. <para>条目名，例如 <c>opensbi</c> 或 <c>u-boot</c>。</para></param>
/// <param name="Type">Item type word; <c>3</c> is observed for a loadable binary. <para>条目类型字；可装载二进制观察到为 <c>3</c>。</para></param>
/// <param name="Offset">Absolute offset of the stored payload inside the image. The record itself stores this value
/// relative to the package start, which the parser adds to the package base before reporting.
/// <para>已存储负载在镜像内的绝对偏移。记录本身存储的是相对包起点的值，解析器会先加上包起点再报告。</para></param>
/// <param name="Length">Stored payload length in bytes. <para>已存储负载的字节长度。</para></param>
/// <param name="RunningAddress">Address the boot ROM loads the payload at. <para>引导 ROM 装载该负载的地址。</para></param>
/// <param name="Content">Content kind observed at the payload. <para>在负载处观察到的内容种类。</para></param>
public sealed record SunxiPackageItem(int Index, string Name, uint Type, long Offset, long Length,
    uint RunningAddress, SunxiCardPayload Content);

/// <summary>
/// Header and item table of a <c>sunxi-package</c> boot package.
/// <para><c>sunxi-package</c> 启动包的头与条目表。</para>
/// </summary>
/// <param name="Marker">Secondary marker word read at offset 20; observed as <c>0xA78ACD0B</c>.
/// <para>在偏移 20 读取的次级标记字；实测为 <c>0xA78ACD0B</c>。</para></param>
/// <param name="Checksum">Checksum word read at offset 16, reported uninterpreted. <para>在偏移 16 读取的校验字，原样报告。</para></param>
/// <param name="ItemCount">Declared item count. <para>声明的条目数。</para></param>
/// <param name="TotalLength">Declared total length of the package in bytes. <para>声明的包总长度（字节）。</para></param>
/// <param name="Items">Validated item records in table order. <para>按表顺序排列的已校验条目记录。</para></param>
public sealed record SunxiPackageHeader(uint Marker, uint Checksum, uint ItemCount, uint TotalLength,
    IReadOnlyList<SunxiPackageItem> Items);

/// <summary>
/// The vendor U-Boot environment block stored in the <c>env</c> and <c>env-redund</c> partitions.
/// <para><c>env</c> 与 <c>env-redund</c> 分区中存储的厂商 U-Boot 环境块。</para>
/// </summary>
/// <param name="Crc32">Stored CRC-32 word, computed by the vendor over every byte after the five-byte prefix.
/// <para>存储的 CRC-32 字；厂商对五字节前缀之后的全部字节计算。</para></param>
/// <param name="Flags">Flags byte; <c>0x01</c> marks an initialized environment. <para>标志字节；<c>0x01</c> 标记环境已初始化。</para></param>
/// <param name="CrcMatches">Whether the stored word equals the CRC-32 the vendor computes over the full 0x20000-byte
/// area after the five-byte prefix. The redundant copy is byte-identical, so it validates the same way.
/// <para>存储的字是否等于厂商对五字节前缀之后完整 0x20000 字节区域计算的 CRC-32。冗余副本逐字节相同，因此同样通过校验。</para></param>
/// <param name="Variables">Variable name to value pairs in stored order. <para>按存储顺序排列的变量名与值。</para></param>
public sealed record SunxiEnvironmentBlock(uint Crc32, byte Flags, bool CrcMatches,
    IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>Byte count the vendor package stores for a full environment area. <para>厂商打包为完整环境区存储的字节数。</para></summary>
    public const int StorageLength = 0x20000;
}

/// <summary>
/// One file of the <c>boot-resource</c> FAT volume, which carries the boot logo the D1 boot chain displays
/// before the kernel starts.
/// <para><c>boot-resource</c> FAT 卷中的一个文件；该卷承载 D1 引导链在内核启动前显示的启动 logo。</para>
/// </summary>
/// <param name="LongName">Long file name when the directory entry chain supplied one, otherwise empty.
/// <para>目录项链提供时的长文件名，否则为空字符串。</para></param>
/// <param name="ShortName">8.3 name assembled from the directory entry. <para>由目录项拼装的 8.3 名称。</para></param>
/// <param name="Attributes">Raw FAT attribute byte. <para>原始 FAT 属性字节。</para></param>
/// <param name="FirstCluster">First cluster of the file. <para>文件的起始簇。</para></param>
/// <param name="SizeBytes">Declared file length in bytes. <para>声明的文件字节长度。</para></param>
/// <param name="Offset">Absolute byte offset inside the image, or <c>-1</c> when the first cluster is outside the partition.
/// <para>镜像内的绝对字节偏移；首簇落在分区之外时为 <c>-1</c>。</para></param>
public sealed record SunxiBootResourceFile(string LongName, string ShortName, byte Attributes, int FirstCluster,
    long SizeBytes, long Offset);

/// <summary>
/// A parsed Allwinner SD-card image: the GPT partition set plus the pre-partition boot region, the U-Boot
/// environments and the <c>boot-resource</c> file list.
/// <para>解析后的全志 SD 卡镜像：GPT 分区集合，外加分区之前的引导区、U-Boot 环境与 <c>boot-resource</c> 文件清单。</para>
/// </summary>
/// <param name="BlockSize">Logical block size the GPT was read with. <para>读取 GPT 所用的逻辑块大小。</para></param>
/// <param name="BlockCount">Device capacity in logical blocks. <para>以逻辑块计的设备容量。</para></param>
/// <param name="DiskId">GPT disk identifier. <para>GPT 磁盘标识符。</para></param>
/// <param name="FirstUsableLba">First usable logical block declared by the GPT headers. <para>GPT 头部声明的首个可用逻辑块。</para></param>
/// <param name="LastUsableLba">Last usable logical block declared by the GPT headers. <para>GPT 头部声明的最后可用逻辑块。</para></param>
/// <param name="Partitions">Validated partitions in table order. <para>按表顺序排列的已校验分区。</para></param>
/// <param name="BootRegion">Pre-partition boot region. <para>分区之前的引导区。</para></param>
/// <param name="BootImage">Segment layout of the partition the vendor environment boots, or <see langword="null"/> when no partition carries an Android-style boot header.
/// <para>厂商环境所引导分区的段布局；没有任何分区携带 Android 风格启动头时为 <see langword="null"/>。</para></param>
/// <param name="Environments">U-Boot environment blocks keyed by partition name. <para>按分区名索引的 U-Boot 环境块。</para></param>
/// <param name="BootResourceFiles"><c>boot-resource</c> file list; empty when the volume is absent or unreadable.
/// <para><c>boot-resource</c> 文件清单；卷不存在或不可读时为空。</para></param>
public sealed record SunxiCardImage(int BlockSize, ulong BlockCount, Guid DiskId, ulong FirstUsableLba,
    ulong LastUsableLba, IReadOnlyList<SunxiCardPartition> Partitions, SunxiCardBootRegion BootRegion,
    SunxiBootImage? BootImage, IReadOnlyDictionary<string, SunxiEnvironmentBlock> Environments,
    IReadOnlyList<SunxiBootResourceFile> BootResourceFiles);
/// <summary>
/// Segment layout of a vendor boot partition. The vendor kernel path reuses the Android boot-image header, so the
/// segment sizes below are read from the same offsets, while the payload at <c>page_size</c> is a RISC-V
/// <c>Image</c> rather than an Android kernel blob.
/// <para>厂商 boot 分区的段布局。厂商内核路径复用了 Android 启动镜像头部，因此下列段长度读取自相同偏移；
/// 而 <c>page_size</c> 处的负载是 RISC-V <c>Image</c>，并非 Android 内核 blob。</para>
/// </summary>
/// <param name="PartitionName">Partition the header was read from. <para>读取头部所用的分区。</para></param>
/// <param name="PageSize">Declared page size that each segment is padded to. <para>各段填充到的声明页大小。</para></param>
/// <param name="KernelSize">Declared kernel segment length. <para>声明的内核段长度。</para></param>
/// <param name="RamdiskSize">Declared ramdisk segment length; <c>0</c> when none is embedded.
/// <para>声明的 ramdisk 段长度；未内嵌时为 <c>0</c>。</para></param>
/// <param name="SecondSize">Declared second-stage segment length. <para>声明的二级段长度。</para></param>
/// <param name="DeviceTreeSize">Declared device tree segment length; <c>0</c> when none is embedded.
/// <para>声明的设备树段长度；未内嵌时为 <c>0</c>。</para></param>
/// <param name="KernelAddress">Load address of the kernel segment. <para>内核段的装载地址。</para></param>
/// <param name="RamdiskAddress">Load address of the ramdisk segment. <para>ramdisk 段的装载地址。</para></param>
/// <param name="SecondAddress">Load address of the second-stage segment. <para>二级段的装载地址。</para></param>
/// <param name="TagsAddress">Load address of the tag list. <para>tag 列表的装载地址。</para></param>
/// <param name="Name">Product name stored in the header. <para>头部中存储的产品名。</para></param>
/// <param name="CommandLine">Kernel command line stored in the header; empty when the header declares none.
/// <para>头部中存储的内核命令行；头部未声明时为空字符串。</para></param>
/// <param name="KernelContent">Content kind observed where the kernel segment starts.
/// <para>在内核段起始处观察到的内容种类。</para></param>
public sealed record SunxiBootImage(string PartitionName, uint PageSize, uint KernelSize, uint RamdiskSize,
    uint SecondSize, uint DeviceTreeSize, uint KernelAddress, uint RamdiskAddress, uint SecondAddress,
    uint TagsAddress, string Name, string CommandLine, SunxiCardPayload KernelContent);