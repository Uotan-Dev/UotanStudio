namespace UotanStudio.Firmware.VendorFirmware.NTPi.Models;

/// <summary>
/// Identifies the kind of a region in the NTPI region chain; stored on disk as an unsigned 64-bit value.
/// <para>标识 NTPI 区域链中区域的种类；在磁盘上以无符号 64 位值存储。</para>
/// </summary>
public enum RegionType : ulong
{
    /// <summary>Package metadata region, extracted as <c>Metadata.xml</c>. <para>包元数据区域，提取为 <c>Metadata.xml</c>。</para></summary>
    Metadata = 1,

    /// <summary>Patch description region, extracted as <c>Patch.xml</c>. <para>补丁说明区域，提取为 <c>Patch.xml</c>。</para></summary>
    Patch = 2,

    /// <summary>Raw program region, extracted as <c>RawProgram.xml</c>. <para>原始刷机脚本区域，提取为 <c>RawProgram.xml</c>。</para></summary>
    RawProgram = 3,

    /// <summary>Binary key map region (<c>KeyMap.bin</c>) supplying per-file key material for Region6 payloads.
    /// <para>二进制密钥映射区域（<c>KeyMap.bin</c>），为 Region6 载荷提供逐文件密钥材料。</para></summary>
    KeyMap = 4,

    /// <summary>File index region (<c>FileIndex.xml</c>) enumerating the file entries stored in Region6.
    /// <para>文件索引区域（<c>FileIndex.xml</c>），列举存储在 Region6 中的文件条目。</para></summary>
    FileIndex = 5,

    /// <summary>Terminal region holding the concatenated encoded file payloads verbatim, without encryption or a block header.
    /// <para>终端区域，原样存放拼接后的编码文件载荷，不加密也不带块头。</para></summary>
    Region6 = 6
}