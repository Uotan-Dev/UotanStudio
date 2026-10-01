namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// One file or directory inside a FAT-family volume (FAT12, FAT16, FAT32 or exFAT).
/// <para>FAT 家族卷（FAT12、FAT16、FAT32 或 exFAT）内的单个文件或目录。</para>
/// </summary>
/// <param name="Path">
/// <para>镜像内路径，使用正斜杠且不含前导分隔符。</para>
/// Image-relative path with forward slashes and no leading separator.
/// </param>
/// <param name="Size">
/// <para>文件字节数；目录为 0。</para>
/// File size in bytes; zero for a directory.
/// </param>
/// <param name="Kind">
/// <para>条目是文件还是目录。</para>
/// Whether the entry is a file or a directory.
/// </param>
/// <param name="Attributes">
/// <para>原始 DOS 属性位。</para>
/// The raw DOS attribute flags.
/// </param>
/// <param name="LastWriteTimeUtc">
/// <para>目录条目记录的最后写入时间，按无时区本地时间解释后换算的 UTC 值。</para>
/// The recorded last-write time converted to UTC from the volume's timezone-free local stamp.
/// </param>
public sealed record FatVolumeEntry(
    string Path,
    long Size,
    FatVolumeEntryKind Kind,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc);

/// <summary>
/// Entry kinds a FAT-family volume can hold.
/// <para>FAT 家族卷可承载的条目类型。</para>
/// </summary>
public enum FatVolumeEntryKind
{
    /// <summary><para>常规文件。</para>A regular file.</summary>
    File,

    /// <summary><para>目录。</para>A directory.</summary>
    Directory,
}