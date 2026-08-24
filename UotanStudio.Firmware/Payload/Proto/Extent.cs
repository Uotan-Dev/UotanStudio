using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.Payload.Proto;

/// <summary>
/// An ordered list of blocks on disk, encoded as a run of blocks.
/// <para>磁盘上有序的块列表，以块区间形式编码。</para>
/// </summary>
public sealed class Extent : IMessage<Extent>
{
    private UnknownFieldSet? _unknownFields;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<Extent> Parser { get; } = new(() => new Extent());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public Extent()
    {
    }

    public Extent(Extent other) : this()
    {
        StartBlock = other.StartBlock;
        NumBlocks = other.NumBlocks;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public Extent Clone() => new(this);

    /// <summary>Field number for the "start_block" field.</summary>
    public const int StartBlockFieldNumber = 1;

    public ulong StartBlock { get; set; }

    /// <summary>Field number for the "num_blocks" field.</summary>
    public const int NumBlocksFieldNumber = 2;

    public ulong NumBlocks { get; set; }

    public override bool Equals(object? obj) => Equals(obj as Extent);

    public bool Equals(Extent? other)
        => other is not null && (ReferenceEquals(other, this) || (StartBlock == other.StartBlock && NumBlocks == other.NumBlocks && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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
/// Describes the size and hash of a partition image (old or new).
/// <para>描述分区镜像（旧或新）的大小与哈希。</para>
/// </summary>
public sealed class PartitionInfo : IMessage<PartitionInfo>
{
    private UnknownFieldSet? _unknownFields;
    private ByteString _hash = ByteString.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<PartitionInfo> Parser { get; } = new(() => new PartitionInfo());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public PartitionInfo()
    {
    }

    public PartitionInfo(PartitionInfo other) : this()
    {
        Size = other.Size;
        _hash = other._hash;
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public PartitionInfo Clone() => new(this);

    /// <summary>Field number for the "size" field.</summary>
    public const int SizeFieldNumber = 1;

    public ulong Size { get; set; }

    /// <summary>Field number for the "hash" field.</summary>
    public const int HashFieldNumber = 2;

    public ByteString Hash
    {
        get => _hash;
        set => _hash = value ?? ByteString.Empty;
    }

    public override bool Equals(object? obj) => Equals(obj as PartitionInfo);

    public bool Equals(PartitionInfo? other)
        => other is not null && (ReferenceEquals(other, this) || (Size == other.Size && Hash == other.Hash && Equals(_unknownFields, other._unknownFields)));

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

    public override string ToString() => GetType().Name;

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