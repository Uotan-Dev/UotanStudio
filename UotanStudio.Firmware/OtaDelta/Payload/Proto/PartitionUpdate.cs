using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.Payload.Proto;

/// <summary>
/// In-memory protobuf message describing the update of one partition: its name, postinstall
/// options, old and new partition identities, and the ordered install operations.
/// <para>描述单个分区更新的内存 protobuf 消息：分区名称、postinstall 选项、新旧分区身份，以及有序的安装操作列表。</para>
/// </summary>
public sealed class PartitionUpdate : IMessage<PartitionUpdate>
{
    private static readonly FieldCodec<InstallOperation> RepeatedOperationsCodec = FieldCodec.ForMessage(66, InstallOperation.Parser);

    private UnknownFieldSet? _unknownFields;
    private string _partitionName = string.Empty;

    /// <summary>
    /// Shared parser that decodes a wire-format <c>PartitionUpdate</c> message.
    /// <para>解码线格式 <c>PartitionUpdate</c> 消息的共享分析器。</para>
    /// </summary>
    /// <value>A singleton <see cref="MessageParser{PartitionUpdate}"/> that creates a fresh instance for each parse.
    /// <para>每次解析都创建新实例的单例 <see cref="MessageParser{PartitionUpdate}"/>。</para></value>
    public static MessageParser<PartitionUpdate> Parser { get; } = new(() => new PartitionUpdate());

    /// <summary>
    /// Reflection descriptor placeholder for the message.
    /// <para>消息的反射描述符占位。</para>
    /// </summary>
    /// <value>Always <see langword="null"/>; this hand-written message carries no generated descriptor metadata.
    /// <para>恒为 <see langword="null"/>；该手写消息不携带生成的描述符元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>
    /// Initializes an empty update whose fields hold the protobuf defaults.
    /// <para>初始化空更新，各字段均为 protobuf 默认值。</para>
    /// </summary>
    public PartitionUpdate()
    {
    }

