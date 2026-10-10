using System.Buffers;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Validates extraction paths and commits complete output files.
/// <para>验证提取路径并提交完整输出文件。</para>
/// </summary>
internal static class FileExtraction
{
    private static readonly SearchValues<char> ForbiddenEntryNameChars = SearchValues.Create(['\0', ':']);

    internal static string ResolvePath(string directory, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrWhiteSpace(entryName) || entryName.AsSpan().ContainsAny(ForbiddenEntryNameChars))
            throw new InvalidDataException("The archive entry name is invalid.");

        var relative = entryName.Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException($"The archive entry name is not a safe relative path: '{entryName}'.");

        var root = Path.GetFullPath(directory);
        var target = Path.GetFullPath(relative, root);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(prefix, comparison))
            throw new InvalidDataException($"The archive entry escapes the output directory: '{entryName}'.");
        RejectLinks(target);
        return target;
    }

    internal static void WriteAtomic(string targetPath, Action<FileStream> write, bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        RejectLinks(target);
        var temporary = TempFileHelper.CreateAdjacentPath(target);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920))
                write(output);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(target);
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void ValidateRange(long offset, long size, long length, string description)
    {
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
            throw new InvalidDataException($"{description} exceeds the input bounds.");
    }

    internal static async Task WriteAtomicAsync(string targetPath, Func<FileStream, Task> write,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        RejectLinks(target);
        var temporary = TempFileHelper.CreateAdjacentPath(target);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous))
                await write(output).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(target);
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Rejects reparse points between the deepest existing non-link directory of the target and the target itself.
    /// <para>
    /// 沿目录向上检查到已存在的最深真实目录为止，中途遇到符号链接即拒绝。边界之上的祖先由调用方或操作系统选定，
    /// 不受归档内容控制，因此不参与判定；macOS 上 <c>/var</c> 是指向 <c>/private/var</c> 的符号链接，
    /// 临时目录必定经由它解析，把链接本身当作边界并拒绝会误伤所有合法的提取目标。
    /// </para>
    /// The walk stops at the deepest existing non-link directory because ancestors above an extraction root are
    /// chosen by the caller or the operating system and are not archive-controlled. macOS resolves
    /// <c>Path.GetTempPath()</c> through the <c>/var</c> to <c>/private/var</c> symbolic link, so treating that link
    /// as the boundary rejects every legitimate extraction below the temporary directory.
    /// </summary>
    private static void RejectLinks(string target)
    {
        var boundary = DeepestExistingAncestor(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = target;
            current is not null && !string.Equals(current, boundary, comparison);
            current = Path.GetDirectoryName(current))
        {
            if (IsReparsePoint(current))
            {
                throw new InvalidDataException($"Extraction through a symbolic link or reparse point is not allowed: '{current}'.");
            }
        }
    }

    /// <summary>
    /// Returns the deepest ancestor that already exists as a real directory. A component that does not exist, or that
    /// is itself a symbolic link, is skipped so the search continues upwards for a real directory; <see cref="RejectLinks"/>
    /// still inspects every skipped link while walking down to the target, so a link planted inside an extraction root
    /// is rejected while an operating-system link above that root is not.
    /// </summary>
    private static string DeepestExistingAncestor(string target)
    {
        for (var current = Path.GetDirectoryName(target); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = new FileInfo(current).Attributes;
            if (attributes == (FileAttributes)(-1) || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            return current;
        }
        return Path.GetPathRoot(target) ?? target;
    }

    private static bool IsReparsePoint(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        return attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0;
    }
}