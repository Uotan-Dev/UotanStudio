using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.Payload.Proto;

/// <summary>
/// Hand-written stand-in for the generated protobuf <c>DynamicPartitionGroup</c> message: one super
/// partition group with its name, virtual size and member partition names.
/// <para>生成代码 <c>DynamicPartitionGroup</c> protobuf 消息的手写替身：super 分区组，包含名称、虚拟大小与所属分区名。</para>
/// </summary>
public sealed class DynamicPartitionGroup : IMessage<DynamicPartitionGroup>
{
    private static readonly FieldCodec<string> RepeatedPartitionNamesCodec = FieldCodec.ForString(26);

    private UnknownFieldSet? _unknownFields;
    private string _name = string.Empty;

    /// <summary>Parser that decodes wire-format data into new <see cref="DynamicPartitionGroup"/>
    /// instances. <para>将线格式数据解码为新的 <see cref="DynamicPartitionGroup"/> 实例的分析器。</para></summary>
    /// <value>Static parser instance backed by a default-constructor factory.
    /// <para>由默认构造函数工厂支撑的静态分析器实例。</para></value>
    public static MessageParser<DynamicPartitionGroup> Parser { get; } = new(() => new DynamicPartitionGroup());

    /// <summary>Proto reflection descriptor hook required by <see cref="IMessage{T}"/>.
    /// <para><see cref="IMessage{T}"/> 所要求的 .proto 反射描述符钩子。</para></summary>
    /// <value>Always <see langword="null"/>; this stand-in embeds no reflection metadata.
    /// <para>恒为 <see langword="null"/>；本替身不内嵌任何反射元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Creates an empty message whose fields all hold their default values.
    /// <para>创建所有字段均为默认值的空消息。</para></summary>
    public DynamicPartitionGroup()
    {
    }

