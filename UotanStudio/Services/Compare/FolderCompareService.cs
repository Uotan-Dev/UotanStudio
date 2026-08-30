using System.Security.Cryptography;

namespace UotanStudio.Services.Compare;

/// <summary>
/// <para>文件夹比对条目状态。</para>
/// Folder comparison item status.
/// </summary>
public enum FolderItemStatus
{
    /// <summary>
    /// <para>两侧均存在且内容相同（目录为存在性相同）。</para>
    /// Exists on both sides with identical content (directories: identical existence).
    /// </summary>
    Equal,

    /// <summary>
    /// <para>两侧均存在但内容不同，或一侧为文件一侧为目录。</para>
    /// Exists on both sides with differing content, or a file on one side and a directory on the other.
    /// </summary>
    Different,

    /// <summary>
    /// <para>仅左侧存在。</para>
    /// Exists on the left side only.
    /// </summary>
    LeftOnly,

    /// <summary>
    /// <para>仅右侧存在。</para>
    /// Exists on the right side only.
    /// </summary>
    RightOnly,
}

/// <summary>
/// <para>文件夹比对结果中的单个条目。</para>
/// A single item in a folder comparison result.
/// </summary>
public sealed class FolderCompareItem
{
    /// <summary>
    /// <para>相对于比对根目录的路径，统一使用 '/' 分隔。</para>
    /// Path relative to the comparison root, always separated by '/'.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// <para>条目状态。</para>
    /// Item status.
    /// </summary>
    public required FolderItemStatus Status { get; init; }

    /// <summary>
    /// <para>是否为目录。</para>
    /// Whether the item is a directory.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// <para>左侧条目大小（字节）；目录或缺失时为 -1。</para>
    /// Size of the left item in bytes; -1 for directories or when absent.
    /// </summary>
    public long LeftSize { get; init; }

    /// <summary>
    /// <para>右侧条目大小（字节）；目录或缺失时为 -1。</para>
    /// Size of the right item in bytes; -1 for directories or when absent.
    /// </summary>
    public long RightSize { get; init; }
}

/// <summary>
/// <para>文件夹比对结果，包含条目列表与各状态计数。</para>
/// Folder comparison result containing the item list and per-status counts.
/// </summary>
public sealed class FolderCompareResult
{
    /// <summary>
    /// <para>全部比对条目（深度优先，同级按名称排序）。</para>
    /// All comparison items (depth-first, siblings sorted by name).
    /// </summary>
    public List<FolderCompareItem> Items { get; } = [];

    /// <summary>
    /// <para>相同条目数。</para>
    /// Count of equal items.
    /// </summary>
    public int EqualCount { get; set; }

    /// <summary>
    /// <para>不同条目数。</para>
    /// Count of different items.
    /// </summary>
    public int DifferentCount { get; set; }

    /// <summary>
    /// <para>仅左侧条目数。</para>
    /// Count of left-only items.
    /// </summary>
    public int LeftOnlyCount { get; set; }

    /// <summary>
    /// <para>仅右侧条目数。</para>
    /// Count of right-only items.
    /// </summary>
    public int RightOnlyCount { get; set; }
}

/// <summary>
/// <para>文件夹比对服务：递归比较两个目录的结构与内容（文件按长度与流式 SHA256 比对），并支持按相对路径复制或删除文件。</para>
/// Folder comparison service: recursively compares two directories by structure and content
/// (files compared by length then streamed SHA256), and supports per-relative-path copy and delete.
/// </summary>
public sealed class FolderCompareService
{
    private static readonly Lazy<FolderCompareService> LazyInstance = new(() => new FolderCompareService());

    /// <summary>
    /// <para>获取 FolderCompareService 单例。</para>
    /// Gets the FolderCompareService singleton instance.
    /// </summary>
    public static FolderCompareService Instance => LazyInstance.Value;

    private FolderCompareService() { }

