using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.Payload.Proto;

/// <summary>
/// The top-level manifest of an Android OTA payload, describing all partition updates.
/// <para>Android OTA 载荷的顶层清单，描述所有分区更新。</para>
/// </summary>
public sealed class DeltaArchiveManifest : IMessage<DeltaArchiveManifest>
{
    private static readonly FieldCodec<PartitionUpdate> RepeatedPartitionsCodec = FieldCodec.ForMessage(106, PartitionUpdate.Parser);

    private UnknownFieldSet? _unknownFields;
    private uint _blockSize;
    private string _securityPatchLevel = string.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<DeltaArchiveManifest> Parser { get; } = new(() => new DeltaArchiveManifest());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public DeltaArchiveManifest()
    {
    }

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

    public DeltaArchiveManifest Clone() => new(this);

    /// <summary>Field number for the "block_size" field.</summary>
    public const int BlockSizeFieldNumber = 3;

    private const uint BlockSizeDefaultValue = 4096;

    /// <summary>
    /// Gets or sets the block size in bytes (usually 4096).
    /// <para>获取或设置块大小（字节，通常为 4096）。</para>
    /// </summary>
    public uint BlockSize
    {
        get => HasBlockSize ? _blockSize : BlockSizeDefaultValue;
        set
        {
            HasBlockSize = true;
            _blockSize = value;
        }
    }

    /// <summary>Gets whether the "block_size" field is set.</summary>
    public bool HasBlockSize { get; private set; }

    /// <summary>Clears the value of the "block_size" field.</summary>
    public void ClearBlockSize() => HasBlockSize = false;

    /// <summary>Field number for the "signatures_offset" field.</summary>
    public const int SignaturesOffsetFieldNumber = 4;

    public ulong SignaturesOffset { get; set; }

    /// <summary>Field number for the "signatures_size" field.</summary>
    public const int SignaturesSizeFieldNumber = 5;

    public ulong SignaturesSize { get; set; }

    /// <summary>Field number for the "minor_version" field.</summary>
    public const int MinorVersionFieldNumber = 12;

    public uint MinorVersion { get; set; }

    /// <summary>Field number for the "partitions" field.</summary>
    public const int PartitionsFieldNumber = 13;

    public RepeatedField<PartitionUpdate> Partitions { get; } = [];

    /// <summary>Field number for the "max_timestamp" field.</summary>
    public const int MaxTimestampFieldNumber = 14;

    public long MaxTimestamp { get; set; }

    /// <summary>Field number for the "dynamic_partition_metadata" field.</summary>
    public const int DynamicPartitionMetadataFieldNumber = 15;

    public DynamicPartitionMetadata? DynamicPartitionMetadata { get; set; }

    /// <summary>Field number for the "partial_update" field.</summary>
    public const int PartialUpdateFieldNumber = 16;

    public bool PartialUpdate { get; set; }

    /// <summary>Field number for the "security_patch_level" field.</summary>
    public const int SecurityPatchLevelFieldNumber = 18;

    public string SecurityPatchLevel
    {
        get => _securityPatchLevel;
        set => _securityPatchLevel = value ?? string.Empty;
    }

    public override bool Equals(object? obj) => Equals(obj as DeltaArchiveManifest);

    public bool Equals(DeltaArchiveManifest? other)
        => other is not null && (ReferenceEquals(other, this) || (BlockSize == other.BlockSize && SignaturesOffset == other.SignaturesOffset && SignaturesSize == other.SignaturesSize && MinorVersion == other.MinorVersion && Partitions.Equals(other.Partitions) && MaxTimestamp == other.MaxTimestamp && Equals(DynamicPartitionMetadata, other.DynamicPartitionMetadata) && PartialUpdate == other.PartialUpdate && SecurityPatchLevel == other.SecurityPatchLevel && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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