using System.Text;

namespace UotanStudio.Firmware.DeviceTree.Dt;

/// <summary>
/// A named value attached to a device-tree node, keeping the raw bytes in FDT storage order.
/// <para>挂在设备树节点上的具名值，按 FDT 存储顺序保留原始字节。</para>
/// </summary>
public sealed class DeviceTreeProperty
{
    private byte[] _value = [];

    /// <summary>Property name used for exact, case-sensitive lookup.
    /// <para>用于精确、区分大小写查找的属性名称。</para></summary>
    /// <value>The name as written in the tree; assignment performs no validation. Defaults to <see cref="string.Empty"/>.
    /// <para>树中写入的名称；赋值不做校验。默认为 <see cref="string.Empty"/>。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw property value bytes in big-endian storage order.
    /// <para>按大端存储顺序排列的属性原始值字节。</para></summary>
    /// <value>A view over the current backing array; a later value replacement can leave an earlier span stale. Empty when the property carries no data.
    /// <para>对当前后台数组的视图；后续替换值可能使先前取得的 span 过期。属性无数据时为空。</para></value>
    public ReadOnlySpan<byte> Value => _value;

    /// <summary>Byte length of the property value.
    /// <para>属性值的字节长度。</para></summary>
    /// <value>Number of bytes in <see cref="Value"/>. <para><see cref="Value"/> 中的字节数。</para></value>
    public int Length => _value.Length;

    /// <summary>Detached copy of the property value bytes.
    /// <para>属性值字节的独立副本。</para></summary>
    /// <value>A new array the caller may mutate; later changes to the property do not affect it.
    /// <para>调用方可修改的新数组；属性的后续变更不会影响它。</para></value>
    public byte[] ValueBytes => (byte[])_value.Clone();

    /// <summary>Whether the property carries no value bytes.
    /// <para>属性是否不携带任何值字节。</para></summary>
    /// <value><see langword="true"/> when the value length is zero, otherwise <see langword="false"/>.
    /// <para>值长度为零时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>Marks the property as a nop placeholder emitted as <c>FDT_NOP</c> when writing DTB.
    /// <para>将属性标记为 nop 占位符，写出 DTB 时以 <c>FDT_NOP</c> 形式发出。</para></summary>
    /// <value><see langword="true"/> when the entry is a placeholder; writable, defaults to <see langword="false"/>.
    /// <para>条目为占位符时为 <see langword="true"/>；可写，默认为 <see langword="false"/>。</para></value>
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
    /// <param name="name">Property name, stored as given. <para>属性名称，按原样存储。</para></param>
    /// <param name="value">Value bytes; the content is copied, so later edits to the caller's buffer are not observed.
    /// <para>值字节；内容会被复制，因此调用方缓冲区的后续修改不会被观察到。</para></param>
    public DeviceTreeProperty(string name, ReadOnlySpan<byte> value)
    {
        Name = name;
        _value = value.ToArray();
    }

    /// <summary>
    /// Initializes a property with the specified name and string value.
    /// <para>使用指定名称和字符串值初始化属性(UTF-8 编码,不含结尾 NUL)。</para>
    /// </summary>
    /// <param name="name">Property name, stored as given. <para>属性名称，按原样存储。</para></param>
    /// <param name="value">String encoded to UTF-8 bytes without a terminating NUL. <para>将编码为不带结尾 NUL 的 UTF-8 字节的字符串。</para></param>
    public DeviceTreeProperty(string name, string value)
        : this(name, Encoding.UTF8.GetBytes(value))
    {
    }

