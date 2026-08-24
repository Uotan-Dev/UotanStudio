namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>为 <see cref="RegionType"/> 枚举提供扩展方法的静态类。</para>
/// Provides extension methods for the <see cref="RegionType"/> enumeration.
/// </summary>
public static class RegionTypeExtensions
{
    /// <summary>
    /// <para>获取指定区域类型对应的文件扩展名。</para>
    /// Gets the file extension associated with the specified region type.
    /// </summary>
    /// <param name="type">
    /// <para>区域类型。</para>
    /// The region type.
    /// </param>
    /// <returns>
    /// <para>文件扩展名字符串，包含前导点号（如 ".xml" 或 ".bin"）。</para>
    /// The file extension string including the leading dot (e.g., ".xml" or ".bin").
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>当区域类型不在已知范围内时抛出。</para>
    /// Thrown when the region type is not within the known range.
    /// </exception>
    public static string GetFileExtension(this RegionType type) => type switch
    {
        RegionType.Metadata => ".xml",
        RegionType.Patch => ".xml",
        RegionType.RawProgram => ".xml",
        RegionType.KeyMap => ".bin",
        RegionType.FileIndex => ".xml",
        RegionType.Region6 => ".bin",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"Unknown region type: {type}")
    };

    /// <summary>
    /// <para>获取指定区域类型的区域名称字符串。</para>
    /// Gets the region name string for the specified region type.
    /// </summary>
    /// <param name="type">
    /// <para>区域类型。</para>
    /// The region type.
    /// </param>
    /// <returns>
    /// <para>区域名称字符串（如 "Metadata"、"Patch" 等）。</para>
    /// The region name string (e.g., "Metadata", "Patch", etc.).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>当区域类型不在已知范围内时抛出。</para>
    /// Thrown when the region type is not within the known range.
    /// </exception>
    public static string GetRegionName(this RegionType type) => type switch
    {
        RegionType.Metadata => "Metadata",
        RegionType.Patch => "Patch",
        RegionType.RawProgram => "RawProgram",
        RegionType.KeyMap => "KeyMap",
        RegionType.FileIndex => "FileIndex",
        RegionType.Region6 => "Region6",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"Unknown region type: {type}")
    };

    /// <summary>
    /// <para>获取指定区域类型的默认文件名。</para>
    /// Gets the default file name for the specified region type.
    /// </summary>
    /// <param name="type">
    /// <para>区域类型。</para>
    /// The region type.
    /// </param>
    /// <returns>
    /// <para>默认文件名字符串（如 "Metadata.xml"、"KeyMap.bin"、"region6block.bin" 等）。</para>
    /// The default file name string (e.g., "Metadata.xml", "KeyMap.bin", "region6block.bin", etc.).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>当区域类型不在已知范围内时抛出。</para>
    /// Thrown when the region type is not within the known range.
    /// </exception>
    public static string GetDefaultFileName(this RegionType type) => type switch
    {
        RegionType.Metadata => "Metadata.xml",
        RegionType.Patch => "Patch.xml",
        RegionType.RawProgram => "RawProgram.xml",
        RegionType.KeyMap => "KeyMap.bin",
        RegionType.FileIndex => "FileIndex.xml",
        RegionType.Region6 => "region6block.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"Unknown region type: {type}")
    };
}