    /// <summary>
    /// <para>递归比较两个目录。目录项只比较存在性（状态为 Equal/LeftOnly/RightOnly，尺寸为 -1，不比较目录内容）；
    /// 文件先比较长度，长度相同再流式比较 SHA256，相同为 Equal，否则为 Different。任一根目录不存在时按空目录处理。</para>
    /// Recursively compares two directories. Directory items are compared by existence only
    /// (status Equal/LeftOnly/RightOnly, sizes -1, directory contents not compared); files are compared
    /// by length first, then streamed SHA256 when lengths match — Equal when hashes match, otherwise
    /// Different. A missing root directory is treated as empty.
    /// </summary>
    /// <param name="leftDirectory">左目录路径。<para>Left directory path.</para></param>
    /// <param name="rightDirectory">右目录路径。<para>Right directory path.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>比对结果。</para> The comparison result.</returns>
    public async Task<FolderCompareResult> CompareAsync(string leftDirectory, string rightDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(leftDirectory);
        ArgumentNullException.ThrowIfNull(rightDirectory);

        if (!Directory.Exists(leftDirectory) && !Directory.Exists(rightDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Neither directory exists: '{leftDirectory}' and '{rightDirectory}'");
        }

        ct.ThrowIfCancellationRequested();

        var result = new FolderCompareResult();
        await CompareLevelAsync(leftDirectory, rightDirectory, string.Empty, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// <para>把 sourceDirectory 下的相对文件复制到 targetDirectory 的相同相对位置
    /// （自动创建父目录，目标存在则覆盖，FileStream 流式复制）。相对路径经规范化后必须仍位于源目录内，否则抛 <see cref="ArgumentException"/>。</para>
    /// Copies the file at the given relative path under sourceDirectory to the same relative location under
    /// targetDirectory (parent directories are created, existing files are overwritten, streamed via FileStream).
    /// The normalized relative path must stay inside the source directory or an <see cref="ArgumentException"/> is thrown.
    /// </summary>
    /// <param name="sourceDirectory">源目录路径。<para>Source directory path.</para></param>
    /// <param name="targetDirectory">目标目录路径。<para>Target directory path.</para></param>
    /// <param name="relativePath">源目录下的相对文件路径。<para>Relative file path under the source directory.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>复制完成时返回。</para> Completes when the copy is finished.</returns>
    public async Task CopyAsync(string sourceDirectory, string targetDirectory, string relativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ct.ThrowIfCancellationRequested();

        var safeRelative = ResolveSafeRelativePath(sourceDirectory, relativePath);
        var sourceFile = Path.Combine(Path.GetFullPath(sourceDirectory), safeRelative);
        var targetFile = Path.Combine(Path.GetFullPath(targetDirectory), safeRelative);

        if (Directory.Exists(sourceFile))
        {
            throw new ArgumentException("Copying directories is not supported; the source must be a file.", nameof(relativePath));
        }

        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException($"Source file not found: '{sourceFile}'", sourceFile);
        }

        var targetDirectoryPath = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDirectoryPath))
        {
            Directory.CreateDirectory(targetDirectoryPath);
        }

        await using var sourceStream = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var targetStream = new FileStream(
            targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await sourceStream.CopyToAsync(targetStream, 81920, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>删除 directory 下指定相对路径的文件。相对路径经规范化后必须仍位于目录内，否则抛 <see cref="ArgumentException"/>。</para>
    /// Deletes the file at the given relative path under directory. The normalized relative path must stay
    /// inside the directory or an <see cref="ArgumentException"/> is thrown.
    /// </summary>
    /// <param name="directory">目录路径。<para>Directory path.</para></param>
    /// <param name="relativePath">目录下的相对文件路径。<para>Relative file path under the directory.</para></param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns><para>删除完成时返回。</para> Completes when the deletion is finished.</returns>
    public Task DeleteAsync(string directory, string relativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ct.ThrowIfCancellationRequested();

        var safeRelative = ResolveSafeRelativePath(directory, relativePath);
        var filePath = Path.Combine(Path.GetFullPath(directory), safeRelative);

        if (Directory.Exists(filePath))
        {
            throw new ArgumentException("Deleting directories is not supported; the target must be a file.", nameof(relativePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: '{filePath}'", filePath);
        }

        File.Delete(filePath);
        return Task.CompletedTask;
    }

    private static async Task CompareLevelAsync(
        string leftDirectory,
        string rightDirectory,
        string prefix,
        FolderCompareResult result,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var leftNames = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(leftDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(leftDirectory))
            {
                leftNames.Add(Path.GetFileName(entry)!);
            }
        }

        var rightNames = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(rightDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(rightDirectory))
            {
                rightNames.Add(Path.GetFileName(entry)!);
            }
        }

        var allNames = new SortedSet<string>(leftNames, StringComparer.Ordinal);
        allNames.UnionWith(rightNames);

        foreach (var name in allNames)
        {
            ct.ThrowIfCancellationRequested();

            var leftPath = Path.Combine(leftDirectory, name);
            var rightPath = Path.Combine(rightDirectory, name);
            var inLeft = leftNames.Contains(name);
            var inRight = rightNames.Contains(name);
            var leftIsDir = inLeft && Directory.Exists(leftPath);
            var rightIsDir = inRight && Directory.Exists(rightPath);
            var relative = prefix + name;

            if (inLeft && inRight)
            {
                if (leftIsDir && rightIsDir)
                {
                    result.Items.Add(new FolderCompareItem
                    {
                        RelativePath = relative,
                        Status = FolderItemStatus.Equal,
                        IsDirectory = true,
                        LeftSize = -1,
                        RightSize = -1,
                    });
                    result.EqualCount++;
                    await CompareLevelAsync(leftPath, rightPath, relative + "/", result, ct).ConfigureAwait(false);
                }
                else if (!leftIsDir && !rightIsDir)
                {
                    var leftInfo = new FileInfo(leftPath);
                    var rightInfo = new FileInfo(rightPath);
                    var equal = leftInfo.Length == rightInfo.Length
                        && await AreFilesEqualAsync(leftPath, rightPath, ct).ConfigureAwait(false);
                    result.Items.Add(new FolderCompareItem
                    {
                        RelativePath = relative,
                        Status = equal ? FolderItemStatus.Equal : FolderItemStatus.Different,
                        IsDirectory = false,
                        LeftSize = leftInfo.Length,
                        RightSize = rightInfo.Length,
                    });
                    if (equal)
                    {
                        result.EqualCount++;
                    }
                    else
                    {
                        result.DifferentCount++;
                    }
                }
                else
                {
                    // 一侧为文件、一侧为目录，视为内容不同。 A file on one side and a directory on the other is Different.
                    result.Items.Add(new FolderCompareItem
                    {
                        RelativePath = relative,
                        Status = FolderItemStatus.Different,
                        IsDirectory = leftIsDir,
                        LeftSize = leftIsDir ? -1 : new FileInfo(leftPath).Length,
                        RightSize = rightIsDir ? -1 : new FileInfo(rightPath).Length,
                    });
                    result.DifferentCount++;
                }
            }
            else if (inLeft)
            {
                result.Items.Add(new FolderCompareItem
                {
                    RelativePath = relative,
                    Status = FolderItemStatus.LeftOnly,
                    IsDirectory = leftIsDir,
                    LeftSize = leftIsDir ? -1 : new FileInfo(leftPath).Length,
                    RightSize = -1,
                });
                result.LeftOnlyCount++;
                if (leftIsDir)
                {
                    AddLeftOnlyTree(leftPath, relative + "/", result, ct);
                }
            }
            else
            {
                result.Items.Add(new FolderCompareItem
                {
                    RelativePath = relative,
                    Status = FolderItemStatus.RightOnly,
                    IsDirectory = rightIsDir,
                    LeftSize = -1,
                    RightSize = rightIsDir ? -1 : new FileInfo(rightPath).Length,
                });
                result.RightOnlyCount++;
                if (rightIsDir)
                {
                    AddRightOnlyTree(rightPath, relative + "/", result, ct);
                }
            }
        }
    }

    private static void AddLeftOnlyTree(string directory, string relativePrefix, FolderCompareResult result, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var isDir = Directory.Exists(entry);
            var relative = relativePrefix + Path.GetRelativePath(directory, entry).Replace('\\', '/');
            result.Items.Add(new FolderCompareItem
            {
                RelativePath = relative,
                Status = FolderItemStatus.LeftOnly,
                IsDirectory = isDir,
                LeftSize = isDir ? -1 : new FileInfo(entry).Length,
                RightSize = -1,
            });
            result.LeftOnlyCount++;
        }
    }

    private static void AddRightOnlyTree(string directory, string relativePrefix, FolderCompareResult result, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var isDir = Directory.Exists(entry);
            var relative = relativePrefix + Path.GetRelativePath(directory, entry).Replace('\\', '/');
            result.Items.Add(new FolderCompareItem
            {
                RelativePath = relative,
                Status = FolderItemStatus.RightOnly,
                IsDirectory = isDir,
                LeftSize = -1,
                RightSize = isDir ? -1 : new FileInfo(entry).Length,
            });
            result.RightOnlyCount++;
        }
    }

    private static async Task<bool> AreFilesEqualAsync(string leftPath, string rightPath, CancellationToken ct)
    {
        var leftHash = await ComputeSha256Async(leftPath, ct).ConfigureAwait(false);
        var rightHash = await ComputeSha256Async(rightPath, ct).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    private static string ResolveSafeRelativePath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path must not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Relative path must not be absolute.", nameof(relativePath));
        }

        var root = Path.GetFullPath(rootDirectory);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, combined);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"Relative path escapes the root directory: '{relativePath}'", nameof(relativePath));
        }

        return relative;
    }
}
