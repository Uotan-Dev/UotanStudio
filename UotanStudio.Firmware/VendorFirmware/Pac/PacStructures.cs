using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UotanStudio.Firmware.VendorFirmware.Pac;

/// <summary>
/// Fixed-layout SPRD/Unisoc PAC package header: version, firmware identification, directory position and checksum words.
/// <para>固定布局的 SPRD/展锐 PAC 包头部：版本、固件标识、目录位置与校验字段。</para>
/// </summary>
internal sealed class SprdHead
{
    /// <summary>
    /// UTF-16 code units of the PAC format version string, padded with trailing NUL entries.
    /// <para>PAC 格式版本字符串的 UTF-16 码元，尾部以 NUL 条目补齐。</para>
    /// </summary>
    public ushort[] PacVersion = new ushort[24];

    /// <summary>
    /// Declared total package size in bytes; the CRC verification pass reads that many bytes starting immediately after the header.
    /// <para>声明的包总大小（字节）；CRC 校验会从紧随头部之后的位置读取这么多个字节。</para>
    /// </summary>
    public uint PacSize;

    /// <summary>
    /// UTF-16 code units of the firmware name, padded with trailing NUL entries.
    /// <para>固件名称的 UTF-16 码元，尾部以 NUL 条目补齐。</para>
    /// </summary>
    public ushort[] FwName = new ushort[256];

    /// <summary>
    /// UTF-16 code units of the firmware version, padded with trailing NUL entries.
    /// <para>固件版本的 UTF-16 码元，尾部以 NUL 条目补齐。</para>
    /// </summary>
    public ushort[] FwVersion = new ushort[256];

    /// <summary>
    /// Number of directory entries stored after the header; callers bound this count before reading the table.
    /// <para>头部之后存放的目录条目数量；调用方会先对该计数做上限校验再读取目录表。</para>
    /// </summary>
    public uint FileCount;

    /// <summary>
    /// Absolute byte offset of the directory table; images accepted by extraction place it immediately after the header.
    /// <para>目录表的绝对字节偏移；提取流程接受的镜像把它放在紧随头部之后。</para>
    /// </summary>
    public uint DirOffset;

    /// <summary>
    /// Five reserved header words with no assigned meaning here; read and rewritten verbatim.
    /// <para>五个在此未定义含义的保留头部字；读取后原样写回。</para>
    /// </summary>
    public uint[] Unknown1 = new uint[5];

    /// <summary>
    /// UTF-16 code units of the firmware alias, padded with trailing NUL entries.
    /// <para>固件别名的 UTF-16 码元，尾部以 NUL 条目补齐。</para>
    /// </summary>
    public ushort[] FwAlias = new ushort[100];

    /// <summary>
    /// Three reserved header words with no assigned meaning here; read and rewritten verbatim.
    /// <para>三个在此未定义含义的保留头部字；读取后原样写回。</para>
    /// </summary>
    public uint[] Unknown2 = new uint[3];

    /// <summary>
    /// Two hundred reserved header words with no assigned meaning here; read and rewritten verbatim.
    /// <para>两百个在此未定义含义的保留头部字；读取后原样写回。</para>
    /// </summary>
    public uint[] Unknown = new uint[200];

    /// <summary>
    /// Package signature word stored ahead of the two checksum words; extraction accepts only the constant it compares against.
    /// <para>存放在两个校验字之前的包签名字；提取流程只接受与其比较常量一致的取值。</para>
    /// </summary>
    public uint PacMagic;

    /// <summary>
    /// Stored CRC-16 of the header bytes up to but excluding this word and <c>DataCrc</c>, that is the first 2120 bytes including the magic.
    /// <para>头部中直到但不包含本字与 <c>DataCrc</c> 的字节（含魔数在内共前 2120 字节）的 CRC-16 存储值。</para>
    /// </summary>
    public ushort HeadCrc;

    /// <summary>
    /// Stored CRC-16 of the package data after the header; verification compares it with the checksum of the bytes it manages to read.
    /// <para>头部之后包数据的 CRC-16 存储值；校验会把它与本次实际读到字节算出的校验和相比较。</para>
    /// </summary>
    public ushort DataCrc;

