using System.Buffers;
using UotanStudio.Compress.Zip;

namespace UotanStudio.Projects;

/// <summary>
/// Imports complete project packages with bounded extraction and directory publication.
/// <para>以有界解压和目录发布的方式导入完整工程包。</para>
/// </summary>
public static class ProjectPackage
{
    /// <summary>
    /// Characters no packaged path segment may contain; one cached set serves every segment of every
    /// entry instead of rebuilding the array for each check.
    /// <para>打包路径段不得包含的字符；单个缓存集合服务每个条目的每一段，而不是每次检查都重新构建数组。</para>
    /// </summary>
    private static readonly SearchValues<char> InvalidSegmentChars = SearchValues.Create(['<', '>', '"', '|', '?', '*']);

    /// <summary>Extracts into a new destination directory and returns its solution path. Existing destinations are never replaced.
    /// <para>解压到一个新的目标目录并返回其解决方案路径。已存在的目标目录不会被替换。</para></summary>
    /// <param name="archivePath">Path of the ZIP package to open for reading; the file itself is never modified.
    /// <para>要打开读取的 ZIP 包路径；该文件本身不会被修改。</para></param>
    /// <param name="destinationDirectory">Not-yet-existing directory that receives the extracted tree; it is created atomically by renaming a private <c>.import-</c> sibling once every check passes.
    /// <para>接收解压目录树的尚不存在的目录；在所有校验通过后，由私有的 <c>.import-</c> 同级目录重命名而来，原子地创建。</para></param>
    /// <param name="cancellationToken">Cancels between entry copies and before publication; a cancelled or failed import deletes the temporary tree and leaves the destination untouched.
    /// <para>在条目拷贝之间与发布之前取消；已取消或失败的导入会删除临时目录树，并保持目标目录不变。</para></param>
    /// <returns>Full path of the single <c>.uotan</c> solution file inside <paramref name="destinationDirectory"/>.
    /// <para><paramref name="destinationDirectory"/> 内唯一 <c>.uotan</c> 解决方案文件的完整路径。</para></returns>
    /// <example>
    /// <code>
    /// var solutionPath = await ProjectPackage.ImportAsync(packageZip, targetDirectory, cancellationToken);
    /// </code>
    /// </example>
    /// <exception cref="IOException"><paramref name="destinationDirectory"/> already exists as a file or directory.
    /// <para><paramref name="destinationDirectory"/> 已作为文件或目录存在。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="archivePath"/> does not exist.
    /// <para><paramref name="archivePath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The package exceeds its budgets (more than 100000 entries, more than 64 GiB total uncompressed bytes, or more than 8 MiB of total entry-name characters), carries an unsafe, duplicate or escaping entry path, a symlink entry, an entry larger than its declared size, or fails the root-solution, sub-project and workflow validation (the root must contain exactly one <c>.uotan</c> solution, each sub-project must live in its own direct subdirectory inside the data root, and the packaged workflow must load).
    /// <para>包超出预算（条目数超过 100000、解压总字节超过 64 GiB、或条目名总字符数超过 8 MiB）、 包含不安全、重复或逃逸的条目路径、符号链接条目、条目实际大小超过声明大小， 或未通过根解决方案、子项目与工作流校验（根目录必须恰好包含一个 <c>.uotan</c> 解决方案， 每个子项目必须位于数据根下的直接子目录中，且打包的工作流必须可加载）。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
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
            using (var archive = ZipContainer.OpenRead(archiveStream))
            {
                if (archive.Entries.Count > 100000) throw new InvalidDataException("Package has too many entries.");
                var remaining = 64L * 1024 * 1024 * 1024;
                var remainingNames = 8L * 1024 * 1024;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    remainingNames -= name.Length;
                    if (remainingNames < 0 || string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':')
                        || name.Split('/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
                            || p.AsSpan().ContainsAny(InvalidSegmentChars) || IsDeviceName(p)))
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
            var root = Path.GetDirectoryName(solutionPath)!;
            foreach (var reference in snapshot.SubProjects ?? [])
            {
                var path = Path.GetFullPath(Path.Combine(root, reference.RelativePath));
                if (!ProjectSaveAsTransaction.IsWithin(root, path) || !File.Exists(path))
                    throw new InvalidDataException("Packaged sub-project is missing or outside the data root.");
                if (!ProjectValidation.IsDirectSubProjectDirectory(root, Path.GetDirectoryName(path)!))
                    throw new InvalidDataException("A packaged sub-project requires its own directory.");
                if (await SolutionPersistenceService.LoadFirmwareProjectAsync(path, cancellationToken) is null)
                    throw new InvalidDataException("Invalid packaged sub-project.");
            }
            foreach (var node in snapshot.NodeProjects ?? [])
            {
                var nodePath = Path.GetFullPath(Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!ProjectSaveAsTransaction.IsWithin(root, nodePath)
                    || !File.Exists(nodePath)
                    || await SolutionPersistenceService.LoadNodeProjectAsync(nodePath, cancellationToken) is null)
                    throw new InvalidDataException("Missing or invalid packaged workflow.");
            }
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