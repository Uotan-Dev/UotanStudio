namespace UotanStudio.Firmware.VendorFirmware.NTPi.Models;

/// <summary>
/// Region naming and file naming helpers surfaced as extension members on <see cref="RegionType"/>.
/// <para>以扩展成员形式挂接到 <see cref="RegionType"/> 的区域命名与文件命名助手。</para>
/// </summary>
public static class RegionTypeExtensions
{
    /// <summary>
    /// Region naming and file naming helpers on a region type.
    /// <para>面向区域类型的区域命名与文件命名助手。</para>
    /// </summary>
    extension(RegionType type)
    {
        /// <summary>
        /// Gets the file extension associated with this region type.
        /// <para>获取该区域类型对应的文件扩展名。</para>
        /// </summary>
        /// <returns>
        /// The extension including the leading dot: <c>.xml</c> for <see cref="RegionType.Metadata"/>, <see cref="RegionType.Patch"/>, <see cref="RegionType.RawProgram"/> and <see cref="RegionType.FileIndex"/>, <c>.bin</c> for <see cref="RegionType.KeyMap"/> and <see cref="RegionType.Region6"/>.
        /// <para>含前导点的扩展名：<see cref="RegionType.Metadata"/>、<see cref="RegionType.Patch"/>、<see cref="RegionType.RawProgram"/> 与 <see cref="RegionType.FileIndex"/> 为 <c>.xml</c>，<see cref="RegionType.KeyMap"/> 与 <see cref="RegionType.Region6"/> 为 <c>.bin</c>。</para>
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The region type carries a value outside the defined region types.
        /// <para>区域类型取值超出已定义的范围。</para>
        /// </exception>
        public string GetFileExtension() => type switch
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
        /// Gets the region name string for this region type.
        /// <para>获取该区域类型的区域名称字符串。</para>
        /// </summary>
        /// <returns>
        /// The region name matching the member: <c>Metadata</c>, <c>Patch</c>, <c>RawProgram</c>, <c>KeyMap</c>, <c>FileIndex</c> or <c>Region6</c>.
        /// <para>与该成员同名的区域名称：<c>Metadata</c>、<c>Patch</c>、<c>RawProgram</c>、<c>KeyMap</c>、<c>FileIndex</c> 或 <c>Region6</c>。</para>
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The region type carries a value outside the defined region types.
        /// <para>区域类型取值超出已定义的范围。</para>
        /// </exception>
        public string GetRegionName() => type switch
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
        /// Gets the default file name for this region type.
        /// <para>获取该区域类型的默认文件名。</para>
        /// </summary>
        /// <returns>
        /// <c>Metadata.xml</c>, <c>Patch.xml</c>, <c>RawProgram.xml</c>, <c>KeyMap.bin</c>, <c>FileIndex.xml</c> or <c>region6block.bin</c>; the Region6 name is lower-case while the other names match the member name.
        /// <para>取值依次为 <c>Metadata.xml</c>、<c>Patch.xml</c>、<c>RawProgram.xml</c>、<c>KeyMap.bin</c>、<c>FileIndex.xml</c> 或 <c>region6block.bin</c>；Region6 的文件名为小写，其余与成员名一致。</para>
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The region type carries a value outside the defined region types.
        /// <para>区域类型取值超出已定义的范围。</para>
        /// </exception>
        public string GetDefaultFileName() => type switch
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
}