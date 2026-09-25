using SharpCompress.Archives;
using SharpCompress.Readers;
using UotanStudio.Firmware.BoundedContexts.Archive.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Detection;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Browsing;

/// <summary>
/// Lists and extracts archives or compressed files without materializing payloads in memory.
/// <para>列出并解包归档或压缩文件，且不会把负载整体物化到内存。</para>
/// </summary>
public sealed class ArchiveBrowser : IDisposable
{
    private readonly FileStream _source;
    private readonly ArchiveFormat? _archiveFormat;
    private readonly IArchive? _sharpArchive;
    private readonly string _memberName;

    /// <summary>
    /// Maximum decompressed scratch-file size; the file is deleted when the browser closes.
    /// <para>解压临时文件的最大容量；浏览器关闭时该文件会被删除。</para>
    /// </summary>
    public const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// A normalized member with no retained payload data.
    /// <para>已规范化、不保留任何负载数据的条目。</para>
    /// </summary>
    /// <param name="Path">Archive-relative normalized member path using forward slashes.
    /// <para>使用正斜杠的归档内规范化相对路径。</para></param>
    /// <param name="Size">Declared payload size in bytes; 0 for directories in backend archives.
    /// <para>声明的负载字节数；后端归档中的目录为 0。</para></param>
    /// <param name="Kind">Normalized entry type; backend archives yield only regular files and directories.
    /// <para>规范化条目类型；后端归档只产生普通文件与目录。</para></param>
    public sealed record Member(string Path, long Size, CpioFileType Kind);

    /// <summary>
    /// The detected container and optional compression format.
    /// <para>检测到的容器类型以及可选的压缩格式。</para>
    /// </summary>
    /// <value>
    /// Uppercase display name such as <c>ZIP</c>, <c>CPIO</c>, <c>TAR</c> or <c>CPIO (Gzip)</c>;
    /// <c>File</c> names a plain single-file payload.
    /// <para>大写显示名，如 <c>ZIP</c>、<c>CPIO</c>、<c>TAR</c> 或 <c>CPIO (Gzip)</c>；
    /// 普通单文件负载显示为 <c>File</c>。</para>
    /// </value>
    public string FormatName { get; }

    /// <summary>
    /// Validated members in archive order.
    /// <para>按归档顺序排列、已通过校验的条目。</para>
    /// </summary>
    /// <value>Every member enumerated while constructing the browser; empty when the archive holds no members.
    /// <para>构造浏览器期间枚举出的全部条目；归档无条目时为空列表。</para></value>
    public IReadOnlyList<Member> Members { get; }

    private ArchiveBrowser(FileStream source, ArchiveFormat? archiveFormat, IArchive? sharpArchive,
        string memberName, string formatName, CancellationToken cancellationToken, long maximumMemberBytes)
    {
        _source = source;
        _archiveFormat = archiveFormat;
        _sharpArchive = sharpArchive;
        _memberName = memberName;
        FormatName = formatName;
        var members = new List<Member>();
        var nameCharacters = 0L;
        var expandedBytes = 0L;
        foreach (var entry in ReadEntries(cancellationToken))
        {
            if (entry.Name.Length == 0) continue;
            if (entry.DataLength > maximumMemberBytes)
                throw new InvalidDataException($"Archive member '{entry.Name}' exceeds the browsing limit.");
            try { expandedBytes = checked(expandedBytes + entry.DataLength); }
            catch (OverflowException) { throw new InvalidDataException("Archive contents exceed the browsing limit."); }
            if (expandedBytes > maximumMemberBytes)
                throw new InvalidDataException("Archive contents exceed the browsing limit.");
            nameCharacters += entry.Name.Length;
            if (members.Count >= 100_000 || nameCharacters > 8 * 1024 * 1024)
                throw new InvalidDataException("Archive directory exceeds the browsing limit.");
            members.Add(new(entry.Name, entry.DataLength, entry.Metadata.FileType));
        }
        Members = members;
    }

    /// <summary>
    /// Recognizes archive extensions and compression extensions supported by the backend.
    /// Extensions not claimed fall back to a bounded container-magic sniff (ZIP/RAR/7z), so renamed
    /// or extension-less archives still open.
    /// <para>识别后端支持的归档扩展名与压缩扩展名。未被声明的扩展名回退到有界的容器魔数嗅探
    /// （ZIP/RAR/7z），因此被改名或无扩展名的归档仍可打开。</para>
    /// </summary>
    /// <param name="path">
    /// File path to test; only its extension and first bytes are consulted, and an unreadable
    /// file simply fails the magic check.
    /// <para>待检测的文件路径；只读取其扩展名与起始字节，不可读的文件直接判定为魔数不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the extension claims a supported archive or compression format,
    /// or the container magic matches; otherwise <see langword="false"/>.
    /// <para>扩展名声明了受支持的归档或压缩格式、或容器魔数匹配时返回 <see langword="true"/>，
    /// 否则返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool SupportsPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cpio" or ".tar"
        or ".zip" or ".apk" or ".rar" or ".7z" or ".arj" or ".ace" or ".arc"
        or ".lzw" or ".z"
        || CompressionForPath(path) != CompressionFormat.None
        || HasContainerMagic(path);