    /// <summary>
    /// Fixed serialized length of the header in bytes, and therefore the directory offset a valid package must report.
    /// <para>头部序列化后的固定字节长度，也是有效包必须报告的目录偏移。</para>
    /// </summary>
    public const int Size = 2124;

    /// <summary>Reads exactly <see cref="Size"/> bytes from the current position as little-endian words in the documented field order.
    /// <para>从当前位置按文档字段顺序以小端字读取恰好 <see cref="Size"/> 字节。</para></summary>
    /// <param name="stream">Stream positioned at the first header byte; it is advanced past the header and left open. The whole header is read with one exact read and decoded in memory.
    /// <para>定位在头部第一个字节的流；调用后其位置越过头部，流本身保持打开。整个头部以一次精确读取完成，随后在内存中解码。</para></param>
    /// <returns>A header whose array fields have the fixed lengths declared by this type.
    /// <para>头部对象，其数组字段长度与本类型声明的固定长度一致。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than <see cref="Size"/> bytes remain at the current position.
    /// <para>当前位置剩余字节少于 <see cref="Size"/>。</para></exception>
    public static SprdHead Read(Stream stream)
    {
        Span<byte> raw = stackalloc byte[Size];
        stream.ReadExactly(raw);
        var head = new SprdHead();
        PacFieldCodec.ReadUInt16Array(raw, 0, head.PacVersion);
        head.PacSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[48..]);
        PacFieldCodec.ReadUInt16Array(raw, 52, head.FwName);
        PacFieldCodec.ReadUInt16Array(raw, 564, head.FwVersion);
        head.FileCount = BinaryPrimitives.ReadUInt32LittleEndian(raw[1076..]);
        head.DirOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[1080..]);
        PacFieldCodec.ReadUInt32Array(raw, 1084, head.Unknown1);
        PacFieldCodec.ReadUInt16Array(raw, 1104, head.FwAlias);
        PacFieldCodec.ReadUInt32Array(raw, 1304, head.Unknown2);
        PacFieldCodec.ReadUInt32Array(raw, 1316, head.Unknown);
        head.PacMagic = BinaryPrimitives.ReadUInt32LittleEndian(raw[2116..]);
        head.HeadCrc = BinaryPrimitives.ReadUInt16LittleEndian(raw[2120..]);
        head.DataCrc = BinaryPrimitives.ReadUInt16LittleEndian(raw[2122..]);
        return head;
    }

    /// <summary>
    /// Serializes the header to a byte array.
    /// <para>将头部序列化为字节数组。</para>
    /// </summary>
    /// <returns>
    /// Little-endian bytes in the documented field order; each array field contributes its declared element count, so extra elements of an over-long array are not written.
    /// <para>按文档字段顺序排列的小端字节；每个数组字段只写入其声明的元素个数，因此过长数组的多余元素不会被写入。</para>
    /// </returns>
    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        PacFieldCodec.WriteUInt16Array(bytes, 0, PacVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), PacSize);
        PacFieldCodec.WriteUInt16Array(bytes, 52, FwName);
        PacFieldCodec.WriteUInt16Array(bytes, 564, FwVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1076), FileCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1080), DirOffset);
        PacFieldCodec.WriteUInt32Array(bytes, 1084, Unknown1);
        PacFieldCodec.WriteUInt16Array(bytes, 1104, FwAlias);
        PacFieldCodec.WriteUInt32Array(bytes, 1304, Unknown2);
        PacFieldCodec.WriteUInt32Array(bytes, 1316, Unknown);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2116), PacMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2120), HeadCrc);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2122), DataCrc);
        return bytes;
    }

    /// <summary>
    /// Gets the PAC version string.
    /// <para>获取 PAC 版本字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="PacVersion"/> with trailing NUL characters removed; empty when every unit is NUL.
    /// <para>对 <see cref="PacVersion"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；全部码元为 NUL 时返回空字符串。</para>
    /// </returns>
    public string GetPacVersion() => ConvertU16ToString(PacVersion);

    /// <summary>
    /// Gets the firmware name string.
    /// <para>获取固件名称字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="FwName"/> with trailing NUL characters removed; empty when every unit is NUL.
    /// <para>对 <see cref="FwName"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；全部码元为 NUL 时返回空字符串。</para>
    /// </returns>
    public string GetFwName() => ConvertU16ToString(FwName);

    /// <summary>
    /// Gets the firmware version string.
    /// <para>获取固件版本字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="FwVersion"/> with trailing NUL characters removed; empty when every unit is NUL.
    /// <para>对 <see cref="FwVersion"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；全部码元为 NUL 时返回空字符串。</para>
    /// </returns>
    public string GetFwVersion() => ConvertU16ToString(FwVersion);

    /// <summary>
    /// Gets the firmware alias string.
    /// <para>获取固件别名字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="FwAlias"/> with trailing NUL characters removed; empty when every unit is NUL.
    /// <para>对 <see cref="FwAlias"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；全部码元为 NUL 时返回空字符串。</para>
    /// </returns>
    public string GetFwAlias() => ConvertU16ToString(FwAlias);

    private static string ConvertU16ToString(ushort[] data)
    {
        return new string(MemoryMarshal.Cast<ushort, char>(data)).TrimEnd('\0');
    }
}

