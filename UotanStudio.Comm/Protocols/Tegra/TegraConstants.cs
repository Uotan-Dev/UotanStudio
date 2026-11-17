namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Wire constants of the NVIDIA Tegra RCM (APX) protocol: the USB identity table, the RCM message framing
/// limits and the packed RCM version words.
/// <para>NVIDIA Tegra RCM（APX）协议的线上常量：USB 身份表、RCM 消息分帧上限与打包后的 RCM 版本字。</para>
/// </summary>
/// <remarks>
/// Every value mirrors <c>tegrarcm</c>'s <c>src/rcm.h</c> and <c>src/usb.h</c>. The product IDs are the full
/// 16-bit values a Tegra port may enumerate with, but tegrarcm matches only the low byte, so
/// <see cref="TryMapProductId"/> applies <see cref="ProductIdMask"/> and callers must not compare whole
/// product IDs.
/// <para>所有取值都对应 <c>tegrarcm</c> 的 <c>src/rcm.h</c> 与 <c>src/usb.h</c>。产品 ID 列出 Tegra 端口可能枚举
/// 出的完整 16 位取值，但 tegrarcm 只比较低字节，因此 <see cref="TryMapProductId"/> 会套用
/// <see cref="ProductIdMask"/>，调用方不得比较完整产品 ID。</para>
/// </remarks>
public static class TegraConstants
{
    /// <summary>
    /// NVIDIA USB vendor ID (<c>USB_VENID_NVIDIA</c>).
    /// <para>NVIDIA USB 厂商 ID（<c>USB_VENID_NVIDIA</c>）。</para>
    /// </summary>
    public const ushort NvidiaVendorId = 0x0955;

    /// <summary>
    /// Mask that reduces a product ID to the RCM device ID tegrarcm compares (<c>USB_DEVID_NVIDIA_*</c>).
    /// <para>把产品 ID 归约为 tegrarcm 比较的 RCM 设备 ID 的掩码（<c>USB_DEVID_NVIDIA_*</c>）。</para>
    /// </summary>
    public const ushort ProductIdMask = 0x00FF;

    /// <summary>
    /// RCM device ID of Tegra 2, the low byte of <see cref="Tegra20ProductId"/>.
    /// <para>Tegra 2 的 RCM 设备 ID，即 <see cref="Tegra20ProductId"/> 的低字节。</para>
    /// </summary>
    public const byte Tegra20DeviceId = 0x20;

    /// <summary>
    /// RCM device ID of Tegra 3, the low byte of <see cref="Tegra30ProductId"/>.
    /// <para>Tegra 3 的 RCM 设备 ID，即 <see cref="Tegra30ProductId"/> 的低字节。</para>
    /// </summary>
    public const byte Tegra30DeviceId = 0x30;

    /// <summary>
    /// RCM device ID of Tegra 4, the low byte of <see cref="Tegra114ProductId"/>.
    /// <para>Tegra 4 的 RCM 设备 ID，即 <see cref="Tegra114ProductId"/> 的低字节。</para>
    /// </summary>
    public const byte Tegra114DeviceId = 0x35;

    /// <summary>
    /// RCM device ID of Tegra K1, the low byte of <see cref="Tegra124ProductId"/>.
    /// <para>Tegra K1 的 RCM 设备 ID，即 <see cref="Tegra124ProductId"/> 的低字节。</para>
    /// </summary>
    public const byte Tegra124DeviceId = 0x40;

    /// <summary>
    /// Product ID of a Tegra 2 RCM port (<c>USB_DEVID_NVIDIA_TEGRA20</c> in full 16-bit form).
    /// <para>Tegra 2 RCM 端口的产品 ID（<c>USB_DEVID_NVIDIA_TEGRA20</c> 的完整 16 位形式）。</para>
    /// </summary>
    public const ushort Tegra20ProductId = 0x7820;

    /// <summary>
    /// Product ID of a Tegra 3 RCM port (<c>USB_DEVID_NVIDIA_TEGRA30</c> in full 16-bit form).
    /// <para>Tegra 3 RCM 端口的产品 ID（<c>USB_DEVID_NVIDIA_TEGRA30</c> 的完整 16 位形式）。</para>
    /// </summary>
    public const ushort Tegra30ProductId = 0x7330;

