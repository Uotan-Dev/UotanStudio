using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

public sealed class InstallOperation : IMessage<InstallOperation>
{
    public static class Types
    {
        public enum Type
        {
            Replace = 0,

            ReplaceBz = 1,

            Move = 2,

            Bsdiff = 3,

            SourceCopy = 4,

            SourceBsdiff = 5,

            Zero = 6,

            Discard = 7,

            ReplaceXz = 8,

            Puffdiff = 9,

            BrotliBsdiff = 10,

            Zucchini = 11,

            Lz4diffBsdiff = 12,

            Lz4diffPuffdiff = 13
        }
    }

    private static readonly FieldCodec<Extent> RepeatedSrcExtentsCodec = FieldCodec.ForMessage(34, Extent.Parser);
    private static readonly FieldCodec<Extent> RepeatedDstExtentsCodec = FieldCodec.ForMessage(50, Extent.Parser);

    private UnknownFieldSet? _unknownFields;
    private ByteString _dataSha256Hash = ByteString.Empty;
    private ByteString _srcSha256Hash = ByteString.Empty;

    public static MessageParser<InstallOperation> Parser { get; } = new(() => new InstallOperation());

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

    public const int TypeFieldNumber = 1;

    public Types.Type Type { get; set; }

    public const int DataOffsetFieldNumber = 2;

    public ulong DataOffset { get; set; }

    public const int DataLengthFieldNumber = 3;

    public ulong DataLength { get; set; }

    public const int SrcExtentsFieldNumber = 4;

    public RepeatedField<Extent> SrcExtents { get; } = [];

    public const int SrcLengthFieldNumber = 5;

    public ulong SrcLength { get; set; }

    public const int DstExtentsFieldNumber = 6;

    public RepeatedField<Extent> DstExtents { get; } = [];

    public const int DstLengthFieldNumber = 7;

    public ulong DstLength { get; set; }

    public const int DataSha256HashFieldNumber = 8;

    public ByteString DataSha256Hash
    {
        get => _dataSha256Hash;
        set => _dataSha256Hash = value ?? ByteString.Empty;
    }

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