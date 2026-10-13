using System.Security.Cryptography;

namespace UotanStudio.Services.Compare;

/// <summary>
/// Folder comparison item status.
/// <para>文件夹比对条目状态。</para>
/// </summary>
public enum FolderItemStatus
{
    /// <summary>
    /// Exists on both sides with identical content (directories: identical existence).
    /// <para>两侧均存在且内容相同（目录为存在性相同）。</para>
    /// </summary>
    Equal,

    /// <summary>
    /// Exists on both sides with differing content, or a file on one side and a directory on the other.
    /// <para>两侧均存在但内容不同，或一侧为文件一侧为目录。</para>
    /// </summary>
    Different,

    /// <summary>
    /// Exists on the left side only.
    /// <para>仅左侧存在。</para>
    /// </summary>
    LeftOnly,

    /// <summary>
    /// Exists on the right side only.
    /// <para>仅右侧存在。</para>
    /// </summary>
    RightOnly,
}

/// <summary>
/// A single item in a folder comparison result.
/// <para>文件夹比对结果中的单个条目。</para>
/// </summary>
public sealed class FolderCompareItem
{
    /// <summary>
    /// Path relative to the comparison root, always separated by '/'.
    /// <para>相对于比对根目录的路径，统一使用 '/' 分隔。</para>
    /// </summary>
    /// <value>The '/'-separated relative path of the item. <para>条目的 '/' 分隔相对路径。</para></value>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Item status.
    /// <para>条目状态。</para>
    /// </summary>
    /// <value>The comparison status of the item. <para>条目的比对状态。</para></value>
    public required FolderItemStatus Status { get; init; }

    /// <summary>
    /// Whether the item is a directory.
    /// <para>是否为目录。</para>
    /// </summary>
    /// <value>True when the item is a directory. <para>条目为目录时为 true。</para></value>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// Size of the left item in bytes; -1 for directories or when absent.
    /// <para>左侧条目大小（字节）；目录或缺失时为 -1。</para>
    /// </summary>
    /// <value>Left item size in bytes, or -1 for directories and missing items. <para>左侧条目字节大小；目录或缺失时为 -1。</para></value>
    public long LeftSize { get; init; }

    /// <summary>
    /// Size of the right item in bytes; -1 for directories or when absent.
    /// <para>右侧条目大小（字节）；目录或缺失时为 -1。</para>
    /// </summary>
    /// <value>Right item size in bytes, or -1 for directories and missing items. <para>右侧条目字节大小；目录或缺失时为 -1。</para></value>
    public long RightSize { get; init; }
}

/// <summary>
/// Folder comparison result containing the item list and per-status counts.
/// <para>文件夹比对结果，包含条目列表与各状态计数。</para>
/// </summary>
public sealed class FolderCompareResult
{
    /// <summary>
    /// All comparison items (depth-first, siblings sorted by name).
    /// <para>全部比对条目（深度优先，同级按名称排序）。</para>
    /// </summary>
    /// <value>The compared items, depth-first with siblings sorted by name. <para>比对条目列表，深度优先且同级按名称排序。</para></value>
    public List<FolderCompareItem> Items { get; } = [];

    /// <summary>
    /// Count of equal items.
    /// <para>相同条目数。</para>
    /// </summary>
    /// <value>Number of items equal on both sides. <para>两侧相同的条目数。</para></value>
    public int EqualCount { get; set; }

    /// <summary>
    /// Count of different items.
    /// <para>不同条目数。</para>
    /// </summary>
    /// <value>Number of items that differ between the sides. <para>两侧存在差异的条目数。</para></value>
    public int DifferentCount { get; set; }

    /// <summary>
    /// Count of left-only items.
    /// <para>仅左侧条目数。</para>
    /// </summary>
    /// <value>Number of items present only on the left. <para>仅左侧存在的条目数。</para></value>
    public int LeftOnlyCount { get; set; }

    /// <summary>
    /// Count of right-only items.
    /// <para>仅右侧条目数。</para>
    /// </summary>
    /// <value>Number of items present only on the right. <para>仅右侧存在的条目数。</para></value>
    public int RightOnlyCount { get; set; }
}

/// <summary>
/// Folder comparison service: recursively compares two directories by structure and content
/// (files compared by length then streamed SHA256), and supports per-relative-path copy and delete.
/// <para>文件夹比对服务：递归比较两个目录的结构与内容（文件按长度与流式 SHA256 比对），并支持按相对路径复制或删除文件。</para>
/// </summary>
public sealed class FolderCompareService
{
    private static readonly Lazy<FolderCompareService> LazyInstance = new(() => new FolderCompareService());

    /// <summary>
    /// Gets the shared instance created on first access.
    /// <para>获取首次访问时创建的共享实例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static FolderCompareService Instance => LazyInstance.Value;

    internal FolderCompareService() { }

    /// <summary>
    /// Recursively compares two directories: files by length then streamed SHA256, directory items by
    /// existence only. A missing root directory is treated as empty.
    /// <para>递归比较两个目录：文件先比长度再流式比较 SHA256，目录项只比较存在性。缺失的根目录按空目录处理。</para>
    /// </summary>
    /// <param name="leftDirectory">Left directory path. <para>左目录路径。</para></param>
    /// <param name="rightDirectory">Right directory path. <para>右目录路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The comparison result. <para>比对结果。</para></returns>
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
    /// Copies the file at a relative path under one directory to the same relative location under
    /// another, creating parent directories and overwriting existing files.
    /// <para>把一个目录下相对路径处的文件复制到另一个目录的相同相对位置，自动创建父目录并覆盖已有文件。</para>
    /// </summary>
    /// <param name="sourceDirectory">Source directory path. <para>源目录路径。</para></param>
    /// <param name="targetDirectory">Target directory path. <para>目标目录路径。</para></param>
    /// <param name="relativePath">Relative file path under the source directory. <para>源目录下的相对文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The normalized relative path leaves the source directory. <para>规范化后的相对路径超出源目录范围。</para></exception>
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
    /// Deletes the file at the given relative path under directory. The normalized relative path must stay
    /// inside the directory or an <see cref="ArgumentException"/> is thrown.
    /// <para>删除 directory 下指定相对路径的文件。相对路径经规范化后必须仍位于目录内，否则抛 <see cref="ArgumentException"/>。</para>
    /// </summary>
    /// <param name="directory">Directory path. <para>目录路径。</para></param>
    /// <param name="relativePath">Relative file path under the directory. <para>目录下的相对文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
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
        var length = new FileInfo(leftPath).Length;
        await using var left = new FileStream(
            leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var right = new FileStream(
            rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var bufferA = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
        var bufferB = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long position = 0;
            while (position < length)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSize, length - position);
                var readA = await left.ReadAsync(bufferA.AsMemory(0, count), ct).ConfigureAwait(false);
                var readB = await right.ReadAsync(bufferB.AsMemory(0, count), ct).ConfigureAwait(false);
                if (readA != count || readB != count)
                {
                    return false;
                }
                if (!bufferA.AsSpan(0, count).SequenceEqual(bufferB.AsSpan(0, count)))
                {
                    return false;
                }
                position += count;
            }
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bufferA);
            System.Buffers.ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    /// <summary>
    /// Chunk size for the streaming equality comparison.
    /// <para>流式相等比较的分块大小。</para>
    /// </summary>
    private const int ChunkSize = 1024 * 1024;

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