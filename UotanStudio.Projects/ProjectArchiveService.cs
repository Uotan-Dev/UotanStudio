using System.IO.Compression;
using UotanStudio.Compress.Zip;

namespace UotanStudio.Projects;

/// <summary>
/// Exports a solution directory through a temporary ZIP.
/// <para>通过临时 ZIP 导出解决方案目录。</para>
/// </summary>
public static class ProjectArchiveService
{
    /// <summary>Archives an entire directory tree into a ZIP file written through a temporary sibling file.
    /// <para>把整个目录树归档到 ZIP 文件，写入过程经过同目录下的临时文件。</para></summary>
    /// <param name="root">Absolute or relative path of the directory to archive; it is resolved to a full path and must exist.
    /// <para>要归档的目录路径，绝对或相对均可；会被解析为全路径，且目录必须存在。</para></param>
    /// <param name="outputPath">Destination ZIP path, replaced only after the archive has been written completely; when it lies inside <paramref name="root"/> both it and its temporary file are skipped during traversal.
    /// <para>目标 ZIP 路径，只有在归档完整写入后才会被替换；当它位于 <paramref name="root"/> 之内时， 遍历过程会跳过它及其临时文件。</para></param>
    /// <param name="cancellationToken">Cancels before or during a directory or file read; the partial temporary archive is deleted.
    /// <para>在目录或文件读取之前或期间取消；未完成的临时归档会被删除。</para></param>
    /// <returns>A task that completes once the finished ZIP has been moved into place at <paramref name="outputPath"/>.
    /// <para>完成的 ZIP 已被移动到 <paramref name="outputPath"/> 后返回的任务。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> does not exist.
    /// <para><paramref name="root"/> 不存在。</para></exception>
    /// <exception cref="IOException">The root or any entry inside it is a reparse point, or a file changed size or timestamp while being copied.
    /// <para>根目录或其中任意条目为重解析点，或文件在复制过程中大小或时间戳发生变化。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public static async Task ExportAsync(string root, string outputPath, CancellationToken cancellationToken = default)
        => await ExportCoreAsync(root, outputPath, null, cancellationToken);

    /// <summary>Exports the solution file and data directory so the package can be reopened after import.
    /// <para>导出解决方案文件与数据目录，使包在导入后可以重新打开。</para></summary>
    /// <remarks>Only the directories referenced by the solution are archived, each relative to the solution's own directory; every sub-project file must exist inside that directory, and each sub-project's <c>.uotan/history.json</c> is left out. The solution file itself is stored at the ZIP root and is rejected when it exceeds 32 MiB or is a reparse point.
    /// <para>仅归档解决方案引用到的目录，且路径相对于解决方案所在目录；每个子项目文件都必须位于该目录内， 并且会排除各子项目的 <c>.uotan/history.json</c>。解决方案文件本身存放在 ZIP 根目录， 超过 32 MiB 或为重解析点时会被拒绝。</para></remarks>
    /// <param name="solutionPath">Path of the <c>.uotan</c> solution file whose references define the package contents.
    /// <para><c>.uotan</c> 解决方案文件路径，其引用决定包内容。</para></param>
    /// <param name="outputPath">Destination ZIP path, which must be outside the solution directory.
    /// <para>目标 ZIP 路径，必须位于解决方案目录之外。</para></param>
    /// <param name="cancellationToken">Cancels reading and writing; the partial temporary archive is deleted.
    /// <para>取消读取与写入；未完成的临时归档会被删除。</para></param>
    /// <returns>A task that completes once the portable package ZIP has been written to <paramref name="outputPath"/>.
    /// <para>可移植包 ZIP 已写入 <paramref name="outputPath"/> 后返回的任务。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The solution directory does not exist.
    /// <para>解决方案目录不存在。</para></exception>
    /// <exception cref="InvalidDataException">The solution cannot be read, or it is larger than 32 MiB.
    /// <para>解决方案无法读取，或其大小超过 32 MiB。</para></exception>
    /// <exception cref="IOException">A sub-project is missing, is not inside the data directory, has no directory of its own, the destination lies inside the project, a reparse point is reached, or a file changed during export.
    /// <para>子项目缺失、不在数据目录内、没有独立目录，目标位于项目内部，遇到重解析点，或文件在导出期间被修改。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    /// <example>
    /// <code>
    /// await ProjectArchiveService.ExportPackageAsync(
    /// @"D:\Firmware\Pixel.pixel.uotan", @"D:\Share\pixel.uotan.zip", cancellationToken);
    /// </code>
    /// </example>
    public static Task ExportPackageAsync(string solutionPath, string outputPath, CancellationToken cancellationToken = default)
        => ExportCoreAsync(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, outputPath, Path.GetFullPath(solutionPath), cancellationToken);

    private static async Task ExportCoreAsync(string root, string outputPath, string? solutionPath, CancellationToken cancellationToken)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        outputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var excludedHistory = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var includeRoots = new List<string>();
        if (solutionPath is not null)
        {
            var solution = await SolutionPersistenceService.LoadSolutionAsync(solutionPath, cancellationToken)
                ?? throw new InvalidDataException("Cannot read the solution to package.");
            foreach (var reference in solution.SubProjects ?? [])
            {
                var path = Path.GetFullPath(Path.Combine(root, reference.RelativePath));
                if (!ProjectSaveAsTransaction.IsWithin(root, path) || !File.Exists(path))
                    throw new IOException("A package requires all sub-projects to exist inside its data directory.");
                if (!ProjectValidation.IsSubProjectDirectory(root, Path.GetDirectoryName(path)!))
                    throw new IOException("A packaged sub-project requires its own directory.");
                includeRoots.Add(Path.GetDirectoryName(path)!);
                excludedHistory.Add(Path.Combine(Path.GetDirectoryName(path)!, ".uotan", "history.json"));
            }
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot archive a linked root.");
        if (solutionPath is not null && (ProjectSaveAsTransaction.IsWithin(root, outputPath)
            || string.Equals(solutionPath, outputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new IOException("The package destination must be outside the project.");
        var temporary = outputPath + ".export-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var archive = ZipContainer.Create(output, leaveOpen: true);
                if (solutionPath is not null)
                {
                    if ((File.GetAttributes(solutionPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot archive a linked solution.");
                    await using var solutionSource = new FileStream(solutionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (solutionSource.Length > 32 * 1024 * 1024) throw new InvalidDataException("Solution exceeds 32 MiB.");
                    await using var solutionOutput = archive.CreateEntry(Path.GetFileName(solutionPath)).Open();
                    await ProjectStreamCopy.CopyExactlyAsync(solutionSource, solutionOutput, solutionSource.Length, cancellationToken);
                }
                var pending = new Stack<string>();
                if (solutionPath is not null)
                {
                    foreach (var includeRoot in includeRoots) pending.Push(includeRoot);
                }
                else pending.Push(root);
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
                        if (solutionPath is not null && excludedHistory.Contains(Path.GetFullPath(path))) continue;
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
                        await ProjectStreamCopy.CopyExactlyAsync(source, destination, length, cancellationToken);
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