    /// <summary>Creates a copy of <paramref name="other"/>, including its unknown fields.
    /// <para>创建 <paramref name="other"/> 的副本，并包含其未知字段。</para></summary>
    /// <param name="other">Message whose field values, partition names and unknown fields are copied.
    /// <para>被复制字段值、分区名与未知字段的消息。</para></param>
    /// <exception cref="NullReferenceException"><paramref name="other"/> is <see langword="null"/>.
    /// <para><paramref name="other"/> 为 <see langword="null"/>。</para></exception>
    public DynamicPartitionGroup(DynamicPartitionGroup other) : this()
    {
        _name = other._name;
        Size = other.Size;
        PartitionNames = other.PartitionNames.Clone();
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Returns an independent copy of this message.
    /// <para>返回本消息的独立副本。</para></summary>
    /// <returns>A new <see cref="DynamicPartitionGroup"/> with the same field values, a copied partition-name
    /// list and a cloned unknown-field set.
    /// <para>字段值相同、分区名列表与未知字段集均已复制的新 <see cref="DynamicPartitionGroup"/>。</para></returns>
    public DynamicPartitionGroup Clone() => new(this);

    /// <summary>Proto field number <c>1</c> of <see cref="Name"/>.
    /// <para><see cref="Name"/> 的 proto 字段号 <c>1</c>。</para></summary>
    public const int NameFieldNumber = 1;

    /// <summary>Name of the partition group.
    /// <para>分区组的名称。</para></summary>
    /// <value>Group name as stored in the payload; <see cref="string.Empty"/> when unset, and empty names
    /// are omitted when serialized. Assigning <see langword="null"/> stores an empty string.
    /// <para>payload 中记录的组名；未设置为 <see cref="string.Empty"/>，空名称在序列化时被省略。
    /// 赋 <see langword="null"/> 会存入空字符串。</para></value>
    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <summary>Proto field number <c>2</c> of <see cref="Size"/>.
    /// <para><see cref="Size"/> 的 proto 字段号 <c>2</c>。</para></summary>
    public const int SizeFieldNumber = 2;

    /// <summary>Virtual size reserved for the group, in bytes.
    /// <para>为分区组预留的虚拟大小，以字节计。</para></summary>
    /// <value>Byte budget shared by the group's partitions; <c>0</c> is the default and is omitted when
    /// serialized. <para>组内各分区共用的字节配额；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public ulong Size { get; set; }

    /// <summary>Proto field number <c>3</c> of <see cref="PartitionNames"/>.
    /// <para><see cref="PartitionNames"/> 的 proto 字段号 <c>3</c>。</para></summary>
    public const int PartitionNamesFieldNumber = 3;

    /// <summary>Names of the partitions that belong to the group.
    /// <para>属于该分区的分区名列表。</para></summary>
    /// <value>Mutable, never-<see langword="null"/> list in payload order; empty when the message declares
    /// no partitions. <para>按 payload 顺序存放的可变列表，永不为 <see langword="null"/>；消息未声明分区时为空。</para></value>
    public RepeatedField<string> PartitionNames { get; } = [];

    /// <summary>Determines whether this message equals another object.
    /// <para>判定本消息是否与另一对象相等。</para></summary>
    /// <param name="obj">Reference to compare; only a <see cref="DynamicPartitionGroup"/> instance can be
    /// equal. <para>待比较的引用；仅 <see cref="DynamicPartitionGroup"/> 实例可能相等。</para></param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a group whose fields and unknown
    /// fields match this message; <see langword="false"/> for <see langword="null"/> or any other type.
    /// <para>当 <paramref name="obj"/> 为分区组，且其字段与未知字段均与本消息一致时返回
    /// <see langword="true"/>；对 <see langword="null"/> 或其他类型返回 <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as DynamicPartitionGroup);

    /// <summary>Compares this message to another <see cref="DynamicPartitionGroup"/> by field values and
    /// unknown fields. <para>按字段值与未知字段将本消息与另一个 <see cref="DynamicPartitionGroup"/> 比较。</para></summary>
    /// <param name="other">Instance to compare; <see langword="null"/> is never equal.
    /// <para>待比较的实例；<see langword="null"/> 永不视为相等。</para></param>
    /// <returns><see langword="true"/> when both messages carry identical <see cref="Name"/>,
    /// <see cref="Size"/>, <see cref="PartitionNames"/> and unknown fields; reference identity
    /// short-circuits to <see langword="true"/>.
    /// <para>当两条消息的 <see cref="Name"/>、<see cref="Size"/>、<see cref="PartitionNames"/> 与未知字段
    /// 完全一致时为 <see langword="true"/>；引用相同直接返回 <see langword="true"/>。</para></returns>
    public bool Equals(DynamicPartitionGroup? other)
        => other is not null && (ReferenceEquals(other, this) || (Name == other.Name && Size == other.Size && PartitionNames.Equals(other.PartitionNames) && Equals(_unknownFields, other._unknownFields)));

    /// <summary>Computes the hash code of this message.
    /// <para>计算本消息的哈希代码。</para></summary>
    /// <returns>Base value <c>1</c> XOR-folded with the <see cref="Name"/> and <see cref="Size"/> hashes
    /// only when they differ from their defaults (empty text and <c>0</c>), with the
    /// <see cref="PartitionNames"/> hash always, and with the unknown-field hash when unknown fields are
    /// present; equal messages therefore hash alike.
    /// <para>以 <c>1</c> 为基数，仅当 <see cref="Name"/> 与 <see cref="Size"/> 不等于默认值（空文本与
    /// <c>0</c>）时异或其哈希代码，<see cref="PartitionNames"/> 始终计入，存在未知字段时同样异或其哈希
    /// 代码；因此相等的消息哈希相同。</para></returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (_name.Length != 0)
        {
            hash ^= Name.GetHashCode();
        }

        if (Size != 0UL)
        {
            hash ^= Size.GetHashCode();
        }

        hash ^= PartitionNames.GetHashCode();
        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>Formats this message as text.
    /// <para>将本消息格式化为文本。</para></summary>
    /// <returns>The runtime type name <c>DynamicPartitionGroup</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>DynamicPartitionGroup</c>；不呈现字段值。</para></returns>
    public override string ToString() => GetType().Name;

    /// <summary>Writes this message to a coded stream in protobuf wire format.
    /// <para>以 protobuf 线格式将本消息写入编码流。</para></summary>
    /// <param name="output">Destination stream. An empty <see cref="Name"/> and a <see cref="Size"/> of
    /// <c>0</c> are skipped, every partition name is written as a separate field 3 occurrence, and stored
    /// unknown fields come last.
    /// <para>目标流。空的 <see cref="Name"/> 与为 <c>0</c> 的 <see cref="Size"/> 会被跳过，每个分区名作为
    /// 字段 3 的一次独立写入，已保存的未知字段最后写出。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        if (_name.Length != 0)
        {
            output.WriteRawTag(10);
            output.WriteString(Name);
        }

        if (Size != 0UL)
        {
            output.WriteRawTag(16);
            output.WriteUInt64(Size);
        }

        PartitionNames.WriteTo(output, RepeatedPartitionNamesCodec);
        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the encoded length of this message in bytes.
    /// <para>计算本消息编码后的字节长度。</para></summary>
    /// <returns>Byte size of what <see cref="WriteTo"/> emits, including all partition-name occurrences and
    /// unknown fields. <para><see cref="WriteTo"/> 将写出的字节数，含所有分区名条目与未知字段。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (_name.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Name);
        }

