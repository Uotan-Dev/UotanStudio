using System.IO.Compression;

namespace UotanStudio.Services.Projects;

/// <summary>Exports a solution directory through a temporary ZIP. 通过临时 ZIP 导出解决方案目录。</summary>
public static class ProjectArchiveService
{
    public static async Task ExportAsync(string root, string outputPath, CancellationToken cancellationToken = default)
        => await ExportCoreAsync(root, outputPath, null, cancellationToken);

    /// <summary>Exports the solution file and data directory so the package can be reopened after import.</summary>
    public static Task ExportPackageAsync(string solutionPath, string outputPath, CancellationToken cancellationToken = default)
        => ExportCoreAsync(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, outputPath, Path.GetFullPath(solutionPath), cancellationToken);

    private static async Task ExportCoreAsync(string root, string outputPath, string? solutionPath, CancellationToken cancellationToken)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        outputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var excludedHistory = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // Package mode archives only the solution file plus the referenced sub-project folders, so the
        // result is self-contained. Plain archive mode (no solution) walks the whole root directory.
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
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                if (solutionPath is not null)
                {
                    if ((File.GetAttributes(solutionPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot archive a linked solution.");
                    await using var solutionSource = new FileStream(solutionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (solutionSource.Length > 32 * 1024 * 1024) throw new InvalidDataException("Solution exceeds 32 MiB.");
                    await using var solutionOutput = archive.CreateEntry(Path.GetFileName(solutionPath)).Open();
                    await ProjectStreamCopy.CopyExactlyAsync(solutionSource, solutionOutput, solutionSource.Length, cancellationToken);
                }
                // Package mode archives only the solution file and the referenced sub-project folders so the
                // result is self-contained: entry names are relative to the solution directory and match the
                // snapshot's sub-project paths. Plain archive mode still walks the whole root directory.
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
                        // Executable history contains machine-local absolute paths and deleted files are excluded.
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