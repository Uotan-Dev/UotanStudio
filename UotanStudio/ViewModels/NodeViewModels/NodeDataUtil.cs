using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点数据辅助工具，提供端口值到字节数组、布尔、整数与浮点数的宽松转换。</para>
    /// Node data utility providing lenient conversion of port values to byte arrays, booleans, integers and floating-point numbers.
    /// </summary>
    internal static class NodeDataUtil
    {
        /// <summary>
        /// <para>把端口值转换为字节数组：字节数组原样返回；字符串若是已存在文件路径则读取文件内容，否则按 UTF-8 编码；其他对象转换为字符串后按 UTF-8 编码。</para>
        /// Converts a port value to a byte array: byte arrays are returned as-is; strings that denote an existing file path are read as file
        /// contents, otherwise encoded as UTF-8; other objects are converted to a string and encoded as UTF-8.
        /// </summary>
        /// <param name="value">
        /// <para>端口值。</para>
        /// The port value.
        /// </param>
        /// <returns>
        /// <para>得到的字节数组；<c>null</c> 表示无法转换。</para>
        /// The resulting byte array; <c>null</c> when conversion is not possible.
        /// </returns>
        public static byte[]? ToBytes(object? value)
        {
            return value switch
            {
                null => null,
                byte[] bytes => bytes,
                string text => File.Exists(text) ? File.ReadAllBytes(text) : Encoding.UTF8.GetBytes(text),
                _ => Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            };
        }

        /// <summary>
        /// <para>把端口值转换为展示用的字符串（字节数组显示其长度与十六进制摘要）。</para>
        /// Converts a port value to a display string (byte arrays show their length and a hexadecimal digest).
        /// </summary>
        /// <param name="value">
        /// <para>端口值。</para>
        /// The port value.
        /// </param>
        /// <returns>
        /// <para>展示文本。</para>
        /// The display text.
        /// </returns>
        public static string ToDisplay(object? value)
        {
            return value switch
            {
                null => "<empty>",
                byte[] bytes => $"<bytes:{bytes.Length}>",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        /// <summary>
        /// <para>把端口值转换为布尔：布尔原样；字符串 <c>true</c>/<c>1</c>/<c>yes</c> 视为真；数值非零视为真。</para>
        /// Converts a port value to a boolean: booleans are returned as-is; strings <c>true</c>/<c>1</c>/<c>yes</c> are true; non-zero numbers are true.
        /// </summary>
        /// <param name="value">
        /// <para>端口值。</para>
        /// The port value.
        /// </param>
        /// <param name="fallback">
        /// <para>无法判定时的默认值。</para>
        /// The default value when undecidable.
        /// </param>
        /// <returns>
        /// <para>转换后的布尔值。</para>
        /// The converted boolean.
        /// </returns>
        public static bool ToBool(object? value, bool fallback = false)
        {
            return value switch
            {
                null => fallback,
                bool b => b,
                string s when bool.TryParse(s, out var b) => b,
                string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n != 0,
                byte[] bytes => bytes.Length > 0 && bytes[0] != 0,
                _ when decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n != 0,
                _ => fallback
            };
        }

        /// <summary>
        /// <para>把端口值转换为 32 位整数，失败时返回 <paramref name="fallback"/>。</para>
        /// Converts a port value to a 32-bit integer, returning <paramref name="fallback"/> on failure.
        /// </summary>
        /// <param name="value">
        /// <para>端口值。</para>
        /// The port value.
        /// </param>
        /// <param name="fallback">
        /// <para>转换失败时的默认值。</para>
        /// The default value on conversion failure.
        /// </param>
        /// <returns>
        /// <para>转换后的整数。</para>
        /// The converted integer.
        /// </returns>
        public static int ToInt(object? value, int fallback = 0)
        {
            return value switch
            {
                null => fallback,
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
                byte[] bytes when bytes.Length >= sizeof(int) => BitConverter.ToInt32(bytes, 0),
                _ when int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
                _ => fallback
            };
        }

        /// <summary>
        /// <para>把端口值转换为双精度浮点数，失败时返回 <paramref name="fallback"/>。</para>
        /// Converts a port value to a double, returning <paramref name="fallback"/> on failure.
        /// </summary>
        /// <param name="value">
        /// <para>端口值。</para>
        /// The port value.
        /// </param>
        /// <param name="fallback">
        /// <para>转换失败时的默认值。</para>
        /// The default value on conversion failure.
        /// </param>
        /// <returns>
        /// <para>转换后的浮点数。</para>
        /// The converted double.
        /// </returns>
        public static double ToDouble(object? value, double fallback = 0)
        {
            return value switch
            {
                null => fallback,
                double d => d,
                float f => f,
                byte[] bytes when bytes.Length >= sizeof(double) => BitConverter.ToDouble(bytes, 0),
                _ when double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => fallback
            };
        }
    }
}