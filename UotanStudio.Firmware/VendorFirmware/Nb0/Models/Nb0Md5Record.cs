namespace UotanStudio.Firmware.VendorFirmware.Nb0.Models;

/// <summary>
/// One fixed-size MD5 verification record stored in the trailing <c>.md5</c> entry of an NB0 archive.
/// <para>NB0 归档末尾 <c>.md5</c> 条目中存储的一条定长 MD5 校验记录。</para>
/// </summary>
public sealed class Nb0Md5Record
{
    /// <summary>
    /// Serialized record size in bytes: 4-byte offset, 4-byte length and a 16-byte digest. Parsing divides the
    /// <c>.md5</c> entry size by this value to count records, so a trailing partial record is not read.
    /// <para>序列化记录的大小（字节）：4 字节偏移、4 字节长度以及 16 字节摘要。解析时用 <c>.md5</c> 条目大小除以该值来统计记录数，因此末尾不完整的记录不会被读取。</para>
    /// </summary>
    public const int StructSize = 24;

    private const int Md5Size = 16;

    /// <summary>
    /// Absolute position in the file where the digested data starts, matching the entry's data-section offset plus
    /// its in-section offset. Offset-based record lookup only covers positions up to <c>uint.MaxValue</c>.
    /// <para>被摘要数据在文件内的绝对起始位置，等于条目的数据段起始偏移加上段内偏移。按偏移查找记录只覆盖 &lt;= <c>uint.MaxValue</c> 的位置。</para>
    /// </summary>
    /// <value>Absolute start position of the digested data in the file. <para>被摘要数据在文件内的绝对起始位置。</para></value>
    public uint Offset { get; set; }

    /// <summary>
    /// Byte length the record declares it covers. Extraction digests each entry in full and compares only the
    /// digest, so this value is carried as metadata and is never range-checked against the archive.
    /// <para>记录声明其覆盖的字节长度。提取时会完整摘要每个条目并只比对摘要，因此该值仅作为元数据保留，不会对归档做范围校验。</para>
    /// </summary>
    /// <value>Byte length declared by the record. <para>记录声明的字节长度。</para></value>
    public uint Length { get; set; }

    /// <summary>
    /// Raw MD5 digest, 16 bytes for a well-formed record. A record read from a truncated archive can hold a shorter
    /// array, and digests are compared byte for byte against the computed hash.
    /// <para>原始 MD5 摘要，格式正确的记录为 16 字节。从截断归档中读出的记录可能持有更短的数组；摘要与计算结果按字节逐一比对。</para>
    /// </summary>
    /// <value>The digest bytes as stored or read; 16 for a complete record. <para>按存储或读取到的摘要字节；完整记录为 16 字节。</para></value>
    public byte[] Md5Checksum { get; set; } = new byte[Md5Size];

    /// <summary>
    /// Lowercase hexadecimal form of <see cref="Md5Checksum"/>, two characters per byte, so a full digest renders as
    /// 32 characters.
    /// <para><see cref="Md5Checksum"/> 的小写十六进制形式，每字节两个字符，完整摘要渲染为 32 个字符。</para>
    /// </summary>
    /// <value>Lowercase hex text of the digest. <para>摘要的小写十六进制文本。</para></value>
    public string Md5Hex => Convert.ToHexStringLower(Md5Checksum);

    /// <summary>
    /// Reads and constructs an Nb0Md5Record instance from a binary reader.
    /// <para>从二进制读取器中读取并构造一个 Nb0Md5Record 实例。</para>
    /// </summary>
    /// <param name="reader">
    /// Reader positioned at the first byte of the record; the 4-byte offset and 4-byte length are decoded as
    /// little-endian unsigned integers and the digest is taken as 16 raw bytes. The reader stays open and its position
    /// advances by <see cref="StructSize"/> bytes.
    /// <para>定位在记录首字节的读取器；4 字节偏移与 4 字节长度按小端无符号整数解码，摘要按 16 个原始字节读取。读取器保持打开，其位置前进 <see cref="StructSize"/> 字节。</para>
    /// </param>
    /// <returns>The record just read. <para>刚读取到的记录。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null. <para><paramref name="reader"/> 为 null。</para></exception>
    /// <exception cref="EndOfStreamException">
    /// Fewer than 8 bytes remain, so the offset or length cannot be decoded. A truncated tail shorter than 16
    /// remaining bytes yields a record with a correspondingly short <see cref="Md5Checksum"/> instead of throwing.
    /// <para>剩余不足 8 字节，无法解码偏移或长度。若剩余可读取的摘要不足 16 字节且未提前抛出，则得到 <see cref="Md5Checksum"/> 相应偏短的记录而不会抛异常。</para>
    /// </exception>
    public static Nb0Md5Record Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var record = new Nb0Md5Record
        {
            Offset = reader.ReadUInt32(),
            Length = reader.ReadUInt32(),
            Md5Checksum = reader.ReadBytes(Md5Size)
        };
        return record;
    }

    /// <summary>
    /// Writes the current MD5 record to the specified binary writer.
    /// <para>将当前 MD5 校验记录写入指定的二进制写入器。</para>
    /// </summary>
    /// <param name="writer">
    /// Writer receiving exactly <see cref="StructSize"/> bytes: <see cref="Offset"/> and <see cref="Length"/> as
    /// little-endian unsigned integers followed by the first 16 bytes of <see cref="Md5Checksum"/>; trailing bytes of
    /// a longer array are dropped. The writer stays open.
    /// <para>接收恰好 <see cref="StructSize"/> 字节的写入器：<see cref="Offset"/> 与 <see cref="Length"/> 按小端无符号整数写出，随后写入 <see cref="Md5Checksum"/> 的前 16 字节；更长的数组其尾部字节会被丢弃。写入器保持打开。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null. <para><paramref name="writer"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">
    /// <see cref="Md5Checksum"/> holds fewer than 16 bytes, so the fixed-size digest cannot be written.
    /// <para><see cref="Md5Checksum"/> 少于 16 字节，无法写出定长摘要。</para>
    /// </exception>
    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(Offset);
        writer.Write(Length);
        writer.Write(Md5Checksum, 0, Md5Size);
    }
}