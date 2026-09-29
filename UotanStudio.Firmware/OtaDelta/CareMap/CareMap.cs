using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.OtaDelta.CareMap;

/// <summary>
/// Care map consumed by the recovery update verifier: the block ranges a verified partition must
/// present after an update, carried as <c>care_map.pb</c> in an OTA package and
/// <c>META/care_map.pb</c> in target files.
/// <para>供 recovery 校验器使用的 care map：记录已校验分区在更新后必须呈现的块区间，
/// OTA 包内成员名为 <c>care_map.pb</c>，target files 内为 <c>META/care_map.pb</c>。</para>
/// </summary>
public sealed class CareMap : IMessage<CareMap>
{
    private static readonly FieldCodec<CareMapPartitionInfo> RepeatedPartitionsCodec =
        FieldCodec.ForMessage(10, CareMapPartitionInfo.Parser);

    private UnknownFieldSet? _unknownFields;

    /// <summary>
    /// ZIP member name carrying the serialized map inside an OTA package. It is stored uncompressed so a
    /// streaming verifier can read it before the payload finishes downloading.
    /// <para>OTA 包内承载序列化 care map 的 ZIP 成员名。该成员不压缩，便于流式校验在下载完成前读取。</para>
    /// </summary>
    /// <value>The literal <c>care_map.pb</c>. <para>字面量 <c>care_map.pb</c>。</para></value>
    public const string ZipEntryName = "care_map.pb";

    /// <summary>
    /// Entry name carrying the serialized map inside a target-files archive.
    /// <para>target files 归档内承载序列化 care map 的条目名。</para>
    /// </summary>
    /// <value>The literal <c>META/care_map.pb</c>. <para>字面量 <c>META/care_map.pb</c>。</para></value>
    public const string TargetFilesEntryName = "META/care_map.pb";

    /// <summary>
    /// Block size the care map counts in, independent of the filesystem or sparse block size.
    /// <para>care map 计数所用的块大小，与文件系统或 sparse 块大小无关。</para>
    /// </summary>
    /// <value>4096 bytes. <para>4096 字节。</para></value>
    public const int BlockSize = 4096;

    /// <summary>
    /// Ceiling on partition records accepted while parsing, bounding work on a hostile message.
    /// <para>解析时接受的分区记录数量上限，限制敌意消息带来的工作量。</para>
    /// </summary>
    /// <value>256 records. <para>256 条记录。</para></value>
    public const int MaximumPartitionCount = 256;

    /// <summary>
    /// Shared parser that decodes a wire-format care map.
    /// <para>解码线格式 care map 的共享分析器。</para>
    /// </summary>
    /// <value>A singleton parser creating a fresh instance per parse.
    /// <para>每次解析创建新实例的单例分析器。</para></value>
    public static MessageParser<CareMap> Parser { get; } = new(() => new CareMap());

    /// <summary>
    /// Reflection descriptor placeholder for the message.
    /// <para>消息的反射描述符占位。</para>
    /// </summary>
    /// <value>Always <see langword="null"/>; this hand-written message carries no generated descriptor metadata.
    /// <para>恒为 <see langword="null"/>；该手写消息不携带生成的描述符元数据。</para></value>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    /// <summary>Initializes an empty care map. <para>初始化空 care map。</para></summary>
    public CareMap()
    {
    }

    private CareMap(CareMap other)
    {
        foreach (var partition in other.Partitions)
        {
            Partitions.Add(partition.Clone());
        }

        _unknownFields = other._unknownFields is null ? null : UnknownFieldSet.Clone(other._unknownFields);
    }

    /// <summary>Gets the partition records in the order the producer wrote them.
    /// <para>按生产者写入顺序获取分区记录。</para></summary>
    /// <value>The mutable repeated field; an empty map carries no records.
    /// <para>可变的重复字段；空 care map 不含任何记录。</para></value>
    public RepeatedField<CareMapPartitionInfo> Partitions { get; } = [];

    /// <summary>Creates a deep copy of this care map. <para>创建本 care map 的深拷贝。</para></summary>
    /// <returns>An independent copy with cloned partition records.
    /// <para>记录已克隆且相互独立的副本。</para></returns>
    public CareMap Clone() => new(this);

