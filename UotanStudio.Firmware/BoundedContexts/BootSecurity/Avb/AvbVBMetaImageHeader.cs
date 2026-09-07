using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

public readonly struct AvbVBMetaImageHeader
{
    public string Magic { get; init; }

    public uint RequiredLibavbVersionMajor { get; init; }

    public uint RequiredLibavbVersionMinor { get; init; }

    public ulong AuthenticationDataBlockSize { get; init; }

    public ulong AuxiliaryDataBlockSize { get; init; }

    public uint AlgorithmType { get; init; }

    public ulong HashOffset { get; init; }

    public ulong HashSize { get; init; }

    public ulong SignatureOffset { get; init; }

    public ulong SignatureSize { get; init; }

    public ulong PublicKeyOffset { get; init; }

    public ulong PublicKeySize { get; init; }

    public ulong PublicKeyMetadataOffset { get; init; }

    public ulong PublicKeyMetadataSize { get; init; }

    public ulong DescriptorsOffset { get; init; }

    public ulong DescriptorsSize { get; init; }

    public ulong RollbackIndex { get; init; }

    public uint Flags { get; init; }

    public uint RollbackIndexLocation { get; init; }

    public string ReleaseString { get; init; }

    public const int Size = 256;

    public const string ExpectedMagic = "AVB0";

    /// <summary>
    /// Parses a AvbVBMetaImageHeader from big-endian bytes.
    /// <para>从大端字节解析 AvbVBMetaImageHeader。</para>
    /// </summary>
    public static AvbVBMetaImageHeader FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new ArgumentException($"VBMeta image header requires at least {Size} bytes.");
        }

        var header = new AvbVBMetaImageHeader
        {
            Magic = Encoding.ASCII.GetString(data[..4]),
            RequiredLibavbVersionMajor = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            RequiredLibavbVersionMinor = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            AuthenticationDataBlockSize = BinaryPrimitives.ReadUInt64BigEndian(data[12..]),
            AuxiliaryDataBlockSize = BinaryPrimitives.ReadUInt64BigEndian(data[20..]),
            AlgorithmType = BinaryPrimitives.ReadUInt32BigEndian(data[28..]),
            HashOffset = BinaryPrimitives.ReadUInt64BigEndian(data[32..]),
            HashSize = BinaryPrimitives.ReadUInt64BigEndian(data[40..]),
            SignatureOffset = BinaryPrimitives.ReadUInt64BigEndian(data[48..]),
            SignatureSize = BinaryPrimitives.ReadUInt64BigEndian(data[56..]),
            PublicKeyOffset = BinaryPrimitives.ReadUInt64BigEndian(data[64..]),
            PublicKeySize = BinaryPrimitives.ReadUInt64BigEndian(data[72..]),
            PublicKeyMetadataOffset = BinaryPrimitives.ReadUInt64BigEndian(data[80..]),
            PublicKeyMetadataSize = BinaryPrimitives.ReadUInt64BigEndian(data[88..]),
            DescriptorsOffset = BinaryPrimitives.ReadUInt64BigEndian(data[96..]),
            DescriptorsSize = BinaryPrimitives.ReadUInt64BigEndian(data[104..]),
            RollbackIndex = BinaryPrimitives.ReadUInt64BigEndian(data[112..]),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(data[120..]),
            RollbackIndexLocation = BinaryPrimitives.ReadUInt32BigEndian(data[124..]),
            ReleaseString = ReadAscii(data, 128, 48)
        };
        return header;
    }

    /// <summary>
    /// Validates that the magic matches "AVB0".
    /// <para>验证魔数是否为 "AVB0"。</para>
    /// </summary>
    public bool IsValid() => string.Equals(Magic, ExpectedMagic, StringComparison.Ordinal);

    private static string ReadAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        var slice = data.Slice(offset, length);
        var end = 0;
        while (end < slice.Length && slice[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(slice[..end]);
    }
}
