using System.IO.Compression;

namespace UotanStudio.Services.Projects;

/// <summary>Exports a solution directory through a temporary ZIP. 通过临时 ZIP 导出解决方案目录。</summary>
internal static class ProjectArchiveService
{
    internal static async Task ExportAsync(string root, string outputPath, CancellationToken cancellationToken = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        outputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var temporary = outputPath + ".export-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.Equals(path, temporary, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(path, outputPath, StringComparison.OrdinalIgnoreCase)) continue;
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Cannot archive a linked entry: {path}");
                        var name = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (Path.GetFileName(path) is ".git" or ".deleted") continue;
                            archive.CreateEntry(name + "/");
                            pending.Push(path);
                            continue;
                        }
                        var info = new FileInfo(path);
                        var length = info.Length;
                        var modified = info.LastWriteTimeUtc;
                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                        await using var destination = entry.Open();
                        await UotanStudio.Firmware.SharedKernel.Streams.StreamCopy.CopyExactlyAsync(source, destination, length, cancellationToken);
                        info.Refresh();
                        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified)
                            throw new IOException($"File changed during export: {path}");
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}