using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

/// <summary>
/// In-memory model of the 256-byte AVB2 VBMeta image header, with every field decoded from big-endian bytes.
/// <para>256 字节 AVB2 VBMeta 镜像头的内存模型，所有字段均按大端字节解码。</para>
/// </summary>
public readonly struct AvbVBMetaImageHeader
{
    /// <summary>Four-byte ASCII tag at offset 0; a well-formed header carries <see cref="ExpectedMagic"/>.
    /// <para>偏移 0 处的 4 字节 ASCII 标签；合法头部应为 <see cref="ExpectedMagic"/>。</para></summary>
    /// <value>The decoded ASCII magic. <para>解码得到的 ASCII 魔数。</para></value>
    public string Magic { get; init; }

    /// <summary>Major libavb version required to interpret this VBMeta (offset 4). <para>解释该 VBMeta 所需的 libavb 主版本号（偏移 4）。</para></summary>
    /// <value>The big-endian 32-bit major version. <para>大端 32 位主版本号。</para></value>
    public uint RequiredLibavbVersionMajor { get; init; }

    /// <summary>Minor libavb version required to interpret this VBMeta (offset 8). <para>解释该 VBMeta 所需的 libavb 次版本号（偏移 8）。</para></summary>
    /// <value>The big-endian 32-bit minor version. <para>大端 32 位次版本号。</para></value>
    public uint RequiredLibavbVersionMinor { get; init; }

    /// <summary>Byte size of the authentication data block that follows the header at offset 256.
    /// <para>位于偏移 256、紧随头部的认证数据块的字节大小。</para></summary>
    /// <value>The authentication block extent holding the hash and signature regions.
    /// <para>承载哈希与签名区域的认证数据块范围。</para></value>
    public ulong AuthenticationDataBlockSize { get; init; }

    /// <summary>Byte size of the auxiliary data block following the authentication block (public key, descriptors).
    /// <para>认证数据块之后辅助数据块的字节大小（公钥、描述符）。</para></summary>
    /// <value>The auxiliary block extent anchoring the public-key and descriptor offsets.
    /// <para>作为公钥与描述符偏移基准的辅助数据块范围。</para></value>
    public ulong AuxiliaryDataBlockSize { get; init; }

    /// <summary>Raw algorithm identifier at offset 28 selecting the hash/signature algorithm pair; this struct stores the
    /// number verbatim and the verification layer decides which values it supports.
    /// <para>偏移 28 处的原始算法编号，选择哈希/签名算法对；本结构原样保存该数值，支持的取值由校验层决定。</para></summary>
    /// <value>The big-endian 32-bit algorithm number, unchecked here. <para>大端 32 位算法编号；此处不做校验。</para></value>
    public uint AlgorithmType { get; init; }

    /// <summary>Offset of the digest inside the authentication data block. <para>摘要在认证数据块内的偏移。</para></summary>
    /// <value>Byte offset relative to the start of the authentication block. <para>相对认证数据块起始的字节偏移。</para></value>
    public ulong HashOffset { get; init; }

    /// <summary>Size in bytes of the digest at <see cref="HashOffset"/>. <para>位于 <see cref="HashOffset"/> 处摘要的字节大小。</para></summary>
    /// <value>Byte length of the stored hash. <para>所存哈希的字节长度。</para></value>
    public ulong HashSize { get; init; }

    /// <summary>Offset of the signature inside the authentication data block. <para>签名在认证数据块内的偏移。</para></summary>
    /// <value>Byte offset relative to the start of the authentication block. <para>相对认证数据块起始的字节偏移。</para></value>
    public ulong SignatureOffset { get; init; }

    /// <summary>Size in bytes of the signature at <see cref="SignatureOffset"/>. <para>位于 <see cref="SignatureOffset"/> 处签名的字节大小。</para></summary>
    /// <value>Byte length of the stored signature. <para>所存签名的字节长度。</para></value>
    public ulong SignatureSize { get; init; }

    /// <summary>Offset of the encoded public key inside the auxiliary data block. <para>编码后公钥在辅助数据块内的偏移。</para></summary>
    /// <value>Byte offset relative to the start of the auxiliary block. <para>相对辅助数据块起始的字节偏移。</para></value>
    public ulong PublicKeyOffset { get; init; }

    /// <summary>Size in bytes of the public key at <see cref="PublicKeyOffset"/>. <para>位于 <see cref="PublicKeyOffset"/> 处公钥的字节大小。</para></summary>
    /// <value>Byte length of the public-key blob. <para>公钥 Blob 的字节长度。</para></value>
    public ulong PublicKeySize { get; init; }

    /// <summary>Offset of the public-key metadata inside the auxiliary data block. <para>公钥元数据在辅助数据块内的偏移。</para></summary>
    /// <value>Byte offset relative to the start of the auxiliary block. <para>相对辅助数据块起始的字节偏移。</para></value>
    public ulong PublicKeyMetadataOffset { get; init; }

    /// <summary>Size in bytes of the public-key metadata at <see cref="PublicKeyMetadataOffset"/>.
    /// <para>位于 <see cref="PublicKeyMetadataOffset"/> 处公钥元数据的字节大小。</para></summary>
    /// <value>Byte length of the public-key metadata, zero when absent. <para>公钥元数据的字节长度；不存在时为 0。</para></value>
    public ulong PublicKeyMetadataSize { get; init; }

    /// <summary>Offset of the descriptor sequence inside the auxiliary data block. <para>描述符序列在辅助数据块内的偏移。</para></summary>
    /// <value>Byte offset relative to the start of the auxiliary block. <para>相对辅助数据块起始的字节偏移。</para></value>
    public ulong DescriptorsOffset { get; init; }

    /// <summary>Total byte size of the descriptor sequence at <see cref="DescriptorsOffset"/>.
    /// <para>位于 <see cref="DescriptorsOffset"/> 处描述符序列的总字节大小。</para></summary>
    /// <value>Combined byte length of all descriptors. <para>全部描述符合计的字节长度。</para></value>
    public ulong DescriptorsSize { get; init; }

    /// <summary>Anti-rollback counter stored in the header (offset 112). <para>头部保存的防回滚计数器（偏移 112）。</para></summary>
    /// <value>The big-endian 64-bit rollback index. <para>大端 64 位回滚索引。</para></value>
    public ulong RollbackIndex { get; init; }

    /// <summary>Raw 32-bit flag word at offset 120; zero means no AVB flag bits are set. <para>偏移 120 处的 32 位标志字；为 0 表示未设置任何 AVB 标志位。</para></summary>
    /// <value>The big-endian 32-bit flags, unchecked here. <para>大端 32 位标志；此处不做校验。</para></value>
    public uint Flags { get; init; }

    /// <summary>Rollback index slot this header is checked against (offset 124). <para>该校验所使用的回滚索引槽位（偏移 124）。</para></summary>
    /// <value>The big-endian 32-bit slot number. <para>大端 32 位槽位编号。</para></value>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>Free-form ASCII release string from the fixed 48-byte field at offset 128, truncated at the first NUL.
    /// <para>偏移 128 处 48 字节固定字段中的自由格式 ASCII 版本说明，在首个 NUL 处截断。</para></summary>
    /// <value>The release text; empty when the field starts with a NUL. <para>版本说明文本；字段以 NUL 开头时为空串。</para></value>
    public string ReleaseString { get; init; }

    /// <summary>On-disk size of the header in bytes. <para>头部在磁盘上的字节大小。</para></summary>
    public const int Size = 256;

    /// <summary>The ASCII magic a valid VBMeta header must carry. <para>合法 VBMeta 头部必须携带的 ASCII 魔数。</para></summary>
    public const string ExpectedMagic = "AVB0";

    /// <summary>
    /// Parses a AvbVBMetaImageHeader from big-endian bytes.
    /// <para>从大端字节解析 AvbVBMetaImageHeader。</para>
    /// </summary>
    /// <param name="data">
    /// At least <see cref="Size"/> bytes positioned at the start of the header; only the first 256 bytes are read.
    /// <para>位于头部起始处、至少 <see cref="Size"/> 字节的区域；仅读取前 256 字节。</para>
    /// </param>
    /// <returns>The decoded header; magic is not checked here. <para>解码后的头部；此处不校验魔数。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="Size"/> bytes. <para><paramref name="data"/> 短于 <see cref="Size"/> 字节。</para>
    /// </exception>
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
    /// <returns><see langword="true"/> when <see cref="Magic"/> equals <see cref="ExpectedMagic"/> under ordinal comparison.
    /// <para>当 <see cref="Magic"/> 与 <see cref="ExpectedMagic"/> 按序数比较相等时返回 <see langword="true"/>。</para></returns>
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