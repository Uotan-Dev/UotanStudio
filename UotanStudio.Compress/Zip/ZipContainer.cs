using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace UotanStudio.Compress.Zip;

/// <summary>
/// Opens and creates the ZIP archives used by this product, and owns the shared entry bounds, entry-name rules and
/// entry-copy helper.
/// <para>打开并创建本产品使用的 ZIP 归档，并集中提供共用的条目上限、条目名规则与条目复制辅助方法。</para>
/// </summary>
public static class ZipContainer
{
    private const int CopyBufferBytes = 81920;

    /// <summary>Opens an existing ZIP archive on disk for read-only access.
    /// <para>以只读方式打开磁盘上已存在的 ZIP 归档。</para></summary>
    /// <param name="path">Archive path opened with <see cref="FileShare.Read"/>; the file itself is never modified.
    /// <para>以 <see cref="FileShare.Read"/> 打开的归档路径；该文件本身不会被修改。</para></param>
    /// <param name="options">Optional entry-count and size bounds checked before the archive is returned; <see langword="null"/> applies no additional bound.
    /// <para>可选的条目数与大小上限，在返回归档前校验；为 <see langword="null"/> 时不施加额外上限。</para></param>
    /// <returns>The open archive; disposing it also closes the underlying file.
    /// <para>已打开的归档；释放它会同时关闭底层文件。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or whitespace.
    /// <para><paramref name="path"/> 为 <see langword="null"/>、空或空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> carries a negative bound.
    /// <para><paramref name="options"/> 含有负数上限。</para></exception>
    /// <exception cref="InvalidDataException">The archive layout is invalid, or it exceeds a bound in <paramref name="options"/>.
    /// <para>归档布局非法，或超出 <paramref name="options"/> 中的上限。</para></exception>
    /// <exception cref="IOException">The file cannot be opened.
    /// <para>文件无法打开。</para></exception>
    public static ZipArchive OpenRead(string path, ZipContainerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateOptions(options);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            EnforceReadBounds(archive, options);
            return archive;
        }
        catch
        {
            if (archive is null)
            {
                stream.Dispose();
            }
            else
            {
                archive.Dispose();
            }

            throw;
        }
    }

    /// <summary>Opens a ZIP archive held by an existing stream for reading.
    /// <para>以读取方式打开由现有流承载的 ZIP 归档。</para></summary>
    /// <param name="stream">Readable, seekable stream positioned for the archive; it is closed on dispose unless <paramref name="leaveOpen"/> is set.
    /// <para>定位在归档起始处、可读且可定位的流；除非设置 <paramref name="leaveOpen"/>，释放归档时会关闭该流。</para></param>
    /// <param name="leaveOpen"><see langword="true"/> to keep <paramref name="stream"/> open after the archive is disposed.
    /// <para>为 <see langword="true"/> 时在归档释放后保持 <paramref name="stream"/> 打开。</para></param>
    /// <param name="options">Optional entry-count and size bounds checked before the archive is returned; <see langword="null"/> applies no additional bound.
    /// <para>可选的条目数与大小上限，在返回归档前校验；为 <see langword="null"/> 时不施加额外上限。</para></param>
    /// <returns>The open archive; disposing it honours <paramref name="leaveOpen"/>.
    /// <para>已打开的归档；释放时遵循 <paramref name="leaveOpen"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> carries a negative bound.
    /// <para><paramref name="options"/> 含有负数上限。</para></exception>
    /// <exception cref="InvalidDataException">The archive layout is invalid, or it exceeds a bound in <paramref name="options"/>.
    /// <para>归档布局非法，或超出 <paramref name="options"/> 中的上限。</para></exception>
    public static ZipArchive OpenRead(Stream stream, bool leaveOpen = false, ZipContainerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateOptions(options);
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        try
        {
            EnforceReadBounds(archive, options);
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>Creates a new ZIP archive file.
    /// <para>创建一个新的 ZIP 归档文件。</para></summary>
    /// <param name="path">Destination path created with <see cref="FileMode.CreateNew"/>; an existing file is never replaced.
    /// <para>以 <see cref="FileMode.CreateNew"/> 创建的目标路径；已存在的文件不会被替换。</para></param>
    /// <param name="options">Bounds for the caller to pair with the same value later passed to <see cref="OpenRead(string, ZipContainerOptions?)"/>; this overload cannot enforce them because the archive is still empty and is returned to the caller for writing.
    /// <para>供调用方随后传给 <see cref="OpenRead(string, ZipContainerOptions?)"/> 的同一组上限；该重载无法执行这些上限，因为归档仍为空并交回调用方写入。</para></param>
    /// <returns>The writable archive; disposing it closes the created file.
    /// <para>可写归档；释放它会关闭已创建的文件。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or whitespace.
    /// <para><paramref name="path"/> 为 <see langword="null"/>、空或空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> carries a negative bound.
    /// <para><paramref name="options"/> 含有负数上限。</para></exception>
    /// <exception cref="IOException">The destination exists or cannot be created.
    /// <para>目标已存在或无法创建。</para></exception>
    public static ZipArchive Create(string path, ZipContainerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateOptions(options);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Creates a ZIP archive over an existing writable stream.
    /// <para>在现有可写流之上创建 ZIP 归档。</para></summary>
    /// <param name="stream">Writable stream receiving the archive; it is closed on dispose unless <paramref name="leaveOpen"/> is set.
    /// <para>接收归档的可写流；除非设置 <paramref name="leaveOpen"/>，释放归档时会关闭该流。</para></param>
    /// <param name="leaveOpen"><see langword="true"/> to keep <paramref name="stream"/> open after the archive is disposed.
    /// <para>为 <see langword="true"/> 时在归档释放后保持 <paramref name="stream"/> 打开。</para></param>
    /// <param name="options">Bounds for the caller to pair with the same value later passed to <see cref="OpenRead(Stream, bool, ZipContainerOptions?)"/>; this overload cannot enforce them because the archive is still empty and is returned to the caller for writing.
    /// <para>供调用方随后传给 <see cref="OpenRead(Stream, bool, ZipContainerOptions?)"/> 的同一组上限；该重载无法执行这些上限，因为归档仍为空并交回调用方写入。</para></param>
    /// <returns>The writable archive; disposing it honours <paramref name="leaveOpen"/>.
    /// <para>可写归档；释放时遵循 <paramref name="leaveOpen"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> carries a negative bound.
    /// <para><paramref name="options"/> 含有负数上限。</para></exception>
    public static ZipArchive Create(Stream stream, bool leaveOpen = false, ZipContainerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateOptions(options);
        return new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen);
    }

    /// <summary>Archives the regular files below a directory into a new ZIP archive.
    /// <para>把目录下的常规文件打包为新的 ZIP 归档。</para></summary>
    /// <param name="sourceDirectory">Existing directory whose files are stored with <c>/</c>-separated relative names; empty directories are not represented.
    /// <para>已存在的源目录，其文件以 <c>/</c> 分隔的相对名称写入；空目录不会被表示。</para></param>
    /// <param name="destinationArchive">Archive path created with <see cref="FileMode.CreateNew"/>; an existing file is never replaced and a failed run deletes the partially written archive.
    /// <para>以 <see cref="FileMode.CreateNew"/> 创建的归档路径；已存在的文件不会被替换，失败时删除写了一半的归档。</para></param>
    /// <param name="options">Optional bounds checked while entries are written; <see langword="null"/> applies no additional bound.
    /// <para>可选的写入期上限；为 <see langword="null"/> 时不施加额外上限。</para></param>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> or <paramref name="destinationArchive"/> is <see langword="null"/>, empty or whitespace.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="destinationArchive"/> 为 <see langword="null"/>、空或空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> carries a negative bound.
    /// <para><paramref name="options"/> 含有负数上限。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDirectory"/> does not exist.
    /// <para><paramref name="sourceDirectory"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">A file name is not a valid relative entry path, or the files exceed a bound in <paramref name="options"/>.
    /// <para>某个文件名不是合法的相对条目路径，或文件超出 <paramref name="options"/> 中的上限。</para></exception>
    /// <exception cref="IOException"><paramref name="destinationArchive"/> exists, a linked entry is present, or the file system operation failed.
    /// <para><paramref name="destinationArchive"/> 已存在、存在链接条目，或文件系统操作失败。</para></exception>
    public static void CreateFromDirectory(string sourceDirectory, string destinationArchive, ZipContainerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationArchive);
        ValidateOptions(options);

        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The source directory does not exist: {source}");
        }

        var destination = Path.GetFullPath(destinationArchive);
        var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteDirectory(archive, source, destination, options);
            }
        }
        catch
        {
            stream.Dispose();
            DeleteArchive(destination);
            throw;
        }

        stream.Dispose();
    }

    /// <summary>Normalizes an entry name to the archive form used by this product.
    /// <para>把条目名规范化为本产品使用的归档形式。</para></summary>
    /// <param name="entryName">Relative entry name with either separator; it must name an entry below the archive root.
    /// <para>相对条目名，可含任一种分隔符；必须指向归档根之下的条目。</para></param>
    /// <returns>The name with <c>\</c> replaced by <c>/</c>.
    /// <para>把 <c>\</c> 替换为 <c>/</c> 后的名称。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="entryName"/> is empty, whitespace, rooted, contains a drive letter or colon, or has an empty, <c>.</c> or <c>..</c> segment.
    /// <para><paramref name="entryName"/> 为空、空白、以根开头、含驱动器号或冒号，或含空段、<c>.</c>、<c>..</c> 段。</para></exception>
    public static string NormalizeEntryPath(string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        var normalized = entryName.Replace('\\', '/');
        if (normalized.Contains(':'))
        {
            throw new ArgumentException(
                "A ZIP entry path cannot contain a drive letter or stream separator.", nameof(entryName));
        }

        if (normalized[0] == '/'
            || normalized.Split('/').Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException(
                "A ZIP entry path must be relative and cannot contain empty, '.' or '..' segments.",
                nameof(entryName));
        }

        return normalized;
    }

    /// <summary>Copies one archive entry to a destination stream with declared-length and optional size bounds.
    /// <para>按声明长度与可选大小上限把单个归档条目复制到目标流。</para></summary>
    /// <param name="entry">Entry to read; it must not already be open.
    /// <para>要读取的条目；不得已处于打开状态。</para></param>
    /// <param name="destination">Writable target receiving the entry bytes; it is neither flushed nor closed.
    /// <para>接收条目字节的可写目标；不会被刷新，也不会被关闭。</para></param>
    /// <param name="maximumBytes">Optional upper bound for the copied bytes, in bytes; negative values are rejected and <see langword="null"/> applies no bound beyond the declared entry length.
    /// <para>可选的复制字节上限（字节）；负值会被拒绝，为 <see langword="null"/> 时只有条目声明长度构成上限。</para></param>
    /// <param name="ct">Cancellation token checked between reads.
    /// <para>在读取之间检查的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="entry"/> 或 <paramref name="destination"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.
    /// <para><paramref name="maximumBytes"/> 为负数。</para></exception>
    /// <exception cref="InvalidDataException">The declared length or the produced bytes exceed <paramref name="maximumBytes"/>, or the entry produces more bytes than it declares.
    /// <para>声明长度或实际字节超出 <paramref name="maximumBytes"/>，或条目产出的字节多于其声明值。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.
    /// <para><paramref name="ct"/> 被取消。</para></exception>
    public static void CopyEntryTo(ZipArchiveEntry entry, Stream destination, long? maximumBytes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "The byte bound cannot be negative.");
        }

        if (maximumBytes is { } declaredBound && entry.Length > declaredBound)
        {
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' declares {entry.Length} bytes; the bound is {declaredBound}.");
        }

        using var source = entry.Open();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long copied = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                copied += read;
                if (maximumBytes is { } maximum && copied > maximum)
                {
                    throw new InvalidDataException(
                        $"ZIP entry '{entry.FullName}' produced more than {maximum} bytes.");
                }

                if (entry.Length >= 0 && copied > entry.Length)
                {
                    throw new InvalidDataException(
                        $"ZIP entry '{entry.FullName}' produced more bytes than its declared length.");
                }

                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnforceReadBounds(ZipArchive archive, ZipContainerOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.MaxEntries is { } maxEntries && archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException(
                $"The ZIP archive holds {archive.Entries.Count} entries; the bound is {maxEntries}.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var length = entry.Length;
            if (options.MaxEntryBytes is { } maxEntryBytes && length > maxEntryBytes)
            {
                throw new InvalidDataException(
                    $"ZIP entry '{entry.FullName}' declares {length} bytes; the per-entry bound is {maxEntryBytes}.");
            }

            if (options.MaxTotalBytes is not { } maxTotalBytes)
            {
                continue;
            }

            if (length > maxTotalBytes - total)
            {
                throw new InvalidDataException($"The ZIP entries exceed the total bound of {maxTotalBytes} bytes.");
            }

            total += length;
        }
    }

    private static void ValidateOptions(ZipContainerOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.MaxEntries is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options, "MaxEntries cannot be negative.");
        }

        if (options.MaxEntryBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options, "MaxEntryBytes cannot be negative.");
        }

        if (options.MaxTotalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options, "MaxTotalBytes cannot be negative.");
        }
    }

    private static void WriteDirectory(ZipArchive archive, string sourceDirectory, string destinationArchive,
        ZipContainerOptions? options)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var files = new List<string>();
        CollectFiles(sourceDirectory, destinationArchive, comparison, files);
        files.Sort(StringComparer.Ordinal);
        if (options?.MaxEntries is { } maxEntries && files.Count > maxEntries)
        {
            throw new InvalidDataException(
                $"The source directory holds {files.Count} files; the bound is {maxEntries}.");
        }

        long total = 0;
        foreach (var file in files)
        {
            var name = NormalizeEntryPath(Path.GetRelativePath(sourceDirectory, file));
            var length = new FileInfo(file).Length;
            if (options?.MaxEntryBytes is { } maxEntryBytes && length > maxEntryBytes)
            {
                throw new InvalidDataException(
                    $"'{name}' declares {length} bytes; the per-entry bound is {maxEntryBytes}.");
            }

            if (options?.MaxTotalBytes is { } maxTotalBytes)
            {
                if (length > maxTotalBytes - total)
                {
                    throw new InvalidDataException(
                        $"The source directory exceeds the total bound of {maxTotalBytes} bytes.");
                }

                total += length;
            }

            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            var modified = File.GetLastWriteTime(file);
            if (modified.Year is >= 1980 and <= 2107)
            {
                entry.LastWriteTime = modified;
            }

            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = entry.Open();
            input.CopyTo(output, CopyBufferBytes);
        }
    }

    private static void CollectFiles(string directory, string destinationArchive, StringComparison comparison,
        List<string> files)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(path, destinationArchive, comparison))
            {
                continue;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Cannot archive a linked entry: {path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                CollectFiles(path, destinationArchive, comparison, files);
                continue;
            }

            files.Add(path);
        }
    }

    private static void DeleteArchive(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Appends a comment to an archive that has none, growing the end-of-central-directory record by the comment length.
    /// <para>向尚无注释的归档追加注释，使中央目录结束记录按注释长度增长。</para>
    /// </summary>
    /// <param name="archive">Seekable, writable archive positioned anywhere; it is left at the end of the comment.
    /// <para>可定位、可写的归档，起始位置不限；调用后停在注释末尾。</para></param>
    /// <param name="comment">Comment bytes; they must not contain the end-record signature, because readers scan
    /// backwards for that signature and would find the comment instead of the record.
    /// <para>注释字节；不得包含结束记录签名，因为读取器会向后扫描该签名并找到注释而不是记录。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="archive"/> is <see langword="null"/>.
    /// <para><paramref name="archive"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException">The archive is not seekable and writable.
    /// <para>归档不可定位或不可写。</para></exception>
    /// <exception cref="InvalidDataException">The archive is too short, its end record is missing, it already has a
    /// comment, or the comment carries the end-record signature. <para>归档过短、缺少结束记录、已有注释，或注释含结束记录签名。</para></exception>
    public static void AppendArchiveComment(Stream archive, ReadOnlySpan<byte> comment)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.CanSeek || !archive.CanWrite)
        {
            throw new NotSupportedException("Appending a ZIP comment requires a seekable writable archive.");
        }

        if (comment.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("The ZIP comment exceeds the 16-bit length field.");
        }

        if (comment.IndexOf("PK\u0005\u0006"u8) >= 0)
        {
            throw new InvalidDataException("Spurious ZIP end marker in the comment.");
        }

        const int endRecordLength = 22;
        const uint endRecordSignature = 0x06054b50;
        if (archive.Length < endRecordLength)
        {
            throw new InvalidDataException("The archive is too short for a ZIP end-of-central-directory record.");
        }

        Span<byte> record = stackalloc byte[endRecordLength];
        archive.Position = archive.Length - endRecordLength;
        archive.ReadExactly(record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != endRecordSignature)
        {
            throw new InvalidDataException("The ZIP end-of-central-directory record is missing.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != 0)
        {
            throw new InvalidDataException("The archive already has a comment.");
        }

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)comment.Length);
        archive.Position = archive.Length - 2;
        archive.Write(length);
        archive.Write(comment);
    }

    /// <summary>
    /// Validates the single-disk layout a hardened OTA or patch bundle requires: one end record, a contiguous central
    /// directory that ends where the record starts, no ZIP64 placeholders, no encryption flag, stored entries only,
    /// and every local header before the directory.
    /// <para>校验加固的 OTA 或补丁包所要求的单盘布局：单一结束记录、紧接记录起点结束的连续中央目录、无 ZIP64 占位值、
    /// 无加密标志、仅 stored 条目，且每个本地头都位于目录之前。</para>
    /// </summary>
    /// <param name="archive">Seekable readable archive. <para>可定位可读的归档。</para></param>
    /// <param name="maximumDirectoryBytes">Upper bound on the central directory size. <para>中央目录大小上限。</para></param>
    /// <param name="maximumEntries">Upper bound on the entry count. <para>条目数量上限。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="archive"/> is <see langword="null"/>.
    /// <para><paramref name="archive"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The layout is ZIP64, multidisk, encrypted, compressed or malformed.
    /// <para>布局为 ZIP64、多盘、加密、已压缩或结构无效。</para></exception>
    public static void ValidateSingleDiskLayout(Stream archive, long maximumDirectoryBytes, int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.CanSeek || !archive.CanRead)
        {
            throw new NotSupportedException("Validating a ZIP layout requires a seekable readable archive.");
        }

        if (archive.Length < 22)
        {
            throw new InvalidDataException("Patch bundle end record is missing.");
        }

        var tail = new byte[(int)Math.Min(archive.Length, 65557)];
        archive.Position = archive.Length - tail.Length;
        archive.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054B50
                && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length)
            {
                end = index;
                break;
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException("Patch bundle end record is missing.");
        }

        var record = tail.AsSpan(end);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count
            || count is 0 || count > maximumEntries || size > maximumDirectoryBytes
            || (long)offset + size != archive.Length - tail.Length + end)
        {
            throw new InvalidDataException("Patch bundle directory is oversized, ZIP64, multidisk or malformed.");
        }

        archive.Position = offset;
        var directory = new byte[(int)size];
        archive.ReadExactly(directory);
        var cursor = 0;
        for (var index = 0; index < count; index++)
        {
            if (directory.Length - cursor < 46)
            {
                throw new InvalidDataException("Truncated patch bundle directory.");
            }

            var header = directory.AsSpan(cursor);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50
                || (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 1) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[42..]) >= offset)
            {
                throw new InvalidDataException("Unsupported patch bundle directory entry.");
            }

            cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (cursor > directory.Length)
            {
                throw new InvalidDataException("Patch bundle directory entry exceeds its range.");
            }
        }

        if (cursor != directory.Length)
        {
            throw new InvalidDataException("Patch bundle directory count is inconsistent.");
        }
    }
}