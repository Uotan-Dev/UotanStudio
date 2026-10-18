using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.Payload.Proto;

/// <summary>
/// Hand-maintained protobuf message model of the payload manifest (<c>DeltaArchiveManifest</c>), storing fields in
/// proto3 style where default values are omitted from the wire. Input bytes are untrusted and only bounds-checked
/// by the protobuf reader during parsing.
/// <para>手工维护的载荷清单 protobuf 消息模型（<c>DeltaArchiveManifest</c>），按 proto3 风格存储字段，
/// 默认值不写入线路。输入字节不可信，仅在解析时由 protobuf 读取器做边界检查。</para>
/// </summary>
public sealed class DeltaArchiveManifest : IMessage<DeltaArchiveManifest>
{
    private static readonly FieldCodec<PartitionUpdate> RepeatedPartitionsCodec = FieldCodec.ForMessage(106, PartitionUpdate.Parser);

    private UnknownFieldSet? _unknownFields;
    private uint _blockSize;
    private string _securityPatchLevel = string.Empty;

    /// <summary>
    /// Shared parser that constructs a new <c>DeltaArchiveManifest</c> per parse call.
    /// <para>为每次解析构造新 <c>DeltaArchiveManifest</c> 实例的共享解析器。</para>
    /// </summary>
    /// <value>
    /// A stateless <see cref="MessageParser{T}"/> instance safe for concurrent use.
    /// <para>无状态的 <see cref="MessageParser{T}"/> 实例，可并发使用。</para>
    /// </value>
    public static MessageParser<DeltaArchiveManifest> Parser { get; } = new(() => new DeltaArchiveManifest());

    /// <summary>
    /// Protocol Buffers message descriptor placeholder.
    /// <para>Protocol Buffers 消息描述符占位成员。</para>
    /// </summary>
    /// <value>
    /// Always <see langword="null"/>; this hand-maintained message carries no runtime descriptor.
    /// <para>恒为 <see langword="null"/>；该手工维护消息不携带运行时描述符。</para>
    /// </value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>
    /// Creates an empty manifest with every field at its proto3 default.
    /// <para>创建一个所有字段均为 proto3 默认值的空清单。</para>
    /// </summary>
    public DeltaArchiveManifest()
    {
    }

