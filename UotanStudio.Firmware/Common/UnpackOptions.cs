namespace UotanStudio.Firmware.Common;

/// <summary>
/// Options for unpack operations.
/// <para>解包操作选项。</para>
/// </summary>
public sealed class UnpackOptions
{
    /// <summary>
    /// Whether to overwrite existing files.
    /// <para>是否覆盖已存在文件。</para>
    /// </summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    /// Optional progress reporter.
    /// <para>可选的进度报告器。</para>
    /// </summary>
    public IFirmwareProgress? Progress { get; set; }
}