    /// <summary>
    /// Gets the property value as a string (UTF-8, trailing NUL trimmed).
    /// <para>以字符串形式获取属性值(UTF-8,去除结尾 NUL)。</para>
    /// </summary>
    /// <returns>The decoded text up to the first NUL byte; the whole value when it contains no NUL, and an empty string when the value is empty.
    /// <para>解码到首个 NUL 字节为止的文本；值不含 NUL 时返回整个值，值为空时返回空字符串。</para></returns>
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
    /// <returns>One entry per complete 4-byte group; trailing bytes that do not form a full cell are ignored. An empty array when the value is shorter than 4 bytes.
    /// <para>每个完整的 4 字节组对应一个元素；不足一格的尾部字节被忽略。值短于 4 字节时返回空数组。</para></returns>
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

/// <summary>
/// A node in the in-memory device tree, owning an ordered set of properties and child nodes.
/// <para>内存设备树中的节点，持有一组按顺序排列的属性和子节点。</para>
/// </summary>
public sealed class DeviceTreeNode
{
    private readonly List<DeviceTreeProperty> _properties = [];
    private readonly List<DeviceTreeNode> _children = [];

    /// <summary>Node name as supplied, including the <c>@unit-address</c> suffix when present.
    /// <para>按传入形式保存的节点名称，包含其中的 <c>@单元地址</c> 后缀。</para></summary>
    /// <value>The full name used by exact-match lookups and <c>GetPath</c>; defaults to <see cref="string.Empty"/> (the root node).
    /// <para>精确匹配查找与 <c>GetPath</c> 使用的完整名称；默认为 <see cref="string.Empty"/>（根节点）。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Unit address parsed from the <c>@</c> suffix of the name given to the constructor.
    /// <para>从构造函数传入名称的 <c>@</c> 后缀解析出的单元地址。</para></summary>
    /// <value>The text after <c>@</c> when the constructed name contains a non-empty suffix, otherwise <see langword="null"/>; writable independently of <see cref="Name"/>.
    /// <para>构造名称含有非空后缀时为 <c>@</c> 之后的文本，否则为 <see langword="null"/>；可独立于 <see cref="Name"/> 写入。</para></value>
    public string? UnitAddress { get; set; }

    /// <summary>Node that contains this node.
    /// <para>包含当前节点的父节点。</para></summary>
    /// <value>The parent, or <see langword="null"/> for the root and for detached nodes; maintained by <c>AddChild</c>, <c>AddChildNode</c> and <c>RemoveChild</c>.
    /// <para>父节点；根节点和已脱离的节点为 <see langword="null"/>；由 <c>AddChild</c>、<c>AddChildNode</c> 与 <c>RemoveChild</c> 维护。</para></value>
    public DeviceTreeNode? Parent { get; set; }

    /// <summary>Properties declared directly on this node.
    /// <para>当前节点上直接声明的属性。</para></summary>
    /// <value>Live read-only view in insertion order; <see cref="AddProperty"/>, <see cref="AddPropertyString"/> and <see cref="AddPropertyCells"/> append to it.
    /// <para>按插入顺序的实时只读视图；<see cref="AddProperty"/>、<see cref="AddPropertyString"/> 与 <see cref="AddPropertyCells"/> 会向其追加。</para></value>
    public IReadOnlyList<DeviceTreeProperty> Properties => _properties;

    /// <summary>Child nodes declared directly under this node.
    /// <para>当前节点下的直接子节点。</para></summary>
    /// <value>Live read-only view in insertion order; <see cref="AddChild"/> and <see cref="AddChildNode(DeviceTreeNode)"/> append to it.
    /// <para>按插入顺序的实时只读视图；<see cref="AddChild"/> 与 <see cref="AddChildNode(DeviceTreeNode)"/> 会向其追加。</para></value>
    public IReadOnlyList<DeviceTreeNode> Children => _children;