        if (Size != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(Size);
        }

        size += PartitionNames.CalculateSize(RepeatedPartitionNamesCodec);
        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Merges another message into this one.
    /// <para>将另一条消息合并进本消息。</para></summary>
    /// <param name="other">Source message. A non-empty <see cref="Name"/> and a non-default
    /// <see cref="Size"/> overwrite the current values, <see cref="PartitionNames"/> entries are appended
    /// (not replaced), and unknown fields are merged; <see langword="null"/> makes the call a silent
    /// no-op. <para>来源消息。非空的 <see cref="Name"/> 与非默认值的 <see cref="Size"/> 会覆盖当前值，
    /// <see cref="PartitionNames"/> 条目按追加（而非替换）合并，未知字段同样合并；传入
    /// <see langword="null"/> 时本调用静默无操作。</para></param>
    public void MergeFrom(DynamicPartitionGroup other)
    {
        if (other is null)
        {
            return;
        }

        if (other._name.Length != 0)
        {
            Name = other._name;
        }

        if (other.Size != 0UL)
        {
            Size = other.Size;
        }

        PartitionNames.Add(other.PartitionNames);
        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>Fills this message by reading protobuf wire-format tags until the end of the message.
    /// <para>读取 protobuf 线格式标签直到消息结束，并据此填充本消息。</para></summary>
    /// <param name="input">Coded input positioned at the message's first tag. Reading stops at tag
    /// <c>0</c>; recognized tags set <see cref="Name"/> and <see cref="Size"/> or append to
    /// <see cref="PartitionNames"/>, and any other tag is kept in the unknown-field set.
    /// <para>定位到消息首个标签处的编码输入。读到标签 <c>0</c> 时停止；可识别的标签会写入
    /// <see cref="Name"/> 与 <see cref="Size"/> 或追加到 <see cref="PartitionNames"/>，其余标签保存到
    /// 未知字段集。</para></param>
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
                case 10:
                    Name = input.ReadString();
                    break;
                case 16:
                    Size = input.ReadUInt64();
                    break;
                case 26:
                    PartitionNames.AddEntriesFrom(input, RepeatedPartitionNamesCodec);
                    break;
            }
        }
    }
}

/// <summary>
/// Hand-written stand-in for the generated protobuf <c>DynamicPartitionMetadata</c> message: the
/// dynamic-partition capability block of an OTA payload, holding the partition groups plus snapshot,
/// VABC, CoW-version and compression settings.
/// <para>生成代码 <c>DynamicPartitionMetadata</c> protobuf 消息的手写替身：OTA payload 的动态分区能力块，
/// 包含分区组以及快照、VABC、CoW 版本与压缩设置。</para>
/// </summary>
public sealed class DynamicPartitionMetadata : IMessage<DynamicPartitionMetadata>
{
    private static readonly FieldCodec<DynamicPartitionGroup> RepeatedGroupsCodec = FieldCodec.ForMessage(10, DynamicPartitionGroup.Parser);

