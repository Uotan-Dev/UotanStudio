using System.Text;

namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>表示设备树节点中的属性,包含名称和二进制值。</para>
/// Represents a property within a device tree node, containing a name and a binary value.
/// </summary>
public sealed class DeviceTreeProperty
{
    private byte[] _value = [];

    /// <summary>属性名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>属性的原始二进制值。</summary>
    public ReadOnlySpan<byte> Value => _value;

    /// <summary>属性值的字节长度。</summary>
    public int Length => _value.Length;

    /// <summary>属性值(字节数组副本)。</summary>
    public byte[] ValueBytes => (byte[])_value.Clone();

    /// <summary>属性是否没有值(零长度)。</summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>是否在 DTB 中标记为 NOP(写入 FDT_NOP 而非 FDT_PROP)。</summary>
    public bool IsNop { get; set; }

    /// <summary>
    /// <para>替换属性值(仅供 DTS 解析器回填 label/phandle 引用)。</para>
    /// Replaces the property value (for the DTS parser to backfill label/phandle references).
    /// </summary>
    internal void ReplaceValue(ReadOnlySpan<byte> value)
    {
        _value = value.ToArray();
    }

    /// <summary>
    /// <para>使用指定名称和值初始化属性。</para>
    /// Initializes a property with the specified name and value.
    /// </summary>
    public DeviceTreeProperty(string name, ReadOnlySpan<byte> value)
    {
        Name = name;
        _value = value.ToArray();
    }

    /// <summary>
    /// <para>使用指定名称和字符串值初始化属性(UTF-8 编码,不含结尾 NUL)。</para>
    /// Initializes a property with the specified name and string value.
    /// </summary>
    public DeviceTreeProperty(string name, string value)
        : this(name, Encoding.UTF8.GetBytes(value))
    {
    }

    /// <summary>
    /// <para>以字符串形式获取属性值(UTF-8,去除结尾 NUL)。</para>
    /// Gets the property value as a string (UTF-8, trailing NUL trimmed).
    /// </summary>
    public string GetString()
    {
        var end = _value.AsSpan().IndexOf((byte)0);
        var span = end >= 0 ? _value.AsSpan(0, end) : _value.AsSpan();
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// <para>将属性值解析为 32 位大端单元数组。</para>
    /// Parses the property value as an array of 32-bit big-endian cells.
    /// </summary>
    public uint[] GetCells()
    {
        var count = _value.Length / 4;
        var cells = new uint[count];
        for (var i = 0; i < count; i++)
        {
            cells[i] = (uint)((_value[i * 4] << 24) | (_value[(i * 4) + 1] << 16) | (_value[(i * 4) + 2] << 8) | _value[(i * 4) + 3]);
        }
        return cells;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} = {_value.Length} bytes";
}

/// <summary>
/// <para>表示设备树中的一个节点,包含属性和子节点。</para>
/// Represents a node in a device tree, containing properties and child nodes.
/// </summary>
public sealed class DeviceTreeNode
{
    private readonly List<DeviceTreeProperty> _properties = [];
    private readonly List<DeviceTreeNode> _children = [];

    /// <summary>节点名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>节点单元地址(名称中 '@' 之后的部分)。</summary>
    public string? UnitAddress { get; set; }

    /// <summary>父节点(根节点为 null)。</summary>
    public DeviceTreeNode? Parent { get; set; }

    /// <summary>节点属性列表。</summary>
    public IReadOnlyList<DeviceTreeProperty> Properties => _properties;

    /// <summary>子节点列表。</summary>
    public IReadOnlyList<DeviceTreeNode> Children => _children;

    /// <summary>
    /// <para>使用指定名称初始化节点。</para>
    /// Initializes a node with the specified name.
    /// </summary>
    public DeviceTreeNode(string name)
    {
        Name = name;
        var at = name.IndexOf('@');
        if (at >= 0 && at < name.Length - 1)
        {
            UnitAddress = name[(at + 1)..];
        }
    }

    /// <summary>
    /// <para>添加一个属性。</para>
    /// Adds a property.
    /// </summary>
    public DeviceTreeProperty AddProperty(string name, ReadOnlySpan<byte> value)
    {
        var property = new DeviceTreeProperty(name, value);
        _properties.Add(property);
        return property;
    }

    /// <summary>
    /// <para>添加一个字符串属性。</para>
    /// Adds a string property.
    /// </summary>
    public DeviceTreeProperty AddPropertyString(string name, string value)
    {
        var property = new DeviceTreeProperty(name, value);
        _properties.Add(property);
        return property;
    }

