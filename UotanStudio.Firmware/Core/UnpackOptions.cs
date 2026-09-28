namespace UotanStudio.Firmware.Core;

/// <summary>
/// Groups the caller-supplied knobs shared by the archive, Payload and filesystem unpack paths.
/// <para>集中管理解包流程（归档、Payload 与文件系统）共用的调用方参数。</para>
/// </summary>
public sealed class UnpackOptions
{
    /// <summary>
    /// Gets or sets the per-file decompression budget for formats without a trusted output size.
    /// <para>获取或设置没有可信输出长度的格式所用的单文件解压预算，默认 64 GiB。</para>
    /// </summary>
    /// <value>The ceiling in bytes for one decompressed entry; growth beyond it aborts that entry.
    /// <para>单个解压条目的字节上限，超出后该条目的解压会中止。</para></value>
    public long MaximumOutputBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets or sets whether extraction replaces a destination file that already exists.
    /// <para>获取或设置解包时是否替换已存在的目标文件。</para>
    /// </summary>
    /// <value><see langword="true"/> to overwrite existing files, <see langword="false"/> to leave them in place; the default is <see langword="true"/>.
    /// <para>为 <see langword="true"/> 时覆盖已存在文件，为 <see langword="false"/> 时保留原文件；默认值为 <see langword="true"/>。</para></value>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    /// Gets or sets the exact container entry names to extract. A null or empty collection extracts all entries.
    /// Name comparison is ordinal and case-insensitive; format handlers do not infer partitions from suffixes.
    /// <para>获取或设置需要解出的容器条目精确名称。为 <see langword="null"/> 或空集合时解出全部条目。
    /// 名称按序数且忽略大小写比较；格式处理器不会根据后缀推断分区。</para>
    /// </summary>
    /// <value>The selected entry names, or <see langword="null"/> when every entry is extracted.
    /// <para>被选中的条目名称；解出全部条目时为 <see langword="null"/>。</para></value>
    public IReadOnlyCollection<string>? SelectedEntries { get; set; }

    /// <summary>Gets or sets whether a container-level checksum must be verified before extraction.
    /// <para>获取或设置解包前是否校验容器级校验和。</para></summary>
    /// <value><see langword="true"/> to require the recorded checksum to match, <see langword="false"/> to skip the check.
    /// <para>为 <see langword="true"/> 时要求记录的校验和必须一致，为 <see langword="false"/> 时跳过检查。</para></value>
    public bool VerifyChecksums { get; set; }

    /// <summary>
    /// Gets or sets the sink that receives percentage progress reports emitted while unpacking.
    /// <para>获取或设置接收解包过程百分比进度上报的回调对象。</para>
    /// </summary>
    /// <value>The progress sink, or <see langword="null"/> when progress is not reported.
    /// <para>进度回调；不上报进度时为 <see langword="null"/>。</para></value>
    public IFirmwareProgress? Progress { get; set; }

    /// <summary>Receives measured bytes for filesystem extraction and preservation phases.
    /// <para>接收文件系统解包与保留阶段实测到的字节数。</para></summary>
    /// <value>The byte counter sink tagged with the current work stage, or <see langword="null"/> when measurement is not requested.
    /// <para>带有当前工作阶段的字节计数回调；无需测量时为 <see langword="null"/>。</para></value>
    public IProgress<FileSystemByteProgress>? FileSystemProgress { get; set; }

    /// <summary>EROFS external device images in device-table order (ids 1..N).
    /// <para>按设备表顺序（编号 1..N）排列的 EROFS 外部设备镜像路径。</para></summary>
    /// <value>The ordered list of extra device image paths, or <see langword="null"/> when the image references no external devices.
    /// <para>外部设备镜像路径的有序列表；镜像不引用外部设备时为 <see langword="null"/>。</para></value>
    public IReadOnlyList<string>? ErofsExtraDevicePaths { get; set; }

    internal HashSet<string>? CreateSelectedEntrySet()
    {
        if (SelectedEntries is not { Count: > 0 })
        {
            return null;
        }

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SelectedEntries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new ArgumentException("Selected entry names must not be empty.", nameof(SelectedEntries));
            }

            selected.Add(entry);
        }

        return selected;
    }
}