    /// <summary>Reads a bounded header and checks the ZIP/RAR/7z container magics; unreadable files return false.</summary>
    private static bool HasContainerMagic(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8, FileOptions.SequentialScan);
            var read = stream.Read(header);
            if (read < 4) return false;

            if (header[0] == (byte)'P' && header[1] == (byte)'K'
                && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
                && (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08))
            {
                return true;
            }

            if (read >= 7 && header[0] == (byte)'R' && header[1] == (byte)'a' && header[2] == (byte)'r'
                && header[3] == (byte)'!' && header[4] == 0x1A && header[5] == 0x07
                && (header[6] == 0x00 || header[6] == 0x01))
            {
                return true;
            }

            if (read >= 6 && header[0] == (byte)'7' && header[1] == (byte)'z' && header[2] == 0xBC
                && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static CompressionFormat CompressionForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tgz" => CompressionFormat.Gzip,
        ".txz" => CompressionFormat.Xz,
        ".tbz" or ".tbz2" => CompressionFormat.Bzip2,
        ".tlz" => CompressionFormat.Lzma,
        ".tzst" => CompressionFormat.Zstd,
        _ => CompressionFormats.FromExtension(path)
    };

    /// <summary>
    /// Opens an archive, spooling at most one compression layer to a bounded temporary file.
    /// <para>打开归档，至多将一层压缩数据中转写入有大小上限的临时文件。</para>
    /// </summary>
    /// <param name="path">
    /// Existing file to open; it is read with share-read and the browser owns the resulting
    /// source stream.
    /// <para>要打开的既有文件；以共享读方式读取，打开后的源流由浏览器持有。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the initial open and member enumeration.
    /// <para>取消初始打开与条目枚举。</para>
    /// </param>
    /// <param name="maximumExpandedBytes">
    /// Ceiling for the spooled scratch file and, implicitly, the total size of the enumerated
    /// members.
    /// <para>中转临时文件的容量上限，同时约束枚举出的条目总大小。</para>
    /// </param>
    /// <returns>
    /// An opened browser with the member list already enumerated.
    /// <para>已打开且完成条目枚举的浏览器实例。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The archive is malformed or unsupported, or a member or the whole directory exceeds the
    /// browsing limits.
    /// <para>归档损坏或不受支持，或单个条目与整体目录超出浏览上限。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// Decompressing the compression layer exceeds <paramref name="maximumExpandedBytes"/>.
    /// <para>解压压缩层时超出 <paramref name="maximumExpandedBytes"/>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已取消。</para>
    /// </exception>
    public static ArchiveBrowser Open(string path, CancellationToken cancellationToken = default,
        long maximumExpandedBytes = MaximumExpandedBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var compression = CompressionDetector.Detect(source);
            if (compression == CompressionFormat.None) compression = CompressionForPath(path);
            var memberName = Path.GetFileName(path);
            if (compression != CompressionFormat.None)
            {
                var scratch = new FileStream(Path.Combine(Path.GetTempPath(), $"uotan-browser-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                try { CompressionService.Decompress(source, scratch, compression, maximumExpandedBytes, cancellationToken); }
                catch { scratch.Dispose(); throw; }
                source.Dispose();
                source = scratch;
                source.Position = 0;
                memberName = Path.GetExtension(path).ToLowerInvariant() is ".tgz" or ".txz" or ".tbz" or ".tbz2" or ".tlz" or ".tzst"
                    ? Path.GetFileNameWithoutExtension(path) + ".tar" : Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(memberName)) memberName = "content";
            }

            IArchive? sharpArchive = null;
            if (IsSharpArchivePath(memberName))
            {
                source.Position = 0;
                try { sharpArchive = ArchiveFactory.OpenArchive(source, new ReaderOptions { LeaveStreamOpen = true }); }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
                { throw new InvalidDataException("The archive is malformed or unsupported.", ex); }
                try
                {
                    return new(source, archiveFormat: null, sharpArchive, memberName,
                        sharpArchive.Type.ToString().ToUpperInvariant(), cancellationToken, maximumExpandedBytes);
                }
                catch
                {
                    sharpArchive.Dispose();
                    throw;
                }
            }

            ArchiveFormat? format = null;
            try { format = new CpioArchive().DetectFormat(source); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                if (Path.GetExtension(memberName).Equals(".tar", StringComparison.OrdinalIgnoreCase))
                    format = ArchiveFormat.Tar;
                else if (compression == CompressionFormat.None || Path.GetExtension(memberName).Equals(".cpio", StringComparison.OrdinalIgnoreCase))
                    throw;
            }
            var container = format is ArchiveFormat.Tar or ArchiveFormat.Ustar ? "TAR" : format is not null ? "CPIO" : "File";
            return new(source, format, sharpArchive: null, memberName,
                compression == CompressionFormat.None ? container : $"{container} ({compression})",
                cancellationToken, maximumExpandedBytes);
        }
        catch { source.Dispose(); throw; }
    }

