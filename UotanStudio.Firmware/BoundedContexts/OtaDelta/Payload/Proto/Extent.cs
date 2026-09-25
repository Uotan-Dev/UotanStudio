using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

/// <summary>
/// Hand-written stand-in for the generated protobuf <c>Extent</c> message of an OTA payload: a block
/// range given by its starting block and its length.
/// <para>OTA payload 中生成代码 <c>Extent</c> protobuf 消息的手写替身：由起始块与长度给出的块区间。</para>
/// </summary>
public sealed class Extent : IMessage<Extent>
{
    private UnknownFieldSet? _unknownFields;

    /// <summary>Parser that decodes wire-format data into new <see cref="Extent"/> instances.
    /// <para>将线格式数据解码为新的 <see cref="Extent"/> 实例的分析器。</para></summary>
    /// <value>Static parser instance backed by a default-constructor factory.
    /// <para>由默认构造函数工厂支撑的静态分析器实例。</para></value>
    public static MessageParser<Extent> Parser { get; } = new(() => new Extent());

    /// <summary>Proto reflection descriptor hook required by <see cref="IMessage{T}"/>.
    /// <para><see cref="IMessage{T}"/> 所要求的 .proto 反射描述符钩子。</para></summary>
    /// <value>Always <see langword="null"/>; this stand-in embeds no reflection metadata.
    /// <para>恒为 <see langword="null"/>；本替身不内嵌任何反射元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Creates an empty message whose fields all hold their default values.
    /// <para>创建所有字段均为默认值的空消息。</para></summary>
    public Extent()
    {
    }

