using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public struct NtEncodeHeader
{
    public byte[] Magic;

    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTENCODE");

    public uint PrimaryType;

    public uint CompressSubtype;

    public uint EncryptSubtype;

    public uint Padding;

    public ulong ProcessedSize;

    public ulong OriginalSize;

    public byte[] Key;

    public byte[] Iv;

    public uint KeySize;

    public uint IvSize;

    /// <summary>
    /// Validates whether the current header represents a valid NTENCODE encode header.
    /// <para>验证当前头部是否为有效的 NTENCODE 编码头部。</para>
    /// </summary>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// Gets the string representation of the magic number.
    /// <para>获取魔数的字符串表示。</para>
    /// </summary>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}