    private UnknownFieldSet? _unknownFields;
    private string _vabcCompressionParam = string.Empty;

    /// <summary>Parser that decodes wire-format data into new <see cref="DynamicPartitionMetadata"/>
    /// instances. <para>将线格式数据解码为新的 <see cref="DynamicPartitionMetadata"/> 实例的分析器。</para></summary>
    /// <value>Static parser instance backed by a default-constructor factory.
    /// <para>由默认构造函数工厂支撑的静态分析器实例。</para></value>
    public static MessageParser<DynamicPartitionMetadata> Parser { get; } = new(() => new DynamicPartitionMetadata());

    /// <summary>Proto reflection descriptor hook required by <see cref="IMessage{T}"/>.
    /// <para><see cref="IMessage{T}"/> 所要求的 .proto 反射描述符钩子。</para></summary>
    /// <value>Always <see langword="null"/>; this stand-in embeds no reflection metadata.
    /// <para>恒为 <see langword="null"/>；本替身不内嵌任何反射元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Creates an empty message whose fields all hold their default values.
    /// <para>创建所有字段均为默认值的空消息。</para></summary>
    public DynamicPartitionMetadata()
    {
    }

    /// <summary>Creates a copy of <paramref name="other"/>, including its groups and unknown fields.
    /// <para>创建 <paramref name="other"/> 的副本，包含其分区组与未知字段。</para></summary>
    /// <param name="other">Message whose field values, cloned groups and unknown fields are copied.
    /// <para>被复制字段值、克隆后的分区组与未知字段的消息。</para></param>
    /// <exception cref="NullReferenceException"><paramref name="other"/> is <see langword="null"/>.
    /// <para><paramref name="other"/> 为 <see langword="null"/>。</para></exception>
    public DynamicPartitionMetadata(DynamicPartitionMetadata other) : this()
    {
        Groups = other.Groups.Clone();
        SnapshotEnabled = other.SnapshotEnabled;
        VabcEnabled = other.VabcEnabled;
        _vabcCompressionParam = other._vabcCompressionParam;
        CowVersion = other.CowVersion;
        CompressionFactor = other.CompressionFactor;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Returns an independent copy of this message.
    /// <para>返回本消息的独立副本。</para></summary>
    /// <returns>A new <see cref="DynamicPartitionMetadata"/> with the same field values, a cloned group
    /// list and a cloned unknown-field set.
    /// <para>字段值相同、分区组列表与未知字段集均已复制的新 <see cref="DynamicPartitionMetadata"/>。</para></returns>
    public DynamicPartitionMetadata Clone() => new(this);

    /// <summary>Proto field number <c>1</c> of <see cref="Groups"/>.
    /// <para><see cref="Groups"/> 的 proto 字段号 <c>1</c>。</para></summary>
    public const int GroupsFieldNumber = 1;

    /// <summary>Partition groups described by the payload.
    /// <para>payload 描述的分区组列表。</para></summary>
    /// <value>Mutable, never-<see langword="null"/> list in payload order; empty when the message declares
    /// no groups. <para>按 payload 顺序存放的可变列表，永不为 <see langword="null"/>；消息未声明分组时为空。</para></value>
    public RepeatedField<DynamicPartitionGroup> Groups { get; } = [];

    /// <summary>Proto field number <c>2</c> of <see cref="SnapshotEnabled"/>.
    /// <para><see cref="SnapshotEnabled"/> 的 proto 字段号 <c>2</c>。</para></summary>
    public const int SnapshotEnabledFieldNumber = 2;

    /// <summary>Whether the payload declares update-snapshot support.
    /// <para>payload 是否声明支持更新快照。</para></summary>
    /// <value><see langword="true"/> when snapshots are enabled; <see langword="false"/> is the default and
    /// is omitted when serialized. <para>启用快照时为 <see langword="true"/>；<see langword="false"/>
    /// 为默认值，序列化时会被省略。</para></value>
    public bool SnapshotEnabled { get; set; }

    /// <summary>Proto field number <c>3</c> of <see cref="VabcEnabled"/>.
    /// <para><see cref="VabcEnabled"/> 的 proto 字段号 <c>3</c>。</para></summary>
    public const int VabcEnabledFieldNumber = 3;

    /// <summary>Whether the payload declares Virtual A/B compression (VABC) support.
    /// <para>payload 是否声明支持 Virtual A/B 压缩（VABC）。</para></summary>
    /// <value><see langword="true"/> when VABC is enabled; <see langword="false"/> is the default and is
    /// omitted when serialized. <para>启用 VABC 时为 <see langword="true"/>；<see langword="false"/>
    /// 为默认值，序列化时会被省略。</para></value>
    public bool VabcEnabled { get; set; }

    /// <summary>Proto field number <c>4</c> of <see cref="VabcCompressionParam"/>.
    /// <para><see cref="VabcCompressionParam"/> 的 proto 字段号 <c>4</c>。</para></summary>
    public const int VabcCompressionParamFieldNumber = 4;

    /// <summary>VABC compression parameter string recorded by the payload.
    /// <para>payload 记录的 VABC 压缩参数字符串。</para></summary>
    /// <value>The parameter exactly as stored in the message; <see cref="string.Empty"/> when unset, and
    /// empty values are omitted when serialized. Assigning <see langword="null"/> stores an empty string.
    /// <para>与消息中完全一致的参数值；未设置为 <see cref="string.Empty"/>，空值在序列化时被省略。
    /// 赋 <see langword="null"/> 会存入空字符串。</para></value>
    public string VabcCompressionParam
    {
        get => _vabcCompressionParam;
        set => _vabcCompressionParam = value ?? string.Empty;
    }

    /// <summary>Proto field number <c>5</c> of <see cref="CowVersion"/>.
    /// <para><see cref="CowVersion"/> 的 proto 字段号 <c>5</c>。</para></summary>
    public const int CowVersionFieldNumber = 5;

    /// <summary>Copy-on-write (CoW) format version declared by the payload.
    /// <para>payload 声明的写时复制（CoW）格式版本。</para></summary>
    /// <value>Version number as encoded; <c>0</c> is the default and is omitted when serialized.
    /// <para>按编码保存的版本号；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public uint CowVersion { get; set; }

    /// <summary>Proto field number <c>7</c> of <see cref="CompressionFactor"/>.
    /// <para><see cref="CompressionFactor"/> 的 proto 字段号 <c>7</c>。</para></summary>
    public const int CompressionFactorFieldNumber = 7;

    /// <summary>Compression factor applied when sizing the copy-on-write device.
    /// <para>计算写时复制设备大小时使用的压缩系数。</para></summary>
    /// <value>Factor as encoded by the payload; <c>0</c> is the default and is omitted when serialized.
    /// <para>payload 编码的系数值；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public ulong CompressionFactor { get; set; }

    /// <summary>Determines whether this message equals another object.
    /// <para>判定本消息是否与另一对象相等。</para></summary>
    /// <param name="obj">Reference to compare; only a <see cref="DynamicPartitionMetadata"/> instance can
    /// be equal. <para>待比较的引用；仅 <see cref="DynamicPartitionMetadata"/> 实例可能相等。</para></param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is metadata whose groups, flags,
    /// compression parameter, CoW version, compression factor and unknown fields match this message;
    /// <see langword="false"/> for <see langword="null"/> or any other type.
    /// <para>当 <paramref name="obj"/> 为动态分区元数据，且其分区组、标志、压缩参数、CoW 版本、压缩系数
    /// 与未知字段均与本消息一致时返回 <see langword="true"/>；对 <see langword="null"/> 或其他类型返回
    /// <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as DynamicPartitionMetadata);

    /// <summary>Compares this message to another <see cref="DynamicPartitionMetadata"/> by field values
    /// and unknown fields. <para>按字段值与未知字段将本消息与另一个 <see cref="DynamicPartitionMetadata"/> 比较。</para></summary>
    /// <param name="other">Instance to compare; <see langword="null"/> is never equal.
    /// <para>待比较的实例；<see langword="null"/> 永不视为相等。</para></param>
    /// <returns><see langword="true"/> when both messages carry identical groups, flags, compression
    /// parameter, CoW version, compression factor and unknown fields; reference identity short-circuits
    /// to <see langword="true"/>. <para>当两条消息的分区组、标志、压缩参数、CoW 版本、压缩系数与未知字段
    /// 完全一致时为 <see langword="true"/>；引用相同直接返回 <see langword="true"/>。</para></returns>
    public bool Equals(DynamicPartitionMetadata? other)
        => other is not null && (ReferenceEquals(other, this) || (Groups.Equals(other.Groups) && SnapshotEnabled == other.SnapshotEnabled && VabcEnabled == other.VabcEnabled && VabcCompressionParam == other.VabcCompressionParam && CowVersion == other.CowVersion && CompressionFactor == other.CompressionFactor && Equals(_unknownFields, other._unknownFields)));

    /// <summary>Computes the hash code of this message.
    /// <para>计算本消息的哈希代码。</para></summary>
    /// <returns>Base value <c>1</c> XOR-folded with the <see cref="Groups"/> hash always, and with the
    /// <see cref="SnapshotEnabled"/>, <see cref="VabcEnabled"/>, <see cref="VabcCompressionParam"/>,
    /// <see cref="CowVersion"/>, <see cref="CompressionFactor"/> and unknown-field hashes only when those
    /// fields differ from their defaults; equal messages therefore hash alike.
    /// <para>以 <c>1</c> 为基数，<see cref="Groups"/> 的哈希代码始终异或进去，
    /// <see cref="SnapshotEnabled"/>、<see cref="VabcEnabled"/>、<see cref="VabcCompressionParam"/>、
    /// <see cref="CowVersion"/>、<see cref="CompressionFactor"/> 与未知字段的哈希代码仅在偏离默认值时
    /// 异或；因此相等的消息哈希相同。</para></returns>
    public override int GetHashCode()
    {
        var hash = 1;
        hash ^= Groups.GetHashCode();
        if (SnapshotEnabled)
        {
            hash ^= SnapshotEnabled.GetHashCode();
        }

        if (VabcEnabled)
        {
            hash ^= VabcEnabled.GetHashCode();
        }

        if (_vabcCompressionParam.Length != 0)
        {
            hash ^= VabcCompressionParam.GetHashCode();
        }

        if (CowVersion != 0U)
        {
            hash ^= CowVersion.GetHashCode();
        }

        if (CompressionFactor != 0UL)
        {
            hash ^= CompressionFactor.GetHashCode();
        }

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>Formats this message as text.
    /// <para>将本消息格式化为文本。</para></summary>
    /// <returns>The runtime type name <c>DynamicPartitionMetadata</c> alone; field values are not
    /// rendered. <para>仅返回运行时类型名 <c>DynamicPartitionMetadata</c>；不呈现字段值。</para></returns>
    public override string ToString() => GetType().Name;

    /// <summary>Writes this message to a coded stream in protobuf wire format.
    /// <para>以 protobuf 线格式将本消息写入编码流。</para></summary>
    /// <param name="output">Destination stream. Every group is written first, <see cref="SnapshotEnabled"/>
    /// and <see cref="VabcEnabled"/> are emitted only when <see langword="true"/>, and empty or
    /// <c>0</c>-valued scalar fields are skipped; stored unknown fields come last.
    /// <para>目标流。先写出全部分区组，<see cref="SnapshotEnabled"/> 与 <see cref="VabcEnabled"/>
    /// 仅在为 <see langword="true"/> 时写入，空值或 <c>0</c> 的标量字段会被跳过，已保存的未知字段最后写出。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        Groups.WriteTo(output, RepeatedGroupsCodec);
        if (SnapshotEnabled)
        {
            output.WriteRawTag(16);
            output.WriteBool(SnapshotEnabled);
        }

        if (VabcEnabled)
        {
            output.WriteRawTag(24);
            output.WriteBool(VabcEnabled);
        }

        if (_vabcCompressionParam.Length != 0)
        {
            output.WriteRawTag(34);
            output.WriteString(VabcCompressionParam);
        }

        if (CowVersion != 0U)
        {
            output.WriteRawTag(40);
            output.WriteUInt32(CowVersion);
        }

        if (CompressionFactor != 0UL)
        {
            output.WriteRawTag(56);
            output.WriteUInt64(CompressionFactor);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the encoded length of this message in bytes.
    /// <para>计算本消息编码后的字节长度。</para></summary>
    /// <returns>Byte size of what <see cref="WriteTo"/> emits, including all group occurrences and
    /// unknown fields; <c>0</c> for an all-default message.
    /// <para><see cref="WriteTo"/> 将写出的字节数（含全部分区组条目与未知字段）；全默认值消息为 <c>0</c>。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        size += Groups.CalculateSize(RepeatedGroupsCodec);
        if (SnapshotEnabled)
        {
            size += 2;
        }

        if (VabcEnabled)
        {
            size += 2;
        }

        if (_vabcCompressionParam.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(VabcCompressionParam);
        }

        if (CowVersion != 0U)
        {
            size += 1 + CodedOutputStream.ComputeUInt32Size(CowVersion);
        }

        if (CompressionFactor != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(CompressionFactor);
        }

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Merges another message into this one.
    /// <para>将另一条消息合并进本消息。</para></summary>
    /// <param name="other">Source message. Groups are appended (not replaced); the flags, compression
    /// parameter, CoW version and compression factor overwrite the current value only when the source
    /// holds a non-default one, and unknown fields are merged; <see langword="null"/> makes the call a
    /// silent no-op. <para>来源消息。分区组按追加（而非替换）合并；标志、压缩参数、CoW 版本与压缩系数
    /// 仅在来源为非默认值时覆盖当前值，未知字段同样合并；传入 <see langword="null"/> 时本调用静默无操作。</para></param>
    public void MergeFrom(DynamicPartitionMetadata other)
    {
        if (other is null)
        {
            return;
        }

        Groups.Add(other.Groups);
        if (other.SnapshotEnabled)
        {
            SnapshotEnabled = other.SnapshotEnabled;
        }

        if (other.VabcEnabled)
        {
            VabcEnabled = other.VabcEnabled;
        }

        if (other._vabcCompressionParam.Length != 0)
        {
            VabcCompressionParam = other._vabcCompressionParam;
        }

        if (other.CowVersion != 0U)
        {
            CowVersion = other.CowVersion;
        }

        if (other.CompressionFactor != 0UL)
        {
            CompressionFactor = other.CompressionFactor;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>Fills this message by reading protobuf wire-format tags until the end of the message.
    /// <para>读取 protobuf 线格式标签直到消息结束，并据此填充本消息。</para></summary>
    /// <param name="input">Coded input positioned at the message's first tag. Reading stops at tag
    /// <c>0</c>; recognized tags append to <see cref="Groups"/> or set the remaining scalar fields, and
    /// any other tag is kept in the unknown-field set.
    /// <para>定位到消息首个标签处的编码输入。读到标签 <c>0</c> 时停止；可识别的标签会追加到
    /// <see cref="Groups"/> 或写入其余标量字段，其余标签保存到未知字段集。</para></param>
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
                case 10:
                    Groups.AddEntriesFrom(input, RepeatedGroupsCodec);
                    break;
                case 16:
                    SnapshotEnabled = input.ReadBool();
                    break;
                case 24:
                    VabcEnabled = input.ReadBool();
                    break;
                case 34:
                    VabcCompressionParam = input.ReadString();
                    break;
                case 40:
                    CowVersion = input.ReadUInt32();
                    break;
                case 56:
                    CompressionFactor = input.ReadUInt64();
                    break;
            }
        }
    }
}