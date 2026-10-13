using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Browsing;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Fat;

namespace UotanStudio.Services.FileManagement;

internal sealed partial class ImageBrowserSession
{
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// The filesystem format of the image.
    /// <para>镜像内文件系统格式。</para>
    /// </summary>
    private enum FilesystemKind
    {
        /// <summary>
        /// Unknown format.
        /// <para>未知格式。</para>
        /// </summary>
        Unknown,

        /// <summary>
        /// ext4.
        /// <para>ext4。</para>
        /// </summary>
        Ext4,

        /// <summary>
        /// EROFS.
        /// <para>EROFS。</para>
        /// </summary>
        Erofs,

        /// <summary>
        /// F2FS.
        /// <para>F2FS。</para>
        /// </summary>
        F2fs,

        /// <summary>
        /// A FAT12, FAT16 or FAT32 volume.
        /// <para>FAT12/FAT16/FAT32 卷。</para>
        /// </summary>
        Fat,

        /// <summary>
        /// An exFAT volume.
        /// <para>exFAT 卷。</para>
        /// </summary>
        ExFat,

        /// <summary>
        /// CPIO/TAR archive.
        /// <para>CPIO/TAR 归档。</para>
        /// </summary>
        Cpio,
    }

    /// <summary>
    /// Detects the filesystem format of an image by probing its physical header and a sparse image's logical header.
    /// <para>通过物理头部探测镜像的文件系统格式，稀疏镜像再探测逻辑头部。</para>
    /// </summary>
    /// <param name="path">
    /// The image file path.
    /// <para>镜像文件路径。</para>
    /// </param>
    /// <returns>
    /// The detected filesystem kind; <see cref="FilesystemKind.Unknown"/> when unreadable or unmatched.
    /// <para>识别出的文件系统格式；无法读取或未命中时为 <see cref="FilesystemKind.Unknown"/>。</para>
    /// </returns>
    private static FilesystemKind DetectFilesystem(string path)
    {
        try
        {
            var kind = MapKind(FormatDetector.Detect(path));
            if (kind != FilesystemKind.Unknown)
            {
                return kind;
            }

            if (IsSparseImage(path))
            {
                kind = DetectKindFromLogicalHeader(SparseReader.PeekLogicalRange(path, 0, FilesystemHeaderLength));
                if (kind != FilesystemKind.Unknown)
                {
                    return kind;
                }
            }
        }
        catch
        {
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// Maps a registry handler to the session kind; unhandled formats report
    /// <see cref="FilesystemKind.Unknown"/>.
    /// <para>把注册表处理器映射为会话内部格式；未处理的格式返回 <see cref="FilesystemKind.Unknown"/>。</para>
    /// </summary>
    /// <param name="handler">The detected registry handler. <para>探测到的注册表处理器。</para></param>
    /// <returns>The session kind for that handler. <para>该处理器对应的会话格式。</para></returns>
    private static FilesystemKind MapKind(IReadonlyFormatHandler? handler) => handler switch
    {
        Ext4FormatHandler => FilesystemKind.Ext4,
        ErofsFormatHandler => FilesystemKind.Erofs,
        F2fsFormatHandler => FilesystemKind.F2fs,
        CpioFormatHandler => FilesystemKind.Cpio,
        _ when handler is not null && handler.FormatName == "FAT" => FilesystemKind.Fat,
        _ when handler is not null && handler.FormatName == "EXFAT" => FilesystemKind.ExFat,
        _ => FilesystemKind.Unknown,
    };

    /// <summary>
    /// Identifies the inner filesystem of a sparse image from the bytes of its logical header.
    /// <para>按稀疏镜像逻辑头部的字节识别其内层文件系统。</para>
    /// </summary>
    /// <param name="logical">
    /// The logical header bytes read from the sparse image.
    /// <para>从稀疏镜像读出的逻辑头部字节。</para>
    /// </param>
    /// <returns>
    /// The detected filesystem kind; <see cref="FilesystemKind.Unknown"/> when unmatched.
    /// <para>识别出的文件系统格式；未命中时为 <see cref="FilesystemKind.Unknown"/>。</para>
    /// </returns>
    private static FilesystemKind DetectKindFromLogicalHeader(byte[] logical)
    {
        using var stream = new MemoryStream(logical, writable: false);
        if (MatchesLogicalHandler<Ext4FormatHandler>(stream))
        {
            return FilesystemKind.Ext4;
        }

        if (MatchesLogicalHandler<ErofsFormatHandler>(stream))
        {
            return FilesystemKind.Erofs;
        }

        if (MatchesLogicalHandler<F2fsFormatHandler>(stream))
        {
            return FilesystemKind.F2fs;
        }

        if (logical.Length >= 512)
        {
            if (FatParser.IsFatBootRecord(logical))
            {
                return FilesystemKind.Fat;
            }

            if (ExFatParser.IsExFatBootRecord(logical))
            {
                return FilesystemKind.ExFat;
            }
        }

        if (logical.Length >= 6)
        {
            var magic = logical.AsSpan(0, 6);
            if (magic.SequenceEqual("070701"u8) || magic.SequenceEqual("070702"u8) || magic.SequenceEqual("070707"u8))
            {
                return FilesystemKind.Cpio;
            }
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// Probes a logical stream with the registered singleton of the requested handler type.
    /// <para>用注册表中该处理器类型的单例判定逻辑流。</para>
    /// </summary>
    /// <typeparam name="THandler">The handler type to probe. <para>要判定的处理器类型。</para></typeparam>
    /// <param name="logical">The logical stream to test. <para>待判定的逻辑流。</para></param>
    /// <returns>True when that handler accepts the logical stream. <para>该处理器接受此逻辑流时为 true。</para></returns>
    private static bool MatchesLogicalHandler<THandler>(Stream logical)
        where THandler : IStreamUnpackableFormatHandler
    {
        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            if (handler is THandler probe)
            {
                return probe.CanHandleLogicalStream(logical);
            }
        }

        return false;
    }

    /// <summary>
    /// Detects an image and indexes its entries, returning ownership only after successful loading.
    /// <para>探测镜像格式并索引其条目，仅在加载成功后返回所有权。</para>
    /// </summary>
    /// <param name="path">The image file path. <para>镜像文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="unrecognizedMessage">Message text for an unrecognized image format. <para>镜像格式未识别时的提示文本。</para></param>
    /// <returns>The session with its entries indexed. <para>已完成条目索引的会话。</para></returns>
    internal static ImageBrowserSession Open(string path, CancellationToken ct, string unrecognizedMessage = "Unrecognized filesystem image.")
    {
        ct.ThrowIfCancellationRequested();

        FirmwareFormatRegistry.RegisterAll();

        if (ArchiveBrowser.SupportsPath(path)) return LoadCpio(path, ct);

        var kind = DetectFilesystem(path);
        var isSparse = IsSparseImage(path);
        switch (kind)
        {
            case FilesystemKind.Ext4:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadExt4(stream, imageSize, owned, ct);
                }

                return LoadExt4(path, ct);
            case FilesystemKind.Erofs:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadErofs(stream, imageSize, owned, ct);
                }

                return LoadErofs(path, ct);
            case FilesystemKind.F2fs:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadF2fs(stream, imageSize, owned, ct);
                }

                return LoadF2fs(path, ct);
            case FilesystemKind.Fat:
                return LoadFat(path, ct);
            case FilesystemKind.ExFat:
                return LoadExFat(path, ct);
            case FilesystemKind.Cpio:
                return LoadCpio(path, ct);
            default:
                throw new InvalidDataException(
                    unrecognizedMessage);
        }
    }