    /// <summary>
    /// Product ID of a Tegra 4 RCM port (<c>USB_DEVID_NVIDIA_TEGRA114</c> in full 16-bit form).
    /// <para>Tegra 4 RCM 端口的产品 ID（<c>USB_DEVID_NVIDIA_TEGRA114</c> 的完整 16 位形式）。</para>
    /// </summary>
    public const ushort Tegra114ProductId = 0x7135;

    /// <summary>
    /// Product ID of a Tegra K1 RCM port (<c>USB_DEVID_NVIDIA_TEGRA124</c> in full 16-bit form).
    /// <para>Tegra K1 RCM 端口的产品 ID（<c>USB_DEVID_NVIDIA_TEGRA124</c> 的完整 16 位形式）。</para>
    /// </summary>
    public const ushort Tegra124ProductId = 0x7140;

    /// <summary>
    /// Minimum length of a host RCM message (<c>RCM_MIN_MSG_LENGTH</c>), header and payload together, before
    /// the AES-block alignment padding is appended.
    /// <para>主机 RCM 消息的最小长度（<c>RCM_MIN_MSG_LENGTH</c>），即头部与负载之和，追加 AES 块对齐填充之前的下限。</para>
    /// </summary>
    public const int RcmMinimumMessageLength = 1024;

    /// <summary>
    /// AES block size in bytes, which is also the CMAC hash length (<c>RCM_AES_BLOCK_SIZE</c>).
    /// <para>AES 块大小（字节），同时也是 CMAC 摘要长度（<c>RCM_AES_BLOCK_SIZE</c>）。</para>
    /// </summary>
    public const int AesBlockSize = 16;

    /// <summary>
    /// RSA modulus and signature size in bytes (<c>RCM_RSA_MODULUS_SIZE</c>, 2048-bit key).
    /// <para>RSA 模数与签名长度（字节，<c>RCM_RSA_MODULUS_SIZE</c>，2048 位密钥）。</para>
    /// </summary>
    public const int RsaModulusSize = 256;

    /// <summary>
    /// Length of the <c>args</c> field every RCM header carries.
    /// <para>每个 RCM 头部携带的 <c>args</c> 字段长度。</para>
    /// </summary>
    public const int ArgsLength = 48;

    /// <summary>
    /// Length of the trailing <c>padding</c> field every RCM header carries.
    /// <para>每个 RCM 头部末尾携带的 <c>padding</c> 字段长度。</para>
    /// </summary>
    public const int HeaderPaddingLength = 16;

    /// <summary>
    /// First byte of a padding run; the remaining bytes are zero (<c>rcm_msg_pad</c>).
    /// <para>填充区的首字节；其余字节为零（<c>rcm_msg_pad</c>）。</para>
    /// </summary>
    public const byte PaddingFillByte = 0x80;

    /// <summary>
    /// Packed <c>RCM_VERSION_1</c> word (version 1.0) used by Tegra 2 and Tegra 3.
    /// <para>Tegra 2 与 Tegra 3 使用的打包 <c>RCM_VERSION_1</c> 字（版本 1.0）。</para>
    /// </summary>
    public const uint RcmVersion1 = 0x00010000;

    /// <summary>
    /// Packed <c>RCM_VERSION_35</c> word (version 0x35.1) used by Tegra 4.
    /// <para>Tegra 4 使用的打包 <c>RCM_VERSION_35</c> 字（版本 0x35.1）。</para>
    /// </summary>
    public const uint RcmVersion35 = 0x00350001;

    /// <summary>
    /// Packed <c>RCM_VERSION_40</c> word (version 0x40.1) used by Tegra K1.
    /// <para>Tegra K1 使用的打包 <c>RCM_VERSION_40</c> 字（版本 0x40.1）。</para>
    /// </summary>
    public const uint RcmVersion40 = 0x00400001;

    /// <summary>
    /// Packs a major and minor RCM version the way <c>NVBOOT_VERSION</c> does.
    /// <para>按 <c>NVBOOT_VERSION</c> 的方式打包主次 RCM 版本号。</para>
    /// </summary>
    /// <param name="major">The major version, truncated to 16 bits. <para>主版本号，截断到 16 位。</para></param>
    /// <param name="minor">The minor version, truncated to 16 bits. <para>次版本号，截断到 16 位。</para></param>
    /// <returns>The packed version word. <para>打包后的版本字。</para></returns>
    public static uint PackRcmVersion(uint major, uint minor) => ((major & 0xffff) << 16) | (minor & 0xffff);