/// <summary>
/// Fixed-layout PAC directory entry describing one packaged file: identifiers, payload range, category and target addresses.
/// <para>固定布局的 PAC 目录条目，描述一个被打包文件：标识、数据范围、类别与目标地址。</para>
/// </summary>
internal sealed class SprdFile
{
    /// <summary>
    /// Byte length the writer recorded for this entry; extraction accepts only <see cref="StructFixedSize"/>.
    /// <para>写入方为该条目记录的字节长度；提取流程只接受等于 <see cref="StructFixedSize"/> 的值。</para>
    /// </summary>
    public uint StructSize;

    /// <summary>
    /// UTF-16 code units of the entry identifier, padded with trailing NUL entries.
    /// <para>条目标识符的 UTF-16 码元，尾部以 NUL 条目补齐。</para>
    /// </summary>
    public ushort[] Id = new ushort[256];

    /// <summary>
    /// UTF-16 code units of the entry file name; extraction skips an entry whose first unit is NUL.
    /// <para>条目文件名的 UTF-16 码元；提取流程会跳过首个码元为 NUL 的条目。</para>
    /// </summary>
    public ushort[] Name = new ushort[256];

    /// <summary>
    /// Two hundred fifty-six UTF-16 units with no assigned meaning here, read and rewritten verbatim.
    /// <para>二百五十六个在此未定义含义的 UTF-16 码元，读取后原样写回。</para>
    /// </summary>
    public ushort[] Unknown1 = new ushort[256];

    /// <summary>
    /// Declared payload length in bytes at <see cref="PacOffset"/>.
    /// <para>在 <see cref="PacOffset"/> 处声明的数据长度（字节）。</para>
    /// </summary>
    public uint Size;

    /// <summary>
    /// Raw entry category word; <see cref="PacFileType"/> names the codes this layout uses for images, XML and certificates.
    /// <para>原始条目类别字；<see cref="PacFileType"/> 给出该布局对镜像、XML 与证书使用的编码。</para>
    /// </summary>
    public uint Type;

    /// <summary>
    /// Flash-usage flag word stored verbatim; no bit layout is interpreted here.
    /// <para>原样存放的刷写用途标志字；此处不解释具体位含义。</para>
    /// </summary>
    public uint FlashUse;

    /// <summary>
    /// Absolute byte offset of the entry payload within the package.
    /// <para>条目数据在包内的绝对字节偏移。</para>
    /// </summary>
    public uint PacOffset;

    /// <summary>
    /// Omission marker word stored verbatim; no value is interpreted here.
    /// <para>原样存放的省略标记字；此处不解释其取值含义。</para>
    /// </summary>
    public uint OmitFlag;