    /// <summary>
    /// Opens a logical sparse stream that owns the sparse file and its physical source handle.
    /// <para>打开逻辑稀疏流，持有稀疏文件及其物理源句柄。</para>
    /// </summary>
    /// <param name="path">The sparse image file path. <para>稀疏镜像文件路径。</para></param>
    /// <returns>The logical stream, its length and the owned source. <para>逻辑流、其长度与被持有的源。</para></returns>
    private static (Stream Stream, long ImageSize, IDisposable OwnedSource) OpenSparseStream(string path)
    {
        var sparseFile = SparseReader.FromImageFile(path);
        try
        {
            var stream = new SparseReadStream(sparseFile);
            return (stream, stream.Length, stream);
        }
        catch { sparseFile.Dispose(); throw; }
    }

    /// <summary>
    /// Opens and retains a raw EXT4 source before constructing its parser, so constructor failures clean up.
    /// <para>先打开并保留原始 EXT4 源，再构造解析器，使构造失败时能够清理。</para>
    /// </summary>
    /// <param name="path">The raw EXT4 image file path. <para>原始 EXT4 镜像文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session built over the opened source. <para>基于已打开源构建的会话。</para></returns>
    private static ImageBrowserSession LoadExt4(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadExt4(stream, stream.Length, stream, ct);
    }

