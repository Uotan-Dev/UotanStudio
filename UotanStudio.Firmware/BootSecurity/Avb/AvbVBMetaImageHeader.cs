using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb;

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
            ReleaseString = NulText.ReadCString(data.Slice(128, 48), Encoding.ASCII)
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

    /// <summary>
    /// Writes the header's fields into a big-endian buffer, mirroring the offsets read by <see cref="FromBytes"/> so the layout has a single source of truth.
    /// <para>按 <see cref="FromBytes"/> 所读取的偏移逆序，将各字段以大端形式写入缓冲区，使布局只有一处真相。</para>
    /// </summary>
    /// <param name="destination">
    /// Destination of at least <see cref="Size"/> bytes; the first <see cref="Size"/> bytes are fully overwritten, including the zero-filled reserved region that follows the release field.
    /// <para>至少 <see cref="Size"/> 字节的目标区域；前 <see cref="Size"/> 字节会被完整覆写，包含释放字段之后置零的保留区。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than <see cref="Size"/> bytes, or <see cref="Magic"/> or
    /// <see cref="ReleaseString"/> does not fit its fixed header capacity (47 bytes plus one NUL for the
    /// release string), mirroring avbtool's rejection instead of silent truncation.
    /// <para><paramref name="destination"/> 短于 <see cref="Size"/> 字节，或 <see cref="Magic"/> /
    /// <see cref="ReleaseString"/> 超出其定长头部容量（release string 为 47 字节加一个 NUL），与 avbtool
    /// 的拒绝行为一致而非静默截断。</para>
    /// </exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"VBMeta image header requires at least {Size} bytes.", nameof(destination));
        }

        var span = destination[..Size];
        span.Clear();
        WriteAsciiFixed(Magic, span[..4]);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], RequiredLibavbVersionMajor);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], RequiredLibavbVersionMinor);
        BinaryPrimitives.WriteUInt64BigEndian(span[12..20], AuthenticationDataBlockSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[20..28], AuxiliaryDataBlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[28..32], AlgorithmType);
        BinaryPrimitives.WriteUInt64BigEndian(span[32..40], HashOffset);
        BinaryPrimitives.WriteUInt64BigEndian(span[40..48], HashSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[48..56], SignatureOffset);
        BinaryPrimitives.WriteUInt64BigEndian(span[56..64], SignatureSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[64..72], PublicKeyOffset);
        BinaryPrimitives.WriteUInt64BigEndian(span[72..80], PublicKeySize);
        BinaryPrimitives.WriteUInt64BigEndian(span[80..88], PublicKeyMetadataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(span[88..96], PublicKeyMetadataSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[96..104], DescriptorsOffset);
        BinaryPrimitives.WriteUInt64BigEndian(span[104..112], DescriptorsSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[112..120], RollbackIndex);
        BinaryPrimitives.WriteUInt32BigEndian(span[120..124], Flags);
        BinaryPrimitives.WriteUInt32BigEndian(span[124..128], RollbackIndexLocation);
        WriteAsciiFixed(ReleaseString, span[128..175]);
    }

    /// <summary>
    /// Serializes the header into a freshly allocated big-endian 256-byte array.
    /// <para>将头部序列化为新分配的大端 256 字节数组。</para>
    /// </summary>
    /// <returns>A newly allocated array of exactly <see cref="Size"/> big-endian bytes; the bytes are the inverse of <see cref="FromBytes"/> for well-formed headers.
    /// <para>新分配的、恰好 <see cref="Size"/> 字节的大端数组；对合法头部而言其字节即 <see cref="FromBytes"/> 的逆过程。</para></returns>
    public byte[] ToBytes()
    {
        var data = new byte[Size];
        WriteTo(data);
        return data;
    }

    private static void WriteAsciiFixed(string? text, Span<byte> destination)
    {
        var bytes = Encoding.ASCII.GetBytes(text ?? string.Empty);
        if (bytes.Length > destination.Length)
        {
            throw new ArgumentException($"Field value needs {bytes.Length} bytes and exceeds the fixed {destination.Length}-byte header capacity; avbtool rejects such values instead of truncating.", nameof(text));
        }

        bytes.CopyTo(destination);
    }
}