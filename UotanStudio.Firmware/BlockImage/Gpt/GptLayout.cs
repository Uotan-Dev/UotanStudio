namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Canonical UEFI GPT 1.0 layout constants shared by the reader, writer and image editor.
/// <para>读取器、写入器与镜像编辑器共用的 UEFI GPT 1.0 规范布局常量。</para>
/// </summary>
public static class GptLayout
{
    /// <summary>Sector size in bytes printed by the layout constants. <para>布局常量使用的扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>Byte offset of the primary GPT header inside the first sector image. <para>首个扇区镜像内主 GPT 头的字节偏移。</para></summary>
    public const int HeaderOffset = 512;

    /// <summary>Single partition-entry size in bytes. <para>单个分区表项大小（字节）。</para></summary>
    public const int EntrySize = 128;

    /// <summary>Minimum GPT header size in bytes. <para>GPT 头的最小字节大小。</para></summary>
    public const int HeaderSize = 92;

    /// <summary>Maximum partition-name UTF-16 units. <para>分区名最大 UTF-16 单元数。</para></summary>
    public const int MaxNameUnits = 36;

    /// <summary>Header CRC-32 field offset within the header. <para>头 CRC-32 字段在头内的偏移。</para></summary>
    public const int HeaderCrcOffset = 16;

    /// <summary>Partition-entry-array CRC-32 field offset within the header. <para>分区表项 CRC-32 字段在头内的偏移。</para></summary>
    public const int EntriesCrcOffset = 88;

    /// <summary>Partition-entry-array start LBA. <para>分区表项起始 LBA。</para></summary>
    public const ulong EntriesStartLba = 2;

    /// <summary>First usable LBA, the header and entries occupying 34 sectors. <para>首个可用 LBA，头与分区表占 34 个扇区。</para></summary>
    public const ulong FirstUsableLba = 34;

    /// <summary>Primary GPT header LBA. <para>主 GPT 头所在 LBA。</para></summary>
    public const ulong PrimaryHeaderLba = 1;

    /// <summary>GPT header revision number. <para>GPT 头修订号。</para></summary>
    public const uint Revision = 0x0001_0000;

    /// <summary>Protective MBR partition type. <para>保护性 MBR 分区类型。</para></summary>
    public const byte ProtectiveMbrType = 0xEE;

    /// <summary>GPT header signature (<c>EFI PART</c>). <para>GPT 头签名（<c>EFI PART</c>）。</para></summary>
    public static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    /// <summary>Default Linux filesystem type GUID. <para>默认 Linux 文件系统类型 GUID。</para></summary>
    public static readonly Guid DefaultTypeGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
}