    /// <summary>
    /// Extracts the major version from a packed RCM version word (<c>RCM_VERSION_MAJOR</c>).
    /// <para>从打包 RCM 版本字中取出主版本号（<c>RCM_VERSION_MAJOR</c>）。</para>
    /// </summary>
    /// <param name="version">The packed version word. <para>打包后的版本字。</para></param>
    /// <returns>The 16-bit major version. <para>16 位主版本号。</para></returns>
    public static uint GetRcmVersionMajor(uint version) => version >> 16;

    /// <summary>
    /// Extracts the minor version from a packed RCM version word (<c>RCM_VERSION_MINOR</c>).
    /// <para>从打包 RCM 版本字中取出次版本号（<c>RCM_VERSION_MINOR</c>）。</para>
    /// </summary>
    /// <param name="version">The packed version word. <para>打包后的版本字。</para></param>
    /// <returns>The 16-bit minor version. <para>16 位次版本号。</para></returns>
    public static uint GetRcmVersionMinor(uint version) => version & 0xffff;

    /// <summary>
    /// Reports whether a vendor ID is NVIDIA's (<see cref="NvidiaVendorId"/>).
    /// <para>报告某个厂商 ID 是否为 NVIDIA 的（<see cref="NvidiaVendorId"/>）。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor ID to test. <para>待判定的 USB 厂商 ID。</para></param>
    /// <returns><see langword="true"/> when the ID is NVIDIA's. <para>该 ID 属于 NVIDIA 时为 <see langword="true"/>。</para></returns>
    public static bool IsNvidiaVendor(ushort vendorId) => vendorId == NvidiaVendorId;

    /// <summary>
    /// Maps a USB product ID onto the Tegra generation whose RCM device ID its low byte equals, exactly as
    /// tegrarcm's <c>usb_match</c> does with <c>desc.idProduct &amp; 0xff</c>.
    /// <para>按低字节与 RCM 设备 ID 相等的关系把 USB 产品 ID 映射到 Tegra 代际，与 tegrarcm 的
    /// <c>usb_match</c> 使用 <c>desc.idProduct &amp; 0xff</c> 的判定一致。</para>
    /// </summary>
    /// <param name="productId">The USB product ID. <para>USB 产品 ID。</para></param>
    /// <param name="chip">Receives the mapped generation, or <see cref="TegraChip.Unknown"/> when the low byte
    /// matches none of them. <para>接收映射到的代际；低字节不匹配任何代际时为 <see cref="TegraChip.Unknown"/>。</para></param>
    /// <returns><see langword="true"/> when the product ID is an RCM port; <see langword="false"/> otherwise.
    /// <para>产品 ID 为 RCM 端口时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    public static bool TryMapProductId(ushort productId, out TegraChip chip)
    {
        chip = (byte)(productId & ProductIdMask) switch
        {
            Tegra20DeviceId => TegraChip.Tegra20,
            Tegra30DeviceId => TegraChip.Tegra30,
            Tegra114DeviceId => TegraChip.Tegra114,
            Tegra124DeviceId => TegraChip.Tegra124,
            _ => TegraChip.Unknown,
        };

        return chip != TegraChip.Unknown;
    }

    /// <summary>
    /// Reports whether a product ID belongs to a Tegra RCM port, without naming the generation.
    /// <para>报告产品 ID 是否属于 Tegra RCM 端口，不指明代际。</para>
    /// </summary>
    /// <param name="productId">The USB product ID. <para>USB 产品 ID。</para></param>
    /// <returns><see langword="true"/> when the low byte is one of the four RCM device IDs.
    /// <para>低字节为四个 RCM 设备 ID 之一时为 <see langword="true"/>。</para></returns>
    public static bool IsRcmProductId(ushort productId) => TryMapProductId(productId, out _);

    /// <summary>
    /// Reports whether a vendor/product pair is a Tegra RCM endpoint: NVIDIA's vendor ID plus an RCM product ID.
    /// <para>报告某个厂商/产品 ID 组合是否为 Tegra RCM 端点：NVIDIA 厂商 ID 加 RCM 产品 ID。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor ID. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product ID. <para>USB 产品 ID。</para></param>
    /// <returns><see langword="true"/> only when both parts match; other NVIDIA products, such as a Tegra
    /// fastboot port, are rejected. <para>仅当两部分都匹配时为 <see langword="true"/>；其它 NVIDIA 产品（例如
    /// Tegra fastboot 端口）会被拒绝。</para></returns>
    public static bool IsTegraRcmEndpoint(ushort vendorId, ushort productId)
        => IsNvidiaVendor(vendorId) && IsRcmProductId(productId);

