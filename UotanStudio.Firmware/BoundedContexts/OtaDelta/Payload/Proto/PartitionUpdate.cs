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
        RunPostinstall = other.RunPostinstall;
        PostinstallPath = other.PostinstallPath;
        FilesystemType = other.FilesystemType;
        PostinstallOptional = other.PostinstallOptional;

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

    /// <summary>Gets or sets the optional update_engine RunPostinstall field, preserving protobuf presence.</summary>
    public bool? RunPostinstall { get; set; }

    /// <summary>Gets or sets the optional update_engine PostinstallPath field, preserving protobuf presence.</summary>
    public string? PostinstallPath { get; set; }

    /// <summary>Gets or sets the optional update_engine FilesystemType field, preserving protobuf presence.</summary>
    public string? FilesystemType { get; set; }

    /// <summary>Gets or sets the optional update_engine PostinstallOptional field, preserving protobuf presence.</summary>
    public bool? PostinstallOptional { get; set; }

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
        => other is not null && (ReferenceEquals(other, this) || (PartitionName == other.PartitionName && RunPostinstall == other.RunPostinstall && PostinstallPath == other.PostinstallPath && FilesystemType == other.FilesystemType && PostinstallOptional == other.PostinstallOptional && Equals(OldPartitionInfo, other.OldPartitionInfo) && Equals(NewPartitionInfo, other.NewPartitionInfo) && Operations.Equals(other.Operations) && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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