    /// <summary>
    /// Builds an EXT4 session, retaining the source as a fallback owner during construction and indexing.
    /// <para>构建 EXT4 会话，在构造与索引期间保留源作为后备所有者。</para>
    /// </summary>
    /// <param name="stream">The raw EXT4 source stream. <para>原始 EXT4 源流。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The built EXT4 session. <para>已构建的 EXT4 会话。</para></returns>
    private static ImageBrowserSession LoadExt4(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new Ext4Parser(stream),
            parser => BuildExt4Result(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>
    /// Indexes the entries of an EXT4 parser into a session without extra node construction.
    /// <para>把 EXT4 解析器的条目录入会话，不做额外的节点构建。</para>
    /// </summary>
    /// <param name="parser">The EXT4 parser to read entries from. <para>读取条目的 EXT4 解析器。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session carrying the indexed EXT4 entries. <para>携带已录入 EXT4 条目的会话。</para></returns>
    private static ImageBrowserSession BuildExt4Result(Ext4Parser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.ListFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<ImageBrowserEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new ImageBrowserEntry(entry.Path, entry.Size, ToKind(entry.Kind)));
        }

        return new ImageBrowserSession("EXT4", imageSize, parser, flat, ownedSource);
    }

    /// <summary>
    /// Opens a raw EROFS source owned by the session, whose stream parser leaves the source open.
    /// <para>打开由会话持有的原始 EROFS 源，其流式解析器不会关闭源。</para>
    /// </summary>
    /// <param name="path">The raw EROFS image file path. <para>原始 EROFS 镜像文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session built over the opened source. <para>基于已打开源构建的会话。</para></returns>
    private static ImageBrowserSession LoadErofs(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadErofs(stream, stream.Length, stream, ct);
    }

    /// <summary>
    /// Builds an EROFS session that owns the externally supplied logical stream.
    /// <para>构建 EROFS 会话，持有外部传入的逻辑流。</para>
    /// </summary>
    /// <param name="stream">The EROFS source stream. <para>EROFS 源流。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The built EROFS session. <para>已构建的 EROFS 会话。</para></returns>
    private static ImageBrowserSession LoadErofs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new ErofsParser(stream),
            parser => BuildErofsResult(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>
    /// Indexes the file entries of an EROFS parser into a session as a flat list.
    /// <para>把 EROFS 解析器的文件条目以扁平列表录入会话。</para>
    /// </summary>
    /// <param name="parser">The EROFS parser to read files from. <para>读取文件的 EROFS 解析器。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session carrying the indexed EROFS files. <para>携带已录入 EROFS 文件的会话。</para></returns>
    private static ImageBrowserSession BuildErofsResult(ErofsParser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.EnumerateFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<ImageBrowserEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new ImageBrowserEntry(entry.Path, entry.Size, ImageBrowserEntryKind.File));
        }

