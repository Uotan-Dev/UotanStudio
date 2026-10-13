using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.Payload.Proto;

/// <summary>
/// In-memory protobuf message for one payload install operation, pairing an operation type
/// with a payload data region and source/destination extents.
/// <para>单个安装操作的内存 protobuf 消息，将操作类型与载荷数据区域及源/目标范围（extents）配对。</para>
/// </summary>
public sealed class InstallOperation : IMessage<InstallOperation>
{
    /// <summary>
    /// Container for the types nested inside the InstallOperation message.
    /// <para>InstallOperation 消息内部嵌套类型的容器。</para>
    /// </summary>
    public static class Types
    {
        /// <summary>
        /// Install operation type selecting how the destination extents are produced.
        /// <para>安装操作类型，决定目标范围如何生成。</para>
        /// </summary>
        public enum Type
        {
            /// <summary>
            /// Write uncompressed payload data directly to the destination extents.
            /// <para>将未压缩的载荷数据直接写入目标范围。</para>
            /// </summary>
            Replace = 0,

            /// <summary>
            /// Decompress the payload data with BZip2, then write it to the destination extents.
            /// <para>先用 BZip2 解压载荷数据，再写入目标范围。</para>
            /// </summary>
            ReplaceBz = 1,

            /// <summary>
            /// Copy the source extents to the destination extents without using payload data.
            /// <para>不使用载荷数据，把源范围复制到目标范围。</para>
            /// </summary>
            Move = 2,

            /// <summary>
            /// Apply a bsdiff patch from the payload against source extents read from the partition.
            /// <para>针对从分区读取的源范围应用载荷中的 bsdiff 补丁。</para>
            /// </summary>
            Bsdiff = 3,

            /// <summary>
            /// Copy source data read from the payload to the destination extents.
            /// <para>把从载荷读取的源数据复制到目标范围。</para>
            /// </summary>
            SourceCopy = 4,

            /// <summary>
            /// Apply a bsdiff patch against source data read from the payload instead of the partition.
            /// <para>针对从载荷而非分区读取的源数据应用 bsdiff 补丁。</para>
            /// </summary>
            SourceBsdiff = 5,

            /// <summary>
            /// Write zeros across the destination length without payload data.
            /// <para>不使用载荷数据，在目标长度范围内写入零。</para>
            /// </summary>
            Zero = 6,

            /// <summary>
            /// Mark the destination extents as discarded instead of writing data to them.
            /// <para>把目标范围标记为丢弃，而不是向其写入数据。</para>
            /// </summary>
            Discard = 7,

            /// <summary>
            /// Decompress the payload data with XZ, then write it to the destination extents.
            /// <para>先用 XZ 解压载荷数据，再写入目标范围。</para>
            /// </summary>
            ReplaceXz = 8,

            /// <summary>
            /// Apply a deflate-aware binary diff (puffdiff) patch from the payload against the source extents.
            /// <para>针对源范围应用载荷中的 deflate 感知二进制差分（puffdiff）补丁。</para>
            /// </summary>
            Puffdiff = 9,

            /// <summary>
            /// Apply a bsdiff patch computed between Brotli-compressed source and destination streams.
            /// <para>应用 Brotli 压缩源流与目标流之间的 bsdiff 补丁。</para>
            /// </summary>
            BrotliBsdiff = 10,

            /// <summary>
            /// Apply a Zucchini recompressive diff patch from the payload against the source extents.
            /// <para>针对源范围应用载荷中的 Zucchini 重压缩差分补丁。</para>
            /// </summary>
            Zucchini = 11,

            /// <summary>
            /// Apply a bsdiff patch wrapped in Lz4diff framing against the source extents.
            /// <para>针对源范围应用经 Lz4diff 封装的 bsdiff 补丁。</para>
            /// </summary>
            Lz4diffBsdiff = 12,

            /// <summary>
            /// Apply a puffdiff patch wrapped in Lz4diff framing against the source extents.
            /// <para>针对源范围应用经 Lz4diff 封装的 puffdiff 补丁。</para>
            /// </summary>
            Lz4diffPuffdiff = 13,

            /// <summary>
            /// Decompress the payload data with Zstandard, then write it to the destination extents.
            /// <para>先用 Zstandard 解压载荷数据，再写入目标范围。</para>
            /// </summary>
            ReplaceZstd = 14,