    /// <summary>
    /// Initializes a copy of another update, cloning partition identities, operations and unknown fields.
    /// <para>初始化另一更新的拷贝，克隆分区身份、操作列表与未知字段。</para>
    /// </summary>
    /// <param name="other">
    /// Update to copy; optional postinstall fields are carried over with their presence preserved.
    /// <para>要拷贝的更新；可选的 postinstall 字段连同其存在性一并带过。</para>
    /// </param>
    public PartitionUpdate(PartitionUpdate other) : this()
    {
        _partitionName = other._partitionName;
        RunPostinstall = other.RunPostinstall;
        PostinstallPath = other.PostinstallPath;
        FilesystemType = other.FilesystemType;
        PostinstallOptional = other.PostinstallOptional;

        OldPartitionInfo = other.OldPartitionInfo?.Clone();
        NewPartitionInfo = other.NewPartitionInfo?.Clone();
        Operations = other.Operations.Clone();
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>
    /// Creates an independent copy of this update.
    /// <para>创建本更新的独立拷贝。</para>
    /// </summary>
    /// <returns>A new <see cref="PartitionUpdate"/> whose partition info, operation list and
    /// unknown fields share no mutable state with this instance.
    /// <para>新的 <see cref="PartitionUpdate"/>，其分区信息、操作列表与未知字段和本实例不共享可变状态。</para></returns>
    public PartitionUpdate Clone() => new(this);

    /// <summary>Protobuf field number for the partition name field. <para>分区名称字段的 protobuf 字段编号。</para></summary>
    public const int PartitionNameFieldNumber = 1;

    /// <summary>
    /// Name of the partition this update applies to (field 1).
    /// <para>本更新所应用分区的名称（字段 1）。</para>
    /// </summary>
    /// <value>Partition name string; assigning <see langword="null"/> stores an empty string, and an
    /// empty name is the protobuf default that is omitted when serialized.
    /// <para>分区名称字符串；赋 <see langword="null"/> 时存储空串，空名称为 protobuf 默认值并在序列化时省略。</para></value>
    public string PartitionName
    {
        get => _partitionName;
        set => _partitionName = value ?? string.Empty;
    }

    /// <summary>Gets or sets the optional update_engine RunPostinstall field, preserving protobuf presence.
    /// <para>获取或设置可选的 update_engine RunPostinstall 字段，并保留 protobuf 存在性。</para></summary>
    /// <value>Tri-state flag serialized as field 16 when present; <see langword="null"/> means the
    /// field is absent from the message and is not written.
    /// <para>存在时作为字段 16 序列化的三态标志；<see langword="null"/> 表示消息中缺少该字段且不写入。</para></value>
    public bool? RunPostinstall { get; set; }

    /// <summary>Gets or sets the optional update_engine PostinstallPath field, preserving protobuf presence.
    /// <para>获取或设置可选的 update_engine PostinstallPath 字段，并保留 protobuf 存在性。</para></summary>
    /// <value>Optional path string serialized as field 26 when present; <see langword="null"/> means
    /// the field is absent from the message and is not written.
    /// <para>存在时作为字段 26 序列化的可选路径串；<see langword="null"/> 表示消息中缺少该字段且不写入。</para></value>
    public string? PostinstallPath { get; set; }

    /// <summary>Gets or sets the optional update_engine FilesystemType field, preserving protobuf presence.
    /// <para>获取或设置可选的 update_engine FilesystemType 字段，并保留 protobuf 存在性。</para></summary>
    /// <value>Optional filesystem type string serialized as field 34 when present;
    /// <see langword="null"/> means the field is absent from the message and is not written.
    /// <para>存在时作为字段 34 序列化的可选文件系统类型串；<see langword="null"/> 表示消息中缺少该字段且不写入。</para></value>
    public string? FilesystemType { get; set; }

    /// <summary>Gets or sets the optional update_engine PostinstallOptional field, preserving protobuf presence.
    /// <para>获取或设置可选的 update_engine PostinstallOptional 字段，并保留 protobuf 存在性。</para></summary>
    /// <value>Tri-state flag serialized as field 72 when present; <see langword="null"/> means the
    /// field is absent from the message and is not written.
    /// <para>存在时作为字段 72 序列化的三态标志；<see langword="null"/> 表示消息中缺少该字段且不写入。</para></value>
    public bool? PostinstallOptional { get; set; }

    /// <summary>Protobuf field number for the new partition info field. <para>新分区信息字段的 protobuf 字段编号。</para></summary>
    public const int NewPartitionInfoFieldNumber = 7;

    /// <summary>Identifies the old partition info protobuf field. <para>标识旧分区信息的 protobuf 字段。</para></summary>
    public const int OldPartitionInfoFieldNumber = 6;

    /// <summary>Gets or sets the immutable source partition identity. <para>获取或设置不可变源分区身份。</para></summary>
    /// <value>Source partition info serialized as field 6 when present; <see langword="null"/> means
    /// the field is absent, and merging or parsing creates the instance on demand.
    /// <para>存在时作为字段 6 序列化的源分区信息；<see langword="null"/> 表示字段缺失，合并或解析时会按需创建实例。</para></value>
    public PartitionInfo? OldPartitionInfo { get; set; }

    /// <summary>
    /// Partition identity describing the target state of this update (field 7).
    /// <para>描述本更新目标状态的分区身份（字段 7）。</para>
    /// </summary>
    /// <value>New partition info serialized as field 7 when present; <see langword="null"/> means the
    /// field is absent, and merging or parsing creates the instance on demand.
    /// <para>存在时作为字段 7 序列化的新分区信息；<see langword="null"/> 表示字段缺失，合并或解析时会按需创建实例。</para></value>
    public PartitionInfo? NewPartitionInfo { get; set; }

    /// <summary>Protobuf field number for the install operations list. <para>安装操作列表的 protobuf 字段编号。</para></summary>
    public const int OperationsFieldNumber = 8;

    /// <summary>
    /// Ordered install operations that transform the partition from its old state to the new state (field 8).
    /// <para>把分区从旧状态变换到新状态的有序安装操作（字段 8）。</para>
    /// </summary>
    /// <value>Mutable repeated list of <see cref="InstallOperation"/> messages; an empty list
    /// contributes nothing to the serialized message.
    /// <para>可变的 <see cref="InstallOperation"/> 消息重复列表；空列表对序列化消息无贡献。</para></value>
    public RepeatedField<InstallOperation> Operations { get; } = [];

    /// <summary>
    /// Determines whether this update equals another object by delegating to the typed comparison.
    /// <para>通过转向强类型比较，判定本更新是否与另一对象相等。</para>
    /// </summary>
    /// <param name="obj">
    /// Reference to compare; only a <see cref="PartitionUpdate"/> instance can be equal.
    /// <para>待比较的引用；仅 <see cref="PartitionUpdate"/> 实例可能相等。</para>
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an update whose fields, optional
    /// presence and retained unknown fields match this message; <see langword="false"/> for
    /// <see langword="null"/> or any other type. <para>当 <paramref name="obj"/> 为分区更新且其字段、可选字段的
    /// 存在性以及保留的未知字段都与本消息一致时返回 <see langword="true"/>；对 <see langword="null"/>
    /// 或其他类型返回 <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as PartitionUpdate);

    /// <summary>
    /// Compares this update field by field against another message, including optional presence and unknown fields.
    /// <para>逐字段比较本更新与另一消息，包含可选值存在性与未知字段。</para>
    /// </summary>
    /// <param name="other">
    /// Message to compare; <see langword="null"/> never equals an instance.
    /// <para>要比较的消息；<see langword="null"/> 与任何实例都不相等。</para>
    /// </param>
    /// <returns><see langword="true"/> when the other message is the same instance or every name,
    /// postinstall, partition-info, operation and unknown-field value matches.
    /// <para>当另一消息为同一实例，或所有名称、postinstall、分区信息、操作与未知字段值均匹配时返回 <see langword="true"/>。</para></returns>
    public bool Equals(PartitionUpdate? other)
        => other is not null && (ReferenceEquals(other, this) || (PartitionName == other.PartitionName && RunPostinstall == other.RunPostinstall && PostinstallPath == other.PostinstallPath && FilesystemType == other.FilesystemType && PostinstallOptional == other.PostinstallOptional && Equals(OldPartitionInfo, other.OldPartitionInfo) && Equals(NewPartitionInfo, other.NewPartitionInfo) && Operations.Equals(other.Operations) && Equals(_unknownFields, other._unknownFields)));

    /// <summary>
    /// Computes the hash code of this update.
    /// <para>计算本更新的哈希代码。</para>
    /// </summary>
    /// <returns>
    /// Base value <c>1</c> XOR-folded with the <see cref="PartitionName"/> hash only when the name is
    /// non-empty, the <see cref="OldPartitionInfo"/>, <see cref="NewPartitionInfo"/> and unknown-field
    /// hashes only when those messages are present, the <see cref="Operations"/> hash always, and the
    /// four optional postinstall hashes folded in as <c>0</c> when absent; equal updates therefore hash
    /// alike. <para>以 <c>1</c> 为基数，仅当 <see cref="PartitionName"/> 非空时异或其哈希代码；
    /// <see cref="OldPartitionInfo"/>、<see cref="NewPartitionInfo"/> 与未知字段的哈希代码仅在相应消息存在时
    /// 异或；<see cref="Operations"/> 始终异或，四个可选 postinstall 字段缺失时按 <c>0</c> 计入，因此相等的
    /// 更新哈希相同。</para>
    /// </returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (_partitionName.Length != 0)
        {
            hash ^= PartitionName.GetHashCode();
        }

        if (NewPartitionInfo != null)
        {
            hash ^= NewPartitionInfo.GetHashCode();
        }

        hash ^= Operations.GetHashCode();
        if (OldPartitionInfo is not null) hash ^= OldPartitionInfo.GetHashCode();
        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        hash ^= RunPostinstall?.GetHashCode() ?? 0;
        hash ^= PostinstallPath?.GetHashCode() ?? 0;
        hash ^= FilesystemType?.GetHashCode() ?? 0;
        hash ^= PostinstallOptional?.GetHashCode() ?? 0;
        return hash;
    }

