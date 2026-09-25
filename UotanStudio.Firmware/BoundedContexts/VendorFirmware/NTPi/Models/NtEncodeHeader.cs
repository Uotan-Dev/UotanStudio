using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Per-block header prefixed to each encrypted NTPI Region6 file block, carrying the ASCII magic, the
/// compression/encryption codec selectors, size fields, and inline key/IV material.
/// <para>作为每个加密 NTPI Region6 文件块前缀的逐块头部，保存 ASCII 魔数、压缩/加密编解码选择符、大小字段以及内联的密钥/IV 材料。</para>
/// </summary>
public struct NtEncodeHeader
{
    /// <summary>The 8-byte format signature read from offsets 0..8, expected to spell <c>NTENCODE</c>.
    /// <para>从偏移 0..8 读取的 8 字节格式签名，预期拼写为 <c>NTENCODE</c>。</para></summary>
    public byte[] Magic;

    /// <summary>The canonical ASCII bytes <c>NTENCODE</c> that <see cref="IsValid"/> compares <see cref="Magic"/> against.
    /// <para><see cref="IsValid"/> 用来比对 <see cref="Magic"/> 的标准 ASCII 字节 <c>NTENCODE</c>。</para></summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTENCODE");

    /// <summary>Little-endian primary codec selector read from offset 8.
    /// <para>从偏移 8 读取的小端主编解码选择符。</para></summary>
    public uint PrimaryType;

    /// <summary>Little-endian compression subtype read from offset 12.
    /// <para>从偏移 12 读取的小端压缩子类型。</para></summary>
    public uint CompressSubtype;

    /// <summary>Little-endian encryption subtype read from offset 16.
    /// <para>从偏移 16 读取的小端加密子类型。</para></summary>
    public uint EncryptSubtype;

    /// <summary>Reserved little-endian <see cref="uint"/> occupying offsets 20..24, written back unchanged.
    /// <para>占据偏移 20..24 的保留小端 <see cref="uint"/>，回写时保持不变。</para></summary>
    public uint Padding;

    /// <summary>Little-endian processed (encrypted) payload length read from offset 24.
    /// <para>从偏移 24 读取的小端已处理（加密）负载长度。</para></summary>
    public ulong ProcessedSize;

    /// <summary>Little-endian length read from offset 32; extraction treats it as the encrypted byte length of the block that follows the header.
    /// <para>从偏移 32 读取的小端长度；提取时将其视为头部之后该块的加密字节长度。</para></summary>
    public ulong OriginalSize;

    /// <summary>The 32-byte key field copied from offsets 40..72; written as zeros because the effective key comes from the KeyMap region.
    /// <para>从偏移 40..72 复制的 32 字节密钥字段；写入为全零，因为实际密钥取自 KeyMap 区域。</para></summary>
    public byte[] Key;

    /// <summary>The 32-byte IV field copied from offsets 72..104; written as zeros because decryption uses the KeyMap-derived zero IV.
    /// <para>从偏移 72..104 复制的 32 字节 IV 字段；写入为全零，因为解密使用由 KeyMap 推导的零 IV。</para></summary>
    public byte[] Iv;

    /// <summary>Little-endian declared key length read from offset 104.
    /// <para>从偏移 104 读取的小端声明密钥长度。</para></summary>
    public uint KeySize;

    /// <summary>Little-endian declared IV length read from offset 108.
    /// <para>从偏移 108 读取的小端声明 IV 长度。</para></summary>
    public uint IvSize;

    /// <summary>
    /// Validates whether the current header represents a valid NTENCODE encode header.
    /// <para>验证当前头部是否为有效的 NTENCODE 编码头部。</para>
    /// </summary>
    /// <returns><see langword="true"/> when <see cref="Magic"/> is non-<see langword="null"/> and equals
    /// <see cref="ExpectedMagic"/> byte for byte; otherwise <see langword="false"/>.
    /// <para>当 <see cref="Magic"/> 非 <see langword="null"/> 且逐字节等于 <see cref="ExpectedMagic"/> 时返回
    /// <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// Gets the string representation of the magic number.
    /// <para>获取魔数的字符串表示。</para>
    /// </summary>
    /// <returns>The ASCII decoding of <see cref="Magic"/>, or <see cref="string.Empty"/> when <see cref="Magic"/>
    /// is <see langword="null"/>.
    /// <para><see cref="Magic"/> 的 ASCII 解码结果；当 <see cref="Magic"/> 为 <see langword="null"/> 时返回
    /// <see cref="string.Empty"/>。</para></returns>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}