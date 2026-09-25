namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;

/// <summary>
/// Fixed 0x70-byte DEX header holding the magic, the verification fields and the count/offset pair of every index section.
/// All values are unsigned 32-bit little-endian integers read from the first 112 bytes of a <c>.dex</c> file.
/// <para>固定 0x70 字节的 DEX 头，保存魔数、校验字段以及每个索引段的数量/偏移对。所有值都是无符号 32 位小端整数，
/// 从 <c>.dex</c> 文件的前 112 字节读出。</para>
/// </summary>
public sealed class DexHeader
{
    /// <summary>
    /// The eight magic bytes <c>dex\n0xx\0</c> read from offset 0.
    /// <para>从偏移 0 读出的 8 字节魔数 <c>dex\n0xx\0</c>。</para>
    /// </summary>
    /// <value>Raw bytes exactly as stored; empty when the header is built without reading a file.
    /// <para>按原始字节保存；未读取文件而构造头部时为空数组。</para></value>
    public byte[] Magic { get; init; } = [];

    /// <summary>
    /// The ASCII format version taken from magic bytes 4 to 6.
    /// <para>取自魔数第 4 到 6 字节的 ASCII 格式版本。</para>
    /// </summary>
    /// <value>One of <c>035</c>, <c>037</c>, <c>038</c>, <c>039</c> or <c>040</c>; parsing rejects any other value.
    /// Defaults to <c>035</c>. <para>取值限于 <c>035</c>、<c>037</c>、<c>038</c>、<c>039</c>、<c>040</c>，其他值解析失败；默认为 <c>035</c>。</para></value>
    public string Version { get; init; } = "035";

    /// <summary>
    /// Adler-32 stored at offset 8, computed over every byte from offset 12 to the end of the file.
    /// <para>偏移 8 处的 Adler-32，覆盖从偏移 12 到文件末尾的全部字节。</para>
    /// </summary>
    /// <value>The stored value only: parsing keeps it as read and does not reject a header whose checksum differs from the recomputed one.
    /// <para>仅为文件中保存的值：解析时原样记录，校验和与重算结果不一致也不会导致解析失败。</para></value>
    public uint Checksum { get; init; }

    /// <summary>
    /// SHA-1 digest stored at offset 12, computed over the file content from offset 32 onward.
    /// <para>偏移 12 处的 SHA-1 摘要，覆盖从偏移 32 开始的文件内容。</para>
    /// </summary>
    /// <value>Always 20 bytes; all zero for a header that was never hashed.
    /// <para>固定 20 字节；未参与哈希计算时为全零。</para></value>
    public byte[] Signature { get; init; } = new byte[20];

    /// <summary>
    /// Total file size recorded at offset 32.
    /// <para>偏移 32 处记录的整个文件大小。</para>
    /// </summary>
    /// <value>Byte count of the complete DEX image; parsing requires it to equal the input length.
    /// <para>完整 DEX 镜像的字节数；解析时要求与输入长度相等。</para></value>
    public uint FileSize { get; init; }

    /// <summary>
    /// Header length recorded at offset 36.
    /// <para>偏移 36 处记录的头部长度。</para>
    /// </summary>
    /// <value>Must be <c>0x70</c>, the only header size accepted during parsing; that is also the default.
    /// <para>必须为 <c>0x70</c>，这是解析接受的唯一头部大小，也是默认值。</para></value>
    public uint HeaderSize { get; init; } = 0x70;

    /// <summary>
    /// Endianness marker recorded at offset 40.
    /// <para>偏移 40 处记录的字节序标记。</para>
    /// </summary>
    /// <value><c>0x12345678</c> for little-endian, the only supported layout; any other value fails parsing.
    /// <para>小端为 <c>0x12345678</c>，这是唯一支持的字节序，其他取值会导致解析失败。</para></value>
    public uint EndianTag { get; init; } = 0x12345678;

    /// <summary>
    /// Size recorded at offset 44 of the optional link section.
    /// <para>偏移 44 处记录的可选链接区大小。</para>
    /// </summary>
    /// <value>Byte count of link data, normally <c>0</c>; the section content is not read.
    /// <para>链接区字节数，通常为 <c>0</c>；解析器不读取该区内容。</para></value>
    public uint LinkSize { get; init; }