            /// <summary>
            /// Apply an IMGDIFF2 container from the payload against source extents; unlike SOURCE_BSDIFF the data
            /// blob is the VERSION2 chunk layout rather than a bare BSDIFF1 stream. Repository extension value;
            /// the parallel extractor does not yet execute this type.
            /// <para>针对源范围应用载荷中的 IMGDIFF2 容器；与 SOURCE_BSDIFF 不同，数据块是 VERSION2 块布局而非裸
            /// BSDIFF1 流。仓库扩展值；并行提取器尚不执行该类型。</para>
            /// </summary>
            SourceImagediff = 15
        }
    }

    private static readonly FieldCodec<Extent> RepeatedSrcExtentsCodec = FieldCodec.ForMessage(34, Extent.Parser);
    private static readonly FieldCodec<Extent> RepeatedDstExtentsCodec = FieldCodec.ForMessage(50, Extent.Parser);

    private UnknownFieldSet? _unknownFields;
    private ByteString _dataSha256Hash = ByteString.Empty;
    private ByteString _srcSha256Hash = ByteString.Empty;

    /// <summary>
    /// Shared parser that decodes a wire-format <c>InstallOperation</c> message.
    /// <para>解码线格式 <c>InstallOperation</c> 消息的共享分析器。</para>
    /// </summary>
    /// <value>A singleton <see cref="MessageParser{InstallOperation}"/> that creates a fresh instance for each parse.
    /// <para>每次解析都创建新实例的单例 <see cref="MessageParser{InstallOperation}"/>。</para></value>
    public static MessageParser<InstallOperation> Parser { get; } = new(() => new InstallOperation());

    /// <summary>
    /// Reflection descriptor placeholder for the message.
    /// <para>消息的反射描述符占位。</para>
    /// </summary>
    /// <value>Always <see langword="null"/>; this hand-written message carries no generated descriptor metadata.
    /// <para>恒为 <see langword="null"/>；该手写消息不携带生成的描述符元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>
    /// Initializes an empty operation whose fields hold the protobuf defaults.
    /// <para>初始化空操作，各字段均为 protobuf 默认值。</para>
    /// </summary>
    public InstallOperation()
    {
    }

