using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.VendorFirmware.OnePlusOps;

/// <summary>
/// The fixed-size OPS package header. The magic occupies the first four ASCII bytes and the
/// remaining fields are little-endian <see cref="uint"/> values at offsets 4, 8, 12, 16 and 20;
/// bytes between offset 24 and the end of the declared header carry no field this type reads.
/// <para>固定大小的 OPS 包头部。魔数为前 4 个 ASCII 字节，其余字段是位于偏移 4、8、12、16、20
/// 的小端序 <see cref="uint"/> 值；偏移 24 到声明头部末尾之间的字节不承载本类型读取的字段。</para>
/// </summary>
public class OpsHeader
{
    /// <summary>Four ASCII signature bytes taken from offset 0.
    /// <para>取自偏移 0 的 4 个 ASCII 签名字节。</para></summary>
    /// <value>The magic decoded as ASCII; <c>"OPS\0"</c> identifies an OPS package. Parsing from
    /// <see cref="FromBytes"/> always fills four characters, and the property initializer yields
    /// <see cref="string.Empty"/> before any parse.
    /// <para>按 ASCII 解码后的魔数；<c>"OPS\0"</c> 表示 OPS 包。经由 <see cref="FromBytes"/>
    /// 解析时始终填入 4 个字符，属性初始化器在未解析时给出 <see cref="string.Empty"/>。</para></value>
    public string Magic { get; set; } = string.Empty;

    /// <summary>Format version declared at offset 4.
    /// <para>在偏移 4 处声明的格式版本。</para></summary>
    /// <value>Little-endian version number. Header acceptance only compares <see cref="Magic"/>,
    /// so the value is informational; packages written by this library declare <c>1</c>.
    /// <para>小端序版本号。头部是否被接受只比较 <see cref="Magic"/>，因此该值仅供参考；
    /// 本库写出的包声明为 <c>1</c>。</para></value>
    public uint Version { get; set; }

    /// <summary>Header length declared at offset 8.
    /// <para>在偏移 8 处声明的头部长度。</para></summary>
    /// <value>Little-endian byte count in front of the entry table; packages written by this library
    /// use <see cref="OpsConstants.HEADER_SIZE"/>. Nothing bounds a read with this declared value:
    /// <see cref="FromBytes"/> demands <see cref="OpsConstants.HEADER_SIZE"/> bytes of buffer and
    /// consumes only the first 24.
    /// <para>条目表之前的小端序字节数；本库写出的包使用 <see cref="OpsConstants.HEADER_SIZE"/>。
    /// 没有任何读取以该声明值作为边界：<see cref="FromBytes"/> 要求缓冲区至少有
    /// <see cref="OpsConstants.HEADER_SIZE"/> 字节，并只消费前 24 字节。</para></value>
    public uint HeaderSize { get; set; }

    /// <summary>Total package size declared at offset 12.
    /// <para>在偏移 12 处声明的包总大小。</para></summary>
    /// <value>Little-endian size in bytes as written by the producing tool. Packages created by this
    /// library declare <c>0</c>, so callers must use the stream length rather than this field to
    /// bound reads.
    /// <para>由生成工具写入的小端序字节数。本库创建的包声明为 <c>0</c>，因此调用方必须以流长度
    /// 而不是该字段来约束读取范围。</para></value>
    public uint FileSize { get; set; }

    /// <summary>Number of entry headers declared at offset 16.
    /// <para>在偏移 16 处声明的条目头数量。</para></summary>
    /// <value>Little-endian count of <see cref="OpsEntry"/> records that follow the header.
    /// Callers bound this value before parsing; a count of <c>0</c> means an entry-table-free package.
    /// <para>头部之后跟随的 <see cref="OpsEntry"/> 记录数量（小端序）。调用方会在解析前对该数量
    /// 设上限；<c>0</c> 表示该包没有条目表内容。</para></value>
    public uint EntryCount { get; set; }

