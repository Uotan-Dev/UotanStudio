using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// Parses an Android Verified Boot VBMeta image header (magic "AVB0").
/// <para>解析 Android Verified Boot VBMeta 镜像头部(魔数 "AVB0")。</para>
/// </summary>
public readonly struct AvbVBMetaImageHeader
{
    /// <summary>
    /// Gets the header magic ("AVB0").
    /// <para>获取头部魔数("AVB0")。</para>
    /// </summary>
    public string Magic { get; init; }

    /// <summary>
    /// Gets the required libavb major version.
    /// <para>获取要求的 libavb 主版本号。</para>
    /// </summary>
    public uint RequiredLibavbVersionMajor { get; init; }

    /// <summary>
    /// Gets the required libavb minor version.
    /// <para>获取要求的 libavb 次版本号。</para>
    /// </summary>
    public uint RequiredLibavbVersionMinor { get; init; }

    /// <summary>
    /// Gets the authentication data block size in bytes.
    /// <para>获取认证数据块大小(字节)。</para>
    /// </summary>
    public ulong AuthenticationDataBlockSize { get; init; }

    /// <summary>
    /// Gets the auxiliary data block size in bytes.
    /// <para>获取辅助数据块大小(字节)。</para>
    /// </summary>
    public ulong AuxiliaryDataBlockSize { get; init; }

    /// <summary>
    /// Gets the signature algorithm type.
    /// <para>获取签名算法类型。</para>
    /// </summary>
    public uint AlgorithmType { get; init; }

    /// <summary>
    /// Gets the hash offset relative to the authentication data block.
    /// <para>获取相对于认证数据块的哈希偏移。</para>
    /// </summary>
    public ulong HashOffset { get; init; }

    /// <summary>
    /// Gets the hash size in bytes.
    /// <para>获取哈希大小(字节)。</para>
    /// </summary>
    public ulong HashSize { get; init; }

    /// <summary>
    /// Gets the signature offset relative to the authentication data block.
    /// <para>获取相对于认证数据块的签名偏移。</para>
    /// </summary>
    public ulong SignatureOffset { get; init; }

    /// <summary>
    /// Gets the signature size in bytes.
    /// <para>获取签名大小(字节)。</para>
    /// </summary>
    public ulong SignatureSize { get; init; }

    /// <summary>
    /// Gets the public key offset relative to the auxiliary data block.
    /// <para>获取相对于辅助数据块的公钥偏移。</para>
    /// </summary>
    public ulong PublicKeyOffset { get; init; }

    /// <summary>
    /// Gets the public key size in bytes.
    /// <para>获取公钥大小(字节)。</para>
    /// </summary>
    public ulong PublicKeySize { get; init; }

    /// <summary>
    /// Gets the public key metadata offset relative to the auxiliary data block.
    /// <para>获取相对于辅助数据块的公钥元数据偏移。</para>
    /// </summary>
    public ulong PublicKeyMetadataOffset { get; init; }

    /// <summary>
    /// Gets the public key metadata size in bytes.
    /// <para>获取公钥元数据大小(字节)。</para>
    /// </summary>
    public ulong PublicKeyMetadataSize { get; init; }

    /// <summary>
    /// Gets the descriptors offset relative to the auxiliary data block.
    /// <para>获取相对于辅助数据块的描述符偏移。</para>
    /// </summary>
    public ulong DescriptorsOffset { get; init; }

    /// <summary>
    /// Gets the descriptors size in bytes.
    /// <para>获取描述符总大小(字节)。</para>
    /// </summary>
    public ulong DescriptorsSize { get; init; }

    /// <summary>
    /// Gets the rollback index.
    /// <para>获取回滚索引。</para>
    /// </summary>
    public ulong RollbackIndex { get; init; }

    /// <summary>
    /// Gets the flags.
    /// <para>获取标志位。</para>
    /// </summary>
    public uint Flags { get; init; }

    /// <summary>
    /// Gets the rollback index location.
    /// <para>获取回滚索引位置。</para>
    /// </summary>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>
    /// Gets the release string.
    /// <para>获取发布字符串。</para>
    /// </summary>
    public string ReleaseString { get; init; }

    /// <summary>
    /// Size of the VBMeta image header in bytes.
    /// <para>VBMeta 镜像头部的字节大小。</para>
    /// </summary>
    public const int Size = 256;

    /// <summary>
    /// Expected header magic.
    /// <para>期望的头部魔数。</para>
    /// </summary>
    public const string ExpectedMagic = "AVB0";

    /// <summary>
    /// Parses a <see cref="AvbVBMetaImageHeader"/> from big-endian bytes.
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