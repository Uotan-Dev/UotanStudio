using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.Payload.Proto;

/// <summary>
/// A single install operation applied while building the target partition image.
/// <para>在构建目标分区镜像时应用的单个安装操作。</para>
/// </summary>
public sealed class InstallOperation : IMessage<InstallOperation>
{
    /// <summary>
    /// Container for nested types of <see cref="InstallOperation"/>.
    /// <para><see cref="InstallOperation"/> 嵌套类型的容器。</para>
    /// </summary>
    public static class Types
    {
        /// <summary>
        /// The type of an install operation.
        /// <para>安装操作的类型。</para>
        /// </summary>
        public enum Type
        {
            /// <summary>Replace destination extents with attached data.</summary>
            Replace = 0,

            /// <summary>Replace destination extents with attached bzipped data.</summary>
            ReplaceBz = 1,

            /// <summary>Move source extents to target extents (deprecated).</summary>
            Move = 2,

            /// <summary>The data is a bsdiff binary diff (deprecated).</summary>
            Bsdiff = 3,

            /// <summary>Copy from source to target partition (minor version 2+).</summary>
            SourceCopy = 4,

            /// <summary>Like BSDIFF, but read from source partition (minor version 2+).</summary>
            SourceBsdiff = 5,

            /// <summary>Write zeros in the destination (minor version 4+).</summary>
            Zero = 6,

            /// <summary>Discard the destination blocks, reading as undefined (minor version 4+).</summary>
            Discard = 7,

            /// <summary>Replace destination extents with attached xz data (minor version 3+).</summary>
            ReplaceXz = 8,

            /// <summary>The data is in puffdiff format (minor version 5+).</summary>
            Puffdiff = 9,

            /// <summary>Like SOURCE_BSDIFF, but compressed with brotli (minor version 4+).</summary>
            BrotliBsdiff = 10,

            /// <summary>Zucchini patch format (minor version 8+).</summary>
            Zucchini = 11,

            /// <summary>LZ4 diff + bsdiff (minor version 9+).</summary>
            Lz4diffBsdiff = 12,

            /// <summary>LZ4 diff + puffdiff (minor version 9+).</summary>
            Lz4diffPuffdiff = 13
        }
    }

    private static readonly FieldCodec<Extent> RepeatedSrcExtentsCodec = FieldCodec.ForMessage(34, Extent.Parser);
    private static readonly FieldCodec<Extent> RepeatedDstExtentsCodec = FieldCodec.ForMessage(50, Extent.Parser);

    private UnknownFieldSet? _unknownFields;
    private ByteString _dataSha256Hash = ByteString.Empty;
    private ByteString _srcSha256Hash = ByteString.Empty;

    /// <summary>
    /// Gets the parser for this message type.
    /// <para>获取此消息类型的解析器。</para>
    /// </summary>
    public static MessageParser<InstallOperation> Parser { get; } = new(() => new InstallOperation());

    /// <summary>
    /// Gets the message descriptor.
    /// <para>获取消息描述符。</para>
    /// </summary>
    public static MessageDescriptor Descriptor => null!;

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public InstallOperation()
    {
    }

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
        _unknownFields = UnknownFieldSet.Clone(other._unknownFields);
    }

    public InstallOperation Clone() => new(this);

    /// <summary>Field number for the "type" field.</summary>
    public const int TypeFieldNumber = 1;

    public Types.Type Type { get; set; }

    /// <summary>Field number for the "data_offset" field.</summary>
    public const int DataOffsetFieldNumber = 2;

    public ulong DataOffset { get; set; }

    /// <summary>Field number for the "data_length" field.</summary>
    public const int DataLengthFieldNumber = 3;

    public ulong DataLength { get; set; }

    /// <summary>Field number for the "src_extents" field.</summary>
    public const int SrcExtentsFieldNumber = 4;

    public RepeatedField<Extent> SrcExtents { get; } = [];

    /// <summary>Field number for the "src_length" field.</summary>
    public const int SrcLengthFieldNumber = 5;

    public ulong SrcLength { get; set; }

    /// <summary>Field number for the "dst_extents" field.</summary>
    public const int DstExtentsFieldNumber = 6;

    public RepeatedField<Extent> DstExtents { get; } = [];

    /// <summary>Field number for the "dst_length" field.</summary>
    public const int DstLengthFieldNumber = 7;

    public ulong DstLength { get; set; }

    /// <summary>Field number for the "data_sha256_hash" field.</summary>
    public const int DataSha256HashFieldNumber = 8;

    public ByteString DataSha256Hash
    {
        get => _dataSha256Hash;
        set => _dataSha256Hash = value ?? ByteString.Empty;
    }

    /// <summary>Field number for the "src_sha256_hash" field.</summary>
    public const int SrcSha256HashFieldNumber = 9;

    public ByteString SrcSha256Hash
    {
        get => _srcSha256Hash;
        set => _srcSha256Hash = value ?? ByteString.Empty;
    }

    public override bool Equals(object? obj) => Equals(obj as InstallOperation);

    public bool Equals(InstallOperation? other)
        => other is not null && (ReferenceEquals(other, this) || (Type == other.Type && DataOffset == other.DataOffset && DataLength == other.DataLength && SrcExtents.Equals(other.SrcExtents) && SrcLength == other.SrcLength && DstExtents.Equals(other.DstExtents) && DstLength == other.DstLength && DataSha256Hash == other.DataSha256Hash && SrcSha256Hash == other.SrcSha256Hash && Equals(_unknownFields, other._unknownFields)));

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

        if (_unknownFields != null)
        {
            hash ^= _unknownFields.GetHashCode();
        }

        return hash;
    }

    public override string ToString() => GetType().Name;

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

        _unknownFields?.WriteTo(output);
    }

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

        if (_unknownFields != null)
        {
            size += _unknownFields.CalculateSize();
        }

        return size;
    }

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
            }
        }
    }
}