using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.CareMap;

/// <summary>
/// One partition record of a care map: which blocks a verifier must read back, plus the optional build
/// identity the record was produced for.
/// <para>care map 的一条分区记录：校验方需要读回的块区间，以及该记录可选对应的构建身份。</para>
/// </summary>
public sealed class CareMapPartitionInfo : IMessage<CareMapPartitionInfo>
{
    private string _name = string.Empty;
    private string _ranges = string.Empty;
    private string _propertyId = string.Empty;
    private string _fingerprint = string.Empty;
    private UnknownFieldSet? _unknownFields;

    /// <summary>
    /// Shared parser that decodes a wire-format partition record.
    /// <para>解码线格式分区记录的共享分析器。</para>
    /// </summary>
    /// <value>A singleton parser creating a fresh instance per parse.
    /// <para>每次解析创建新实例的单例分析器。</para></value>
    public static MessageParser<CareMapPartitionInfo> Parser { get; } = new(() => new CareMapPartitionInfo());

    /// <summary>
    /// Reflection descriptor placeholder for the message.
    /// <para>消息的反射描述符占位。</para>
    /// </summary>
    /// <value>Always <see langword="null"/>; this hand-written message carries no generated descriptor metadata.
    /// <para>恒为 <see langword="null"/>；该手写消息不携带生成的描述符元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Initializes an empty record. <para>初始化空记录。</para></summary>
    public CareMapPartitionInfo()
    {
    }

    private CareMapPartitionInfo(CareMapPartitionInfo other)
    {
        _name = other._name;
        _ranges = other._ranges;
        _propertyId = other._propertyId;
        _fingerprint = other._fingerprint;
        _unknownFields = other._unknownFields is null ? null : UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Gets or sets the dynamic or physical partition name the ranges belong to.
    /// <para>获取或设置区间所属的动态或物理分区名。</para></summary>
    /// <value>The partition name; empty until a producer or parser sets it. <para>分区名；未被写入时为空。</para></value>
    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <summary>Gets or sets the block ranges as care-map text.
    /// <para>获取或设置以 care map 文本表示的块区间。</para></summary>
    /// <value>Text parseable by <see cref="CareMapRanges.Parse"/>; use <see cref="ParseRanges"/> to decode it.
    /// <para>可由 <see cref="CareMapRanges.Parse"/> 解析的文本；用 <see cref="ParseRanges"/> 解码。</para></value>
    public string Ranges
    {
        get => _ranges;
        set => _ranges = value ?? string.Empty;
    }

    /// <summary>Gets or sets the build property the fingerprint came from.
    /// <para>获取或设置指纹值来源的构建属性名。</para></summary>
    /// <value>A property name such as <c>ro.system.build.fingerprint</c>, or an empty string when the
    /// care map was produced without fingerprints.
    /// <para>形如 <c>ro.system.build.fingerprint</c> 的属性名；不带指纹的 care map 为空字符串。</para></value>
    public string PropertyId
    {
        get => _propertyId;
        set => _propertyId = value ?? string.Empty;
    }

    /// <summary>Gets or sets the build fingerprint or thumbprint value.
    /// <para>获取或设置构建指纹或缩印值。</para></summary>
    /// <value>The recorded value, or an empty string when fingerprints were not captured.
    /// <para>记录到的值；未捕获指纹时为空字符串。</para></value>
    public string Fingerprint
    {
        get => _fingerprint;
        set => _fingerprint = value ?? string.Empty;
    }

    /// <summary>
    /// Reads the block ranges recorded for this partition.
    /// <para>读取该分区记录的块区间。</para>
    /// </summary>
    /// <returns>The decoded ranges in producer order. <para>按生产者顺序解码出的区间。</para></returns>
    /// <exception cref="FormatException">
    /// <see cref="Ranges"/> is not valid care-map range text. <para><see cref="Ranges"/> 不是合法的 care map 区间文本。</para>
    /// </exception>
    public IReadOnlyList<CareMapRange> ParseRanges() => CareMapRanges.Parse(Ranges);

    /// <summary>Creates a deep copy of this record. <para>创建本记录的深拷贝。</para></summary>
    /// <returns>An independent copy carrying the same field values.
    /// <para>字段值相同且相互独立的副本。</para></returns>
    public CareMapPartitionInfo Clone() => new(this);

    /// <summary>
    /// Returns whether every field of this record equals the other message.
    /// <para>判断本记录的所有字段是否与另一消息相等。</para>
    /// </summary>
    /// <param name="other">Candidate message; <see langword="null"/> never equals.
    /// <para>候选消息；<see langword="null"/> 永不相等。</para></param>
    /// <returns><see langword="true"/> when the records are field-equal. <para>字段完全相等时为 <see langword="true"/>。</para></returns>
    public bool Equals(CareMapPartitionInfo? other) => other is not null
        && _name == other._name
        && _ranges == other._ranges
        && _propertyId == other._propertyId
        && _fingerprint == other._fingerprint
        && Equals(_unknownFields, other._unknownFields);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CareMapPartitionInfo);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = 1;
        hash ^= _name.GetHashCode();
        hash ^= _ranges.GetHashCode();
        hash ^= _propertyId.GetHashCode();
        hash ^= _fingerprint.GetHashCode();
        if (_unknownFields is not null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <inheritdoc/>
    public override string ToString() => GetType().Name;

    /// <summary>
    /// Writes each non-default field, then any retained unknown fields.
    /// <para>逐个写入非默认字段，随后写入保留的未知字段。</para>
    /// </summary>
    /// <param name="output">Sink receiving the wire-format body. <para>接收线格式消息体的输出流。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        if (_name.Length != 0)
        {
            output.WriteRawTag(10);
            output.WriteString(Name);
        }

        if (_ranges.Length != 0)
        {
            output.WriteRawTag(18);
            output.WriteString(Ranges);
        }

        if (_propertyId.Length != 0)
        {
            output.WriteRawTag(26);
            output.WriteString(PropertyId);
        }

        if (_fingerprint.Length != 0)
        {
            output.WriteRawTag(34);
            output.WriteString(Fingerprint);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the serialized length in bytes. <para>计算序列化后的字节长度。</para></summary>
    /// <returns>The number of bytes <see cref="WriteTo"/> emits. <para><see cref="WriteTo"/> 将写出的字节数。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (_name.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Name);
        }

        if (_ranges.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Ranges);
        }

