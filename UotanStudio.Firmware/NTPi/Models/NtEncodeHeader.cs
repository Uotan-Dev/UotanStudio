using System.Text;

namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包中编码数据块头部的结构体，包含加密和压缩相关的元数据。</para>
/// Represents the header of an encoded data block within an NTPI firmware package, containing encryption and compression related metadata.
/// </summary>
/// <remarks>
/// <para>该结构体使用顺序内存布局，大小为 112 字节。魔数字段必须为 "NTENCODE" 才表示有效的编码头部。</para>
/// This struct uses sequential memory layout and is 112 bytes in size. The magic field must be "NTENCODE" to indicate a valid encode header.
/// </remarks>
public struct NtEncodeHeader
{
    /// <summary>
    /// <para>编码块魔数标识，固定为 "NTENCODE" 的 ASCII 字节数组。</para>
    /// The encode block magic number identifier, fixed as the ASCII byte array for "NTENCODE".
    /// </summary>
    public byte[] Magic;

    /// <summary>
    /// <para>预期的魔数字节数组，固定为 "NTENCODE" 的 ASCII 编码。</para>
    /// The expected magic byte array, fixed as the ASCII encoding of "NTENCODE".
    /// </summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTENCODE");

    /// <summary>
    /// <para>主要编码类型标识符。</para>
    /// The primary encoding type identifier.
    /// </summary>
    public uint PrimaryType;

    /// <summary>
    /// <para>压缩子类型标识符。</para>
    /// The compression subtype identifier.
    /// </summary>
    public uint CompressSubtype;

    /// <summary>
    /// <para>加密子类型标识符。</para>
    /// The encryption subtype identifier.
    /// </summary>
    public uint EncryptSubtype;

    /// <summary>
    /// <para>填充字段，用于内存对齐。</para>
    /// Padding field for memory alignment.
    /// </summary>
    public uint Padding;

    /// <summary>
    /// <para>处理后（加密/压缩后）的数据大小（以字节为单位）。</para>
    /// The processed (encrypted/compressed) data size in bytes.
    /// </summary>
    public ulong ProcessedSize;

    /// <summary>
    /// <para>原始（未处理）的数据大小（以字节为单位）。</para>
    /// The original (unprocessed) data size in bytes.
    /// </summary>
    public ulong OriginalSize;

    /// <summary>
    /// <para>加密密钥，固定长度为 32 字节。</para>
    /// The encryption key, fixed at 32 bytes in length.
    /// </summary>
    public byte[] Key;

    /// <summary>
    /// <para>加密初始化向量（IV），固定长度为 32 字节。</para>
    /// The encryption initialization vector (IV), fixed at 32 bytes in length.
    /// </summary>
    public byte[] Iv;

    /// <summary>
    /// <para>密钥的实际有效长度（以字节为单位）。</para>
    /// The actual effective length of the key in bytes.
    /// </summary>
    public uint KeySize;

    /// <summary>
    /// <para>初始化向量的实际有效长度（以字节为单位）。</para>
    /// The actual effective length of the IV in bytes.
    /// </summary>
    public uint IvSize;

    /// <summary>
    /// <para>验证当前头部是否为有效的 NTENCODE 编码头部。</para>
    /// Validates whether the current header represents a valid NTENCODE encode header.
    /// </summary>
    /// <returns>
    /// <para>如果魔数为 "NTENCODE" 则返回 true，否则返回 false。</para>
    /// Returns true if the magic number is "NTENCODE"; otherwise, false.
    /// </returns>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// <para>获取魔数的字符串表示。</para>
    /// Gets the string representation of the magic number.
    /// </summary>
    /// <returns>
    /// <para>魔数字符串，如果字节数组为 null 则返回空字符串。</para>
    /// The magic string, or an empty string if the byte array is null.
    /// </returns>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}