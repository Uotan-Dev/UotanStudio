using System.Text;

namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包中解压缩数据块头部的结构体，包含解压缩相关的元数据。</para>
/// Represents the header of a decompression data block within an NTPI firmware package, containing decompression related metadata.
/// </summary>
/// <remarks>
/// <para>该结构体使用顺序内存布局，大小为 112 字节。</para>
/// This struct uses sequential memory layout and is 112 bytes in size.
/// </remarks>
public struct NtDecompressHeader
{
    /// <summary>
    /// <para>解压缩块魔数标识，固定为 "NTDECOMP" 的 ASCII 字节数组。</para>
    /// The decompression block magic number identifier, fixed as the ASCII byte array for "NTDECOMP".
    /// </summary>
    public byte[] Magic;

    /// <summary>
    /// <para>预期的魔数字节数组，固定为 "NTDECOMP" 的 ASCII 编码。</para>
    /// The expected magic byte array, fixed as the ASCII encoding of "NTDECOMP".
    /// </summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    /// <summary>
    /// <para>主要解压缩类型标识符。</para>
    /// The primary decompression type identifier.
    /// </summary>
    public uint PrimaryType;

    /// <summary>
    /// <para>解压缩子类型标识符。</para>
    /// The decompression subtype identifier.
    /// </summary>
    public uint DecompressSubtype;

    /// <summary>
    /// <para>填充字段，用于内存对齐。</para>
    /// Padding field for memory alignment.
    /// </summary>
    public ulong Padding;

    /// <summary>
    /// <para>处理后（压缩后）的数据大小（以字节为单位）。</para>
    /// The processed (compressed) data size in bytes.
    /// </summary>
    public ulong ProcessedSize;

    /// <summary>
    /// <para>原始（未压缩）的数据大小（以字节为单位）。</para>
    /// The original (uncompressed) data size in bytes.
    /// </summary>
    public ulong OriginalSize;

    /// <summary>
    /// <para>填充字节数组，固定长度为 72 字节，用于结构体对齐至 112 字节。</para>
    /// Padding byte array, fixed at 72 bytes in length, used to align the struct to 112 bytes.
    /// </summary>
    public byte[] Padding2;

    /// <summary>
    /// <para>验证当前头部是否为有效的 NTDECOMP 解压缩头部。</para>
    /// Validates whether the current header represents a valid NTDECOMP decompression header.
    /// </summary>
    /// <returns>
    /// <para>如果魔数为 "NTDECOMP" 则返回 true，否则返回 false。</para>
    /// Returns true if the magic number is "NTDECOMP"; otherwise, false.
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