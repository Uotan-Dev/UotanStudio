namespace UotanStudio.Abstractions.Nodes;

/// <summary>
/// <para>连接器（端口）方向。</para>
/// Connector (port) direction.
/// </summary>
public enum ConnectorDirection
{
    /// <summary><para>输入端口。</para>Input port.</summary>
    Input,

    /// <summary><para>输出端口。</para>Output port.</summary>
    Output
}

/// <summary>
/// <para>连接器数据类型标识，用于连接校验。字符串形式便于插件扩展自定义类型。</para>
/// Connector data type identifier used for connection validation. String form allows plugins to extend with custom types.
/// </summary>
public static class WellKnownConnectorTypes
{
    /// <summary><para>任意类型（兼容所有）。</para>Any type (compatible with all).</summary>
    public const string Any = "any";

    /// <summary><para>字节流。</para>Byte stream.</summary>
    public const string Data = "data";

    /// <summary><para>文件路径。</para>File path.</summary>
    public const string FilePath = "filepath";

    /// <summary><para>目录路径。</para>Directory path.</summary>
    public const string DirPath = "dirpath";

    /// <summary><para>设备句柄。</para>Device handle.</summary>
    public const string Device = "device";

    /// <summary><para>固件镜像。</para>Firmware image.</summary>
    public const string Firmware = "firmware";

    /// <summary><para>执行流控制信号。</para>Execution flow control signal.</summary>
    public const string Flow = "flow";

    /// <summary><para>字符串。</para>String.</summary>
    public const string Text = "text";

    /// <summary><para>布尔值。</para>Boolean.</summary>
    public const string Boolean = "bool";

    /// <summary><para>整型数值。</para>Integer.</summary>
    public const string Integer = "int";

    /// <summary><para>浮点数值。</para>Float.</summary>
    public const string Float = "float";
}

/// <summary>
/// <para>连接器数据类型校验工具。</para>
/// Connector data type validation utility.
/// </summary>
public static class ConnectorTypeCompat
{
    /// <summary>
    /// <para>判断两个连接器类型是否兼容可连接。</para>
    /// Determines whether two connector types are compatible for connection.
    /// </summary>
    /// <param name="a">
    /// <para>第一个类型标识。</para>
    /// First type identifier.
    /// </param>
    /// <param name="b">
    /// <para>第二个类型标识。</para>
    /// Second type identifier.
    /// </param>
    /// <returns>
    /// <para>可连接返回 <c>true</c>。</para>
    /// Returns <c>true</c> if connectable.
    /// </returns>
    public static bool IsCompatible(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
        if (a == WellKnownConnectorTypes.Any || b == WellKnownConnectorTypes.Any) return true;
        return a == b;
    }
}