    /// <summary>
    /// Extracts the selected member or directory subtree using the archive backend's safe writer.
    /// <para>使用归档后端的安全写入器解出所选条目或目录子树。</para>
    /// </summary>
    /// <param name="relativePath">
    /// Member path or directory prefix to extract; a value that normalizes to empty (for example
    /// <c>.</c>) extracts every member.
    /// <para>要解出的条目路径或目录前缀；规范化后为空（例如 <c>.</c>）时解出全部条目。</para>
    /// </param>
    /// <param name="destination">
    /// Destination directory handed to the extractor, which confines every write beneath it.
    /// <para>传给解包器的目标目录，所有写入都被约束在其内部。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels entry enumeration during extraction.
    /// <para>取消解包期间的条目枚举。</para>
    /// </param>
    /// <exception cref="InvalidDataException">
    /// <paramref name="relativePath"/> is absolute or contains traversal, drive, or NUL segments.
    /// <para><paramref name="relativePath"/> 为绝对路径，或含上级回溯、盘符、NUL 等不安全片段。</para>
    /// </exception>
    public void Extract(string relativePath, string destination, CancellationToken cancellationToken = default)
    {
        var selected = Normalize(relativePath);
        var entries = ReadEntries(cancellationToken).Where(entry => entry.Name.Length > 0 &&
            (selected.Length == 0 || entry.Name == selected || entry.Name.StartsWith(selected + "/", StringComparison.Ordinal)));
        new CpioExtractor().ExtractToDirectory(entries, destination, new ExtractOptions
        {
            RestoreModificationTime = _archiveFormat is not null,
            RestorePermissions = _archiveFormat is not null
        });
    }

    private IEnumerable<ArchiveEntry> ReadEntries(CancellationToken cancellationToken)
    {
        if (_sharpArchive is not null)
        {
            foreach (var entry in _sharpArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Key)) continue;
                var name = Normalize(entry.Key);
                if (name.Length == 0) continue;
                var isDirectory = entry.IsDirectory;
                yield return new ArchiveEntry
                {
                    Name = name,
                    Data = [],
                    DataStreamFactory = isDirectory ? null : entry.OpenEntryStream,
                    DeclaredDataLength = isDirectory ? 0 : entry.Size,
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = isDirectory ? CpioFileType.Directory : CpioFileType.Regular,
                        Mode = isDirectory ? 0x41EDu : 0x81A4u,
                        ModificationTimeUnixSeconds = entry.LastModifiedTime is { } modified
                            ? new DateTimeOffset(modified.ToUniversalTime()).ToUnixTimeSeconds() : 0
                    }
                };
            }
            yield break;
        }

        _source.Position = 0;
        if (_archiveFormat is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ArchiveEntry
            {
                Name = Normalize(_memberName),
                Data = [],
                DataStream = _source,
                DeclaredDataLength = _source.Length,
                Metadata = new() { FileType = CpioFileType.Regular }
            };
            yield break;
        }
        foreach (var entry in new CpioArchive().ReadEntriesStreaming(_source, _archiveFormat))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ArchiveEntry
            {
                Name = Normalize(entry.Name),
                Metadata = entry.Metadata,
                Data = entry.Data,
                DataStream = entry.DataStream,
                DataStreamFactory = entry.DataStreamFactory,
                DeclaredDataLength = entry.DataLength,
                LinkName = entry.LinkName
            };
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"Unsafe archive member path: '{path}'.");
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".." || part.Contains(':') || part.Contains('\0')))
            throw new InvalidDataException($"Unsafe archive member path: '{path}'.");
        return string.Join('/', parts.Where(part => part != "."));
    }

    /// <summary>
    /// Releases the source and deletes any decompressed scratch file.
    /// <para>释放源流并删除解压产生的临时文件。</para>
    /// </summary>
    public void Dispose()
    {
        _sharpArchive?.Dispose();
        _source.Dispose();
    }

    private static bool IsSharpArchivePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".zip" or ".apk" or ".rar" or ".7z" or ".arj" or ".ace" or ".arc"
            or ".lzw" or ".z";
    }
}