    /// <summary>
    /// Initializes a deep copy of another operation, including extents, hashes and unknown fields.
    /// <para>初始化另一操作的深拷贝，包含范围、哈希与未知字段。</para>
    /// </summary>
    /// <param name="other">
    /// Operation to copy; its fields are read as they are, without default filtering.
    /// <para>要拷贝的操作；按原值读取其字段，不做默认值过滤。</para>
    /// </param>
    public InstallOperation(InstallOperation other) : this()
    {
        Type = other.Type;
        DataOffset = other.DataOffset;
        DataLength = other.DataLength;
        SrcExtents = other.SrcExtents.Clone();
        SrcLength = other.SrcLength;
        DstExtents = other.DstExtents.Clone();
        DstLength = other.DstLength;
        _dataSha256Hash = other._dataSha256Hash;
        _srcSha256Hash = other._srcSha256Hash;
        SourcePartitionName = other.SourcePartitionName;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>
    /// Creates an independent deep copy of this operation.
    /// <para>创建本操作的独立深拷贝。</para>
    /// </summary>
    /// <returns>A new <see cref="InstallOperation"/> whose collections and hashes share no mutable
    /// state with this instance.
    /// <para>新的 <see cref="InstallOperation"/>，其集合与哈希与本实例不共享可变状态。</para></returns>
    public InstallOperation Clone() => new(this);

    /// <summary>Protobuf field number for the operation type field. <para>操作类型字段的 protobuf 字段编号。</para></summary>
    public const int TypeFieldNumber = 1;

    /// <summary>
    /// Install operation type discriminant of this operation (field 1).
    /// <para>本操作的安装操作类型判别值（字段 1）。</para>
    /// </summary>
    /// <value>The operation type; the numeric default <c>Replace</c> (0) is omitted when serialized.
    /// <para>操作类型；数值默认值 <c>Replace</c>（0）在序列化时省略。</para></value>
    public Types.Type Type { get; set; }

    /// <summary>Protobuf field number of the payload data offset field. <para>载荷数据偏移字段的 protobuf 字段编号。</para></summary>
    public const int DataOffsetFieldNumber = 2;

    /// <summary>
    /// Byte offset inside the payload blob where this operation's data region starts (field 2).
    /// <para>载荷 blob 中本操作数据区域起始处的字节偏移（字段 2）。</para>
    /// </summary>
    /// <value>Offset in bytes from the start of the payload data; <c>0</c> is the protobuf default
    /// and is omitted when serialized.
    /// <para>距载荷数据起始处的字节数；<c>0</c> 为 protobuf 默认值，序列化时省略。</para></value>
    public ulong DataOffset { get; set; }

    /// <summary>Protobuf field number of the payload data length field. <para>载荷数据长度字段的 protobuf 字段编号。</para></summary>
    public const int DataLengthFieldNumber = 3;

    /// <summary>
    /// Length in bytes of the payload data region consumed by this operation (field 3).
    /// <para>本操作消耗的载荷数据区域字节长度（字段 3）。</para>
    /// </summary>
    /// <value>Size in bytes of the data starting at <see cref="DataOffset"/>; <c>0</c> is the
    /// protobuf default and is omitted when serialized.
    /// <para>自 <see cref="DataOffset"/> 起的数据字节数；<c>0</c> 为 protobuf 默认值，序列化时省略。</para></value>
    public ulong DataLength { get; set; }

    /// <summary>Protobuf field number for the source extents field. <para>源范围字段的 protobuf 字段编号。</para></summary>
    public const int SrcExtentsFieldNumber = 4;

    /// <summary>
    /// Extents inside the partition that this operation reads as source data (field 4).
    /// <para>本操作作为源数据读取的分区内范围（字段 4）。</para>
    /// </summary>
    /// <value>Mutable repeated list of <see cref="Extent"/> messages; an empty list contributes
    /// nothing to the serialized message.
    /// <para>可变的 <see cref="Extent"/> 消息重复列表；空列表对序列化消息无贡献。</para></value>
    public RepeatedField<Extent> SrcExtents { get; } = [];

    /// <summary>Protobuf field number for the source length field. <para>源长度字段的 protobuf 字段编号。</para></summary>
    public const int SrcLengthFieldNumber = 5;

    /// <summary>
    /// Total byte count of the source region spanned by <see cref="SrcExtents"/> (field 5).
    /// <para><see cref="SrcExtents"/> 所覆盖源区域的字节总数（字段 5）。</para>
    /// </summary>
    /// <value>Byte count; <c>0</c> is the protobuf default and is omitted when serialized.
    /// <para>字节数；<c>0</c> 为 protobuf 默认值，序列化时省略。</para></value>
    public ulong SrcLength { get; set; }

    /// <summary>Protobuf field number for the destination extents field. <para>目标范围字段的 protobuf 字段编号。</para></summary>
    public const int DstExtentsFieldNumber = 6;

    /// <summary>
    /// Extents inside the partition that this operation writes (field 6).
    /// <para>本操作写入的分区内范围（字段 6）。</para>
    /// </summary>
    /// <value>Mutable repeated list of <see cref="Extent"/> messages; an empty list contributes
    /// nothing to the serialized message.
    /// <para>可变的 <see cref="Extent"/> 消息重复列表；空列表对序列化消息无贡献。</para></value>
    public RepeatedField<Extent> DstExtents { get; } = [];

    /// <summary>Protobuf field number for the destination length field. <para>目标长度字段的 protobuf 字段编号。</para></summary>
    public const int DstLengthFieldNumber = 7;

    /// <summary>
    /// Total byte count written across <see cref="DstExtents"/> (field 7).
    /// <para>跨 <see cref="DstExtents"/> 写入的字节总数（字段 7）。</para>
    /// </summary>
    /// <value>Byte count; <c>0</c> is the protobuf default and is omitted when serialized.
    /// <para>字节数；<c>0</c> 为 protobuf 默认值，序列化时省略。</para></value>
    public ulong DstLength { get; set; }

    /// <summary>Protobuf field number for the payload data hash field. <para>载荷数据哈希字段的 protobuf 字段编号。</para></summary>
    public const int DataSha256HashFieldNumber = 8;

    /// <summary>
    /// SHA-256 digest of the payload data consumed by this operation (field 8).
    /// <para>本操作消耗载荷数据的 SHA-256 摘要（字段 8）。</para>
    /// </summary>
    /// <value>Raw digest bytes; assigning <see langword="null"/> stores an empty byte string, and an
    /// empty digest is omitted when serialized.
    /// <para>原始摘要字节；赋 <see langword="null"/> 时存储空字节串，空摘要在序列化时省略。</para></value>
    public ByteString DataSha256Hash
    {
        get => _dataSha256Hash;
        set => _dataSha256Hash = value ?? ByteString.Empty;
    }

    /// <summary>Protobuf field number for the source data hash field. <para>源数据哈希字段的 protobuf 字段编号。</para></summary>
    public const int SrcSha256HashFieldNumber = 9;

    /// <summary>
    /// SHA-256 digest of the source data read from <see cref="SrcExtents"/> (field 9).
    /// <para>从 <see cref="SrcExtents"/> 读取的源数据的 SHA-256 摘要（字段 9）。</para>
    /// </summary>
    /// <value>Raw digest bytes; assigning <see langword="null"/> stores an empty byte string, and an
    /// empty digest is omitted when serialized.
    /// <para>原始摘要字节；赋 <see langword="null"/> 时存储空字节串，空摘要在序列化时省略。</para></value>
    public ByteString SrcSha256Hash
    {
        get => _srcSha256Hash;
        set => _srcSha256Hash = value ?? ByteString.Empty;
    }

    /// <summary>Protobuf field number for the source partition name field. <para>源分区名字段的 protobuf 字段编号。</para></summary>
    public const int SourcePartitionNameFieldNumber = 15;

    /// <summary>
    /// Partition whose blocks back this operation's source extents (field 15); an empty name means the
    /// partition the update writes. Repository extension used by SOURCE_IMAGEDIFF operations.
    /// <para>为本操作源范围提供块的分区名（字段 15）；空名表示本更新所写入的分区。SOURCE_IMAGEDIFF 操作使用的仓库扩展。</para>
    /// </summary>
    /// <value>
    /// The partition name text; assigning <see langword="null"/> stores <see cref="string.Empty"/>, and an
    /// empty name is omitted when serialized. <para>分区名文本；赋 <see langword="null"/> 存入 <see cref="string.Empty"/>，
    /// 空名在序列化时省略。</para>
    /// </value>
    public string SourcePartitionName { get; set; } = string.Empty;

    /// <summary>
    /// Determines whether this operation equals another object by delegating to the typed comparison.
    /// <para>通过转向强类型比较，判定本操作是否与另一对象相等。</para>
    /// </summary>
    /// <param name="obj">
    /// Reference to compare; only an <see cref="InstallOperation"/> instance can be equal.
    /// <para>待比较的引用；仅 <see cref="InstallOperation"/> 实例可能相等。</para>
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an operation whose fields and
    /// retained unknown fields match this message; <see langword="false"/> for <see langword="null"/> or
    /// any other type. <para>当 <paramref name="obj"/> 为安装操作且其字段与保留的未知字段与本消息一致时
    /// 返回 <see langword="true"/>；对 <see langword="null"/> 或其他类型返回 <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as InstallOperation);

    /// <summary>
    /// Compares this operation field by field against another message, including retained unknown fields.
    /// <para>逐字段比较本操作与另一消息，包含保留的未知字段。</para>
    /// </summary>
    /// <param name="other">
    /// Message to compare; <see langword="null"/> never equals an instance.
    /// <para>要比较的消息；<see langword="null"/> 与任何实例都不相等。</para>
    /// </param>
    /// <returns><see langword="true"/> when the other message is the same instance or every scalar,
    /// extent, hash and unknown-field value matches.
    /// <para>当另一消息为同一实例，或所有标量、范围、哈希与未知字段值均匹配时返回 <see langword="true"/>。</para></returns>
    public bool Equals(InstallOperation? other)
        => other is not null && (ReferenceEquals(other, this) || (Type == other.Type && DataOffset == other.DataOffset && DataLength == other.DataLength && SrcExtents.Equals(other.SrcExtents) && SrcLength == other.SrcLength && DstExtents.Equals(other.DstExtents) && DstLength == other.DstLength && DataSha256Hash == other.DataSha256Hash && SrcSha256Hash == other.SrcSha256Hash && SourcePartitionName == other.SourcePartitionName && Equals(_unknownFields, other._unknownFields)));

    /// <summary>
    /// Computes the hash code of this operation.
    /// <para>计算本操作的哈希代码。</para>
    /// </summary>
    /// <returns>
    /// Base value <c>1</c> XOR-folded with the <c>Type</c>, <see cref="DataOffset"/>,
    /// <see cref="DataLength"/>, <see cref="SrcLength"/>, <see cref="DstLength"/>,
    /// <see cref="DataSha256Hash"/> and <see cref="SrcSha256Hash"/> hashes only when those fields differ
    /// from their protobuf defaults (<c>Replace</c>, <c>0</c>, empty bytes), with the
    /// <see cref="SrcExtents"/> and <see cref="DstExtents"/> hashes always, and with the unknown-field
    /// hash when unknown fields are retained; equal operations therefore hash alike.
    /// <para>以 <c>1</c> 为基数，仅当 <c>Type</c>、<see cref="DataOffset"/>、<see cref="DataLength"/>、
    /// <see cref="SrcLength"/>、<see cref="DstLength"/>、<see cref="DataSha256Hash"/> 与
    /// <see cref="SrcSha256Hash"/> 不同于 protobuf 默认值（<c>Replace</c>、<c>0</c>、空字节串）时异或其
    /// 哈希代码；<see cref="SrcExtents"/> 与 <see cref="DstExtents"/> 始终异或，存在未知字段时同样异或，
    /// 因此相等的操作哈希相同。</para>
    /// </returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (Type != 0)
        {
            hash ^= Type.GetHashCode();
        }

        if (DataOffset != 0UL)
        {
            hash ^= DataOffset.GetHashCode();
        }

        if (DataLength != 0UL)
        {
            hash ^= DataLength.GetHashCode();
        }

        hash ^= SrcExtents.GetHashCode();
        if (SrcLength != 0UL)
        {
            hash ^= SrcLength.GetHashCode();
        }

        hash ^= DstExtents.GetHashCode();
        if (DstLength != 0UL)
        {
            hash ^= DstLength.GetHashCode();
        }

        if (_dataSha256Hash.Length != 0)
        {
            hash ^= DataSha256Hash.GetHashCode();
        }

        if (_srcSha256Hash.Length != 0)
        {
            hash ^= SrcSha256Hash.GetHashCode();
        }

        if (SourcePartitionName.Length != 0)
        {
            hash ^= SourcePartitionName.GetHashCode();
        }

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>
    /// Formats this operation as text.
    /// <para>将本操作格式化为文本。</para>
    /// </summary>
    /// <returns>
    /// The runtime type name <c>InstallOperation</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>InstallOperation</c>；不呈现字段值。</para>
    /// </returns>
    public override string ToString() => GetType().Name;

    /// <summary>
    /// Serializes every field that differs from its protobuf default, followed by any retained unknown fields.
    /// <para>序列化所有与 protobuf 默认值不同的字段，随后写入保留的未知字段。</para>
    /// </summary>
    /// <param name="output">
    /// Stream sink that receives the wire-format message bytes; fields at their default value are
    /// skipped entirely.
    /// <para>接收线格式消息字节的输出流；处于默认值的字段会被完全跳过。</para>
    /// </param>
    public void WriteTo(CodedOutputStream output)
    {
        if (Type != 0)
        {
            output.WriteRawTag(8);
            output.WriteEnum((int)Type);
        }

        if (DataOffset != 0UL)
        {
            output.WriteRawTag(16);
            output.WriteUInt64(DataOffset);
        }

        if (DataLength != 0UL)
        {
            output.WriteRawTag(24);
            output.WriteUInt64(DataLength);
        }

        SrcExtents.WriteTo(output, RepeatedSrcExtentsCodec);
        if (SrcLength != 0UL)
        {
            output.WriteRawTag(40);
            output.WriteUInt64(SrcLength);
        }

        DstExtents.WriteTo(output, RepeatedDstExtentsCodec);
        if (DstLength != 0UL)
        {
            output.WriteRawTag(56);
            output.WriteUInt64(DstLength);
        }

        if (_dataSha256Hash.Length != 0)
        {
            output.WriteRawTag(66);
            output.WriteBytes(DataSha256Hash);
        }

        if (_srcSha256Hash.Length != 0)
        {
            output.WriteRawTag(74);
            output.WriteBytes(SrcSha256Hash);
        }

        if (SourcePartitionName.Length != 0)
        {
            output.WriteRawTag(122);
            output.WriteString(SourcePartitionName);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>
    /// Computes the exact encoded size of this message in the protobuf wire format.
    /// <para>计算本消息在 protobuf 线格式下的精确编码大小。</para>
    /// </summary>
    /// <returns>Size in bytes counting tags, nested extent lengths and unknown fields; <c>0</c> for
    /// a message whose fields are all default.
    /// <para>以字节计的大小，包含标签、嵌套范围长度与未知字段；全默认字段的消息为 <c>0</c>。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (Type != 0)
        {
            size += 1 + CodedOutputStream.ComputeEnumSize((int)Type);
        }

        if (DataOffset != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(DataOffset);
        }

        if (DataLength != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(DataLength);
        }

        size += SrcExtents.CalculateSize(RepeatedSrcExtentsCodec);
        if (SrcLength != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(SrcLength);
        }

        size += DstExtents.CalculateSize(RepeatedDstExtentsCodec);
        if (DstLength != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(DstLength);
        }

        if (_dataSha256Hash.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeBytesSize(DataSha256Hash);
        }

        if (_srcSha256Hash.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeBytesSize(SrcSha256Hash);
        }

        if (SourcePartitionName.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(SourcePartitionName);
        }

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>
    /// Merges another message into this one using protobuf semantics: non-default scalars
    /// overwrite, while extent lists and unknown fields accumulate.
    /// <para>按 protobuf 语义把另一消息合并进本消息：非默认标量覆盖，范围列表与未知字段累加。</para>
    /// </summary>
    /// <param name="other">
    /// Source message; <see langword="null"/> leaves this instance unchanged.
    /// <para>来源消息；为 <see langword="null"/> 时本实例保持不变。</para>
    /// </param>
    public void MergeFrom(InstallOperation other)
    {
        if (other is null)
        {
            return;
        }

        if (other.Type != 0)
        {
            Type = other.Type;
        }

        if (other.DataOffset != 0UL)
        {
            DataOffset = other.DataOffset;
        }

        if (other.DataLength != 0UL)
        {
            DataLength = other.DataLength;
        }

        SrcExtents.Add(other.SrcExtents);
        if (other.SrcLength != 0UL)
        {
            SrcLength = other.SrcLength;
        }

        DstExtents.Add(other.DstExtents);
        if (other.DstLength != 0UL)
        {
            DstLength = other.DstLength;
        }

        if (other._dataSha256Hash.Length != 0)
        {
            DataSha256Hash = other._dataSha256Hash;
        }

        if (other._srcSha256Hash.Length != 0)
        {
            SrcSha256Hash = other._srcSha256Hash;
        }

        if (other.SourcePartitionName.Length != 0)
        {
            SourcePartitionName = other.SourcePartitionName;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>
    /// Reads wire-format fields until <c>ReadTag</c> returns 0 at the end of the stream or
    /// enclosing group, storing unrecognized tags as unknown fields.
    /// <para>持续读取线格式字段直到 <c>ReadTag</c> 在流或外层分组末尾返回 0；无法识别的标签存为未知字段。</para>
    /// </summary>
    /// <param name="input">
    /// Stream positioned at the start of the message body; a later occurrence of a scalar tag
    /// overwrites the earlier value, and repeated extent tags append.
    /// <para>定位在消息体起始处的流；同一标量标签后出现的值覆盖先前值，重复范围标签则追加。</para>
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
                case 8:
                    Type = (Types.Type)input.ReadEnum();
                    break;
                case 16:
                    DataOffset = input.ReadUInt64();
                    break;
                case 24:
                    DataLength = input.ReadUInt64();
                    break;
                case 34:
                    SrcExtents.AddEntriesFrom(input, RepeatedSrcExtentsCodec);
                    break;
                case 40:
                    SrcLength = input.ReadUInt64();
                    break;
                case 50:
                    DstExtents.AddEntriesFrom(input, RepeatedDstExtentsCodec);
                    break;
                case 56:
                    DstLength = input.ReadUInt64();
                    break;
                case 66:
                    DataSha256Hash = input.ReadBytes();
                    break;
                case 74:
                    SrcSha256Hash = input.ReadBytes();
                    break;
                case 122:
                    SourcePartitionName = input.ReadString();
                    break;
            }
        }
    }
}