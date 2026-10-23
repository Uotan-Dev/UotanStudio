namespace UotanStudio.Projects;

/// <summary>Prepares a project copy beside its destination and rolls back caught publication failures.</summary>
internal sealed class ProjectSaveAsTransaction : IDisposable
{
    private readonly string _backupRoot;
    private bool _committed;
    internal string SolutionPath { get; }
    internal string RootDirectory { get; }
    internal string StagingRoot { get; }
    internal string StagingSolution { get; }

    internal ProjectSaveAsTransaction(string solutionPath)
    {
        SolutionPath = Path.GetFullPath(solutionPath);
        var name = Path.GetFileNameWithoutExtension(SolutionPath);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            throw new ArgumentException("A non-empty project name is required.", nameof(solutionPath));
        RootDirectory = Path.GetFullPath(Path.ChangeExtension(SolutionPath, null));
        if (RootDirectory == SolutionPath) throw new ArgumentException("A solution file extension is required.", nameof(solutionPath));
        if (IsWithin(RootDirectory, SolutionPath))
            throw new ArgumentException("The solution file must be outside its project data directory.", nameof(solutionPath));
        var id = Guid.NewGuid().ToString("N");
        StagingRoot = RootDirectory + ".saveas-" + id;
        StagingSolution = SolutionPath + ".saveas-" + id;
        _backupRoot = RootDirectory + ".previous-" + id;
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(CanonicalPath(root), CanonicalPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Maps aliases to a relative destination without allowing an absolute or parent path.</summary>
    internal static string RelativePathWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(CanonicalPath(root), CanonicalPath(path));
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Save As requires sub-projects to be inside the source project directory.");
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            if (Directory.Exists(resolved))
                resolved = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        return resolved;
    }

    internal Task PrepareAsync(string? sourceRoot, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        Directory.CreateDirectory(StagingRoot);
        if (Directory.Exists(RootDirectory)) CopyTree(RootDirectory, StagingRoot, cancellationToken);
        if (sourceRoot is not null) CopyTree(sourceRoot, StagingRoot, cancellationToken);
    });

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(directory.Source);
            Directory.CreateDirectory(directory.Destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = RejectLink(entry);
                var target = Path.Combine(directory.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, target));
                else
                {
                    using var input = File.OpenRead(entry);
                    using var output = File.Create(target);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                    }
                }
            }
        }
    }

    private static FileAttributes RejectLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Save As cannot copy a symbolic link or junction: {path}");
        return attributes;
    }

    internal void Commit()
    {
        var movedOldRoot = false;
        var installedRoot = false;
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Move(RootDirectory, _backupRoot);
                movedOldRoot = true;
            }
            Directory.Move(StagingRoot, RootDirectory);
            installedRoot = true;
            File.Move(StagingSolution, SolutionPath, overwrite: true);
            _committed = true;
        }
        catch
        {
            if (installedRoot) Directory.Move(RootDirectory, StagingRoot);
            if (movedOldRoot) Directory.Move(_backupRoot, RootDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        Cleanup(StagingRoot, directory: true);
        Cleanup(StagingSolution, directory: false);
        if (_committed) Cleanup(_backupRoot, directory: true);
    }

    private static void Cleanup(string ownedPath, bool directory)
    {
        try
        {
            if (directory) { if (Directory.Exists(ownedPath)) Directory.Delete(ownedPath, recursive: true); }
            else File.Delete(ownedPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Projects", $"Temporary project cleanup failed ({ownedPath}): {ex.Message}"); }
    }
}