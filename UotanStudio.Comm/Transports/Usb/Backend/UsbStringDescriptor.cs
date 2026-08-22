using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Backend;

/// <summary>
/// Decodes USB string descriptors (GET_DESCRIPTOR STRING responses).
/// <para>解码 USB 字符串描述符（GET_DESCRIPTOR STRING 响应）。</para>
/// </summary>
internal static class UsbStringDescriptor
{
    /// <summary>
    /// Decodes a UTF-16LE string descriptor payload, skipping the 2-byte descriptor header
    /// and trimming trailing NUL characters.
    /// <para>解码 UTF-16LE 字符串描述符载荷，跳过 2 字节描述符头并去除尾部 NUL。</para>
    /// </summary>
    /// <param name="descriptor">The raw descriptor response buffer. <para>原始描述符响应缓冲区。</para></param>
    /// <param name="responseLength">The number of valid bytes in the buffer. <para>缓冲区有效字节数。</para></param>
    /// <returns>The decoded string, or <see cref="string.Empty"/> when the payload is too short.
    /// <para>解码后的字符串，载荷过短时返回空字符串。</para></returns>
    public static string Decode(byte[] descriptor, int responseLength)
    {
        if (responseLength <= 2) return string.Empty;
        return Encoding.Unicode.GetString(descriptor, 2, responseLength - 2).TrimEnd('\0');
    }
}