        return new ImageBrowserSession("EROFS", imageSize, parser, flat, ownedSource);
    }

    /// <summary>
    /// Opens and retains a raw F2FS source before constructing its parser.
    /// <para>先打开并保留原始 F2FS 源，再构造其解析器。</para>
    /// </summary>
    /// <param name="path">The raw F2FS image file path. <para>原始 F2FS 镜像文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session built over the opened source. <para>基于已打开源构建的会话。</para></returns>
    private static ImageBrowserSession LoadF2fs(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadF2fs(stream, stream.Length, stream, ct);
    }

    /// <summary>
    /// Builds an F2FS session, retaining the source as a fallback owner during construction and indexing.
    /// <para>构建 F2FS 会话，在构造与索引期间保留源作为后备所有者。</para>
    /// </summary>
    /// <param name="stream">The raw F2FS source stream. <para>原始 F2FS 源流。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The built F2FS session. <para>已构建的 F2FS 会话。</para></returns>
    private static ImageBrowserSession LoadF2fs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new F2fsParser(stream),
            parser => BuildF2fsResult(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>
    /// Indexes the entries of an F2FS parser into a session without extra node construction.
    /// <para>把 F2FS 解析器的条目录入会话，不做额外的节点构建。</para>
    /// </summary>
    /// <param name="parser">The F2FS parser to read entries from. <para>读取条目的 F2FS 解析器。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="ownedSource">The source handed to the session on success. <para>成功时移交给会话的源。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session carrying the indexed F2FS entries. <para>携带已录入 F2FS 条目的会话。</para></returns>
    private static ImageBrowserSession BuildF2fsResult(F2fsParser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.ListFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<ImageBrowserEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new ImageBrowserEntry(entry.Path, entry.Size, ToKind(entry.Kind)));
        }

        return new ImageBrowserSession("F2FS", imageSize, parser, flat, ownedSource);
    }

    private static ImageBrowserSession LoadCpio(string path, CancellationToken ct)
    {
        var browser = ArchiveBrowser.Open(path, ct);
        try
        {
            var entries = browser.Members.Select(entry => new ImageBrowserEntry(entry.Path, entry.Size, entry.Kind switch
            {
                CpioFileType.Directory => ImageBrowserEntryKind.Directory,
                CpioFileType.SymbolicLink => ImageBrowserEntryKind.Symlink,
                _ => ImageBrowserEntryKind.File
            })).ToList();
            return new ImageBrowserSession(browser.FormatName, new FileInfo(path).Length, browser, entries, null);
        }
        catch { browser.Dispose(); throw; }
    }

    private static ImageBrowserSession LoadFat(string path, CancellationToken ct)
    {
        var parser = new FatParser(path);
        try
        {
            return BuildFatSession(parser.Variant, parser.ImageBytes, parser, parser.ListFiles(), ct);
        }
        catch
        {
            parser.Dispose();
            throw;
        }
    }

    private static ImageBrowserSession LoadExFat(string path, CancellationToken ct)
    {
        var parser = new ExFatParser(path);
        try
        {
            return BuildFatSession(parser.Variant, parser.ImageBytes, parser, parser.ListFiles(), ct);
        }
        catch
        {
            parser.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Indexes a FAT-family volume tree into the entry list of a session.
    /// <para>把 FAT 系列卷的目录树录入会话的条目列表。</para>
    /// </summary>
    /// <param name="formatName">The reported FAT variant name. <para>上报的 FAT 变体名称。</para></param>
    /// <param name="imageSize">The image size in bytes. <para>镜像字节大小。</para></param>
    /// <param name="parser">The volume parser owned by the session. <para>由会话持有的卷解析器。</para></param>
    /// <param name="entries">The volume entries to index. <para>待录入的卷条目。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session carrying the indexed volume entries. <para>携带已录入卷条目的会话。</para></returns>
    private static ImageBrowserSession BuildFatSession(string formatName, long imageSize, IDisposable parser,
        IReadOnlyList<FatVolumeEntry> entries, CancellationToken ct)
    {
        var flat = new List<ImageBrowserEntry>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            flat.Add(new ImageBrowserEntry(entry.Path, entry.Size,
                entry.Kind == FatVolumeEntryKind.Directory
                    ? ImageBrowserEntryKind.Directory
                    : ImageBrowserEntryKind.File));
        }

        return new ImageBrowserSession(formatName, imageSize, parser, flat, null);
    }

    private static ImageBrowserSession LoadParser<TParser>(Func<TParser> create, Func<TParser, ImageBrowserSession> index, IDisposable? source)
        where TParser : class, IDisposable
    {
        TParser? parser = null;
        try
        {
            parser = create();
            return index(parser);
        }
        catch
        {
            try { parser?.Dispose(); }
            finally { source?.Dispose(); }
            throw;
        }
    }

    private static bool IsSparseImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var magic = FormatDetector.ReadHeader(path, 4);
            return magic.Length == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic)
                == SparseFormat.SparseHeaderMagic;
        }
        catch { return false; }
    }
}