    /// <summary>
    /// Formats this update as text.
    /// <para>将本更新格式化为文本。</para>
    /// </summary>
    /// <returns>
    /// The runtime type name <c>PartitionUpdate</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>PartitionUpdate</c>，不呈现字段值。</para>
    /// </returns>
    public override string ToString() => GetType().Name;

    /// <summary>
    /// Serializes each field that is set or differs from its protobuf default, then any retained unknown fields.
    /// <para>序列化所有已设置或不同于 protobuf 默认值的字段，随后写入保留的未知字段。</para>
    /// </summary>
    /// <param name="output">
    /// Stream sink that receives the wire-format message bytes; optional postinstall fields are
    /// written only when presence is recorded.
    /// <para>接收线格式消息字节的输出流；可选的 postinstall 字段仅在记录了存在性时写入。</para>
    /// </param>
    public void WriteTo(CodedOutputStream output)
    {
        if (RunPostinstall is { } value16) { output.WriteRawTag(16); output.WriteBool(value16); }
        if (PostinstallPath is { } value26) { output.WriteRawTag(26); output.WriteString(value26); }
        if (FilesystemType is { } value34) { output.WriteRawTag(34); output.WriteString(value34); }
        if (PostinstallOptional is { } value72) { output.WriteRawTag(72); output.WriteBool(value72); }

        if (OldPartitionInfo is not null) { output.WriteRawTag(50); output.WriteMessage(OldPartitionInfo); }
        if (_partitionName.Length != 0)
        {
            output.WriteRawTag(10);
            output.WriteString(PartitionName);
        }

        if (NewPartitionInfo != null)
        {
            output.WriteRawTag(58);
            output.WriteMessage(NewPartitionInfo);
        }

        Operations.WriteTo(output, RepeatedOperationsCodec);
        _unknownFields?.WriteTo(output);
    }