    /// <summary>
    /// <para>添加一个 32 位单元数组属性(大端)。</para>
    /// Adds a property with 32-bit big-endian cells.
    /// </summary>
    public DeviceTreeProperty AddPropertyCells(string name, params uint[] cells)
    {
        var bytes = new byte[cells.Length * 4];
        for (var i = 0; i < cells.Length; i++)
        {
            bytes[i * 4] = (byte)(cells[i] >> 24);
            bytes[(i * 4) + 1] = (byte)(cells[i] >> 16);
            bytes[(i * 4) + 2] = (byte)(cells[i] >> 8);
            bytes[(i * 4) + 3] = (byte)cells[i];
        }
        return AddProperty(name, bytes);
    }

    /// <summary>
    /// <para>添加一个子节点。</para>
    /// Adds a child node.
    /// </summary>
    public DeviceTreeNode AddChild(string name)
    {
        var child = new DeviceTreeNode(name) { Parent = this };
        _children.Add(child);
        return child;
    }

    /// <summary>
    /// <para>将已存在的节点作为子节点挂接到当前节点(供 overlay 合并等场景使用)。</para>
    /// Attaches an existing node as a child of this node (used by overlay merging).
    /// </summary>
    internal void AddChildNode(DeviceTreeNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        node.Parent = this;
        _children.Add(node);
    }

    /// <summary>
    /// <para>按名称查找属性,未找到返回 null。</para>
    /// Finds a property by name, or null if not found.
    /// </summary>
    public DeviceTreeProperty? FindProperty(string name)
        => _properties.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// <para>按名称查找子节点,未找到返回 null。</para>
    /// Finds a child node by name, or null if not found.
    /// </summary>
    public DeviceTreeNode? FindChild(string name)
        => _children.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// <para>按名称移除属性,找到并移除返回 true,否则返回 false。</para>
    /// Removes the property with the specified name; returns true if it was found and removed, otherwise false.
    /// </summary>
    public bool RemoveProperty(string name)
        => _properties.RemoveAll(p => p.Name == name) > 0;

    /// <summary>
    /// <para>按名称移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// Removes the child node with the specified name; returns true if it was found and removed, otherwise false.
    /// </summary>
    public bool RemoveChild(string name)
    {
        var child = FindChild(name);
        return child is not null && RemoveChild(child);
    }

    /// <summary>
    /// <para>按引用移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// Removes the specified child node; returns true if it was found and removed, otherwise false.
    /// </summary>
    public bool RemoveChild(DeviceTreeNode node)
    {
        if (!_children.Remove(node)) return false;
        node.Parent = null;
        return true;
    }

    /// <summary>
    /// <para>获取节点在树中的完整路径(如 /soc/i2c@0)。根节点返回 /，其余节点跳过空名称的根段，
    /// 与 DTS 写入器生成的行号映射键保持一致(避免 //soc 双斜杠)。</para>
    /// Gets the full path of the node in the tree (e.g. /soc/i2c@0). The root returns / and empty-name root
    /// segments are skipped, consistent with the line-map keys produced by the DTS writer (avoiding a
    /// leading // such as //soc).
    /// </summary>
    public string GetPath()
    {
        var parts = new List<string>();
        var current = this;
        while (current is not null)
        {
            if (current.Name.Length > 0)
            {
                parts.Add(current.Name);
            }

            current = current.Parent;
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    /// <inheritdoc />
    public override string ToString() => GetPath();
}

/// <summary>
/// <para>表示完整的设备树,提供解析、序列化和导航能力。</para>
/// Represents a complete device tree, providing parsing, serialization, and navigation capabilities.
/// </summary>
public sealed class DeviceTree
{
    /// <summary>根节点。</summary>
    public DeviceTreeNode Root { get; } = new("/") { Name = "" };

    /// <summary>内存保留条目列表。</summary>
    public List<MemoryReservation> MemoryReservations { get; } = [];

    /// <summary>引导 CPU 物理 ID。</summary>
    public uint BootCpuId { get; set; }

    /// <summary>FDT 格式版本。</summary>
    public uint Version { get; set; } = FdtConstants.FdtVersion;

    /// <summary>
    /// <para>按路径查找节点,未找到返回 null。</para>
    /// Finds a node by path, or null if not found.
    /// </summary>
    public DeviceTreeNode? FindNode(string path)
    {
        if (path == "/")
        {
            return Root;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        foreach (var segment in segments)
        {
            current = current?.FindChild(segment);
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }
}