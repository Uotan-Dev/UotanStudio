using System.Text;

namespace UotanStudio.Firmware.Core.Text;

/// <summary>
/// Decodes fixed-width binary fields to text and strips the trailing NUL padding those fields
/// carry. Centralizes the repeated "decode then <c>TrimEnd('\\0')</c>" idiom across firmware
/// format readers so the encoding choice stays explicit at the call site.
/// <para>把定宽二进制字段解码为文本并去除这类字段自带的尾部 NUL 填充，集中固件格式读取器中重复出现的
/// "解码后 <c>TrimEnd('\\0')</c>" 惯例，使编码选择在调用点保持显式。</para>
/// </summary>
public static class NulText
{
    extension(Encoding encoding)
    {
        /// <summary>
        /// Decodes <paramref name="bytes"/> with the receiver encoding and trims trailing NULs.
        /// <para>用接收者编码解码 <paramref name="bytes"/> 并去除尾部 NUL。</para>
        /// </summary>
        public string GetStringTrimNul(ReadOnlySpan<byte> bytes)
            => encoding.GetString(bytes).TrimEnd('\0');

        /// <summary>
        /// Decodes the <paramref name="count"/> bytes of <paramref name="buffer"/> starting at
        /// <paramref name="index"/> and trims trailing NULs.
        /// <para>解码 <paramref name="buffer"/> 中自 <paramref name="index"/> 起的 <paramref name="count"/> 字节并去除尾部 NUL。</para>
        /// </summary>
        public string GetStringTrimNul(byte[] buffer, int index, int count)
            => encoding.GetString(buffer, index, count).TrimEnd('\0');
    }

    /// <summary>
    /// Decodes the bytes before the first NUL of <paramref name="bytes"/>, or the whole span when it
    /// holds no NUL. Used for C strings whose length is not stored separately.
    /// <para>解码 <paramref name="bytes"/> 中首个 NUL 之前的内容；整段不含 NUL 时解码整段。用于长度未单独存储的 C 字符串。</para>
    /// </summary>
    /// <param name="bytes">Field bytes, usually a fixed-width slot whose tail is NUL padding.
    /// <para>字段字节，通常是尾部以 NUL 填充的定宽槽位。</para></param>
    /// <param name="encoding">Encoding of the stored characters; it is never inferred from the bytes.
    /// <para>所存字符的编码；绝不从字节推断。</para></param>
    /// <returns>The decoded text up to the first NUL. <para>首个 NUL 之前的解码文本。</para></returns>
    public static string ReadCString(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var length = bytes.IndexOf((byte)0);
        return encoding.GetString(length < 0 ? bytes : bytes[..length]);
    }
}