namespace UotanStudio.Firmware.Common;

public sealed class UnpackOptions
{
    /// <summary>
    /// Gets or sets the per-file decompression budget for formats without a trusted output size.
    /// <para>获取或设置没有可信输出长度的格式所用的单文件解压预算，默认 64 GiB。</para>
    /// </summary>
    public long MaximumOutputBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    public bool Overwrite { get; set; } = true;

    public IFirmwareProgress? Progress { get; set; }
}