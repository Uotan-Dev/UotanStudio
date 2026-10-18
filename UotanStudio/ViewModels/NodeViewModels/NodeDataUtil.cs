using System.Globalization;
using System.Text;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Node data utility providing lenient conversion of port values to byte arrays, booleans, integers and floating-point numbers.
/// <para>节点数据辅助工具，提供端口值到字节数组、布尔、整数与浮点数的宽松转换。</para>
/// </summary>
internal static class NodeDataUtil
{
    internal const int MaxInMemoryBytes = 64 * 1024 * 1024;

    /// <summary>Determines whether two port types can be connected: either side being <see cref="NodePortType.Any"/> permits the connection; <see cref="NodePortType.Device"/> only connects to <see cref="NodePortType.Device"/> or <see cref="NodePortType.Any"/>; otherwise the types must match.
    /// <para>判断两个端口类型是否可以连接：任意一方为 <see cref="NodePortType.Any"/> 则允许连接； <see cref="NodePortType.Device"/> 仅能与 <see cref="NodePortType.Device"/> 或 <see cref="NodePortType.Any"/> 连接；其余要求类型相同。</para></summary>
    /// <param name="source">The source (output) port type.<para>源端口（输出）类型。</para></param>
    /// <param name="target">The target (input) port type.<para>目标端口（输入）类型。</para></param>
    /// <returns>Whether the connection is allowed.<para>是否可以连接。</para></returns>
    public static bool CanConnect(NodePortType source, NodePortType target)
    {
        if (source == NodePortType.Any || target == NodePortType.Any) return true;
        if (source == NodePortType.Device) return target == NodePortType.Device;
        if (target == NodePortType.Device) return source == NodePortType.Device;
        return source == target;
    }

    /// <summary>Converts a port value to a <see cref="DetectedDevice"/>: <see cref="DetectedDevice"/> values are returned as-is; <see cref="DeviceInfo"/> values yield their <see cref="DeviceInfo.Device"/>; anything else yields <c>null</c>.
    /// <para>把端口值转换为 <see cref="DetectedDevice"/>：值为 <see cref="DetectedDevice"/> 时原样返回； 值为 <see cref="DeviceInfo"/> 时返回其 <see cref="DeviceInfo.Device"/>；其余返回 <c>null</c>。</para></summary>
    /// <param name="value">The port value.<para>端口值。</para></param>
    /// <returns>The device handle; <c>null</c> when not convertible.
    /// <para>设备句柄；无法转换时返回 <c>null</c>。</para></returns>
    public static DetectedDevice? ToDevice(object? value)
    {
        return value switch
        {
            DetectedDevice device => device,
            DeviceInfo info => info.Device,
            _ => null
        };
    }

    /// <summary>
    /// Converts a port value to a byte array for node execution.
    /// <para>把端口值转换为节点执行所需的字节数组。</para>
    /// </summary>
    /// <param name="value">
    /// The port value.
    /// <para>端口值。</para>
    /// </param>
    /// <returns>
    /// The resulting byte array; <c>null</c> when conversion is not possible.
    /// <para>得到的字节数组；<c>null</c> 表示无法转换。</para>
    /// </returns>
    public static byte[]? ToBytes(object? value, CancellationToken cancellationToken = default)
    {
        return value switch
        {
            null => null,
            byte[] bytes when bytes.Length <= MaxInMemoryBytes => bytes,
            byte[] => throw new InvalidDataException("The in-memory node data limit was exceeded. Use a file artifact for large inputs."),
            Services.Projects.FileArtifact artifact => ReadFileBytes(artifact.Path, cancellationToken),
            string text when File.Exists(text) => ReadFileBytes(text, cancellationToken),
            string text => EncodeText(text),
            _ => EncodeText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    internal static byte[] ReadFileBytes(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var bytes = AllocateFileBuffer(stream.Length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes.AsSpan(offset));
            if (read == 0)
            {
                throw new EndOfStreamException("The node input file changed while reading.");
            }
            offset += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The node input file changed while reading.");
        }
        return bytes;
    }

    internal static async Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = AllocateFileBuffer(stream.Length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The node input file changed while reading.");
            }
            offset += read;
        }
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The node input file changed while reading.");
        }
        return bytes;
    }

    private static byte[] AllocateFileBuffer(long length)
    {
        if (length > MaxInMemoryBytes)
        {
            throw new InvalidDataException("The in-memory node data limit was exceeded. Use a file artifact for large inputs.");
        }
        return new byte[checked((int)length)];
    }

    private static byte[] EncodeText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxInMemoryBytes)
        {
            throw new InvalidDataException("The in-memory node data limit was exceeded. Use a file artifact for large inputs.");
        }
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Converts a port value to a display string (byte arrays show their length and a hexadecimal digest).
    /// <para>把端口值转换为展示用的字符串（字节数组显示其长度与十六进制摘要）。</para>
    /// </summary>
    /// <param name="value">
    /// The port value.
    /// <para>端口值。</para>
    /// </param>
    /// <returns>
    /// The display text.
    /// <para>展示文本。</para>
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

    /// <summary>Converts a port value to a boolean: booleans are returned as-is; strings <c>true</c>/<c>1</c>/<c>yes</c> are true; non-zero numbers are true.
    /// <para>把端口值转换为布尔：布尔原样；字符串 <c>true</c>/<c>1</c>/<c>yes</c> 视为真；数值非零视为真。</para></summary>
    /// <param name="value">The port value.<para>端口值。</para></param>
    /// <param name="fallback">The default value when undecidable.<para>无法判定时的默认值。</para></param>
    /// <returns>The converted boolean.<para>转换后的布尔值。</para></returns>
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

    /// <summary>Converts a port value to a 32-bit integer, returning <paramref name="fallback"/> on failure.
    /// <para>把端口值转换为 32 位整数，失败时返回 <paramref name="fallback"/>。</para></summary>
    /// <param name="value">The port value.<para>端口值。</para></param>
    /// <param name="fallback">The default value on conversion failure.<para>转换失败时的默认值。</para></param>
    /// <returns>The converted integer.<para>转换后的整数。</para></returns>
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

    /// <summary>Converts a port value to a double, returning <paramref name="fallback"/> on failure.
    /// <para>把端口值转换为双精度浮点数，失败时返回 <paramref name="fallback"/>。</para></summary>
    /// <param name="value">The port value.<para>端口值。</para></param>
    /// <param name="fallback">The default value on conversion failure.<para>转换失败时的默认值。</para></param>
    /// <returns>The converted double.<para>转换后的浮点数。</para></returns>
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