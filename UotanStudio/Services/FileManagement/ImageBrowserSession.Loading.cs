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
    // 逻辑头部预读长度：EXT4 魔数结束于 1082，EROFS/F2FS 魔数结束于 1028。
    // Logical header peek length: the EXT4 magic ends at byte 1082 and the EROFS/F2FS magic at 1028.
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
        /// <para>FAT12/FAT16/FAT32 卷。</para>
        /// A FAT12, FAT16 or FAT32 volume.
        /// </summary>
        Fat,

        /// <summary>
        /// <para>exFAT 卷。</para>
        /// An exFAT volume.
        /// </summary>
        ExFat,

        /// <summary>
        /// <para>CPIO/TAR 归档。</para>
        /// CPIO/TAR archive.
        /// </summary>
        Cpio,
    }

    /// <summary>
    /// <para>识别镜像的文件系统格式：物理布局一律交给 <see cref="FormatDetector"/> 的注册表处理器，
    /// 按处理器类型映射到会话内部 kind，宿主不再重复魔数字节比较。注册表只探测物理头部，
    /// sparse 包装镜像（如 product.img）的内层文件系统位于逻辑偏移上，此时 Detect 只会返回
    /// <see cref="SparseFormatHandler"/>，因此保留一次逻辑头部重探。</para>
    /// Detects the filesystem format of the image: the physical layout is decided by the registered
    /// <see cref="FormatDetector"/> handlers and mapped to a session kind by handler type, so the host keeps
    /// no duplicated magic-byte comparisons. The registry only probes the physical header, and the inner
    /// filesystem of a sparse-wrapped image (e.g. product.img) lives at the logical offset where detection
    /// can only return <see cref="SparseFormatHandler"/>, so one logical re-probe is kept.
    /// </summary>
    /// <param name="path">
    /// <para>镜像文件路径。</para>
    /// The image file path.
    /// </param>
    /// <returns>
    /// <para>识别出的文件系统格式；无法读取或未命中时为 <see cref="FilesystemKind.Unknown"/>。</para>
    /// The detected filesystem kind; <see cref="FilesystemKind.Unknown"/> when unreadable or unmatched.
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

            // 物理头部没有文件系统处理器认领：sparse 包装的镜像按逻辑头部重新探测内层文件系统。
            // No filesystem handler claimed the physical header: re-probe the logical header of a
            // sparse-wrapped image.
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
            // 读取失败按未知格式处理。
            // A read failure is treated as an unknown format.
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// <para>把注册表处理器映射为会话内部 kind；非文件系统或未支持的容器返回
    /// <see cref="FilesystemKind.Unknown"/>。</para>
    /// Maps a registry handler to the session kind; filesystems the session cannot browse report
    /// <see cref="FilesystemKind.Unknown"/>.
    /// </summary>
    private static FilesystemKind MapKind(IReadonlyFormatHandler? handler) => handler switch
    {
        Ext4FormatHandler => FilesystemKind.Ext4,
        ErofsFormatHandler => FilesystemKind.Erofs,
        F2fsFormatHandler => FilesystemKind.F2fs,
        CpioFormatHandler => FilesystemKind.Cpio,
        // FAT/exFAT 处理器对宿主内部不可见，按注册表 FormatName 判定。
        // The FAT/exFAT handlers are assembly-internal; match them by registry FormatName.
        _ when handler is not null && handler.FormatName == "FAT" => FilesystemKind.Fat,
        _ when handler is not null && handler.FormatName == "EXFAT" => FilesystemKind.ExFat,
        _ => FilesystemKind.Unknown,
    };

    /// <summary>
    /// <para>按 sparse 逻辑头部识别内层文件系统。ext4/EROFS/F2FS 复用注册表处理器的逻辑流判定
    /// （<see cref="IStreamUnpackableFormatHandler.CanHandleLogicalStream"/>），顺序与注册表一致。
    /// FAT 系列与 CPIO 的注册表处理器没有逻辑流入口，只能读取整卷，因此保留宿主侧判定：
    /// FAT/exFAT 复用库解析器的引导记录几何校验，sparse 逻辑头部只覆盖镜像前缀、无法给出真实
    /// 长度，故传入 null 跳过数据区上限，由解析器在装载时校验。</para>
    /// Identifies the inner filesystem from a sparse logical header. EXT4, EROFS and F2FS reuse the registry
    /// handlers' logical-stream check in registry order. The FAT family and CPIO handlers expose no logical
    /// stream entry point and only read a whole volume, so their checks stay here: the FAT geometry is
    /// reused from the Firmware parsers, and a sparse logical header only covers a prefix, so its unknown
    /// length is passed as null and the parser validates when the volume loads.
    /// </summary>
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

        // CPIO newc 魔数 "070701" 或 "070702"，CPIO bin 魔数 0x71C7（大端）。
        // CPIO newc magic "070701" or "070702", CPIO bin magic 0x71C7 (big-endian).
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
    /// <para>用注册表中该类型的处理器单例判定逻辑流，避免在宿主新建处理器实例；普通遍历注册表快照，
    /// 保持裁剪与 Native AOT 下的静态可分发。</para>
    /// Probes the logical stream with the registered singleton of this handler type so the host never
    /// constructs its own handler; the registry snapshot is walked with a plain loop to stay statically
    /// dispatchable under trimming and Native AOT.
    /// </summary>
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

    /// <summary>Indexes a FAT-family volume tree into browser entries.</summary>
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