    /// <summary>
    /// Number of meaningful entries in <see cref="Addr"/>; the array itself always holds five words.
    /// <para><see cref="Addr"/> 中有效地址的个数；数组本身始终存放五个字。</para>
    /// </summary>
    public uint AddrNum;

    /// <summary>
    /// Up to five target storage addresses recorded for the entry, kept in table order.
    /// <para>为该条目记录的最多五个目标存储地址，按表顺序保存。</para>
    /// </summary>
    public uint[] Addr = new uint[5];

    /// <summary>
    /// Two hundred forty-nine reserved entry words with no assigned meaning here, read and rewritten verbatim.
    /// <para>二百四十九个在此未定义含义的保留条目字，读取后原样写回。</para>
    /// </summary>
    public uint[] Unknown2 = new uint[249];

    /// <summary>
    /// Fixed serialized length of an entry in bytes, and the stride used to locate consecutive entries in the directory table.
    /// <para>条目的固定序列化字节长度，也是在目录表中定位相邻条目的步长。</para>
    /// </summary>
    public const int StructFixedSize = 2580;

    /// <summary>Reads exactly <see cref="StructFixedSize"/> bytes from the current position as little-endian words in the documented field order.
    /// <para>从当前位置按文档字段顺序以小端字读取恰好 <see cref="StructFixedSize"/> 字节。</para></summary>
    /// <param name="stream">Stream positioned at the first byte of the entry; it is advanced past the entry and left open. The whole entry is read with one exact read and decoded in memory.
    /// <para>定位在条目第一个字节的流；调用后其位置越过条目，流本身保持打开。整个条目以一次精确读取完成，随后在内存中解码。</para></param>
    /// <returns>An entry whose array fields have the fixed lengths declared by this type. Field values are not validated here, so a caller must bound <see cref="Size"/> and <see cref="PacOffset"/> before reading payload bytes.
    /// <para>条目对象，其数组字段长度与本类型声明的固定长度一致。此处不校验字段取值，调用方必须先对 <see cref="Size"/> 与 <see cref="PacOffset"/> 做上限校验，然后才能读取数据字节。</para></returns>
    /// <exception cref="EndOfStreamException">Fewer than <see cref="StructFixedSize"/> bytes remain at the current position.
    /// <para>当前位置剩余字节少于 <see cref="StructFixedSize"/>。</para></exception>
    public static SprdFile Read(Stream stream)
    {
        Span<byte> raw = stackalloc byte[StructFixedSize];
        stream.ReadExactly(raw);
        var file = new SprdFile
        {
            StructSize = BinaryPrimitives.ReadUInt32LittleEndian(raw)
        };
        PacFieldCodec.ReadUInt16Array(raw, 4, file.Id);
        PacFieldCodec.ReadUInt16Array(raw, 516, file.Name);
        PacFieldCodec.ReadUInt16Array(raw, 1028, file.Unknown1);
        file.Size = BinaryPrimitives.ReadUInt32LittleEndian(raw[1540..]);
        file.Type = BinaryPrimitives.ReadUInt32LittleEndian(raw[1544..]);
        file.FlashUse = BinaryPrimitives.ReadUInt32LittleEndian(raw[1548..]);
        file.PacOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[1552..]);
        file.OmitFlag = BinaryPrimitives.ReadUInt32LittleEndian(raw[1556..]);
        file.AddrNum = BinaryPrimitives.ReadUInt32LittleEndian(raw[1560..]);
        PacFieldCodec.ReadUInt32Array(raw, 1564, file.Addr);
        PacFieldCodec.ReadUInt32Array(raw, 1584, file.Unknown2);
        return file;
    }

    /// <summary>
    /// Serializes this directory entry using the fixed PAC layout.
    /// <para>按 PAC 固定布局序列化此目录条目。</para>
    /// </summary>
    /// <returns>The 2,580-byte little-endian entry representation. <para>2,580 字节的小端条目表示。</para></returns>
    public byte[] ToBytes()
    {
        var bytes = new byte[StructFixedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, StructSize);
        PacFieldCodec.WriteUInt16Array(bytes, 4, Id);
        PacFieldCodec.WriteUInt16Array(bytes, 516, Name);
        PacFieldCodec.WriteUInt16Array(bytes, 1028, Unknown1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1540), Size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1544), Type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1548), FlashUse);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1552), PacOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1556), OmitFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1560), AddrNum);
        PacFieldCodec.WriteUInt32Array(bytes, 1564, Addr);
        PacFieldCodec.WriteUInt32Array(bytes, 1584, Unknown2);
        return bytes;
    }

    /// <summary>
    /// Gets the entry ID string.
    /// <para>获取条目 ID 字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="Id"/> with trailing NUL characters removed; embedded NUL units stay in the string.
    /// <para>对 <see cref="Id"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；位于中间的 NUL 码元会保留在字符串中。</para>
    /// </returns>
    public string GetId() => ConvertU16ToString(Id);

    /// <summary>
    /// Gets the entry name string.
    /// <para>获取条目名称字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-16 decoding of <see cref="Name"/> with trailing NUL characters removed; empty when every unit is NUL.
    /// <para>对 <see cref="Name"/> 做 UTF-16 解码并去掉结尾的 NUL 字符；全部码元为 NUL 时返回空字符串。</para>
    /// </returns>
    public string GetName() => ConvertU16ToString(Name);

    private static string ConvertU16ToString(ushort[] data)
    {
        return new string(MemoryMarshal.Cast<ushort, char>(data)).TrimEnd('\0');
    }
}

