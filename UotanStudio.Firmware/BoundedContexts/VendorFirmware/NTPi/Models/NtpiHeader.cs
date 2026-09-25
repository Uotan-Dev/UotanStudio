using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Fixed 48-byte header at the start of an NTPI firmware package, holding the ASCII magic, the
/// version triple used to derive the decryption key, and the descriptor of the first region.
/// <para>位于 NTPI 固件包开头的固定 48 字节头部，保存 ASCII 魔数、用于推导解密密钥的版本三元组，以及首个区域的描述符。</para>
/// </summary>
public struct NtpiHeader
{
    /// <summary>The 4-byte format signature read from offsets 0..4, expected to spell <c>NTPI</c>.
    /// <para>从偏移 0..4 读取的 4 字节格式签名，预期拼写为 <c>NTPI</c>。</para></summary>
    public byte[] Magic;

    /// <summary>The canonical ASCII bytes <c>NTPI</c> that <see cref="IsValid"/> compares <see cref="Magic"/> against.
    /// <para><see cref="IsValid"/> 用来比对 <see cref="Magic"/> 的标准 ASCII 字节 <c>NTPI</c>。</para></summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTPI");

    /// <summary>Reserved little-endian <see cref="uint"/> occupying offsets 4..8, written back unchanged.
    /// <para>占据偏移 4..8 的保留小端 <see cref="uint"/>，回写时保持不变。</para></summary>
    public uint Padding;

    /// <summary>Little-endian major component of the package version, read from offset 8.
    /// <para>从偏移 8 读取的软件包版本主号（小端）。</para></summary>
    public ulong VersionMajor;

    /// <summary>Little-endian minor component of the package version, read from offset 16.
    /// <para>从偏移 16 读取的软件包版本次号（小端）。</para></summary>
    public ulong VersionMinor;

    /// <summary>Little-endian patch component of the package version, read from offset 24.
    /// <para>从偏移 24 读取的软件包版本修订号（小端）。</para></summary>
    public ulong VersionPatch;

    /// <summary>The 16-byte region header at offset 32 that starts the region chain following this header.
    /// <para>偏移 32 处的 16 字节区域头，用于启动本头部之后的区域链。</para></summary>
    public RegionHeader FirstRegionHeader;

    /// <summary>Total on-disk size of the header in bytes, also the offset at which the first region block begins.
    /// <para>头部在磁盘上的总字节数，同时也是首个区域块开始的偏移。</para></summary>
    public const int Size = 48;

    /// <summary>
    /// Validates whether the current header represents a valid NTPI file header.
    /// <para>验证当前头部是否为有效的 NTPI 文件头部。</para>
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