    /// <summary>
    /// Reports whether an operating mode permits an RCM download, the same five modes tegrarcm accepts before
    /// downloading a BCT and bootloader.
    /// <para>报告某个操作模式是否允许 RCM 下载，即 tegrarcm 在下载 BCT 与 bootloader 之前接受的同五个模式。</para>
    /// </summary>
    /// <param name="operatingMode">The operating mode read from the nv3p platform info.
    /// <para>从 nv3p 平台信息读出的操作模式。</para></param>
    /// <returns><see langword="true"/> for pre-production, developer, ODM secure, ODM open and ODM secure with
    /// PKC modes. <para>预生产、开发者、ODM 安全、ODM 开放与带 PKC 的 ODM 安全模式为 <see langword="true"/>。</para></returns>
    public static bool IsFlashableOperatingMode(uint operatingMode) =>
        (TegraOperatingMode)operatingMode is
            TegraOperatingMode.PreProduction or
            TegraOperatingMode.Developer or
            TegraOperatingMode.OdmSecure or
            TegraOperatingMode.OdmOpen or
            TegraOperatingMode.OdmSecurePkc;
}

/// <summary>
/// Recovery mode commands a host sends in the RCM header's <c>opcode</c> field (<c>RCM_CMD_*</c>).
/// <para>主机在 RCM 头部 <c>opcode</c> 字段发送的恢复模式命令（<c>RCM_CMD_*</c>）。</para>
/// </summary>
/// <remarks>
/// The wire field is a 32-bit word; values outside this enum are preserved through a cast so a device reply
/// that names an unknown command still round-trips.
/// <para>线上字段为 32 位字；本枚举之外的取值经强制转换原样保留，使指明未知命令的设备应答仍可原样往返。</para>
/// </remarks>
public enum TegraRcmOpcode : uint
{
    /// <summary>
    /// No command (<c>RCM_CMD_NONE</c>).
    /// <para>无命令（<c>RCM_CMD_NONE</c>）。</para>
    /// </summary>
    None = 0x0,

    /// <summary>
    /// Sync handshake with the boot ROM (<c>RCM_CMD_SYNC</c>).
    /// <para>与 boot ROM 的同步握手（<c>RCM_CMD_SYNC</c>）。</para>
    /// </summary>
    Sync = 0x1,

    /// <summary>
    /// Download the miniloader into the boot ROM (<c>RCM_CMD_DL_MINILOADER</c>).
    /// <para>把 miniloader 下载到 boot ROM（<c>RCM_CMD_DL_MINILOADER</c>）。</para>
    /// </summary>
    DownloadMiniloader = 0x4,

    /// <summary>
    /// Query the boot ROM version (<c>RCM_CMD_QUERY_BR_VERSION</c>).
    /// <para>查询 boot ROM 版本（<c>RCM_CMD_QUERY_BR_VERSION</c>）。</para>
    /// </summary>
    QueryBootRomVersion = 0x5,

    /// <summary>
    /// Query the RCM protocol version (<c>RCM_CMD_QUERY_RCM_VERSION</c>).
    /// <para>查询 RCM 协议版本（<c>RCM_CMD_QUERY_RCM_VERSION</c>）。</para>
    /// </summary>
    QueryRcmVersion = 0x6,

    /// <summary>
    /// Query the boot device version (<c>RCM_CMD_QUERY_BD_VERSION</c>).
    /// <para>查询启动设备版本（<c>RCM_CMD_QUERY_BD_VERSION</c>）。</para>
    /// </summary>
    QueryBootDeviceVersion = 0x7,
}

/// <summary>
/// Security operating modes the device reports in nv3p platform info (<c>RCM_OP_MODE_*</c>).
/// <para>设备在 nv3p 平台信息中报告的安全操作模式（<c>RCM_OP_MODE_*</c>）。</para>
/// </summary>
public enum TegraOperatingMode : uint
{
    /// <summary>
    /// Pre-production device (<c>RCM_OP_MODE_PRE_PRODUCTION</c>).
    /// <para>预生产设备（<c>RCM_OP_MODE_PRE_PRODUCTION</c>）。</para>
    /// </summary>
    PreProduction = 0x1,

