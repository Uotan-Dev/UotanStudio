using System.Text;

namespace UotanStudio.Firmware.NTPi.Models;

public struct NtDecompressHeader
{
    public byte[] Magic;

    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    public uint PrimaryType;

    public uint DecompressSubtype;

    public ulong Padding;

    public ulong ProcessedSize;

    public ulong OriginalSize;

    public byte[] Padding2;

    /// <summary>
    /// Validates whether the current header represents a valid NTDECOMP decompression header.
    /// <para>验证当前头部是否为有效的 NTDECOMP 解压缩头部。</para>
    /// </summary>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// Gets the string representation of the magic number.
    /// <para>获取魔数的字符串表示。</para>
    /// </summary>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}
