namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public static class RegionTypeExtensions
{
    /// <summary>
    /// Gets the file extension associated with the specified region type.
    /// <para>获取指定区域类型对应的文件扩展名。</para>
    /// </summary>
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
    /// Gets the region name string for the specified region type.
    /// <para>获取指定区域类型的区域名称字符串。</para>
    /// </summary>
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
    /// Gets the default file name for the specified region type.
    /// <para>获取指定区域类型的默认文件名。</para>
    /// </summary>
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