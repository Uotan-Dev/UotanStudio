namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>DEX 文件头部（header_item，0x70 字节，小端序）。</para>
/// DEX file header (header_item, 0x70 bytes, little-endian).
/// </summary>
public sealed class DexHeader
{
    /// <summary>魔数字节（"dex\n035\0" 等）。<para>Magic bytes ("dex\n035\0" etc.).</para></summary>
    public byte[] Magic { get; init; } = [];

    /// <summary>版本号（如 "035"）。<para>Version string (e.g. "035").</para></summary>
    public string Version { get; init; } = "035";

    /// <summary>校验和（字节 12..文件尾的 Adler-32）。<para>Checksum (Adler-32 of bytes 12..EOF).</para></summary>
    public uint Checksum { get; init; }

    /// <summary>签名（字节 32..文件尾的 SHA-1，20 字节）。<para>Signature (SHA-1 of bytes 32..EOF, 20 bytes).</para></summary>
    public byte[] Signature { get; init; } = new byte[20];

    /// <summary>文件总大小（字节）。<para>Total file size in bytes.</para></summary>
    public uint FileSize { get; init; }

    /// <summary>头部大小（恒为 0x70）。<para>Header size (always 0x70).</para></summary>
    public uint HeaderSize { get; init; } = 0x70;

    /// <summary>字节序标记（0x12345678 = 小端）。<para>Endian tag (0x12345678 = little-endian).</para></summary>
    public uint EndianTag { get; init; } = 0x12345678;

    /// <summary>链接段大小。<para>Link section size.</para></summary>
    public uint LinkSize { get; init; }

    /// <summary>链接段偏移。<para>Link section offset.</para></summary>
    public uint LinkOffset { get; init; }

    /// <summary>map_list 偏移。<para>Map list offset.</para></summary>
    public uint MapOffset { get; init; }

    /// <summary>string_ids 表大小。<para>string_ids table size.</para></summary>
    public uint StringIdsSize { get; init; }

    /// <summary>string_ids 表偏移。<para>string_ids table offset.</para></summary>
    public uint StringIdsOffset { get; init; }

    /// <summary>type_ids 表大小。<para>type_ids table size.</para></summary>
    public uint TypeIdsSize { get; init; }

    /// <summary>type_ids 表偏移。<para>type_ids table offset.</para></summary>
    public uint TypeIdsOffset { get; init; }

    /// <summary>proto_ids 表大小。<para>proto_ids table size.</para></summary>
    public uint ProtoIdsSize { get; init; }

    /// <summary>proto_ids 表偏移。<para>proto_ids table offset.</para></summary>
    public uint ProtoIdsOffset { get; init; }

    /// <summary>field_ids 表大小。<para>field_ids table size.</para></summary>
    public uint FieldIdsSize { get; init; }

    /// <summary>field_ids 表偏移。<para>field_ids table offset.</para></summary>
    public uint FieldIdsOffset { get; init; }

    /// <summary>method_ids 表大小。<para>method_ids table size.</para></summary>
    public uint MethodIdsSize { get; init; }

    /// <summary>method_ids 表偏移。<para>method_ids table offset.</para></summary>
    public uint MethodIdsOffset { get; init; }

    /// <summary>class_defs 表大小。<para>class_defs table size.</para></summary>
    public uint ClassDefsSize { get; init; }

    /// <summary>class_defs 表偏移。<para>class_defs table offset.</para></summary>
    public uint ClassDefsOffset { get; init; }

    /// <summary>数据段大小。<para>Data section size.</para></summary>
    public uint DataSize { get; init; }

    /// <summary>数据段偏移。<para>Data section offset.</para></summary>
    public uint DataOffset { get; init; }
}