/// <summary>
/// Entry category codes carried in <see cref="SprdFile.Type"/>.
/// <para>存放于 <see cref="SprdFile.Type"/> 的条目类别编码。</para>
/// </summary>
public enum PacFileType : uint
{
    /// <summary>No category recorded (code <c>0</c>). <para>未记录类别（编码 <c>0</c>）。</para></summary>
    Unknown = 0,

    /// <summary>Flashable image payload (code <c>1</c>). <para>可刷写镜像数据（编码 <c>1</c>）。</para></summary>
    Image = 1,

    /// <summary>XML configuration payload (code <c>2</c>). <para>XML 配置数据（编码 <c>2</c>）。</para></summary>
    Xml = 2,

    /// <summary>Signing certificate payload (code <c>3</c>). <para>签名证书数据（编码 <c>3</c>）。</para></summary>
    Certificate = 3
}
/// <summary>
/// Copies little-endian PAC word arrays between raw structure bytes and the strongly typed fields.
/// <para>在原始结构字节与强类型字段之间复制小端 PAC 字数组。</para>
/// </summary>
internal static class PacFieldCodec
{
    /// <summary>
    /// Decodes a run of little-endian 16-bit words into a destination array.
    /// <para>把一段小端 16 位字解码到目标数组。</para>
    /// </summary>
    internal static void ReadUInt16Array(ReadOnlySpan<byte> source, int offset, ushort[] destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + i * 2)..]);
        }
    }

    /// <summary>
    /// Decodes a run of little-endian 32-bit words into a destination array.
    /// <para>把一段小端 32 位字解码到目标数组。</para>
    /// </summary>
    internal static void ReadUInt32Array(ReadOnlySpan<byte> source, int offset, uint[] destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + i * 4)..]);
        }
    }

    /// <summary>
    /// Encodes a 16-bit word array as little-endian bytes at the given offset.
    /// <para>把 16 位字数组按小端编码到给定偏移处。</para>
    /// </summary>
    internal static void WriteUInt16Array(Span<byte> destination, int offset, ReadOnlySpan<ushort> source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + i * 2)..], source[i]);
        }
    }

    /// <summary>
    /// Encodes a 32-bit word array as little-endian bytes at the given offset.
    /// <para>把 32 位字数组按小端编码到给定偏移处。</para>
    /// </summary>
    internal static void WriteUInt32Array(Span<byte> destination, int offset, ReadOnlySpan<uint> source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(offset + i * 4)..], source[i]);
        }
    }
}