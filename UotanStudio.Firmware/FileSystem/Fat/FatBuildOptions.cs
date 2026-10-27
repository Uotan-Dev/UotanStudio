using System.Globalization;

namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// FAT variant a <see cref="FatBuilder"/> build writes.
/// <para><see cref="FatBuilder"/> 构建所写出的 FAT 变体。</para>
/// </summary>
public enum FatVariant
{
    /// <summary>
    /// Derives the variant from the volume's cluster count so the boot record, the allocation table width and
    /// the directory limits agree: fewer than 4085 clusters build FAT12, fewer than 65525 build FAT16 and any
    /// larger volume builds FAT32.
    /// <para>按卷的簇数推导变体，使引导记录、分配表宽度与目录上限彼此一致：少于 4085 簇构建 FAT12，
    /// 少于 65525 簇构建 FAT16，更大的卷构建 FAT32。</para>
    /// </summary>
    Auto,

    /// <summary>FAT12, valid up to 4084 clusters. <para>FAT12，上限为 4084 簇。</para></summary>
    Fat12,

    /// <summary>FAT16, padded to at least 4085 clusters and valid up to 65524. <para>FAT16，至少补齐到 4085 簇，上限为 65524 簇。</para></summary>
    Fat16,

    /// <summary>FAT32, padded to at least 65525 clusters. <para>FAT32，至少补齐到 65525 簇。</para></summary>
    Fat32,
}

/// <summary>
/// Geometry and identity settings for one <see cref="FatBuilder"/> build. Every member has a value the
/// builder accepts without further input; set a member only to reproduce a specific volume layout, such as the
/// geometry recorded by an extraction of the same volume.
/// <para>单次 <see cref="FatBuilder"/> 构建的几何与标识设置。每个成员都有构建器可直接接受的取值；只有在复现
/// 特定卷布局（例如同一卷解包时记录的几何）时才需要设置相应成员。</para>
/// </summary>
public sealed record FatBuildOptions
{
    /// <summary>
    /// Requested FAT variant; <see cref="FatVariant.Auto"/> derives it from the cluster count.
    /// <para>请求的 FAT 变体；<see cref="FatVariant.Auto"/> 按簇数推导。</para>
    /// </summary>
    public FatVariant Variant { get; init; } = FatVariant.Auto;

    /// <summary>
    /// Logical bytes per sector; a power of two from 512 through 4096.
    /// <para>每扇区逻辑字节数；取 512 至 4096 之间的 2 的幂。</para>
    /// </summary>
    public int BytesPerSector { get; init; } = 512;

    /// <summary>
    /// Sectors per cluster; zero or a power of two up to 128, with zero selecting a cluster size from the volume
    /// size the way <c>mkfs.fat</c> does.
    /// <para>每簇扇区数；取 0 或不超过 128 的 2 的幂，取 0 时按 <c>mkfs.fat</c> 的方式依卷大小选择簇大小。</para>
    /// </summary>
    public int SectorsPerCluster { get; init; }

    /// <summary>
    /// Reserved sectors ahead of the first allocation table; zero selects 1 for FAT12/FAT16 and 32 for FAT32,
    /// which leaves room for the FSInfo and backup boot sectors.
    /// <para>首个分配表之前的保留扇区数；取 0 时 FAT12/FAT16 用 1、FAT32 用 32，为 FSInfo 与备份引导扇区留出空间。</para>
    /// </summary>
    public int ReservedSectors { get; init; }

    /// <summary>
    /// Fixed root directory slots on FAT12/FAT16; zero selects 512, and FAT32 always stores its root as a cluster
    /// chain and therefore writes zero.
    /// <para>FAT12/FAT16 的固定根目录槽位数；取 0 时用 512，FAT32 始终以簇链保存根目录，因此写入 0。</para>
    /// </summary>
    public int RootDirectoryEntries { get; init; }

    /// <summary>
    /// Volume label stored in the boot record and as a root-directory volume entry; empty writes no label.
    /// <para>写入引导记录并作为根目录卷条目的卷标；为空时不写卷标。</para>
    /// </summary>
    public string VolumeLabel { get; init; } = string.Empty;

    /// <summary>
    /// Eight-character OEM name stored at offset 3 of the boot record; padded with spaces when shorter.
    /// <para>写入引导记录偏移 3 的八字符 OEM 名称；不足时以空格补齐。</para>
    /// </summary>
    public string OemName { get; init; } = "UOTAN";