    /// <summary>
    /// Returns whether the partition records and unknown fields of two maps match.
    /// <para>判断两个 care map 的分区记录与未知字段是否一致。</para>
    /// </summary>
    /// <param name="other">Candidate message; <see langword="null"/> never equals.
    /// <para>候选消息；<see langword="null"/> 永不相等。</para></param>
    /// <returns><see langword="true"/> when the maps are field-equal. <para>字段完全相等时为 <see langword="true"/>。</para></returns>
    public bool Equals(CareMap? other) => other is not null
        && Partitions.Equals(other.Partitions)
        && Equals(_unknownFields, other._unknownFields);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CareMap);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = 1;
        hash ^= Partitions.GetHashCode();
        if (_unknownFields is not null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    /// <inheritdoc/>
    public override string ToString() => GetType().Name;

    /// <summary>
    /// Serializes the map to a new array.
    /// <para>把 care map 序列化到新建的数组。</para>
    /// </summary>
    /// <returns>The wire-format bytes of the whole message. <para>整条消息的线格式字节。</para></returns>
    /// <exception cref="InvalidOperationException">The message needs more than 2 GiB of output.
    /// <para>消息输出需要超过 2 GiB。</para></exception>
    public byte[] ToBytes()
    {
        var output = new byte[CalculateSize()];
        var stream = new CodedOutputStream(output);
        WriteTo(stream);
        return output;
    }

    /// <summary>
    /// Decodes a care map from wire-format bytes.
    /// <para>从线格式字节解码 care map。</para>
    /// </summary>
    /// <param name="bytes">Complete message body. <para>完整的消息体。</para></param>
    /// <returns>The decoded message. <para>解码得到的消息。</para></returns>
    /// <exception cref="InvalidDataException">
    /// The bytes are truncated, malformed, or carry more than <see cref="MaximumPartitionCount"/> records.
    /// <para>字节被截断、格式非法，或记录数超过 <see cref="MaximumPartitionCount"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is <see langword="null"/>.
    /// <para><paramref name="bytes"/> 为 <see langword="null"/>。</para></exception>
    public static CareMap FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var map = Parser.ParseFrom(bytes);
        if (map.Partitions.Count > MaximumPartitionCount)
        {
            throw new InvalidDataException($"Care map carries {map.Partitions.Count} partitions, above the {MaximumPartitionCount} limit.");
        }

        return map;
    }

    /// <summary>
    /// Writes the map into an archive or file stream.
    /// <para>把 care map 写入归档或文件流。</para>
    /// </summary>
    /// <param name="output">Destination stream receiving exactly <see cref="CalculateSize"/> bytes.
    /// <para>恰好接收 <see cref="CalculateSize"/> 字节的目标流。</para></param>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(ToBytes());
    }

    /// <summary>
    /// Writes each partition record, then any retained unknown fields.
    /// <para>逐条写入分区记录，随后写入保留的未知字段。</para>
    /// </summary>
    /// <param name="output">Sink receiving the wire-format body. <para>接收线格式消息体的输出流。</para></param>
    public void WriteTo(CodedOutputStream output)
    {
        foreach (var partition in Partitions)
        {
            output.WriteRawTag(10);
            output.WriteMessage(partition);
        }

        _unknownFields?.WriteTo(output);
    }

    /// <summary>Computes the serialized length in bytes. <para>计算序列化后的字节长度。</para></summary>
    /// <returns>The number of bytes <see cref="WriteTo(CodedOutputStream)"/> emits.
    /// <para>线格式写入将输出的字节数。</para></returns>
    public int CalculateSize()
    {
        var size = Partitions.CalculateSize(RepeatedPartitionsCodec);
        if (_unknownFields is not null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

    /// <summary>Appends another map's records onto this one. <para>把另一 care map 的记录追加到本实例。</para></summary>
    /// <param name="other">Source message; ignored when <see langword="null"/>.
    /// <para>来源消息；为 <see langword="null"/> 时忽略。</para></param>
    public void MergeFrom(CareMap? other)
    {
        if (other is null)
        {
            return;
        }

        Partitions.Add(other.Partitions);
        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    void IMessage.MergeFrom(CodedInputStream input) => MergeFrom(input);

    /// <summary>
    /// Reads wire-format records until the stream or enclosing limit ends, storing unknown tags.
    /// <para>持续读取线格式记录直到流或外层边界结束，未知标签会被保留。</para>
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
                case 10: Partitions.AddEntriesFrom(input, RepeatedPartitionsCodec); break;
            }
        }
    }
}