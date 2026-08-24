using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.Payload.Proto;

/// <summary>
/// A group of dynamic partitions with a shared maximum size.
/// <para>一组共享最大尺寸的动态分区。</para>
/// </summary>
public sealed class DynamicPartitionGroup : IMessage<DynamicPartitionGroup>
{
    private static readonly FieldCodec<string> RepeatedPartitionNamesCodec = FieldCodec.ForString(26);

    private UnknownFieldSet? _unknownFields;
    private string _name = string.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<DynamicPartitionGroup> Parser { get; } = new(() => new DynamicPartitionGroup());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public DynamicPartitionGroup()
    {
    }

    public DynamicPartitionGroup(DynamicPartitionGroup other) : this()
    {
        _name = other._name;
        Size = other.Size;
        PartitionNames = other.PartitionNames.Clone();
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public DynamicPartitionGroup Clone() => new(this);

    /// <summary>Field number for the "name" field.</summary>
    public const int NameFieldNumber = 1;

    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <summary>Field number for the "size" field.</summary>
    public const int SizeFieldNumber = 2;

    public ulong Size { get; set; }

    /// <summary>Field number for the "partition_names" field.</summary>
    public const int PartitionNamesFieldNumber = 3;

    public RepeatedField<string> PartitionNames { get; } = [];

    public override bool Equals(object? obj) => Equals(obj as DynamicPartitionGroup);

    public bool Equals(DynamicPartitionGroup? other)
        => other is not null && (ReferenceEquals(other, this) || (Name == other.Name && Size == other.Size && PartitionNames.Equals(other.PartitionNames) && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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
/// Metadata related to all dynamic partitions in the payload.
/// <para>与载荷中所有动态分区相关的元数据。</para>
/// </summary>
public sealed class DynamicPartitionMetadata : IMessage<DynamicPartitionMetadata>
{
    private static readonly FieldCodec<DynamicPartitionGroup> RepeatedGroupsCodec = FieldCodec.ForMessage(10, DynamicPartitionGroup.Parser);

    private UnknownFieldSet? _unknownFields;
    private string _vabcCompressionParam = string.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<DynamicPartitionMetadata> Parser { get; } = new(() => new DynamicPartitionMetadata());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public DynamicPartitionMetadata()
    {
    }

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

    public DynamicPartitionMetadata Clone() => new(this);

    /// <summary>Field number for the "groups" field.</summary>
    public const int GroupsFieldNumber = 1;

    public RepeatedField<DynamicPartitionGroup> Groups { get; } = [];

    /// <summary>Field number for the "snapshot_enabled" field.</summary>
    public const int SnapshotEnabledFieldNumber = 2;

    public bool SnapshotEnabled { get; set; }

    /// <summary>Field number for the "vabc_enabled" field.</summary>
    public const int VabcEnabledFieldNumber = 3;

    public bool VabcEnabled { get; set; }

    /// <summary>Field number for the "vabc_compression_param" field.</summary>
    public const int VabcCompressionParamFieldNumber = 4;

    public string VabcCompressionParam
    {
        get => _vabcCompressionParam;
        set => _vabcCompressionParam = value ?? string.Empty;
    }

    /// <summary>Field number for the "cow_version" field.</summary>
    public const int CowVersionFieldNumber = 5;

    public uint CowVersion { get; set; }

    /// <summary>Field number for the "compression_factor" field.</summary>
    public const int CompressionFactorFieldNumber = 7;

    public ulong CompressionFactor { get; set; }

    public override bool Equals(object? obj) => Equals(obj as DynamicPartitionMetadata);

    public bool Equals(DynamicPartitionMetadata? other)
        => other is not null && (ReferenceEquals(other, this) || (Groups.Equals(other.Groups) && SnapshotEnabled == other.SnapshotEnabled && VabcEnabled == other.VabcEnabled && VabcCompressionParam == other.VabcCompressionParam && CowVersion == other.CowVersion && CompressionFactor == other.CompressionFactor && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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