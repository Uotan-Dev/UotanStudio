using UotanStudio.Firmware.BoundedContexts.Archive.Browsing;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.FileManagement;

internal sealed partial class ImageBrowserSession
{
    // EXT4 magic ends at byte 1082; EROFS/F2FS magic ends at byte 1028.
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// <para>镜像内文件系统格式。</para>
    /// The filesystem format of the image.
    /// </summary>
    private enum FilesystemKind
    {
        /// <summary>
        /// <para>未知格式。</para>
        /// Unknown format.
        /// </summary>
        Unknown,

        /// <summary>
        /// <para>ext4。</para>
        /// ext4.
        /// </summary>
        Ext4,

        /// <summary>
        /// <para>EROFS。</para>
        /// EROFS.
        /// </summary>
        Erofs,

        /// <summary>
        /// <para>F2FS。</para>
        /// F2FS.
        /// </summary>
        F2fs,

        /// <summary>
        /// <para>CPIO/TAR 归档。</para>
        /// CPIO/TAR archive.
        /// </summary>
        Cpio,
    }

    /// <summary>
    /// <para>按魔数识别镜像的文件系统格式（ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024）。
    /// 物理偏移未命中时，sparse 包装镜像（如 product.img）按逻辑偏移重新探测内部文件系统。</para>
    /// Detects the filesystem format of the image by magic (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024).
    /// When the physical offset misses, sparse-wrapped images (e.g. product.img) are re-probed at the logical offset.
    /// </summary>
    /// <param name="path">
    /// <para>镜像文件路径。</para>
    /// The image file path.
    /// </param>
    /// <returns>
    /// <para>识别出的文件系统格式；无法读取或未命中魔数时为 <see cref="FilesystemKind.Unknown"/>。</para>
    /// The detected filesystem kind; <see cref="FilesystemKind.Unknown"/> when unreadable or unmatched.
    /// </returns>
    private static FilesystemKind DetectFilesystem(string path)
    {
        try
        {
            // 先按物理偏移探测（raw 镜像直接命中）。
            // Probe the physical header first (raw images match directly).
            var kind = DetectKindFromHeader(FormatDetector.ReadHeader(path, FilesystemHeaderLength));
            if (kind != FilesystemKind.Unknown)
            {
                return kind;
            }

            // 物理头部未命中：sparse 包装的镜像按逻辑偏移重新探测内部文件系统。
            // Physical header missed: re-probe the logical header for sparse-wrapped images.
            if (IsSparseImage(path))
            {
                var logical = SparseReader.PeekLogicalRange(path, 0, FilesystemHeaderLength);
                kind = DetectKindFromHeader(logical);
                if (kind != FilesystemKind.Unknown)
                {
                    return kind;
                }
            }
        }
        catch
        {
            // 读取失败按未知格式处理。
            // A read failure is treated as an unknown format.
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// <para>按头部字节中的文件系统魔数识别格式（ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024）。
    /// 长度不足或未命中时返回 <see cref="FilesystemKind.Unknown"/>。</para>
    /// Detects the filesystem kind from header bytes by magic (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024,
    /// f2fs 0xF2F52010@1024). Returns <see cref="FilesystemKind.Unknown"/> when too short or unmatched.
    /// </summary>
    private static FilesystemKind DetectKindFromHeader(byte[] header)
    {
        if (header.Length >= 1082
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
        {
            return FilesystemKind.Ext4;
        }

        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
        {
            return FilesystemKind.Erofs;
        }

        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
        {
            return FilesystemKind.F2fs;
        }

        // CPIO newc 魔数 "070701" 或 "070702"，CPIO bin 魔数 0x71C7（大端）。
        // CPIO newc magic "070701" or "070702", CPIO bin magic 0x71C7 (big-endian).
        if (header.Length >= 6)
        {
            var magic = System.Text.Encoding.ASCII.GetString(header, 0, 6);
            if (magic is "070701" or "070702" or "070707")
            {
                return FilesystemKind.Cpio;
            }
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>Detects an image and indexes its entries, returning ownership only after successful loading.</summary>
    internal static ImageBrowserSession Open(string path, CancellationToken ct, string unrecognizedMessage = "Unrecognized filesystem image.")
    {
        ct.ThrowIfCancellationRequested();

        // 幂等注册全部格式处理器（应用启动时通常已注册），与固件面板共用同一注册表语义。
        // Idempotently register every format handler (the app usually does at startup), sharing the
        // registry semantics with the firmware panel.
        FirmwareFormatRegistry.RegisterAll();

        if (ArchiveBrowser.SupportsPath(path)) return LoadCpio(path, ct);

        var kind = DetectFilesystem(path);
        // 输入是否为 sparse 包装镜像（如 product.img）：sparse 以逻辑流视图解析。
        // Whether the input is sparse-wrapped (e.g. product.img): sparse images are parsed via a
        // logical stream view.
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
            case FilesystemKind.Cpio:
                return LoadCpio(path, ct);
            default:
                throw new InvalidDataException(
                    unrecognizedMessage);
        }
    }

    /// <summary>Opens a logical sparse stream that owns the sparse file and its physical source handle.</summary>
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

    /// <summary>Opens and retains a raw EXT4 source before constructing its parser, so constructor failures can be cleaned up.</summary>
    private static ImageBrowserSession LoadExt4(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadExt4(stream, stream.Length, stream, ct);
    }

    /// <summary>Builds an EXT4 session, retaining the source as a fallback owner during construction and indexing.</summary>
    private static ImageBrowserSession LoadExt4(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new Ext4Parser(stream),
            parser => BuildExt4Result(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>Indexes EXT4 entries without constructing presentation nodes.</summary>
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

    /// <summary>Opens a raw EROFS source owned by the session; the stream parser leaves its source open.</summary>
    private static ImageBrowserSession LoadErofs(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadErofs(stream, stream.Length, stream, ct);
    }

    /// <summary>Builds an EROFS session that owns the externally supplied logical stream.</summary>
    private static ImageBrowserSession LoadErofs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new ErofsParser(stream),
            parser => BuildErofsResult(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>Indexes EROFS file entries; the presentation layer synthesizes intermediate directories.</summary>
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

    /// <summary>Opens and retains a raw F2FS source before constructing its parser.</summary>
    private static ImageBrowserSession LoadF2fs(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return LoadF2fs(stream, stream.Length, stream, ct);
    }

    /// <summary>Builds an F2FS session, retaining the source as a fallback owner during construction and indexing.</summary>
    private static ImageBrowserSession LoadF2fs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return LoadParser(() => new F2fsParser(stream),
            parser => BuildF2fsResult(parser, imageSize, ownedSource, ct), ownedSource);
    }

    /// <summary>Indexes F2FS entries without constructing presentation nodes.</summary>
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