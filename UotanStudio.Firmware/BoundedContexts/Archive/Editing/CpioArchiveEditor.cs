using UotanStudio.Firmware.BoundedContexts.Archive.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Editing;

/// <summary>
/// Mutable operations for ramdisk CPIO archives, backed by the shared archive model.
/// <para>基于共享归档模型提供 ramdisk CPIO 的可变操作。</para>
/// </summary>
public sealed class CpioArchiveEditor
{
    private readonly CpioArchive _archive = new();
    private readonly List<ArchiveEntry> _entries = [];

    /// <summary>
    /// Current in-memory entries in archive order; edits made through this class are visible immediately.
    /// <para>按归档顺序排列的当前内存条目；通过本类所做的修改立即可见。</para>
    /// </summary>
    /// <value>
    /// A live read-only view over the working entry set; empty before <see cref="Load"/> or after
    /// removing every entry.
    /// <para>对工作条目集合的实时只读视图；未调用 <see cref="Load"/> 或移除全部条目后为空。</para>
    /// </value>
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    /// <summary>
    /// Replaces the working entry set with all entries read from an archive stream, materializing
    /// each payload in memory.
    /// <para>用从归档流读取的全部条目替换工作条目集合，并把每个负载物化到内存。</para>
    /// </summary>
    /// <param name="stream">
    /// Archive stream positioned at its start; ownership stays with the caller.
    /// <para>定位在起始处的归档流；流的所有权仍归调用方。</para>
    /// </param>
    /// <param name="formatHint">
    /// Format assumed while parsing; <see langword="null"/> auto-detects, which requires a seekable
    /// stream.
    /// <para>解析时假定的格式；为 <see langword="null"/> 时自动检测，且要求流可寻址。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The stream is not seekable and <paramref name="formatHint"/> is <see langword="null"/>.
    /// <para>流不可寻址且 <paramref name="formatHint"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public void Load(Stream stream, ArchiveFormat? formatHint = ArchiveFormat.NewAscii)
    {
        _entries.Clear();
        _entries.AddRange(_archive.ReadAll(stream, formatHint));
    }

    /// <summary>
    /// Reports whether an entry whose normalized path equals the given path is present.
    /// <para>判断是否存在规范化路径与给定路径完全一致的条目。</para>
    /// </summary>
    /// <param name="path">
    /// Entry path to look up; see <see cref="Find"/> for the normalization rules.
    /// <para>待查找的条目路径；规范化规则见 <see cref="Find"/>。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a matching entry exists.
    /// <para>存在匹配条目时返回 <see langword="true"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is <see langword="null"/> or white space.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或仅含空白。</para>
    /// </exception>
    public bool Exists(string path) => Find(path) is not null;

    /// <summary>
    /// Finds the first entry whose normalized path equals the given path.
    /// <para>查找规范化路径与给定路径一致的首个条目。</para>
    /// </summary>
    /// <param name="path">
    /// Entry path to look up; backslashes become forward slashes and leading <c>/</c> or <c>./</c>
    /// prefixes are stripped.
    /// <para>待查找的条目路径；反斜杠转为正斜杠，并去除开头的 <c>/</c> 或 <c>./</c> 前缀。</para>
    /// </param>
    /// <returns>
    /// The matching entry, or <see langword="null"/> when no entry has that normalized path.
    /// <para>匹配的条目；没有条目具有该规范化路径时返回 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is <see langword="null"/> or white space.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或仅含空白。</para>
    /// </exception>
    public ArchiveEntry? Find(string path)
    {
        var normalized = Normalize(path);
        return _entries.FirstOrDefault(entry => Normalize(entry.Name) == normalized);
    }

    /// <summary>
    /// Removes every entry whose normalized path equals the given path.
    /// <para>移除所有规范化路径与给定路径一致的条目。</para>
    /// </summary>
    /// <param name="path">Entry path to remove; the same normalization as in <see cref="Find"/> applies.</param>
    /// <returns><see langword="true"/> when this call removes at least one entry.
    /// <para>本次调用至少移除一个条目时返回 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or white space.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或仅含空白。</para></exception>
    public bool Remove(string path)
    {
        var normalized = Normalize(path);
        return _entries.RemoveAll(entry => Normalize(entry.Name) == normalized) > 0;
    }

