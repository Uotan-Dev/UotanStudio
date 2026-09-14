using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public struct NtpiHeader
{
    public byte[] Magic;

    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTPI");

    public uint Padding;

    public ulong VersionMajor;

    public ulong VersionMinor;

    public ulong VersionPatch;

    public RegionHeader FirstRegionHeader;

    public const int Size = 48;

    /// <summary>
    /// Validates whether the current header represents a valid NTPI file header.
    /// <para>验证当前头部是否为有效的 NTPI 文件头部。</para>
    /// </summary>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// Gets the string representation of the magic number.
    /// <para>获取魔数的字符串表示。</para>
    /// </summary>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}