    /// <summary>
    /// Developer device with an unlocked boot ROM (<c>RCM_OP_MODE_DEVEL</c>).
    /// <para>boot ROM 未锁定的开发者设备（<c>RCM_OP_MODE_DEVEL</c>）。</para>
    /// </summary>
    Developer = 0x3,

    /// <summary>
    /// ODM secure mode, which accepts only signed blobs (<c>RCM_OP_MODE_ODM_SECURE</c>).
    /// <para>ODM 安全模式，只接受已签名的 blob（<c>RCM_OP_MODE_ODM_SECURE</c>）。</para>
    /// </summary>
    OdmSecure = 0x4,

    /// <summary>
    /// ODM open mode (<c>RCM_OP_MODE_ODM_OPEN</c>).
    /// <para>ODM 开放模式（<c>RCM_OP_MODE_ODM_OPEN</c>）。</para>
    /// </summary>
    OdmOpen = 0x5,

    /// <summary>
    /// ODM secure mode with public-key cryptography (<c>RCM_OP_MODE_ODM_SECURE_PKC</c>).
    /// <para>使用公钥密码的 ODM 安全模式（<c>RCM_OP_MODE_ODM_SECURE_PKC</c>）。</para>
    /// </summary>
    OdmSecurePkc = 0x6,
}
/// <summary>
/// Commands a host sends over nv3p once the miniloader is running (<c>NV3P_CMD_*</c>).
/// <para>miniloader 运行之后主机经 nv3p 发送的命令（<c>NV3P_CMD_*</c>）。</para>
/// </summary>
public enum TegraNv3pCommand : uint
{
    /// <summary>
    /// Read the platform information (<c>NV3P_CMD_GET_PLATFORM_INFO</c>).
    /// <para>读取平台信息（<c>NV3P_CMD_GET_PLATFORM_INFO</c>）。</para>
    /// </summary>
    GetPlatformInfo = 0x01,

    /// <summary>
    /// Read the BCT that currently sits on the boot device (<c>NV3P_CMD_GET_BCT</c>).
    /// <para>读取引导设备上当前的 BCT（<c>NV3P_CMD_GET_BCT</c>）。</para>
    /// </summary>
    GetBct = 0x02,

    /// <summary>
    /// Download the BCT (<c>NV3P_CMD_DL_BCT</c>).
    /// <para>下载 BCT（<c>NV3P_CMD_DL_BCT</c>）。</para>
    /// </summary>
    DownloadBct = 0x04,

    /// <summary>
    /// Download a bootloader binary (<c>NV3P_CMD_DL_BL</c>).
    /// <para>下载 bootloader 二进制（<c>NV3P_CMD_DL_BL</c>）。</para>
    /// </summary>
    DownloadBootloader = 0x06,

    /// <summary>
    /// Read the status of the previous command (<c>NV3P_CMD_STATUS</c>).
    /// <para>读取上一条命令的状态（<c>NV3P_CMD_STATUS</c>）。</para>
    /// </summary>
    Status = 0x0a,
}

/// <summary>
/// Packet types an nv3p frame header names (<c>NV3P_PACKET_TYPE_*</c>).
/// <para>nv3p 帧头声明的包类型（<c>NV3P_PACKET_TYPE_*</c>）。</para>
/// </summary>
public enum TegraNv3pPacketType : uint
{
    /// <summary>A command packet. <para>命令包。</para></summary>
    Command = 0x1,

    /// <summary>A payload packet following a download command. <para>跟随下载命令的负载包。</para></summary>
    Data = 0x2,

    /// <summary>An encrypted payload packet. <para>加密负载包。</para></summary>
    Encrypted = 0x3,

    /// <summary>A positive acknowledgement. <para>肯定应答。</para></summary>
    Ack = 0x4,

    /// <summary>A negative acknowledgement carrying a four-byte code. <para>携带四字节码的否定应答。</para></summary>
    Nack = 0x5,
}

/// <summary>
/// Wire constants of the nv3p protocol the miniloader speaks, mirroring <c>tegrarcm</c>'s <c>src/nv3p.h</c>.
/// <para>miniloader 使用的 nv3p 协议线上常量，对应 <c>tegrarcm</c> 的 <c>src/nv3p.h</c>。</para>
/// </summary>
public static class TegraNv3pConstants
{
    /// <summary>
    /// The single nv3p protocol version (<c>NV3P_VERSION</c>).
    /// <para>唯一的 nv3p 协议版本（<c>NV3P_VERSION</c>）。</para>
    /// </summary>
    public const uint ProtocolVersion = 1;