    /// <summary>Creates a copy of <paramref name="other"/>, including its unknown fields.
    /// <para>创建 <paramref name="other"/> 的副本，并包含其未知字段。</para></summary>
    /// <param name="other">Message whose field values and unknown fields are copied.
    /// <para>被复制字段值与未知字段的消息。</para></param>
    /// <exception cref="System.NullReferenceException"><paramref name="other"/> is <see langword="null"/>.
    /// <para><paramref name="other"/> 为 <see langword="null"/>。</para></exception>
    public Extent(Extent other) : this()
    {
        StartBlock = other.StartBlock;
        NumBlocks = other.NumBlocks;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Returns an independent copy of this message.
    /// <para>返回本消息的独立副本。</para></summary>
    /// <returns>A new <see cref="Extent"/> with the same field values and a cloned unknown-field set.
    /// <para>字段值相同且未知字段集已复制的新 <see cref="Extent"/>。</para></returns>
    public Extent Clone() => new(this);

    /// <summary>Proto field number <c>1</c> of <see cref="StartBlock"/>.
    /// <para><see cref="StartBlock"/> 的 proto 字段号 <c>1</c>。</para></summary>
    public const int StartBlockFieldNumber = 1;

    /// <summary>Starting block index of the extent.
    /// <para>区间的起始块索引。</para></summary>
    /// <value>Zero-based index of the first covered block; <c>0</c> is the default and is omitted when
    /// serialized. <para>所覆盖首块的从零开始索引；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public ulong StartBlock { get; set; }

    /// <summary>Proto field number <c>2</c> of <see cref="NumBlocks"/>.
    /// <para><see cref="NumBlocks"/> 的 proto 字段号 <c>2</c>。</para></summary>
    public const int NumBlocksFieldNumber = 2;

    /// <summary>Length of the extent in blocks.
    /// <para>区间以块计的长度。</para></summary>
    /// <value>Number of consecutive blocks covered; <c>0</c> is the default and is omitted when
    /// serialized. <para>所覆盖的连续块数量；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public ulong NumBlocks { get; set; }

    /// <summary>Determines whether this message equals another object.
    /// <para>判定本消息是否与另一对象相等。</para></summary>
    /// <param name="obj">Reference to compare; only an <see cref="Extent"/> instance can be equal.
    /// <para>待比较的引用；仅 <see cref="Extent"/> 实例可能相等。</para></param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an <see cref="Extent"/> whose
    /// fields and unknown fields match this message; <see langword="false"/> for <see langword="null"/>
    /// or any other type. <para>当 <paramref name="obj"/> 为 <see cref="Extent"/>，且其字段与未知字段
    /// 均与本消息一致时返回 <see langword="true"/>；对 <see langword="null"/> 或其他类型返回
    /// <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as Extent);

    /// <summary>Compares this message to another <see cref="Extent"/> by field values and unknown
    /// fields. <para>按字段值与未知字段将本消息与另一个 <see cref="Extent"/> 比较。</para></summary>
    /// <param name="other">Instance to compare; <see langword="null"/> is never equal.
    /// <para>待比较的实例；<see langword="null"/> 永不视为相等。</para></param>
    /// <returns><see langword="true"/> when both messages carry identical <see cref="StartBlock"/>,
    /// <see cref="NumBlocks"/> and unknown fields; reference identity short-circuits to
    /// <see langword="true"/>. <para>当两条消息的 <see cref="StartBlock"/>、<see cref="NumBlocks"/>
    /// 与未知字段完全一致时为 <see langword="true"/>；引用相同直接返回 <see langword="true"/>。</para></returns>
    public bool Equals(Extent? other)
        => other is not null && (ReferenceEquals(other, this) || (StartBlock == other.StartBlock && NumBlocks == other.NumBlocks && Equals(_unknownFields, other._unknownFields)));

    /// <summary>Computes the hash code of this message.
    /// <para>计算本消息的哈希代码。</para></summary>
    /// <returns>Base value <c>1</c> XOR-folded with the hash codes of <see cref="StartBlock"/> and
    /// <see cref="NumBlocks"/> only when they differ from their default <c>0</c>, plus the unknown-field
    /// hash when unknown fields are present; equal messages therefore hash alike.
    /// <para>以 <c>1</c> 为基数，仅当 <see cref="StartBlock"/> 与 <see cref="NumBlocks"/> 不等于默认值
    /// <c>0</c> 时异或其哈希代码，并在存在未知字段时异或其哈希代码；因此相等的消息哈希相同。</para></returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (StartBlock != 0UL)
        {
            hash ^= StartBlock.GetHashCode();
        }

        if (NumBlocks != 0UL)
        {
            hash ^= NumBlocks.GetHashCode();
        }

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>Formats this message as text.
    /// <para>将本消息格式化为文本。</para></summary>
    /// <returns>The runtime type name <c>Extent</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>Extent</c>；不呈现字段值。</para></returns>
    public override string ToString() => GetType().Name;

    /// <summary>Writes this message to a coded stream in protobuf wire format.
    /// <para>以 protobuf 线格式将本消息写入编码流。</para></summary>
    /// <param name="output">Destination stream. Fields holding their default value <c>0</c> are skipped,
    /// and any stored unknown fields are written last.
    /// <para>目标流。值为默认值 <c>0</c> 的字段会被跳过，已保存的未知字段最后写出。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        if (StartBlock != 0UL)
        {
            output.WriteRawTag(8);
            output.WriteUInt64(StartBlock);
        }

        if (NumBlocks != 0UL)
        {
            output.WriteRawTag(16);
            output.WriteUInt64(NumBlocks);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the encoded length of this message in bytes.
    /// <para>计算本消息编码后的字节长度。</para></summary>
    /// <returns>Byte size of what <see cref="WriteTo"/> emits, including unknown fields; <c>0</c> for an
    /// all-default message. <para><see cref="WriteTo"/> 将写出的字节数（含未知字段）；全默认值消息为
    /// <c>0</c>。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (StartBlock != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(StartBlock);
        }

        if (NumBlocks != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(NumBlocks);
        }

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Merges another message into this one.
    /// <para>将另一条消息合并进本消息。</para></summary>
    /// <param name="other">Source message. Non-default scalars overwrite the current value and unknown
    /// fields are appended; <see langword="null"/> makes the call a silent no-op.
    /// <para>来源消息。非默认值的标量会覆盖当前值，未知字段会被追加；传入 <see langword="null"/> 时本调用静默无操作。</para></param>
    public void MergeFrom(Extent other)
    {
        if (other is null)
        {
            return;
        }

        if (other.StartBlock != 0UL)
        {
            StartBlock = other.StartBlock;
        }

        if (other.NumBlocks != 0UL)
        {
            NumBlocks = other.NumBlocks;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>Fills this message by reading protobuf wire-format tags until the end of the message.
    /// <para>读取 protobuf 线格式标签直到消息结束，并据此填充本消息。</para></summary>
    /// <param name="input">Coded input positioned at the message's first tag. Reading stops at tag
    /// <c>0</c>; recognized tags set <see cref="StartBlock"/> and <see cref="NumBlocks"/>, and any
    /// other tag is kept in the unknown-field set.
    /// <para>定位到消息首个标签处的编码输入。读到标签 <c>0</c> 时停止；可识别的标签会写入
    /// <see cref="StartBlock"/> 与 <see cref="NumBlocks"/>，其余标签保存到未知字段集。</para></param>
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
                case 8:
                    StartBlock = input.ReadUInt64();
                    break;
                case 16:
                    NumBlocks = input.ReadUInt64();
                    break;
            }
        }
    }
}