    /// <summary>Raw flag word declared at offset 20.
    /// <para>在偏移 20 处声明的原始标志字。</para></summary>
    /// <value>Little-endian bit field preserved verbatim from the input. No bit is interpreted when
    /// reading or writing a package, and packages created by this library store <c>0</c>.
    /// <para>原样保留自输入的小端序位字段。读包或写包时都不解释其中任何位，本库创建的包写入
    /// <c>0</c>。</para></value>
    public uint Flags { get; set; }

    /// <summary>Whether the magic matches the OPS signature.
    /// <para>魔数是否与 OPS 签名一致。</para></summary>
    /// <value><see langword="true"/> when <see cref="Magic"/> equals
    /// <see cref="OpsConstants.OPS_MAGIC"/>; every other field is ignored by this check.
    /// <para>当 <see cref="Magic"/> 等于 <see cref="OpsConstants.OPS_MAGIC"/> 时为
    /// <see langword="true"/>；该检查忽略其它所有字段。</para></value>
    public bool IsValid => Magic == OpsConstants.OPS_MAGIC;

    /// <summary>
    /// Parses an OpsHeader from a read-only byte span
    /// <para>从只读字节跨度解析OpsHeader</para>
    /// </summary>
    /// <param name="data">
    /// Buffer holding the whole declared header, at least <see cref="OpsConstants.HEADER_SIZE"/>
    /// bytes; the first four bytes are decoded as ASCII and offsets 4 through 23 are read as
    /// little-endian unsigned 32-bit values. Bytes beyond offset 23 are ignored, and only the first
    /// 24 bytes are consumed even though a longer buffer is required.
    /// <para>承载完整声明头部的缓冲区，长度至少为 <see cref="OpsConstants.HEADER_SIZE"/> 字节；
    /// 前 4 字节按 ASCII 解码，偏移 4 至 23 按小端序无符号 32 位值读取。偏移 23 之后的字节被忽略，
    /// 即使要求更长的缓冲区，实际也只消费前 24 字节。</para></param>
    /// <returns>A header populated from the span; <see cref="IsValid"/> is not consulted, so an
    /// unrelated magic still produces an object.
    /// <para>由该跨度填充的头部；不会调用 <see cref="IsValid"/>，因此非 OPS 魔数同样会返回对象。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="OpsConstants.HEADER_SIZE"/> bytes.
    /// <para><paramref name="data"/> 短于 <see cref="OpsConstants.HEADER_SIZE"/> 字节。</para></exception>
    public static OpsHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS header", nameof(data))
            : new OpsHeader
            {
                Magic = Encoding.ASCII.GetString(data[..4].ToArray()),
                Version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4)),
                EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4))
            };
    }
}

/// <summary>
/// One OPS entry-table record plus the payload bytes associated with it. The on-disk record is
/// <see cref="OpsConstants.ENTRY_HEADER_SIZE"/> bytes: little-endian identifiers at offsets 0, 4
/// and 8 followed by a fixed-width ASCII name.
/// <para>一条 OPS 条目表记录及其关联的载荷字节。磁盘上的记录长
/// <see cref="OpsConstants.ENTRY_HEADER_SIZE"/> 字节：偏移 0、4、8 处是小端序标识值，
/// 其后是定宽 ASCII 名称。</para>
/// </summary>
public class OpsEntry
{
    /// <summary>Entry identifier stored at offset 0.
    /// <para>存储在偏移 0 的条目标识。</para></summary>
    /// <value>Little-endian ordinal of the entry. Repacking assigns the position index, so the value
    /// identifies an entry within one package only.
    /// <para>条目的小端序序号。重新打包时按位置索引赋值，因此该值只在单个包内标识条目。</para></value>
    public uint Id { get; set; }

    /// <summary>Payload start declared at offset 4.
    /// <para>在偏移 4 处声明的载荷起始位置。</para></summary>
    /// <value>Absolute little-endian byte offset from the beginning of the package to the first
    /// encrypted payload byte; payloads start on <see cref="OpsConstants.SECTOR_SIZE"/> boundaries.
    /// A repacked table is written with <c>0</c> here and patched once the real offsets are known.
    /// <para>小端序绝对字节偏移，指向包内首个加密载荷字节；载荷从
    /// <see cref="OpsConstants.SECTOR_SIZE"/> 对齐边界开始。重新写入的条目表此处为 <c>0</c>，
    /// 待真实偏移确定后再回填。</para></value>
    public uint Offset { get; set; }

