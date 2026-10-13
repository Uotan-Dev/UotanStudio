using UotanStudio.Firmware.Archive.Browsing;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Fat;

namespace UotanStudio.Services.FileManagement;

internal sealed partial class ImageBrowserSession
{
    /// <summary>
    /// Extracts the selected entry (file/symlink/directory subtree) into the target directory.
    /// <para>将选中的条目（文件/符号链接/目录子树）提取到目标目录。</para>
    /// </summary>
    /// <param name="relativePath">
    /// The relative path of the selected entry inside the image.
    /// <para>选中条目在镜像内的相对路径。</para>
    /// </param>
    /// <param name="targetDirectory">
    /// The target directory.
    /// <para>目标目录。</para>
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
            case FatParser fat:
                ExtractFilesystem(relativePath, targetDirectory, fat.ReadFile);
                break;
            case ExFatParser exfat:
                ExtractFilesystem(relativePath, targetDirectory, exfat.ReadFile);
                break;
        }
    }

    /// <summary>
    /// Extracts an EXT4 or F2FS selection using its parser-owned file reader.
    /// <para>用解析器自带的文件读取方法提取 EXT4 或 F2FS 中的选中内容。</para>
    /// </summary>
    /// <param name="relativePath">Path of the selected entry inside the image.<para>选中条目在镜像内的相对路径。</para></param>
    /// <param name="targetDirectory">Directory the entries are written into.<para>写入这些条目的目标目录。</para></param>
    /// <param name="readFile">Reads one image file into the supplied stream.<para>把镜像中的单个文件读入给定的流。</para></param>
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

    /// <summary>
    /// Writes a shared filesystem entry, preserving the existing directory and symlink fallback policy.
    /// <para>写出共享的文件系统条目，沿用现有的目录创建与符号链接回退策略。</para>
    /// </summary>
    /// <param name="flat">The entry to write.<para>需要写出的条目。</para></param>
    /// <param name="targetDirectory">Directory the entry is written into.<para>写入该条目的目标目录。</para></param>
    /// <param name="readFile">Reads one image file into the supplied stream.<para>把镜像中的单个文件读入给定的流。</para></param>
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
    /// Extracts the selected entry from an EROFS parser: files are written to their relative path and a
    /// directory extracts its whole subtree.
    /// <para>从 EROFS 解析器提取选中条目：文件写出到相对路径，目录提取整棵子树。</para>
    /// </summary>
    /// <param name="parser">The EROFS parser supplying the file reads.<para>提供文件读取的 EROFS 解析器。</para></param>
    /// <param name="relativePath">Path of the selected entry inside the image.<para>选中条目在镜像内的相对路径。</para></param>
    /// <param name="targetDirectory">Directory the entries are written into.<para>写入这些条目的目标目录。</para></param>
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

        if (!matched || !_flatEntries.Any(e => e.Path == relativePath))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// Maps an ext4 entry kind to the unified <see cref="ImageBrowserEntryKind"/>.
    /// <para>将 ext4 条目类型映射为统一的 <see cref="ImageBrowserEntryKind"/>。</para>
    /// </summary>
    /// <param name="kind">The ext4 entry kind to translate.<para>需要转换的 ext4 条目类型。</para></param>
    /// <returns>The unified entry kind for the ext4 kind.<para>该 ext4 条目类型对应的统一条目类型。</para></returns>
    private static ImageBrowserEntryKind ToKind(Ext4Parser.Ext4EntryKind kind) => kind switch
    {
        Ext4Parser.Ext4EntryKind.Directory => ImageBrowserEntryKind.Directory,
        Ext4Parser.Ext4EntryKind.Symlink => ImageBrowserEntryKind.Symlink,
        _ => ImageBrowserEntryKind.File,
    };

    /// <summary>
    /// Maps an F2FS entry kind to the unified <see cref="ImageBrowserEntryKind"/>.
    /// <para>将 F2FS 条目类型映射为统一的 <see cref="ImageBrowserEntryKind"/>。</para>
    /// </summary>
    /// <param name="kind">The F2FS entry kind to translate.<para>需要转换的 F2FS 条目类型。</para></param>
    /// <returns>The unified entry kind for the F2FS kind.<para>该 F2FS 条目类型对应的统一条目类型。</para></returns>
    private static ImageBrowserEntryKind ToKind(F2fsParser.F2fsEntryKind kind) => kind switch
    {
        F2fsParser.F2fsEntryKind.Directory => ImageBrowserEntryKind.Directory,
        F2fsParser.F2fsEntryKind.Symlink => ImageBrowserEntryKind.Symlink,
        _ => ImageBrowserEntryKind.File,
    };

}