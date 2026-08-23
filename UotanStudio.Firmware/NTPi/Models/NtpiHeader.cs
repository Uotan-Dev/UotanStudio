using System.Text;

namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 固件包文件头部的结构体，包含魔数、版本信息和首个区域头部。</para>
/// Represents the file header of an NTPI firmware package, containing the magic number, version information, and the first region header.
/// </summary>
/// <remarks>
/// <para>该结构体使用顺序内存布局，大小为 48 字节。魔数字段必须为 "NTPI" 才表示有效的 NTPI 文件。</para>
/// This struct uses sequential memory layout and is 48 bytes in size. The magic field must be "NTPI" to indicate a valid NTPI file.
/// </remarks>
public struct NtpiHeader
{
    /// <summary>
    /// <para>文件魔数标识，固定为 "NTPI" 的 ASCII 字节数组。</para>
    /// The file magic number identifier, fixed as the ASCII byte array for "NTPI".
    /// </summary>
    public byte[] Magic;

    /// <summary>
    /// <para>预期的魔数字节数组，固定为 "NTPI" 的 ASCII 编码。</para>
    /// The expected magic byte array, fixed as the ASCII encoding of "NTPI".
    /// </summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTPI");

    /// <summary>
    /// <para>填充字段，用于内存对齐。</para>
    /// Padding field for memory alignment.
    /// </summary>
    public uint Padding;

    /// <summary>
    /// <para>主版本号。</para>
    /// The major version number.
    /// </summary>
    public ulong VersionMajor;

    /// <summary>
    /// <para>次版本号。</para>
    /// The minor version number.
    /// </summary>
    public ulong VersionMinor;

    /// <summary>
    /// <para>修订版本号。</para>
    /// The patch version number.
    /// </summary>
    public ulong VersionPatch;

    /// <summary>
    /// <para>首个区域头部信息。</para>
    /// The first region header information.
    /// </summary>
    public RegionHeader FirstRegionHeader;

    /// <summary>
    /// <para><see cref="NtpiHeader"/> 结构体的固定内存大小（以字节为单位）。</para>
    /// The fixed in-memory size of the <see cref="NtpiHeader"/> struct in bytes.
    /// </summary>
    public const int Size = 48;

    /// <summary>
    /// <para>验证当前头部是否为有效的 NTPI 文件头部。</para>
    /// Validates whether the current header represents a valid NTPI file header.
    /// </summary>
    /// <returns>
    /// <para>如果魔数为 "NTPI" 则返回 true，否则返回 false。</para>
    /// Returns true if the magic number is "NTPI"; otherwise, false.
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