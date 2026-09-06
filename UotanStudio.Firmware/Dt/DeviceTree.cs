using System.Text;

namespace UotanStudio.Firmware.Dt;

public sealed class DeviceTreeProperty
{
    private byte[] _value = [];

    public string Name { get; set; } = string.Empty;

    public ReadOnlySpan<byte> Value => _value;

    public int Length => _value.Length;

    public byte[] ValueBytes => (byte[])_value.Clone();

    public bool IsEmpty => _value.Length == 0;

    public bool IsNop { get; set; }

    /// <summary>
    /// Replaces the property value (for the DTS parser to backfill label/phandle references).
    /// <para>替换属性值(仅供 DTS 解析器回填 label/phandle 引用)。</para>
    /// </summary>
    internal void ReplaceValue(ReadOnlySpan<byte> value)
    {
        _value = value.ToArray();
    }

    /// <summary>
    /// Initializes a property with the specified name and value.
    /// <para>使用指定名称和值初始化属性。</para>
    /// </summary>
    public DeviceTreeProperty(string name, ReadOnlySpan<byte> value)
    {
        Name = name;
        _value = value.ToArray();
    }

    /// <summary>
    /// Initializes a property with the specified name and string value.
    /// <para>使用指定名称和字符串值初始化属性(UTF-8 编码,不含结尾 NUL)。</para>
    /// </summary>
    public DeviceTreeProperty(string name, string value)
        : this(name, Encoding.UTF8.GetBytes(value))
    {
    }

    /// <summary>
    /// Gets the property value as a string (UTF-8, trailing NUL trimmed).
    /// <para>以字符串形式获取属性值(UTF-8,去除结尾 NUL)。</para>
    /// </summary>
    public string GetString()
    {
        var end = _value.AsSpan().IndexOf((byte)0);
        var span = end >= 0 ? _value.AsSpan(0, end) : _value.AsSpan();
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Parses the property value as an array of 32-bit big-endian cells.
    /// <para>将属性值解析为 32 位大端单元数组。</para>
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

    /// <summary>
    /// Returns a string representation of this value.
    /// <para>返回此值的字符串表示。</para>
    /// </summary>
    public override string ToString() => $"{Name} = {_value.Length} bytes";
}

public sealed class DeviceTreeNode
{
    private readonly List<DeviceTreeProperty> _properties = [];
    private readonly List<DeviceTreeNode> _children = [];

    public string Name { get; set; } = string.Empty;

    public string? UnitAddress { get; set; }

    public DeviceTreeNode? Parent { get; set; }

    public IReadOnlyList<DeviceTreeProperty> Properties => _properties;

    public IReadOnlyList<DeviceTreeNode> Children => _children;

    /// <summary>
    /// Initializes a node with the specified name.
    /// <para>使用指定名称初始化节点。</para>
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
    /// Adds a property.
    /// <para>添加一个属性。</para>
    /// </summary>
    public DeviceTreeProperty AddProperty(string name, ReadOnlySpan<byte> value)
    {
        var property = new DeviceTreeProperty(name, value);
        _properties.Add(property);
        return property;
    }

    /// <summary>
    /// Adds a string property.
    /// <para>添加一个字符串属性。</para>
    /// </summary>
    public DeviceTreeProperty AddPropertyString(string name, string value)
    {
        var property = new DeviceTreeProperty(name, value);
        _properties.Add(property);
        return property;
    }

    /// <summary>
    /// Adds a property with 32-bit big-endian cells.
    /// <para>添加一个 32 位单元数组属性(大端)。</para>
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
    /// Adds a child node.
    /// <para>添加一个子节点。</para>
    /// </summary>
    public DeviceTreeNode AddChild(string name)
    {
        var child = new DeviceTreeNode(name) { Parent = this };
        _children.Add(child);
        return child;
    }

    /// <summary>
    /// Attaches an existing node as a child of this node (used by overlay merging).
    /// <para>将已存在的节点作为子节点挂接到当前节点(供 overlay 合并等场景使用)。</para>
    /// </summary>
    internal void AddChildNode(DeviceTreeNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        node.Parent = this;
        _children.Add(node);
    }

    /// <summary>
    /// Finds a property by name, or null if not found.
    /// <para>按名称查找属性,未找到返回 null。</para>
    /// </summary>
    public DeviceTreeProperty? FindProperty(string name)
        => _properties.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Finds a child node by name, or null if not found.
    /// <para>按名称查找子节点,未找到返回 null。</para>
    /// </summary>
    public DeviceTreeNode? FindChild(string name)
        => _children.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// Removes the property with the specified name; returns true if it was found and removed, otherwise false.
    /// <para>按名称移除属性,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    public bool RemoveProperty(string name)
        => _properties.RemoveAll(p => p.Name == name) > 0;

    /// <summary>
    /// Removes the child node with the specified name; returns true if it was found and removed, otherwise false.
    /// <para>按名称移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    public bool RemoveChild(string name)
    {
        var child = FindChild(name);
        return child is not null && RemoveChild(child);
    }

    /// <summary>
    /// Removes the specified child node; returns true if it was found and removed, otherwise false.
    /// <para>按引用移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    public bool RemoveChild(DeviceTreeNode node)
    {
        if (!_children.Remove(node)) return false;
        node.Parent = null;
        return true;
    }

    /// <summary>
    /// Gets the full path of the node in the tree (e.g. /soc/i2c@0).
    /// <para>获取节点在树中的完整路径(如 /soc/i2c@0)。</para>
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

    /// <summary>
    /// Returns a string representation of this value.
    /// <para>返回此值的字符串表示。</para>
    /// </summary>
    public override string ToString() => GetPath();
}

public sealed class DeviceTree
{
    public DeviceTreeNode Root { get; } = new("/") { Name = "" };

    public List<MemoryReservation> MemoryReservations { get; } = [];

    public uint BootCpuId { get; set; }

    public uint Version { get; set; } = FdtConstants.FdtVersion;

    /// <summary>
    /// Finds a node by path, or null if not found.
    /// <para>按路径查找节点,未找到返回 null。</para>
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