    /// <summary>
    /// Media descriptor stored in the boot record and in the first allocation-table entry; <c>0xF8</c> is a fixed
    /// disk and <c>0xF0</c> a removable one.
    /// <para>写入引导记录与分配表首项的介质描述符；<c>0xF8</c> 为固定盘，<c>0xF0</c> 为可移动盘。</para>
    /// </summary>
    public byte MediaDescriptor { get; init; } = 0xF8;

    /// <summary>
    /// Sectors of the parent partition or disk ahead of this volume, recorded in the boot record.
    /// <para>本卷之前父分区或磁盘的扇区数，写入引导记录。</para>
    /// </summary>
    public int HiddenSectors { get; init; }

    /// <summary>
    /// Total capacity to declare in bytes; <see langword="null"/> builds the smallest volume that holds the
    /// content, while a value reproduces the capacity of an original volume as closely as the geometry allows.
    /// A requested capacity smaller than the content receives a clear failure instead of a truncated image; the
    /// FAT16 and FAT32 minimum-cluster floors may still grow the declared capacity above the request.
    /// <para>要声明的总容量（字节）；为 <see langword="null"/> 时构建刚好容纳内容的最小卷，给出取值时则尽可能
    /// 复现原始卷的容量。请求容量小于内容时明确失败，而不是写出被截断的镜像。</para>
    /// </summary>
    public long? TotalBytes { get; init; }

    internal FatBuildOptions Validated()
    {
        if (!IsPowerOfTwo(BytesPerSector) || BytesPerSector is < 512 or > 4_096)
        {
            throw new InvalidDataException(
                $"FAT bytes per sector must be a power of two from 512 through 4096, not {BytesPerSector}.");
        }

        if (SectorsPerCluster != 0 && (!IsPowerOfTwo(SectorsPerCluster) || SectorsPerCluster > 128))
        {
            throw new InvalidDataException(
                $"FAT sectors per cluster must be zero or a power of two up to 128, not {SectorsPerCluster}.");
        }

        if (!Enum.IsDefined(Variant))
        {
            throw new InvalidDataException($"Unsupported FAT variant '{Variant}'.");
        }

        if (ReservedSectors is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"FAT reserved sector count is out of range: {ReservedSectors}.");
        }

        if (RootDirectoryEntries is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"FAT root directory entry count is out of range: {RootDirectoryEntries}.");
        }

        if (RootDirectoryEntries != 0 && (long)RootDirectoryEntries * FatBuilder.DirectoryEntryBytes % BytesPerSector != 0)
        {
            throw new InvalidDataException(
                $"FAT root directory entries must fill whole {BytesPerSector}-byte sectors, not {RootDirectoryEntries} entries.");
        }

        if (MediaDescriptor is not (0xF0 or 0xF8 or 0xF9 or 0xFA or 0xFB or 0xFC or 0xFD or 0xFE or 0xFF))
        {
            throw new InvalidDataException($"0x{MediaDescriptor:X2} is not a media descriptor FAT accepts.");
        }

        if (HiddenSectors < 0)
        {
            throw new InvalidDataException("FAT hidden sector count cannot be negative.");
        }

        if (TotalBytes is { } requested && (requested <= 0 || requested > FatBuilder.MaximumVolumeBytes))
        {
            throw new InvalidDataException(
                $"FAT total capacity must be positive and at most {FatBuilder.MaximumVolumeBytes} bytes, not {requested.ToString(CultureInfo.InvariantCulture)}.");
        }

        return this with
        {
            VolumeLabel = NormalizeLabel(VolumeLabel),
            OemName = NormalizeOemName(OemName),
        };
    }

    internal static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static string NormalizeLabel(string label)
    {
        var trimmed = label.Trim().ToUpperInvariant();
        if (trimmed.Length > 11)
        {
            throw new InvalidDataException($"The FAT volume label '{label}' exceeds 11 characters.");
        }

        foreach (var character in trimmed)
        {
            if (character < 0x20 || character > 0x7E || character is '"' or '*' or '/' or ':' or '<' or '>' or '?' or '\\' or '|')
            {
                throw new InvalidDataException($"The FAT volume label '{label}' contains '{character}', which FAT cannot store.");
            }
        }

        return trimmed;
    }

    private static string NormalizeOemName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 8)
        {
            throw new InvalidDataException($"The FAT OEM name '{name}' exceeds 8 characters.");
        }

        foreach (var character in trimmed)
        {
            if (character < 0x20 || character > 0x7E)
            {
                throw new InvalidDataException($"The FAT OEM name '{name}' must be printable ASCII.");
            }
        }

        return trimmed.PadRight(8, ' ');
    }
}