    /// <summary>
    /// Initializes a node with the specified name.
    /// <para>使用指定名称初始化节点。</para>
    /// </summary>
    /// <param name="name">Node name; when it contains <c>@</c> followed by at least one character, the suffix is stored as <see cref="UnitAddress"/>.
    /// <para>节点名称；当其中包含 <c>@</c> 且后面至少还有一个字符时，后缀会存入 <see cref="UnitAddress"/>。</para></param>
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
    /// <param name="name">Property name, stored as given. <para>属性名称，按原样存储。</para></param>
    /// <param name="value">Value bytes; copied into the new property. <para>值字节；会被复制进新属性。</para></param>
    /// <returns>The appended <see cref="DeviceTreeProperty"/>. <para>已追加的 <see cref="DeviceTreeProperty"/>。</para></returns>
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
    /// <param name="name">Property name, stored as given. <para>属性名称，按原样存储。</para></param>
    /// <param name="value">String stored as UTF-8 bytes without a terminating NUL. <para>以不带结尾 NUL 的 UTF-8 字节存储的字符串。</para></param>
    /// <returns>The appended <see cref="DeviceTreeProperty"/>. <para>已追加的 <see cref="DeviceTreeProperty"/>。</para></returns>
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
    /// <param name="name">Property name, stored as given. <para>属性名称，按原样存储。</para></param>
    /// <param name="cells">Cell values, serialized most-significant byte first; an empty array yields a zero-length property.
    /// <para>单元值，按最高有效字节在前序列化；空数组产生零长度属性。</para></param>
    /// <returns>The appended <see cref="DeviceTreeProperty"/> whose value is <c>cells.Length × 4</c> bytes.
    /// <para>已追加的 <see cref="DeviceTreeProperty"/>，其值长度为 <c>cells.Length × 4</c> 字节。</para></returns>
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
    /// <param name="name">Name for the new child; a <c>@</c> suffix is parsed into its unit address.
    /// <para>新子节点的名称；<c>@</c> 后缀会被解析为其单元地址。</para></param>
    /// <returns>The created child with <see cref="Parent"/> set to this node. <para>新建的子节点，其 <see cref="Parent"/> 已指向当前节点。</para></returns>
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
    /// <param name="name">Exact, case-sensitive property name. <para>精确、区分大小写的属性名称。</para></param>
    /// <returns>The first property with that name in insertion order, or <see langword="null"/>.
    /// <para>按插入顺序第一个同名属性，未找到时为 <see langword="null"/>。</para></returns>
    public DeviceTreeProperty? FindProperty(string name)
        => _properties.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Finds a child node by name, or null if not found.
    /// <para>按名称查找子节点,未找到返回 null。</para>
    /// </summary>
    /// <param name="name">Exact, case-sensitive child node name. <para>精确、区分大小写的子节点名称。</para></param>
    /// <returns>The first child with that name in insertion order, or <see langword="null"/>.
    /// <para>按插入顺序第一个同名子节点，未找到时为 <see langword="null"/>。</para></returns>
    public DeviceTreeNode? FindChild(string name)
        => _children.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// Removes the property with the specified name; returns true if it was found and removed, otherwise false.
    /// <para>按名称移除属性,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    /// <param name="name">Exact, case-sensitive property name; every matching property is removed.
    /// <para>精确、区分大小写的属性名称；所有同名属性都会被移除。</para></param>
    /// <returns><see langword="true"/> when this call removes at least one property; otherwise <see langword="false"/>.
    /// <para>本次调用至少移除一个属性时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool RemoveProperty(string name)
        => _properties.RemoveAll(p => p.Name == name) > 0;

    /// <summary>
    /// Removes the child node with the specified name; returns true if it was found and removed, otherwise false.
    /// <para>按名称移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    /// <param name="name">Exact, case-sensitive child node name; only the first match is removed.
    /// <para>精确、区分大小写的子节点名称；仅移除第一个匹配项。</para></param>
    /// <returns><see langword="true"/> when this call removes a matching child; otherwise <see langword="false"/>.
    /// <para>本次调用移除匹配的子节点时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool RemoveChild(string name)
    {
        var child = FindChild(name);
        return child is not null && RemoveChild(child);
    }