    /// <summary>
    /// File offset recorded at offset 48 of the optional link section.
    /// <para>偏移 48 处记录的可选链接区文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of link data, normally <c>0</c> when <see cref="LinkSize"/> is <c>0</c>.
    /// <para>链接区的绝对偏移；<see cref="LinkSize"/> 为 <c>0</c> 时通常也是 <c>0</c>。</para></value>
    public uint LinkOffset { get; init; }

    /// <summary>
    /// File offset recorded at offset 52 of the <c>map_list</c> section.
    /// <para>偏移 52 处记录的 <c>map_list</c> 区文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the map, or <c>0</c> when absent; a zero value skips method-handle and call-site parsing.
    /// <para>映射表的绝对偏移，缺失时为 <c>0</c>；为 <c>0</c> 时跳过方法句柄与调用点解析。</para></value>
    public uint MapOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>string_ids</c> table, recorded at offset 56.
    /// <para>偏移 56 处记录的 <c>string_ids</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 4-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 4 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint StringIdsSize { get; init; }

    /// <summary>
    /// File offset of the <c>string_ids</c> table, recorded at offset 60.
    /// <para>偏移 60 处记录的 <c>string_ids</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 4-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>4 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint StringIdsOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>type_ids</c> table, recorded at offset 64.
    /// <para>偏移 64 处记录的 <c>type_ids</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 4-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 4 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint TypeIdsSize { get; init; }

    /// <summary>
    /// File offset of the <c>type_ids</c> table, recorded at offset 68.
    /// <para>偏移 68 处记录的 <c>type_ids</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 4-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>4 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint TypeIdsOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>proto_ids</c> table, recorded at offset 72.
    /// <para>偏移 72 处记录的 <c>proto_ids</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 12-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 12 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint ProtoIdsSize { get; init; }

    /// <summary>
    /// File offset of the <c>proto_ids</c> table, recorded at offset 76.
    /// <para>偏移 76 处记录的 <c>proto_ids</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 12-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>12 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint ProtoIdsOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>field_ids</c> table, recorded at offset 80.
    /// <para>偏移 80 处记录的 <c>field_ids</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 8-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 8 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint FieldIdsSize { get; init; }

    /// <summary>
    /// File offset of the <c>field_ids</c> table, recorded at offset 84.
    /// <para>偏移 84 处记录的 <c>field_ids</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 8-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>8 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint FieldIdsOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>method_ids</c> table, recorded at offset 88.
    /// <para>偏移 88 处记录的 <c>method_ids</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 8-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 8 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint MethodIdsSize { get; init; }

    /// <summary>
    /// File offset of the <c>method_ids</c> table, recorded at offset 92.
    /// <para>偏移 92 处记录的 <c>method_ids</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 8-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>8 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint MethodIdsOffset { get; init; }

    /// <summary>
    /// Number of entries in the <c>class_defs</c> table, recorded at offset 96.
    /// <para>偏移 96 处记录的 <c>class_defs</c> 表条目数。</para>
    /// </summary>
    /// <value><c>0</c> for an empty table; the parser rejects a count whose 32-byte entries do not fit the remaining bytes.
    /// <para>空表为 <c>0</c>；若按每条 32 字节计算超出剩余字节数，解析器会拒绝该数量。</para></value>
    public uint ClassDefsSize { get; init; }

    /// <summary>
    /// File offset of the <c>class_defs</c> table, recorded at offset 100.
    /// <para>偏移 100 处记录的 <c>class_defs</c> 表文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset of the 32-byte entries; <c>0</c> when the table is empty, otherwise at least <c>0x70</c>.
    /// <para>32 字节条目的绝对偏移；表为空时为 <c>0</c>，否则不小于 <c>0x70</c>。</para></value>
    public uint ClassDefsOffset { get; init; }

    /// <summary>
    /// Size of the <c>data</c> section recorded at offset 104.
    /// <para>偏移 104 处记录的 <c>data</c> 区大小。</para>
    /// </summary>
    /// <value>Byte count of the data region; the parser rejects a size that does not fit the remaining bytes.
    /// <para>数据区字节数；超出剩余字节数时解析器会拒绝。</para></value>
    public uint DataSize { get; init; }

    /// <summary>
    /// File offset of the <c>data</c> section recorded at offset 108.
    /// <para>偏移 108 处记录的 <c>data</c> 区文件偏移。</para>
    /// </summary>
    /// <value>Absolute offset where encoded method, class and value data begins; writing a DEX patches it to the first data byte.
    /// <para>编码方法、类与常量数据的起始绝对偏移；写出 DEX 时会把它补丁为第一个数据字节的位置。</para></value>
    public uint DataOffset { get; init; }
}