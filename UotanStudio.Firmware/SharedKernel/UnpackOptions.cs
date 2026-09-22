namespace UotanStudio.Firmware.SharedKernel;

public sealed class UnpackOptions
{
    /// <summary>
    /// Gets or sets the per-file decompression budget for formats without a trusted output size.
    /// <para>获取或设置没有可信输出长度的格式所用的单文件解压预算，默认 64 GiB。</para>
    /// </summary>
    public long MaximumOutputBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    public bool Overwrite { get; set; } = true;

    /// <summary>
    /// Gets or sets the exact container entry names to extract. A null or empty collection extracts all entries.
    /// Name comparison is ordinal and case-insensitive; format handlers do not infer partitions from suffixes.
    /// </summary>
    public IReadOnlyCollection<string>? SelectedEntries { get; set; }

    /// <summary>Gets or sets whether a container-level checksum must be verified before extraction.</summary>
    public bool VerifyChecksums { get; set; }

    public IFirmwareProgress? Progress { get; set; }

    /// <summary>Receives measured bytes for filesystem extraction and preservation phases.</summary>
    public IProgress<FileSystemByteProgress>? FileSystemProgress { get; set; }

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