    /// <summary>
    /// Computes the exact encoded size of this message in the protobuf wire format.
    /// <para>计算本消息在 protobuf 线格式下的精确编码大小。</para>
    /// </summary>
    /// <returns>Size in bytes counting tags, nested message lengths and unknown fields; <c>0</c> for
    /// a message whose fields are all absent or default.
    /// <para>以字节计的大小，包含标签、嵌套消息长度与未知字段；所有字段均缺失或为默认值时为 <c>0</c>。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (RunPostinstall is { } value16) size += 2;
        if (PostinstallPath is { } value26) size += 1 + CodedOutputStream.ComputeStringSize(value26);
        if (FilesystemType is { } value34) size += 1 + CodedOutputStream.ComputeStringSize(value34);
        if (PostinstallOptional is { } value72) size += 2;

        if (OldPartitionInfo is not null) size += 1 + CodedOutputStream.ComputeMessageSize(OldPartitionInfo);
        if (_partitionName.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(PartitionName);
        }

        if (NewPartitionInfo != null)
        {
            size += 1 + CodedOutputStream.ComputeMessageSize(NewPartitionInfo);
        }

        size += Operations.CalculateSize(RepeatedOperationsCodec);
        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>
    /// Merges another update into this one: present optional fields overwrite, a non-empty name
    /// replaces the current one, partition info instances are created on demand and merged, and
    /// operations accumulate.
    /// <para>按 protobuf 语义把另一更新合并进本消息：存在的可选字段覆盖，非空名称替换当前名称，分区信息实例按需创建并合并，操作列表累加。</para>
    /// </summary>
    /// <param name="other">
    /// Source message; <see langword="null"/> leaves this instance unchanged.
    /// <para>来源消息；为 <see langword="null"/> 时本实例保持不变。</para>
    /// </param>
    public void MergeFrom(PartitionUpdate other)
    {
        if (other is null)
        {
            return;
        }

        if (other.RunPostinstall is not null) RunPostinstall = other.RunPostinstall;
        if (other.PostinstallPath is not null) PostinstallPath = other.PostinstallPath;
        if (other.FilesystemType is not null) FilesystemType = other.FilesystemType;
        if (other.PostinstallOptional is not null) PostinstallOptional = other.PostinstallOptional;
        if (other._partitionName.Length != 0)
        {
            PartitionName = other._partitionName;
        }

        if (other.NewPartitionInfo != null)
        {
            (NewPartitionInfo ??= new PartitionInfo()).MergeFrom(other.NewPartitionInfo);
        }
        if (other.OldPartitionInfo is not null) (OldPartitionInfo ??= new PartitionInfo()).MergeFrom(other.OldPartitionInfo);

        Operations.Add(other.Operations);
        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>
    /// Reads wire-format fields until <c>ReadTag</c> returns 0 at the end of the stream or
    /// enclosing group, storing unrecognized tags as unknown fields.
    /// <para>持续读取线格式字段直到 <c>ReadTag</c> 在流或外层分组末尾返回 0；无法识别的标签存为未知字段。</para>
    /// </summary>
    /// <param name="input">
    /// Stream positioned at the start of the message body; partition info sub-messages are created
    /// on first sight of their tag and decoded into the existing instance, while repeated operation
    /// tags append.
    /// <para>定位在消息体起始处的流；分区信息子消息在首次遇到其标签时创建并解码进已有实例，重复的操作标签则累加。</para>
    /// </param>
    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                default:
                    _unknownFields = UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
                    break;
                case 16: RunPostinstall = input.ReadBool(); break;
                case 26: PostinstallPath = input.ReadString(); break;
                case 34: FilesystemType = input.ReadString(); break;
                case 72: PostinstallOptional = input.ReadBool(); break;
                case 10:
                    PartitionName = input.ReadString();
                    break;
                case 58:
                    input.ReadMessage(NewPartitionInfo ??= new PartitionInfo());
                    break;
                case 50:
                    input.ReadMessage(OldPartitionInfo ??= new PartitionInfo());
                    break;
                case 66:
                    Operations.AddEntriesFrom(input, RepeatedOperationsCodec);
                    break;
            }
        }
    }
}