/// <summary>
/// Hand-written stand-in for the generated protobuf <c>PartitionInfo</c> message: a partition's content
/// size and the raw digest bytes recorded for it.
/// <para>OTA payload 中生成代码 <c>PartitionInfo</c> protobuf 消息的手写替身：记录分区内容大小及其原始摘要字节。</para>
/// </summary>
public sealed class PartitionInfo : IMessage<PartitionInfo>
{
    private UnknownFieldSet? _unknownFields;
    private ByteString _hash = ByteString.Empty;

    /// <summary>Parser that decodes wire-format data into new <see cref="PartitionInfo"/> instances.
    /// <para>将线格式数据解码为新的 <see cref="PartitionInfo"/> 实例的分析器。</para></summary>
    /// <value>Static parser instance backed by a default-constructor factory.
    /// <para>由默认构造函数工厂支撑的静态分析器实例。</para></value>
    public static MessageParser<PartitionInfo> Parser { get; } = new(() => new PartitionInfo());

    /// <summary>Proto reflection descriptor hook required by <see cref="IMessage{T}"/>.
    /// <para><see cref="IMessage{T}"/> 所要求的 .proto 反射描述符钩子。</para></summary>
    /// <value>Always <see langword="null"/>; this stand-in embeds no reflection metadata.
    /// <para>恒为 <see langword="null"/>；本替身不内嵌任何反射元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Creates an empty message whose fields all hold their default values.
    /// <para>创建所有字段均为默认值的空消息。</para></summary>
    public PartitionInfo()
    {
    }

