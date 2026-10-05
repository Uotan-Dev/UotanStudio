using System.Text;

namespace UotanStudio.Firmware.Core.Text;

/// <summary>
/// Decodes fixed-width binary fields to text and strips the trailing NUL padding those fields
/// carry. Centralizes the repeated "decode then <c>TrimEnd('\\0')</c>" idiom across firmware
/// format readers so the encoding choice stays explicit at the call site.
/// <para>把定宽二进制字段解码为文本并去除这类字段自带的尾部 NUL 填充，集中固件格式读取器中重复出现的
/// "解码后 <c>TrimEnd('\\0')</c>" 惯例，使编码选择在调用点保持显式。</para>
/// </summary>
internal static class NulText
{
    /// <summary>
    /// Decodes <paramref name="bytes"/> with <paramref name="encoding"/> and trims trailing NULs.
    /// <para>用 <paramref name="encoding"/> 解码 <paramref name="bytes"/> 并去除尾部 NUL。</para>
    /// </summary>
    internal static string GetStringTrimNul(this Encoding encoding, ReadOnlySpan<byte> bytes)
        => encoding.GetString(bytes).TrimEnd('\0');

    /// <summary>
    /// Decodes the <paramref name="count"/> bytes of <paramref name="buffer"/> starting at
    /// <paramref name="index"/> and trims trailing NULs.
    /// <para>解码 <paramref name="buffer"/> 中自 <paramref name="index"/> 起的 <paramref name="count"/> 字节并去除尾部 NUL。</para>
    /// </summary>
    internal static string GetStringTrimNul(this Encoding encoding, byte[] buffer, int index, int count)
        => encoding.GetString(buffer, index, count).TrimEnd('\0');
}