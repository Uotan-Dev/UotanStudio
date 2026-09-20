using UotanStudio.Firmware.BoundedContexts.Archive.Browsing;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;

namespace UotanStudio.Services.FileManagement;

internal sealed partial class ImageBrowserSession
{
    /// <summary>
    /// <para>将选中的条目（文件/符号链接/目录子树）提取到目标目录。</para>
    /// Extracts the selected entry (file/symlink/directory subtree) into the target directory.
    /// </summary>
    /// <param name="relativePath">
    /// <para>选中条目在镜像内的相对路径。</para>
    /// The relative path of the selected entry inside the image.
    /// </param>
    /// <param name="targetDirectory">
    /// <para>目标目录。</para>
    /// The target directory.
    /// </param>
    private void ExtractEntryCore(string relativePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        switch (_parser)
        {
            case ArchiveBrowser archive:
                archive.Extract(relativePath, targetDirectory);
                break;
            case Ext4Parser ext4:
                ExtractFilesystem(relativePath, targetDirectory, ext4.ReadFile);
                break;
            case ErofsParser erofs:
                ExtractErofs(erofs, relativePath, targetDirectory);
                break;
            case F2fsParser f2fs:
                ExtractFilesystem(relativePath, targetDirectory, f2fs.ReadFile);
                break;
            case Fat12ImageParser fat12:
                ExtractFilesystem(relativePath, targetDirectory, fat12.ReadFile);
                break;
        }
    }

    /// <summary>Extracts an EXT4 or F2FS selection using its parser-owned file reader.</summary>
    private void ExtractFilesystem(string relativePath, string targetDirectory, Action<string, Stream> readFile)
    {
        var prefix = relativePath + "/";
        var isDirectory = _flatEntries.Any(e => e.Path == relativePath && e.Kind == ImageBrowserEntryKind.Directory);

        foreach (var flat in _flatEntries)
        {
            if (flat.Path == relativePath)
            {
                if (!isDirectory)
                {
                    ExtractFilesystemEntry(flat, targetDirectory, readFile);
                }

                continue;
            }

            if (flat.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                ExtractFilesystemEntry(flat, targetDirectory, readFile);
            }
        }

        if (isDirectory)
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>Writes a shared filesystem entry, preserving the existing directory and symlink fallback policy.</summary>
    private static void ExtractFilesystemEntry(ImageBrowserEntry flat, string targetDirectory, Action<string, Stream> readFile)
    {
        var target = Path.Combine(targetDirectory, flat.Path.Replace('/', Path.DirectorySeparatorChar));
        if (flat.Kind == ImageBrowserEntryKind.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (flat.Kind == ImageBrowserEntryKind.Symlink)
        {
            // 符号链接目标为极短文本（快速符号链接 ≤ 60 字节），读入内存写出。
            // A symlink target is a short text (fast symlinks are ≤ 60 bytes); read it into memory.
            using var buffer = new MemoryStream();
            readFile(flat.Path, buffer);
            var targetText = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            try
            {
                File.CreateSymbolicLink(target, targetText);
            }
            catch
            {
                File.WriteAllText(target, targetText);
            }

            return;
        }

        using (var output = File.Create(target))
        {
            readFile(flat.Path, output);
        }
    }

    /// <summary>
    /// <para>从 EROFS 解析器提取选中条目：文件写出到相对路径，目录提取整棵子树。</para>
    /// Extracts the selected entry from an EROFS parser: files are written to their relative path and a
    /// directory extracts its whole subtree.
    /// </summary>
    private void ExtractErofs(ErofsParser parser, string relativePath, string targetDirectory)
    {
        var prefix = relativePath + "/";
        var matched = false;
        foreach (var flat in _flatEntries)
        {
            if (flat.Path != relativePath && !flat.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            matched = true;
            var target = Path.Combine(targetDirectory, flat.Path.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var output = File.Create(target);
            parser.ReadFile(flat.Path, output);
        }

        // 目录本身也要创建（含空目录情形）。
        // The directory itself must be created too (including empty directories).
        if (!matched || !_flatEntries.Any(e => e.Path == relativePath))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// <para>将 ext4 条目类型映射为统一的 <see cref="ImageBrowserEntryKind"/>。</para>
    /// Maps an ext4 entry kind to the unified <see cref="ImageBrowserEntryKind"/>.
    /// </summary>
    private static ImageBrowserEntryKind ToKind(Ext4Parser.Ext4EntryKind kind) => kind switch
    {
        Ext4Parser.Ext4EntryKind.Directory => ImageBrowserEntryKind.Directory,
        Ext4Parser.Ext4EntryKind.Symlink => ImageBrowserEntryKind.Symlink,
        _ => ImageBrowserEntryKind.File,
    };

    /// <summary>
    /// <para>将 F2FS 条目类型映射为统一的 <see cref="ImageBrowserEntryKind"/>。</para>
    /// Maps an F2FS entry kind to the unified <see cref="ImageBrowserEntryKind"/>.
    /// </summary>
    private static ImageBrowserEntryKind ToKind(F2fsParser.F2fsEntryKind kind) => kind switch
    {
        F2fsParser.F2fsEntryKind.Directory => ImageBrowserEntryKind.Directory,
        F2fsParser.F2fsEntryKind.Symlink => ImageBrowserEntryKind.Symlink,
        _ => ImageBrowserEntryKind.File,
    };

}