    /// <summary>
    /// Header size of a basic packet: packet type, sequence and checksum (<c>NV3P_PACKET_SIZE_BASIC</c>).
    /// <para>基础包头长度：包类型、序号与校验和（<c>NV3P_PACKET_SIZE_BASIC</c>）。</para>
    /// </summary>
    public const int BasicPacketSize = 12;

    /// <summary>
    /// Extra bytes a command packet carries after the basic header (<c>NV3P_PACKET_SIZE_COMMAND</c>).
    /// <para>命令包在基础包头之后额外携带的字节数（<c>NV3P_PACKET_SIZE_COMMAND</c>）。</para>
    /// </summary>
    public const int CommandPacketSize = 8;

    /// <summary>
    /// Extra bytes a data packet carries after the basic header (<c>NV3P_PACKET_SIZE_DATA</c>).
    /// <para>数据包在基础包头之后额外携带的字节数（<c>NV3P_PACKET_SIZE_DATA</c>）。</para>
    /// </summary>
    public const int DataPacketSize = 4;

    /// <summary>
    /// Extra bytes an encrypted packet carries after the basic header (<c>NV3P_PACKET_SIZE_ENCRYPTED</c>).
    /// <para>加密包在基础包头之后额外携带的字节数（<c>NV3P_PACKET_SIZE_ENCRYPTED</c>）。</para>
    /// </summary>
    public const int EncryptedPacketSize = 4;

    /// <summary>
    /// Header-only footer size (<c>NV3P_PACKET_SIZE_FOOTER</c>).
    /// <para>仅含头部的尾部长度（<c>NV3P_PACKET_SIZE_FOOTER</c>）。</para>
    /// </summary>
    public const int FooterPacketSize = 4;

    /// <summary>
    /// Bytes an acknowledgement carries beyond the header (<c>NV3P_PACKET_SIZE_ACK</c>).
    /// <para>肯定应答在头部之外携带的字节数（<c>NV3P_PACKET_SIZE_ACK</c>）。</para>
    /// </summary>
    public const int AckPacketSize = 0;

    /// <summary>
    /// Bytes a negative acknowledgement carries beyond the header, which is the four-byte code
    /// (<c>NV3P_PACKET_SIZE_NACK</c>).
    /// <para>否定应答在头部之外携带的字节数，即四字节码（<c>NV3P_PACKET_SIZE_NACK</c>）。</para>
    /// </summary>
    public const int NackPacketSize = 4;

    /// <summary>
    /// Largest payload a single data packet may carry; the layer rejects anything above it instead of trusting the
    /// device-declared length.
    /// <para>单个数据包可携带的最大负载；超过该值的长度一律拒绝，不信任设备声明的长度。</para>
    /// </summary>
    public const int MaximumDataPayloadLength = 1024 * 1024;

    /// <summary>
    /// Status string limit the device uses in <c>NV3P_CMD_STATUS</c> replies (<c>NV3P_STRING_MAX</c>).
    /// <para>设备在 <c>NV3P_CMD_STATUS</c> 应答中使用的状态串上限（<c>NV3P_STRING_MAX</c>）。</para>
    /// </summary>
    public const int StatusStringLength = 32;

    /// <summary>
    /// Accumulates the nv3p checksum over a buffer: a byte-wise sum, as <c>tegrarcm</c> computes it. A received
    /// header is valid only when the received checksum plus this sum is zero.
    /// <para>对缓冲区累加 nv3p 校验和：逐字节求和，与 <c>tegrarcm</c> 的算法一致。只有当接收到的校验和与本和相加为零时，
    /// 收到的头部才有效。</para>
    /// </summary>
    /// <param name="buffer">The bytes to sum. <para>要求和的字节。</para></param>
    /// <returns>The 32-bit wrapping sum. <para>32 位回绕求和结果。</para></returns>
    public static uint ComputeChecksum(ReadOnlySpan<byte> buffer)
    {
        var sum = 0u;
        foreach (var value in buffer)
        {
            sum = unchecked(sum + value);
        }

        return sum;
    }
}