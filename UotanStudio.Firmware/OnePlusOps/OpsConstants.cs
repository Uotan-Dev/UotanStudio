namespace UotanStudio.Firmware.OnePlusOps;

/// <summary>
/// Constants for OnePlus OPS firmware format
/// </summary>
/// <para>OnePlus OPS固件格式的常量</para>
public static class OpsConstants
{
    /// <summary>
    /// Magic bytes identifying an OPS firmware file ("OPS\0")
    /// </summary>
    /// <para>标识OPS固件文件的魔术字节（"OPS\0"）</para>
    public const string OPS_MAGIC = "OPS\0";

    /// <summary>
    /// Size of the OPS header in bytes
    /// </summary>
    /// <para>OPS头部的字节大小</para>
    public const int HEADER_SIZE = 52;

    /// <summary>
    /// Size of an OPS entry header in bytes
    /// </summary>
    /// <para>OPS条目头部的字节大小</para>
    public const int ENTRY_HEADER_SIZE = 32;

    /// <summary>
    /// Size of the entry name field in bytes
    /// </summary>
    /// <para>OPS条目名称字段的字节大小</para>
    public const int ENTRY_NAME_SIZE = 20;

    /// <summary>
    /// AES block size in bytes used for ECB decryption
    /// </summary>
    /// <para>用于ECB解密的AES块大小（字节）</para>
    public const int AES_BLOCK_SIZE = 16;

    /// <summary>
    /// Default sector size in bytes for OPS firmware
    /// </summary>
    /// <para>OPS固件的默认扇区大小（字节）</para>
    public const int SECTOR_SIZE = 0x200;

    /// <summary>
    /// Size of the OPS footer containing metadata in bytes
    /// </summary>
    /// <para>包含元数据的OPS页脚大小（字节）</para>
    public const int FOOTER_SIZE = 0x200;
}