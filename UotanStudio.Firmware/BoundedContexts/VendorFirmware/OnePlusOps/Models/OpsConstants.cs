namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Models;

/// <summary>
/// Fixed sizes and the signature that define the OPS package layout. Every value is a compile-time
/// constant shared by the reader, the entry walker and the repacker, so layout changes stay in one
/// place.
/// <para>定义 OPS 包布局的固定长度与签名。所有值都是编译期常量，由读取器、条目遍历和重新打包
/// 流程共用，布局调整只需集中修改一处。</para>
/// </summary>
public static class OpsConstants
{
    /// <summary>Four-byte ASCII package signature, <c>O</c> <c>P</c> <c>S</c> followed by a NUL
    /// byte. <para>4 字节 ASCII 包签名，即 <c>O</c> <c>P</c> <c>S</c> 加一个 NUL 字节。</para></summary>
    public const string OPS_MAGIC = "OPS\0";

    /// <summary>Total declared OPS header length in bytes, 24 of which carry field values and the
    /// remainder is zero padding. <para>OPS 头部声明的总长度（字节），其中 24 字节承载字段值，
    /// 其余为 NUL 填充。</para></summary>
    public const int HEADER_SIZE = 52;

    /// <summary>Length of one entry-table record in bytes: three little-endian 32-bit values plus
    /// the fixed-width name field. <para>单条条目表记录的长度（字节）：三个小端序 32 位值加上定宽
    /// 名称字段。</para></summary>
    public const int ENTRY_HEADER_SIZE = 32;

    /// <summary>Width in bytes of the ASCII entry name field, zero padded and truncated on
    /// write. <para>ASCII 条目名称字段的宽度（字节），写入时以 NUL 补齐并截断。</para></summary>
    public const int ENTRY_NAME_SIZE = 20;

    /// <summary>Byte size of one AES block; encrypted payloads whose length is not a multiple of it
    /// are rejected before decryption. <para>单个 AES 分组的字节大小；长度不是其整数倍的加密载荷在
    /// 解密前即被拒绝。</para></summary>
    public const int AES_BLOCK_SIZE = 16;

    /// <summary>Alignment granularity in bytes applied before each entry payload and after the last
    /// one. <para>在每个条目载荷之前以及最后一个条目之后应用的字节对齐粒度。</para></summary>
    public const int SECTOR_SIZE = 0x200;

    /// <summary>Declared size in bytes of the trailing region that follows the last payload,
    /// 0x200 bytes. <para>最后一个载荷之后的尾部区域声明大小，为 0x200 字节。</para></summary>
    public const int FOOTER_SIZE = 0x200;
}