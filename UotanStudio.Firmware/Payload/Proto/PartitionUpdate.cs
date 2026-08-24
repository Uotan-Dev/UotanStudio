using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.Payload.Proto;

/// <summary>
/// Describes the update to apply to a single partition.
/// <para>描述应用于单个分区的更新。</para>
/// </summary>
public sealed class PartitionUpdate : IMessage<PartitionUpdate>
{
    private static readonly FieldCodec<InstallOperation> RepeatedOperationsCodec = FieldCodec.ForMessage(66, InstallOperation.Parser);

    private UnknownFieldSet? _unknownFields;
    private string _partitionName = string.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<PartitionUpdate> Parser { get; } = new(() => new PartitionUpdate());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public PartitionUpdate()
    {
    }

    public PartitionUpdate(PartitionUpdate other) : this()
    {
        _partitionName = other._partitionName;
        NewPartitionInfo = other.NewPartitionInfo?.Clone();
        Operations = other.Operations.Clone();
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public PartitionUpdate Clone() => new(this);

    /// <summary>Field number for the "partition_name" field.</summary>
    public const int PartitionNameFieldNumber = 1;

    public string PartitionName
    {
        get => _partitionName;
        set => _partitionName = value ?? string.Empty;
    }

    /// <summary>Field number for the "new_partition_info" field.</summary>
    public const int NewPartitionInfoFieldNumber = 7;

    public PartitionInfo? NewPartitionInfo { get; set; }

    /// <summary>Field number for the "operations" field.</summary>
    public const int OperationsFieldNumber = 8;

    public RepeatedField<InstallOperation> Operations { get; } = [];

    public override bool Equals(object? obj) => Equals(obj as PartitionUpdate);

    public bool Equals(PartitionUpdate? other)
        => other is not null && (ReferenceEquals(other, this) || (PartitionName == other.PartitionName && Equals(NewPartitionInfo, other.NewPartitionInfo) && Operations.Equals(other.Operations) && Equals(_unknownFields, other._unknownFields)));

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
        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    public override string ToString() => GetType().Name;

    public void WriteTo(CodedOutputStream output)
    {
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

    public int CalculateSize()
    {
        var size = 0;
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

    public void MergeFrom(PartitionUpdate other)
    {
        if (other is null)
        {
            return;
        }

        if (other._partitionName.Length != 0)
        {
            PartitionName = other._partitionName;
        }

        if (other.NewPartitionInfo != null)
        {
            (NewPartitionInfo ??= new PartitionInfo()).MergeFrom(other.NewPartitionInfo);
        }

        Operations.Add(other.Operations);
        _unknownFields = UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

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
                    PartitionName = input.ReadString();
                    break;
                case 58:
                    input.ReadMessage(NewPartitionInfo ??= new PartitionInfo());
                    break;
                case 66:
                    Operations.AddEntriesFrom(input, RepeatedOperationsCodec);
                    break;
            }
        }
    }
}