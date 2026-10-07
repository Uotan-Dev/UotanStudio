using System.Text;

namespace UotanStudio.Comm.Text;

/// <summary>
/// Decodes device-reported fixed-width fields to text and strips the trailing NUL padding many
/// protocols leave in them. The Comm layer keeps its own copy because it must not depend on the
/// firmware library; the firmware-side counterpart is <c>UotanStudio.Firmware.Core.Text.NulText</c>.
/// <para>把设备上报的定宽字段解码为文本并去除许多协议遗留的尾部 NUL 填充。Comm 层持有自己的副本，
/// 因为它不得依赖固件库；固件侧对应类型为 <c>UotanStudio.Firmware.Core.Text.NulText</c>。</para>
/// </summary>
internal static class NulText
{
    extension(Encoding encoding)
    {
        /// <summary>
        /// Decodes <paramref name="bytes"/> with the receiver encoding and trims trailing NULs.
        /// <para>用接收者编码解码 <paramref name="bytes"/> 并去除尾部 NUL。</para>
        /// </summary>
        internal string GetStringTrimNul(ReadOnlySpan<byte> bytes)
            => encoding.GetString(bytes).TrimEnd('\0');

        /// <summary>
        /// Decodes the <paramref name="count"/> bytes of <paramref name="buffer"/> starting at
        /// <paramref name="index"/> and trims trailing NULs.
        /// <para>解码 <paramref name="buffer"/> 中自 <paramref name="index"/> 起的 <paramref name="count"/> 字节并去除尾部 NUL。</para>
        /// </summary>
        internal string GetStringTrimNul(byte[] buffer, int index, int count)
            => encoding.GetString(buffer, index, count).TrimEnd('\0');
    }
}