        if (_propertyId.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(PropertyId);
        }

        if (_fingerprint.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Fingerprint);
        }

        if (_unknownFields is not null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Copies this record's fields onto another instance. <para>把本记录的字段复制到另一实例。</para></summary>
    /// <param name="other">Target message; ignored when <see langword="null"/>. <para>目标消息；为 <see langword="null"/> 时忽略。</para></param>
    public void MergeFrom(CareMapPartitionInfo? other)
    {
        if (other is null)
        {
            return;
        }

        if (other._name.Length != 0)
        {
            Name = other.Name;
        }

        if (other._ranges.Length != 0)
        {
            Ranges = other.Ranges;
        }

        if (other._propertyId.Length != 0)
        {
            PropertyId = other.PropertyId;
        }

        if (other._fingerprint.Length != 0)
        {
            Fingerprint = other.Fingerprint;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    void IMessage.MergeFrom(CodedInputStream input) => MergeFrom(input);

    /// <summary>
    /// Reads wire-format fields until the stream or enclosing limit ends, storing unknown tags.
    /// <para>持续读取线格式字段直到流或外层边界结束，未知标签会被保留。</para>
    /// </summary>
    /// <param name="input">Stream positioned at the message body. <para>定位在消息体起始处的流。</para></param>
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
                case 10: Name = input.ReadString(); break;
                case 18: Ranges = input.ReadString(); break;
                case 26: PropertyId = input.ReadString(); break;
                case 34: Fingerprint = input.ReadString(); break;
            }
        }
    }
}