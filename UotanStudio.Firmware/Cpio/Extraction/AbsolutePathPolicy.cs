namespace UotanStudio.Firmware.Cpio.Extraction;

/// <summary>
/// Controls how extraction handles absolute entry paths.
/// <para>控制解包如何处理绝对条目路径。</para>
/// </summary>
public enum AbsolutePathPolicy
{
    /// <summary>
    /// Reject absolute paths and throw.
    /// <para>拒绝绝对路径并抛出异常。</para>
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Strip drive/root prefix and extract relative to destination.
    /// Closest behavior to GNU cpio --no-absolute-filenames.
    /// <para>去除驱动器/根前缀并相对于目标目录解包，最接近 GNU cpio --no-absolute-filenames 的行为。</para>
    /// </summary>
    StripRoot,
}