    /// <summary>
    /// Creates a deep copy of another manifest, including its partition list, dynamic partition metadata and unknown fields.
    /// <para>深拷贝另一份清单，含分区列表、动态分区元数据与未知字段。</para>
    /// </summary>
    /// <param name="other">Manifest to copy field values and presence flags from. <para>要复制字段值与存在标志的清单。</para></param>
    public DeltaArchiveManifest(DeltaArchiveManifest other) : this()
    {
        HasBlockSize = other.HasBlockSize;
        _blockSize = other._blockSize;
        SignaturesOffset = other.SignaturesOffset;
        SignaturesSize = other.SignaturesSize;
        MinorVersion = other.MinorVersion;
        Partitions = other.Partitions.Clone();
        MaxTimestamp = other.MaxTimestamp;
        DynamicPartitionMetadata = other.DynamicPartitionMetadata?.Clone();
        PartialUpdate = other.PartialUpdate;
        _securityPatchLevel = other._securityPatchLevel;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>
    /// Returns a deep copy of this manifest.
    /// <para>返回本清单的深拷贝。</para>
    /// </summary>
    /// <returns>A new <c>DeltaArchiveManifest</c> whose field values, presence flags and unknown fields are independent of this instance.
    /// <para>字段值、存在标志与未知字段均与本实例独立的新 <c>DeltaArchiveManifest</c>。</para></returns>
    public DeltaArchiveManifest Clone() => new(this);

    /// <summary>Wire field number of <c>block_size</c>: <c>3</c>. <para><c>block_size</c> 的线路字段号：<c>3</c>。</para></summary>
    public const int BlockSizeFieldNumber = 3;

    private const uint BlockSizeDefaultValue = 4096;

    /// <summary>
    /// Payload block size in bytes used to convert extent block counts to offsets (proto field 3).
    /// <para>载荷块大小（字节），用于把 extent 块数换算为偏移（proto 字段 3）。</para>
    /// </summary>
    /// <value>
    /// <c>4096</c> while unset; assigning records field presence so the value is written even when equal to the default.
    /// <para>未设置时为 <c>4096</c>；赋值会记录字段存在标志，即使等于默认值也会写出。</para>
    /// </value>
    public uint BlockSize
    {
        get => HasBlockSize ? _blockSize : BlockSizeDefaultValue;
        set
        {
            HasBlockSize = true;
            _blockSize = value;
        }
    }

    /// <summary>
    /// Whether <c>block_size</c> was explicitly set or parsed from the wire.
    /// <para>是否显式设置或从线路解析出了 <c>block_size</c>。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> once assigned or parsed; cleared by <see cref="ClearBlockSize"/> and settable only within this type.
    /// <para>赋值或解析后为 <see langword="true"/>；由 <see cref="ClearBlockSize"/> 清除，仅本类型内部可写。</para>
    /// </value>
    public bool HasBlockSize { get; private set; }

    /// <summary>
    /// Clears the block_size field.
    /// <para>清除 block_size 字段。</para>
    /// </summary>
    public void ClearBlockSize() => HasBlockSize = false;

    /// <summary>Wire field number of <c>signatures_offset</c>: <c>4</c>. <para><c>signatures_offset</c> 的线路字段号：<c>4</c>。</para></summary>
    public const int SignaturesOffsetFieldNumber = 4;

    /// <summary>
    /// Producer-recorded offset of the signature region, in bytes (proto field 4).
    /// <para>生产者记录的签名字节区域偏移（proto 字段 4）。</para>
    /// </summary>
    /// <value>
    /// Byte offset as stored on the wire; <c>0</c> is the default and is omitted when written.
    /// <para>按线路原样存储的字节偏移；默认 <c>0</c>，写出时被省略。</para>
    /// </value>
    public ulong SignaturesOffset { get; set; }

    /// <summary>Wire field number of <c>signatures_size</c>: <c>5</c>. <para><c>signatures_size</c> 的线路字段号：<c>5</c>。</para></summary>
    public const int SignaturesSizeFieldNumber = 5;

    /// <summary>
    /// Producer-recorded size of the signature region, in bytes (proto field 5).
    /// <para>生产者记录的签名字节区域大小（proto 字段 5）。</para>
    /// </summary>
    /// <value>
    /// Byte count as stored on the wire; <c>0</c> is the default and is omitted when written.
    /// <para>按线路原样存储的字节数；默认 <c>0</c>，写出时被省略。</para>
    /// </value>
    public ulong SignaturesSize { get; set; }

    /// <summary>Wire field number of <c>minor_version</c>: <c>12</c>. <para><c>minor_version</c> 的线路字段号：<c>12</c>。</para></summary>
    public const int MinorVersionFieldNumber = 12;

    /// <summary>
    /// Payload minor protocol version (proto field 12), validated against the header during manifest reads.
    /// <para>载荷 minor 协议版本（proto 字段 12），在读取清单时与头部互相校验。</para>
    /// </summary>
    /// <value>
    /// Zero for a manifest that predates minor versioning; nonzero values gate newer payload features.
    /// <para>早于 minor 版本机制的清单为 0；非零值决定较新载荷特性是否可用。</para>
    /// </value>
    public uint MinorVersion { get; set; }

    /// <summary>Wire field number of <c>partitions</c>: <c>13</c>. <para><c>partitions</c> 的线路字段号：<c>13</c>。</para></summary>
    public const int PartitionsFieldNumber = 13;

    /// <summary>
    /// Per-partition update records in manifest order (repeated proto field 13).
    /// <para>按清单顺序排列的每分区更新记录（重复 proto 字段 13）。</para>
    /// </summary>
    /// <value>
    /// A mutable, never-null collection; empty when the payload names no partitions. Merging appends without deduplication.
    /// <para>可变且永不为 <see langword="null"/> 的集合；载荷未命名列分区时为空。合并时追加且不查重。</para>
    /// </value>
    public RepeatedField<PartitionUpdate> Partitions { get; } = [];

    /// <summary>Wire field number of <c>max_timestamp</c>: <c>14</c>. <para><c>max_timestamp</c> 的线路字段号：<c>14</c>。</para></summary>
    public const int MaxTimestampFieldNumber = 14;

    /// <summary>
    /// Latest installation timestamp recorded by the producer (proto field 14).
    /// <para>生产者记录的最近一次安装时间戳（proto 字段 14）。</para>
    /// </summary>
    /// <value>
    /// Epoch-based value carried verbatim from the wire; <c>0</c> is the default and is omitted when written.
    /// <para>按线路原样存储的纪元时间值；默认 <c>0</c>，写出时被省略。</para>
    /// </value>
    public long MaxTimestamp { get; set; }

    /// <summary>Wire field number of <c>dynamic_partition_metadata</c>: <c>15</c>. <para><c>dynamic_partition_metadata</c> 的线路字段号：<c>15</c>。</para></summary>
    public const int DynamicPartitionMetadataFieldNumber = 15;

    /// <summary>
    /// Dynamic-partition (super) descriptors for this payload (proto field 15).
    /// <para>本载荷的动态分区（super）描述信息（proto 字段 15）。</para>
    /// </summary>
    /// <value>
    /// <see langword="null"/> when the payload carries no dynamic-partition information; merging creates and merges the sub-message.
    /// <para>载荷不含动态分区信息时为 <see langword="null"/>；合并时创建并合并子消息。</para>
    /// </value>
    public DynamicPartitionMetadata? DynamicPartitionMetadata { get; set; }

    /// <summary>Wire field number of <c>partial_update</c>: <c>16</c>. <para><c>partial_update</c> 的线路字段号：<c>16</c>。</para></summary>
    public const int PartialUpdateFieldNumber = 16;

    /// <summary>
    /// Whether the manifest describes an incremental (partial) update rather than a full one (proto field 16).
    /// <para>清单描述的是增量（部分）更新还是完整更新（proto 字段 16）。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> for incremental payloads whose operations depend on base images; <see langword="false"/> is the default.
    /// <para>依赖基座镜像的增量载荷为 <see langword="true"/>；默认 <see langword="false"/>。</para>
    /// </value>
    public bool PartialUpdate { get; set; }

    /// <summary>Wire field number of <c>security_patch_level</c>: <c>18</c>. <para><c>security_patch_level</c> 的线路字段号：<c>18</c>。</para></summary>
    public const int SecurityPatchLevelFieldNumber = 18;

    /// <summary>
    /// Security patch level string recorded by the producer (proto field 18).
    /// <para>生产者记录的安全补丁级别字符串（proto 字段 18）。</para>
    /// </summary>
    /// <value>
    /// Empty when unset; assigning <see langword="null"/> stores <see cref="string.Empty"/>. Non-empty values are always written.
    /// <para>未设置时为空串；赋 <see langword="null"/> 会存入 <see cref="string.Empty"/>。非空值总是写出。</para>
    /// </value>
    public string SecurityPatchLevel
    {
        get => _securityPatchLevel;
        set => _securityPatchLevel = value ?? string.Empty;
    }

    /// <summary>
    /// Determines whether this manifest equals another object by delegating to the typed comparison.
    /// <para>通过转向强类型比较，判定本清单是否与另一对象相等。</para>
    /// </summary>
    /// <param name="obj">
    /// Reference to compare; only a <see cref="DeltaArchiveManifest"/> instance can be equal.
    /// <para>待比较的引用；仅 <see cref="DeltaArchiveManifest"/> 实例可能相等。</para>
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a manifest whose resolved fields
    /// and unknown fields match this one; <see langword="false"/> for <see langword="null"/> or any
    /// other type. <para>当 <paramref name="obj"/> 为清单且其解析后字段与未知字段与本实例一致时返回
    /// <see langword="true"/>；对 <see langword="null"/> 或其他类型返回 <see langword="false"/>。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as DeltaArchiveManifest);

    /// <summary>
    /// Compares two manifests field by field, including unknown fields.
    /// <para>逐字段比较两份清单，包含未知字段。</para>
    /// </summary>
    /// <param name="other">Manifest to compare with; <see langword="null"/> is never equal. <para>待比较的清单；<see langword="null"/> 恒不相等。</para></param>
    /// <returns><see langword="true"/> when every resolved field value and the unknown-field set match; default-valued
    /// and unset fields compare equal because resolution hides presence.
    /// <para>所有解析后字段值与未知字段集合一致时为 <see langword="true"/>；由于解析隐藏了存在标志，默认值字段与未设置字段视为相等。</para></returns>
    public bool Equals(DeltaArchiveManifest? other)
        => other is not null && (ReferenceEquals(other, this) || (BlockSize == other.BlockSize && SignaturesOffset == other.SignaturesOffset && SignaturesSize == other.SignaturesSize && MinorVersion == other.MinorVersion && Partitions.Equals(other.Partitions) && MaxTimestamp == other.MaxTimestamp && Equals(DynamicPartitionMetadata, other.DynamicPartitionMetadata) && PartialUpdate == other.PartialUpdate && SecurityPatchLevel == other.SecurityPatchLevel && Equals(_unknownFields, other._unknownFields)));

    /// <summary>Computes the hash code of this manifest.<para>计算本清单的哈希代码。</para></summary>
    /// <returns>Base value <c>1</c> XOR-folded with each component that carries content: <see cref="BlockSize"/> only when <see cref="HasBlockSize"/> is set, the remaining scalars only when they differ from their proto3 defaults, <see cref="Partitions"/> always, and the stored sub-message, <see cref="PartialUpdate"/>, <see cref="SecurityPatchLevel"/> and unknown-field hashes when present. Manifests that compare equal therefore hash alike, because a default-valued field contributes nothing whether or not it was explicitly set.
    /// <para>以 <c>1</c> 为基数，依次异或各有效分量：仅在 <see cref="HasBlockSize"/> 置位时计入 <see cref="BlockSize"/>，其余标量仅在不同于 proto3 默认值时计入，<see cref="Partitions"/> 始终计入， 子消息、<see cref="PartialUpdate"/>、<see cref="SecurityPatchLevel"/> 与未知字段也仅在有内容时计入。 因此相等的清单哈希相同，因为默认值字段无论是否显式设置都不贡献哈希。</para></returns>
    public override int GetHashCode()
    {
        var hash = 1;
        if (HasBlockSize)
        {
            hash ^= BlockSize.GetHashCode();
        }

        if (SignaturesOffset != 0UL)
        {
            hash ^= SignaturesOffset.GetHashCode();
        }

        if (SignaturesSize != 0UL)
        {
            hash ^= SignaturesSize.GetHashCode();
        }

        if (MinorVersion != 0U)
        {
            hash ^= MinorVersion.GetHashCode();
        }

        hash ^= Partitions.GetHashCode();
        if (MaxTimestamp != 0L)
        {
            hash ^= MaxTimestamp.GetHashCode();
        }

        if (DynamicPartitionMetadata != null)
        {
            hash ^= DynamicPartitionMetadata.GetHashCode();
        }

        if (PartialUpdate)
        {
            hash ^= PartialUpdate.GetHashCode();
        }

        if (_securityPatchLevel.Length != 0)
        {
            hash ^= SecurityPatchLevel.GetHashCode();
        }

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <summary>
    /// Formats this manifest as text.
    /// <para>将本清单格式化为文本。</para>
    /// </summary>
    /// <returns>
    /// The runtime type name <c>DeltaArchiveManifest</c> alone; field values are not rendered.
    /// <para>仅返回运行时类型名 <c>DeltaArchiveManifest</c>；不呈现字段值。</para>
    /// </returns>
    public override string ToString() => GetType().Name;

    /// <summary>
    /// Serializes the manifest in proto3 style, omitting fields that still hold their default value.
    /// <para>按 proto3 风格序列化清单，仍为默认值的字段被省略。</para>
    /// </summary>
    /// <param name="output">Destination stream; written tags follow the same field numbers as parsing.
    /// <para>目标流；写出的标签与解析使用相同的字段号。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        if (HasBlockSize)
        {
            output.WriteRawTag(24);
            output.WriteUInt32(BlockSize);
        }

        if (SignaturesOffset != 0UL)
        {
            output.WriteRawTag(32);
            output.WriteUInt64(SignaturesOffset);
        }

        if (SignaturesSize != 0UL)
        {
            output.WriteRawTag(40);
            output.WriteUInt64(SignaturesSize);
        }

        if (MinorVersion != 0U)
        {
            output.WriteRawTag(96);
            output.WriteUInt32(MinorVersion);
        }

        Partitions.WriteTo(output, RepeatedPartitionsCodec);
        if (MaxTimestamp != 0L)
        {
            output.WriteRawTag(112);
            output.WriteInt64(MaxTimestamp);
        }

        if (DynamicPartitionMetadata != null)
        {
            output.WriteRawTag(122);
            output.WriteMessage(DynamicPartitionMetadata);
        }

        if (PartialUpdate)
        {
            output.WriteRawTag(128, 1);
            output.WriteBool(PartialUpdate);
        }

        if (_securityPatchLevel.Length != 0)
        {
            output.WriteRawTag(146, 1);
            output.WriteString(SecurityPatchLevel);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>
    /// Computes the exact wire size of this manifest without writing anything.
    /// <para>精确计算本清单的线路字节大小，不写出任何内容。</para>
    /// </summary>
    /// <returns>Byte count that <see cref="WriteTo"/> would produce, including tags, repeated entries and unknown fields.
    /// <para><see cref="WriteTo"/> 将产生的字节数，含标签、重复条目与未知字段。</para></returns>
    public int CalculateSize()
    {
        var size = 0;
        if (HasBlockSize)
        {
            size += 1 + CodedOutputStream.ComputeUInt32Size(BlockSize);
        }

        if (SignaturesOffset != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(SignaturesOffset);
        }

        if (SignaturesSize != 0UL)
        {
            size += 1 + CodedOutputStream.ComputeUInt64Size(SignaturesSize);
        }

        if (MinorVersion != 0U)
        {
            size += 1 + CodedOutputStream.ComputeUInt32Size(MinorVersion);
        }

        size += Partitions.CalculateSize(RepeatedPartitionsCodec);
        if (MaxTimestamp != 0L)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(MaxTimestamp);
        }

        if (DynamicPartitionMetadata != null)
        {
            size += 1 + CodedOutputStream.ComputeMessageSize(DynamicPartitionMetadata);
        }

        if (PartialUpdate)
        {
            size += 2;
        }

        if (_securityPatchLevel.Length != 0)
        {
            size += 2 + CodedOutputStream.ComputeStringSize(SecurityPatchLevel);
        }

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>
    /// Merges another manifest into this one using proto3 field-merge semantics.
    /// <para>按 proto3 字段合并语义把另一份清单合并进本清单。</para>
    /// </summary>
    /// <param name="other">Source manifest; non-default scalars overwrite, partitions are appended, dynamic partition
    /// metadata is created and merged, and unknown fields are combined. A <see langword="null"/> source leaves this
    /// instance unchanged.
    /// <para>来源清单；非默认标量覆盖，分区追加，动态分区元数据按需创建并合并，未知字段并入。来源为
    /// <see langword="null"/> 时本实例保持不变。</para></param>
    public void MergeFrom(DeltaArchiveManifest other)
    {
        if (other is null)
        {
            return;
        }

        if (other.HasBlockSize)
        {
            BlockSize = other._blockSize;
        }

        if (other.SignaturesOffset != 0UL)
        {
            SignaturesOffset = other.SignaturesOffset;
        }

        if (other.SignaturesSize != 0UL)
        {
            SignaturesSize = other.SignaturesSize;
        }

        if (other.MinorVersion != 0U)
        {
            MinorVersion = other.MinorVersion;
        }

        Partitions.Add(other.Partitions);
        if (other.MaxTimestamp != 0L)
        {
            MaxTimestamp = other.MaxTimestamp;
        }

        if (other.DynamicPartitionMetadata != null)
        {
            (DynamicPartitionMetadata ??= new DynamicPartitionMetadata()).MergeFrom(other.DynamicPartitionMetadata);
        }
        if (other.PartialUpdate)
        {
            PartialUpdate = other.PartialUpdate;
        }

        if (other._securityPatchLevel.Length != 0)
        {
            SecurityPatchLevel = other._securityPatchLevel;
        }

        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    /// <summary>
    /// Parses wire-format fields into this manifest until the stream reports the end tag.
    /// <para>从线路格式解析字段到本清单，直到流报告结束标记。</para>
    /// </summary>
    /// <param name="input">Protobuf input positioned at the first field tag; unrecognized fields are retained in the
    /// unknown-field set so a later <see cref="WriteTo(CodedOutputStream)"/> reproduces them.
    /// <para>定位在首个字段标签处的 Protobuf 输入；无法识别的字段保留在未知字段集合中，稍后
    /// <see cref="WriteTo(CodedOutputStream)"/> 会原样复写。</para></param>
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
                case 24:
                    BlockSize = input.ReadUInt32();
                    break;
                case 32:
                    SignaturesOffset = input.ReadUInt64();
                    break;
                case 40:
                    SignaturesSize = input.ReadUInt64();
                    break;
                case 96:
                    MinorVersion = input.ReadUInt32();
                    break;
                case 106:
                    Partitions.AddEntriesFrom(input, RepeatedPartitionsCodec);
                    break;
                case 112:
                    MaxTimestamp = input.ReadInt64();
                    break;
                case 122:
                    input.ReadMessage(DynamicPartitionMetadata ??= new DynamicPartitionMetadata());
                    break;
                case 128:
                    PartialUpdate = input.ReadBool();
                    break;
                case 146:
                    SecurityPatchLevel = input.ReadString();
                    break;
            }
        }
    }
}