    /// <summary>Payload length declared at offset 8.
    /// <para>在偏移 8 处声明的载荷长度。</para></summary>
    /// <value>Exact little-endian byte count of the stored payload, encrypted length included.
    /// <c>0</c> means the entry carries no data bytes and reading it returns an empty array without
    /// moving the stream position.
    /// <para>存储载荷精确的小端序字节数，包含加密后的长度。<c>0</c> 表示该条目不含数据字节，
    /// 读取时直接返回空数组且不移动流位置。</para></value>
    public uint Size { get; set; }

    /// <summary>Entry name held in the fixed field that starts at offset 12.
    /// <para>存放在以偏移 12 起始的定宽字段中的条目名称。</para></summary>
    /// <value>ASCII text of at most <see cref="OpsConstants.ENTRY_NAME_SIZE"/> bytes with trailing
    /// NUL characters removed; an all-NUL field produces <see cref="string.Empty"/>. Names are not
    /// required to be unique, and writing truncates anything longer than the field width.
    /// <para>最多 <see cref="OpsConstants.ENTRY_NAME_SIZE"/> 字节的 ASCII 文本，尾部 NUL 字符会被
    /// 去除；全为 NUL 的字段得到 <see cref="string.Empty"/>。名称不要求唯一，写入时超出字段宽度的
    /// 部分会被截断。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Payload bytes held in memory.
    /// <para>保存在内存中的载荷字节。</para></summary>
    /// <value>The raw or decrypted entry content. <see cref="FromBytes"/> leaves it empty because it
    /// reads only the fixed record, and an empty array means the bytes have not been loaded, so the
    /// caller must read <see cref="Size"/> bytes at <see cref="Offset"/>. When data is packed from
    /// files this property supplies the bytes to write, otherwise the payload is copied from the
    /// source stream.
    /// <para>原始或已解密的条目内容。<see cref="FromBytes"/> 只读取定长记录，因此该值保持为空；
    /// 空数组表示字节尚未加载，调用方需在 <see cref="Offset"/> 处读取 <see cref="Size"/> 字节。
    /// 由文件打包时本属性提供待写入的字节，否则载荷会从源流复制。</para></value>
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// Parses an OpsEntry from a read-only byte span
    /// <para>从只读字节跨度解析OpsEntry</para>
    /// </summary>
    /// <param name="data">
    /// Buffer holding one complete entry record, at least <see cref="OpsConstants.ENTRY_HEADER_SIZE"/>
    /// bytes; offsets 0, 4 and 8 are read as little-endian unsigned 32-bit values and offsets 12
    /// through 31 as the ASCII name.
    /// <para>承载一条完整条目记录的缓冲区，长度至少为
    /// <see cref="OpsConstants.ENTRY_HEADER_SIZE"/> 字节；偏移 0、4、8 按小端序无符号 32 位值读取，
    /// 偏移 12 至 31 按 ASCII 名称读取。</para></param>
    /// <returns>The parsed record with an empty <see cref="Data"/>; the payload itself is not touched
    /// and no offset or size bound is checked here.
    /// <para>解析得到的记录，其 <see cref="Data"/> 为空；本方法不读取载荷本身，也不校验偏移或长度上限。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is shorter than <see cref="OpsConstants.ENTRY_HEADER_SIZE"/> bytes.
    /// <para><paramref name="data"/> 短于 <see cref="OpsConstants.ENTRY_HEADER_SIZE"/> 字节。</para></exception>
    public static OpsEntry FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < OpsConstants.ENTRY_HEADER_SIZE
            ? throw new ArgumentException("Insufficient data to parse OPS entry", nameof(data))
            : new OpsEntry
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
                Name = Encoding.ASCII.GetStringTrimNul(data.Slice(12, OpsConstants.ENTRY_NAME_SIZE))
            };
    }
}