    /// <summary>
    /// Removes the specified child node; returns true if it was found and removed, otherwise false.
    /// <para>按引用移除子节点,找到并移除返回 true,否则返回 false。</para>
    /// </summary>
    /// <param name="node">The child to remove, matched by reference. <para>要移除的子节点，按引用匹配。</para></param>
    /// <returns><see langword="true"/> when the node was in the child list and is now detached (its <see cref="Parent"/> cleared); otherwise <see langword="false"/>.
    /// <para>节点原本在子列表中并已被脱离（其 <see cref="Parent"/> 已清空）时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
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
    /// <returns>An absolute path joining every non-empty ancestor name with <c>/</c>; the root node yields <c>"/"</c>. Unit addresses are included because they are part of <see cref="Name"/>.
    /// <para>用 <c>/</c> 连接所有非空祖先名称得到的绝对路径；根节点返回 <c>"/"</c>。单元地址因属于 <see cref="Name"/> 也会被包含。</para></returns>
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

/// <summary>
/// An in-memory device tree: a root node, memory reservations, and the header fields carried into DTB output.
/// <para>内存中的设备树：根节点、内存保留区，以及写入 DTB 时携带的头部字段。</para>
/// </summary>
public sealed class DeviceTree
{
    internal Dictionary<string, string> LabelPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>The unnamed root node of the tree.
    /// <para>树的无名根节点。</para></summary>
    /// <value>A <see cref="DeviceTreeNode"/> whose <see cref="DeviceTreeNode.Name"/> is empty, so its path is <c>"/"</c> and its <see cref="DeviceTreeNode.Parent"/> is <see langword="null"/>.
    /// <para>一个 <see cref="DeviceTreeNode"/>，其 <see cref="DeviceTreeNode.Name"/> 为空，因此路径为 <c>"/"</c> 且 <see cref="DeviceTreeNode.Parent"/> 为 <see langword="null"/>。</para></value>
    public DeviceTreeNode Root { get; } = new("/") { Name = "" };

    /// <summary>Memory reservation entries corresponding to the DTB reserve map.
    /// <para>对应 DTB 保留映射的内存保留区条目。</para></summary>
    /// <value>Mutable list, empty by default; each <see cref="MemoryReservation"/> marks a physical range the kernel must not use.
    /// <para>默认清空的可变列表；每个 <see cref="MemoryReservation"/> 标记一段内核不得使用的物理区间。</para></value>
    public List<MemoryReservation> MemoryReservations { get; } = [];

    /// <summary>Identifier of the boot CPU recorded in the DTB header.
    /// <para>记录在 DTB 头部中的启动 CPU 标识。</para></summary>
    /// <value>Writable 32-bit value, <c>0</c> unless a parser or the caller sets it.
    /// <para>可写的 32 位值；除非解析器或调用方设置，否则为 <c>0</c>。</para></value>
    public uint BootCpuId { get; set; }

    /// <summary>Device-tree format version reported in the DTB header.
    /// <para>写入 DTB 头部并上报的设备树格式版本。</para></summary>
    /// <value>Writable 32-bit value, initialized to <c>FdtConstants.FdtVersion</c>.
    /// <para>可写的 32 位值，初始化为 <c>FdtConstants.FdtVersion</c>。</para></value>
    public uint Version { get; set; } = FdtConstants.FdtVersion;

    /// <summary>
    /// Finds a node by path, or null if not found.
    /// <para>按路径查找节点,未找到返回 null。</para>
    /// </summary>
    /// <param name="path">Absolute path such as <c>/soc/i2c@0</c>; empty segments are ignored and <c>"/"</c> denotes the root. Each segment must match a child <see cref="DeviceTreeNode.Name"/> exactly, unit address included.
    /// <para>以 <c>/soc/i2c@0</c> 这类绝对路径给出；空路径段被忽略，<c>"/"</c> 表示根节点。每一段必须与子节点的 <see cref="DeviceTreeNode.Name"/> 完全一致（含单元地址）。</para></param>
    /// <returns>The node at that path, or <see langword="null"/> when any segment does not resolve.
    /// <para>该路径上的节点；任一路径段无法解析时返回 <see langword="null"/>。</para></returns>
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