    /// <summary>
    /// Replaces the source entry with one carrying the normalized destination name, keeping its
    /// list position, payload, link target and metadata.
    /// <para>用携带目标规范化名称的新条目替换源条目，保留其列表位置、负载、链接目标与元数据。</para>
    /// </summary>
    /// <param name="sourcePath">
    /// Existing entry path to rename; the same normalization as in <see cref="Find"/> applies.
    /// <para>待重命名的既有条目路径；规范化规则与 <see cref="Find"/> 相同。</para>
    /// </param>
    /// <param name="destinationPath">
    /// New path for the entry; it must not already exist in the archive.
    /// <para>条目的新路径；该路径不得在归档中已存在。</para>
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the source entry is missing or the destination path is already
    /// taken; <see langword="true"/> after the entry was swapped.
    /// <para>源条目不存在或目标路径已被占用时返回 <see langword="false"/>；完成替换后返回
    /// <see langword="true"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourcePath"/> or <paramref name="destinationPath"/> is <see langword="null"/>
    /// or white space.
    /// <para><paramref name="sourcePath"/> 或 <paramref name="destinationPath"/> 为
    /// <see langword="null"/> 或仅含空白。</para>
    /// </exception>
    public bool Rename(string sourcePath, string destinationPath)
    {
        var source = Find(sourcePath);
        if (source is null || Exists(destinationPath))
            return false;

        var index = _entries.IndexOf(source);
        _entries[index] = new ArchiveEntry
        {
            Name = Normalize(destinationPath),
            LinkName = source.LinkName,
            Data = source.Data,
            DataStream = source.DataStream,
            DataStreamFactory = source.DataStreamFactory,
            DeclaredDataLength = source.DeclaredDataLength,
            Metadata = source.Metadata
        };
        return true;
    }

    /// <summary>
    /// Removes any entry at the path and appends a new in-memory entry carrying the given bytes.
    /// <para>移除该路径上的既有条目，并追加一个携带给定字节的新内存条目。</para>
    /// </summary>
    /// <param name="path">Entry path to (re)create; the same normalization as in <see cref="Find"/> applies.</param>
    /// <param name="data">Payload bytes stored directly on the new entry. <para>直接存储在新条目上的负载字节。</para></param>
    /// <param name="metadata">
    /// Entry metadata, or <see langword="null"/> for a regular file with permission bits 0x81A4
    /// and link count 1.
    /// <para>条目元数据；为 <see langword="null"/> 时使用权限位 0x81A4、链接计数 1 的普通文件。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or white space.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或仅含空白。</para></exception>
    public void AddOrReplace(string path, byte[] data, ArchiveEntryMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Remove(path);
        _entries.Add(new ArchiveEntry
        {
            Name = Normalize(path),
            Data = data,
            Metadata = metadata ?? new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                UnixPermissions = 0x81A4,
                LinkCount = 1
            }
        });
    }

    /// <summary>
    /// Writes the current entries to a destination directory through <see cref="CpioExtractor"/>
    /// and its path-safety checks.
    /// <para>经由 <see cref="CpioExtractor"/> 及其路径安全检查，把当前条目写入目标目录。</para>
    /// </summary>
    /// <param name="destinationDirectory">
    /// Directory that bounds every extracted path.
    /// <para>约束每条解包路径的目标目录。</para>
    /// </param>
    /// <param name="options">
    /// Extraction behaviour; <see langword="null"/> uses the <see cref="ExtractOptions"/> defaults.
    /// <para>解包行为；为 <see langword="null"/> 时使用 <see cref="ExtractOptions"/> 默认值。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// An entry path escapes the destination, traverses a symbolic link, or is absolute under a
    /// rejecting policy.
    /// <para>条目路径逃出目标目录、穿过符号链接，或在拒绝策略下为绝对路径。</para>
    /// </exception>
    public void ExtractToDirectory(string destinationDirectory, ExtractOptions? options = null)
        => new CpioExtractor().ExtractToDirectory(_entries, destinationDirectory, options);

    /// <summary>
    /// Writes all current entries to an output stream in the given archive format; the stream stays
    /// owned by the caller.
    /// <para>按指定归档格式把全部当前条目写入输出流；流的所有权仍归调用方。</para>
    /// </summary>
    /// <param name="output">
    /// Writable target stream advanced from its current position.
    /// <para>从当前位置开始写入的可写目标流。</para>
    /// </param>
    /// <param name="format">
    /// On-disk format to emit; <see cref="ArchiveFormat.Tar"/> and <see cref="ArchiveFormat.Ustar"/>
    /// use the TAR writer, every other value the CPIO writer.
    /// <para>要写出的磁盘格式；<see cref="ArchiveFormat.Tar"/> 与 <see cref="ArchiveFormat.Ustar"/>
    /// 由 TAR 写出器处理，其余取值由 CPIO 写出器处理。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public void Save(Stream output, ArchiveFormat format = ArchiveFormat.NewAscii)
        => _archive.SaveAll(output, _entries, format);

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }
}