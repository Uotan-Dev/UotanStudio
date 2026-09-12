using System.IO.Compression;

namespace UotanStudio.Services.Projects;

/// <summary>Imports complete project packages with bounded extraction and directory publication.</summary>
public static class ProjectPackage
{
    /// <summary>Extracts into a new destination directory and returns its solution path. Existing destinations are never replaced.</summary>
    public static async Task<string> ImportAsync(string archivePath, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Package destination already exists.");
        var temporary = destination + ".import-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ProjectPackageIndex.Validate(archiveStream);
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > 100000) throw new InvalidDataException("Package has too many entries.");
                long remaining = 64L * 1024 * 1024 * 1024;
                long remainingNames = 8L * 1024 * 1024;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    remainingNames -= name.Length;
                    if (remainingNames < 0 || string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':')
                        || name.Split('/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
                            || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || IsDeviceName(p)))
                        throw new InvalidDataException("Invalid package entry path.");
                    if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                        throw new InvalidDataException("Package links are not supported.");
                    var target = Path.GetFullPath(Path.Combine(temporary, name));
                    if (!ProjectSaveAsTransaction.IsWithin(temporary, target) || !paths.Add(Path.TrimEndingDirectorySeparator(target)))
                        throw new InvalidDataException("Duplicate or escaping package entry.");
                    if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    remaining -= entry.Length;
                    if (entry.Length < 0 || remaining < 0) throw new InvalidDataException("Package exceeds 64 GiB.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await ProjectStreamCopy.CopyExactlyAsync(source, output, entry.Length, cancellationToken);
                    if (source.ReadByte() != -1) throw new InvalidDataException("Package entry exceeds its declared size.");
                }
            }
            var solutions = Directory.GetFiles(temporary, "*.uotan", SearchOption.TopDirectoryOnly);
            if (solutions.Length != 1) throw new InvalidDataException("A package must contain exactly one solution at its root.");
            var solutionPath = solutions[0];
            var snapshot = await SolutionPersistenceService.LoadSolutionAsync(solutionPath, cancellationToken)
                ?? throw new InvalidDataException("Invalid packaged solution.");
            ProjectValidation.ValidateSolution(snapshot);
            var root = Path.ChangeExtension(solutionPath, null);
            foreach (var reference in snapshot.SubProjects ?? [])
            {
                var path = Path.GetFullPath(Path.Combine(root, reference.RelativePath));
                if (!ProjectSaveAsTransaction.IsWithin(root, path) || !File.Exists(path))
                    throw new InvalidDataException("Packaged sub-project is missing or outside the data root.");
                if (!ProjectValidation.IsSubProjectDirectory(root, Path.GetDirectoryName(path)!))
                    throw new InvalidDataException("A packaged sub-project requires its own directory.");
                if (await SolutionPersistenceService.LoadFirmwareProjectAsync(path, cancellationToken) is null)
                    throw new InvalidDataException("Invalid packaged sub-project.");
            }
            var nodePath = Path.Combine(root, Path.GetFileName(root) + ".uotannode");
            if (await SolutionPersistenceService.LoadNodeProjectAsync(nodePath, cancellationToken) is null)
                throw new InvalidDataException("Missing or invalid packaged workflow.");
            // Never reconstruct source-machine absolute undo paths in the imported workspace.
            foreach (var project in snapshot.SubProjects ?? [])
            {
                var subRoot = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, project.RelativePath)))!;
                await new ProjectHistory().SaveAsync(Path.Combine(subRoot, ".uotan", "history.json"), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
            return Path.Combine(destination, Path.GetFileName(solutionPath));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9';
    }
}
