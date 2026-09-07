using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

public sealed class PartitionUpdate : IMessage<PartitionUpdate>
{
    private static readonly FieldCodec<InstallOperation> RepeatedOperationsCodec = FieldCodec.ForMessage(66, InstallOperation.Parser);

    private UnknownFieldSet? _unknownFields;
    private string _partitionName = string.Empty;

    public static MessageParser<PartitionUpdate> Parser { get; } = new(() => new PartitionUpdate());

    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public PartitionUpdate()
    {
    }

    public PartitionUpdate(PartitionUpdate other) : this()
    {
        _partitionName = other._partitionName;
        OldPartitionInfo = other.OldPartitionInfo?.Clone();
        NewPartitionInfo = other.NewPartitionInfo?.Clone();
        Operations = other.Operations.Clone();
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public PartitionUpdate Clone() => new(this);

    public const int PartitionNameFieldNumber = 1;

    public string PartitionName
    {
        get => _partitionName;
        set => _partitionName = value ?? string.Empty;
    }

    public const int NewPartitionInfoFieldNumber = 7;

    /// <summary>Identifies the old partition info protobuf field. <para>标识旧分区信息的 protobuf 字段。</para></summary>
    public const int OldPartitionInfoFieldNumber = 6;

    /// <summary>Gets or sets the immutable source partition identity. <para>获取或设置不可变源分区身份。</para></summary>
    public PartitionInfo? OldPartitionInfo { get; set; }

    public PartitionInfo? NewPartitionInfo { get; set; }

    public const int OperationsFieldNumber = 8;

    public RepeatedField<InstallOperation> Operations { get; } = [];

    public override bool Equals(object? obj) => Equals(obj as PartitionUpdate);

    public bool Equals(PartitionUpdate? other)
        => other is not null && (ReferenceEquals(other, this) || (PartitionName == other.PartitionName && Equals(OldPartitionInfo, other.OldPartitionInfo) && Equals(NewPartitionInfo, other.NewPartitionInfo) && Operations.Equals(other.Operations) && Equals(_unknownFields, other._unknownFields)));

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

        return hash;
    }

    public override string ToString() => GetType().Name;

    public void WriteTo(CodedOutputStream output)
    {
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

    public int CalculateSize()
    {
        var size = 0;
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
        if (other.OldPartitionInfo is not null) (OldPartitionInfo ??= new PartitionInfo()).MergeFrom(other.OldPartitionInfo);

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