    /// <summary>Creates a copy of <paramref name="other"/>, including its unknown fields.
    /// <para>创建 <paramref name="other"/> 的副本，并包含其未知字段。</para></summary>
    /// <param name="other">Message whose field values and unknown fields are copied.
    /// <para>被复制字段值与未知字段的消息。</para></param>
    /// <exception cref="System.NullReferenceException"><paramref name="other"/> is <see langword="null"/>.
    /// <para><paramref name="other"/> 为 <see langword="null"/>。</para></exception>
    public PartitionInfo(PartitionInfo other) : this()
    {
        Size = other.Size;
        _hash = other._hash;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Returns an independent copy of this message.
    /// <para>返回本消息的独立副本。</para></summary>
    /// <returns>A new <see cref="PartitionInfo"/> with the same field values and a cloned
    /// unknown-field set. <para>字段值相同且未知字段集已复制的新 <see cref="PartitionInfo"/>。</para></returns>
    public PartitionInfo Clone() => new(this);

    /// <summary>Proto field number <c>1</c> of <see cref="Size"/>.
    /// <para><see cref="Size"/> 的 proto 字段号 <c>1</c>。</para></summary>
    public const int SizeFieldNumber = 1;

    /// <summary>Content size of the partition in bytes.
    /// <para>以字节计的分区内容大小。</para></summary>
    /// <value>Uncompressed size recorded by the payload; <c>0</c> is the default and is omitted when
    /// serialized. <para>payload 记录的未压缩大小；<c>0</c> 为默认值，序列化时会被省略。</para></value>
    public ulong Size { get; set; }

    /// <summary>Proto field number <c>2</c> of <see cref="Hash"/>.
    /// <para><see cref="Hash"/> 的 proto 字段号 <c>2</c>。</para></summary>
    public const int HashFieldNumber = 2;

    /// <summary>Digest bytes recorded for the partition content.
    /// <para>为分区内容记录的摘要字节。</para></summary>
    /// <value>The hash bytes exactly as present in the payload; an empty <see cref="ByteString"/> when
    /// unset, and empty values are omitted when serialized. Assigning <see langword="null"/> stores an
    /// empty sequence. <para>与 payload 中完全一致的摘要字节；未设置时为空 <see cref="ByteString"/>，
    /// 且空值在序列化时被省略。赋 <see langword="null"/> 会存入空序列。</para></value>
    public ByteString Hash
    {
        get => _hash;
        set => _hash = value ?? ByteString.Empty;
    }

    /// <summary>Determines whether this message equals another object.
    /// <para>判定本消息是否与另一对象相等。</para></summary>
    /// <param name="obj">Reference to compare; only a <see cref="PartitionInfo"/> instance can be equal.
    /// <para>待比较的引用；仅 <see cref="PartitionInfo"/> 实例可能相等。</para></param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a <see cref="PartitionInfo"/>
    /// whose fields and unknown fields match this message; <see langword="false"/> for
    /// <see langword="null"/> or any other type. <para>当 <paramref name="obj"/> 为
    /// <see cref="PartitionInfo"/> 且其字段与未知字段与本消息一致时返回 <see langword="true"/>；
    /// 对 <see langword="null"/> 或其他类型返回 <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as PartitionInfo);

    /// <summary>Compares this message to another <see cref="PartitionInfo"/> by field values and unknown
    /// fields. <para>按字段值与未知字段将本消息与另一个 <see cref="PartitionInfo"/> 比较。</para></summary>
    /// <param name="other">Instance to compare; <see langword="null"/> is never equal.
    /// <para>待比较的实例；<see langword="null"/> 永不视为相等。</para></param>
    /// <returns><see langword="true"/> when both messages carry identical <see cref="Size"/>,
    /// <see cref="Hash"/> and unknown fields; reference identity short-circuits to
    /// <see langword="true"/>. <para>当两条消息的 <see cref="Size"/>、<see cref="Hash"/> 与未知字段
    /// 完全一致时为 <see langword="true"/>；引用相同直接返回 <see langword="true"/>。</para></returns>
    public bool Equals(PartitionInfo? other)
        => other is not null && (ReferenceEquals(other, this) || (Size == other.Size && Hash == other.Hash && Equals(_unknownFields, other._unknownFields)));

    /// <summary>Computes the hash code of this message.
    /// <para>计算本消息的哈希代码。</para></summary>
    /// <returns>Base value <c>1</c> XOR-folded with the hash codes of <see cref="Size"/> and
    /// <see cref="Hash"/> only when they differ from their defaults (<c>0</c> and an empty sequence),
    /// plus the unknown-field hash when unknown fields are present; equal messages therefore hash
    /// alike. <para>以 <c>1</c> 为基数，仅当 <see cref="Size"/> 与 <see cref="Hash"/> 不等于默认值
    /// （<c>0</c> 与空序列）时异或其哈希代码，并在存在未知字段时异或其哈希代码；因此相等的消息哈希相同。</para></returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (Size != 0UL)
        {
            hash ^= Size.GetHashCode();
        }

        if (_hash.Length != 0)
        {
            hash ^= Hash.GetHashCode();
        }

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>Formats this message as text.
    /// <para>将本消息格式化为文本。</para></summary>
    /// <returns>The runtime type name <c>PartitionInfo</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>PartitionInfo</c>；不呈现字段值。</para></returns>
    public override string ToString() => GetType().Name;

    /// <summary>Writes this message to a coded stream in protobuf wire format.
    /// <para>以 protobuf 线格式将本消息写入编码流。</para></summary>
    /// <param name="output">Destination stream. A <see cref="Size"/> of <c>0</c> and an empty
    /// <see cref="Hash"/> are skipped, and any stored unknown fields are written last.
    /// <para>目标流。<see cref="Size"/> 为 <c>0</c> 与空的 <see cref="Hash"/> 会被跳过，已保存的未知字段最后写出。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        if (Size != 0UL)
        {
            output.WriteRawTag(8);
            output.WriteUInt64(Size);
        }

        if (_hash.Length != 0)
        {
            output.WriteRawTag(18);
            output.WriteBytes(Hash);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the encoded length of this message in bytes.
    /// <para>计算本消息编码后的字节长度。</para></summary>
    /// <returns>Byte size of what <see cref="WriteTo"/> emits, including unknown fields; <c>0</c> for an
    /// all-default message. <para><see cref="WriteTo"/> 将写出的字节数（含未知字段）；全默认值消息为
    /// <c>0</c>。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (Size != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(Size);
        }

        if (_hash.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeBytesSize(Hash);
        }

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Merges another message into this one.
    /// <para>将另一条消息合并进本消息。</para></summary>
    /// <param name="other">Source message. A non-default <see cref="Size"/> and a non-empty
    /// <see cref="Hash"/> overwrite the current values and unknown fields are appended;
    /// <see langword="null"/> makes the call a silent no-op.
    /// <para>来源消息。非默认值的 <see cref="Size"/> 与非空的 <see cref="Hash"/> 会覆盖当前值，
    /// 未知字段会被追加；传入 <see langword="null"/> 时本调用静默无操作。</para></param>
    public void MergeFrom(PartitionInfo other)
    {
        if (other is null)
        {
            return;
        }

        if (other.Size != 0UL)
        {
            Size = other.Size;
        }

        if (other._hash.Length != 0)
        {
            Hash = other._hash;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>Fills this message by reading protobuf wire-format tags until the end of the message.
    /// <para>读取 protobuf 线格式标签直到消息结束，并据此填充本消息。</para></summary>
    /// <param name="input">Coded input positioned at the message's first tag. Reading stops at tag
    /// <c>0</c>; recognized tags set <see cref="Size"/> and <see cref="Hash"/>, and any other tag is
    /// kept in the unknown-field set.
    /// <para>定位到消息首个标签处的编码输入。读到标签 <c>0</c> 时停止；可识别的标签会写入
    /// <see cref="Size"/> 与 <see cref="Hash"/>，其余标签保存到未知字段集。</para></param>
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
                case 8:
                    Size = input.ReadUInt64();
                    break;
                case 18:
                    Hash = input.ReadBytes();
                    break;
            }
        }
    }
}