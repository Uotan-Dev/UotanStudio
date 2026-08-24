using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Core.Verification;

/// <summary>
/// <para>差异条目类型：新增 / 修改 / 删除。</para>
/// Diff entry kind: added / modified / deleted.
/// </summary>
public enum DiffChangeKind
{
    /// <summary>新增。 Added.</summary>
    Added,

    /// <summary>修改。 Modified.</summary>
    Modified,

    /// <summary>删除。 Deleted.</summary>
    Deleted
}

/// <summary>
/// <para>固件差异条目：记录相对路径、大小、SHA-256 与变更类型。</para>
/// Firmware diff entry: records the relative path, size, SHA-256, and change kind.
/// </summary>
/// <param name="RelativePath">
/// <para>相对基准目录的文件路径。</para>
/// The file path relative to the base directory.
/// </param>
/// <param name="Size">
/// <para>文件大小（字节）。</para>
/// The file size in bytes.
/// </param>
/// <param name="Sha256">
/// <para>文件 SHA-256（小写十六进制）。</para>
/// The file SHA-256 (lower-case hex).
/// </param>
/// <param name="Kind">
/// <para>变更类型。</para>
/// The change kind.
/// </param>
public sealed record FirmwareDiffEntry(string RelativePath, long Size, string Sha256, DiffChangeKind Kind);

/// <summary>
/// <para>固件目录差异对比结果。</para>
/// Result of a firmware directory comparison.
/// </summary>
public sealed class FirmwareDiffResult
{
    /// <summary>
    /// <para>获取或设置新增文件列表。</para>
    /// Gets or sets the list of added files.
    /// </summary>
    public IReadOnlyList<FirmwareDiffEntry> Added { get; set; } = [];

    /// <summary>
    /// <para>获取或设置修改文件列表。</para>
    /// Gets or sets the list of modified files.
    /// </summary>
    public IReadOnlyList<FirmwareDiffEntry> Modified { get; set; } = [];

    /// <summary>
    /// <para>获取或设置删除文件列表。</para>
    /// Gets or sets the list of deleted files.
    /// </summary>
    public IReadOnlyList<FirmwareDiffEntry> Deleted { get; set; } = [];
}

/// <summary>
/// <para>固件差异对比服务：比较两个目录，输出新增/修改/删除列表。</para>
/// Firmware diff service: compares two directories and outputs added/modified/deleted lists.
/// </summary>
public static class FirmwareDiffService
{
    /// <summary>
    /// <para>比较基准目录与修改后目录，返回差异结果（大小或 SHA-256 不同视为修改）。</para>
    /// Compares the baseline directory against the modified directory and returns the diff
    /// (files are considered modified when the size or SHA-256 differs).
    /// </summary>
    /// <param name="baseline">
    /// <para>基准目录。</para>
    /// The baseline directory.
    /// </param>
    /// <param name="modified">
    /// <para>修改后目录。</para>
    /// The modified directory.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    /// <returns>
    /// <para>目录差异对比结果。</para>
    /// The directory diff result.
    /// </returns>
    public static async Task<FirmwareDiffResult> CompareDirectoriesAsync(string baseline, string modified, CancellationToken cancellationToken = default)
    {
        var baselineFiles = await IndexFiles(baseline, cancellationToken).ConfigureAwait(false);
        var modifiedFiles = await IndexFiles(modified, cancellationToken).ConfigureAwait(false);

        var added = new List<FirmwareDiffEntry>();
        var modifiedEntries = new List<FirmwareDiffEntry>();
        var deleted = new List<FirmwareDiffEntry>();

        foreach (var (relativePath, baselineInfo) in baselineFiles)
        {
            if (!modifiedFiles.TryGetValue(relativePath, out var modifiedInfo))
            {
                deleted.Add(new FirmwareDiffEntry(relativePath, baselineInfo.Size, baselineInfo.Hash, DiffChangeKind.Deleted));
                continue;
            }

            if (baselineInfo.Size != modifiedInfo.Size
                || !string.Equals(baselineInfo.Hash, modifiedInfo.Hash, StringComparison.OrdinalIgnoreCase))
            {
                modifiedEntries.Add(new FirmwareDiffEntry(relativePath, modifiedInfo.Size, modifiedInfo.Hash, DiffChangeKind.Modified));
            }
        }

        foreach (var (relativePath, modifiedInfo) in modifiedFiles)
        {
            if (!baselineFiles.ContainsKey(relativePath))
            {
                added.Add(new FirmwareDiffEntry(relativePath, modifiedInfo.Size, modifiedInfo.Hash, DiffChangeKind.Added));
            }
        }

        return new FirmwareDiffResult
        {
            Added = added,
            Modified = modifiedEntries,
            Deleted = deleted
        };
    }

    private static async Task<Dictionary<string, FileInfoRecord>> IndexFiles(string root, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileInfoRecord>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var size = new FileInfo(file).Length;
            var hash = await HashService.Sha256Async(file, cancellationToken).ConfigureAwait(false);
            result[relativePath] = new FileInfoRecord(size, hash);
        }

        return result;
    }

    private sealed record FileInfoRecord(long Size, string Hash);
}