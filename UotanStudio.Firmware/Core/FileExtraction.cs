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
    /// Rejects reparse points between the deepest already existing ancestor of the target and the target itself.
    /// The walk stops at that existing ancestor instead of continuing to the filesystem root, because ancestors above
    /// an extraction root are chosen by the user or the operating system and are not archive-controlled. macOS
    /// resolves <c>Path.GetTempPath()</c> through the <c>/var</c> to <c>/private/var</c> symbolic link, so an unbounded
    /// walk rejects every legitimate extraction below the temporary directory.
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
        if (!string.Equals(boundary, target, comparison) && IsReparsePoint(boundary))
        {
            throw new InvalidDataException($"Extraction through a symbolic link or reparse point is not allowed: '{boundary}'.");
        }
    }

    /// <summary>
    /// Returns the deepest ancestor that already exists as a directory or reparse point. Descendants of a dangling
    /// symbolic link report no attributes at all, so the walk skips them and keeps descending until it reaches the
    /// link itself, which <see cref="RejectLinks"/> then rejects. A path that does not exist anywhere stops the walk.
    /// </summary>
    private static string DeepestExistingAncestor(string target)
    {
        for (var current = Path.GetDirectoryName(target); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = new FileInfo(current).Attributes;
            if (attributes == (FileAttributes)(-1))
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(current))
            {
                return current;
            }
        }
        return Path.GetPathRoot(target) ?? target;
    }

    private static bool IsReparsePoint(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        return attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0;
    }
}