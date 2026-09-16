namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Validates extraction paths and commits complete output files.
/// <para>验证提取路径并提交完整输出文件。</para>
/// </summary>
internal static class FileExtraction
{
    internal static string ResolvePath(string directory, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOfAny(['\0', ':']) >= 0)
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

    internal static void WriteAtomic(string targetPath, Action<FileStream> write, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(write);
        var target = Path.GetFullPath(targetPath);
        RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        RejectLinks(target);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920))
                write(output);
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
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
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

    private static void RejectLinks(string target)
    {
        for (string? current = target; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = new FileInfo(current).Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Extraction through a symbolic link or reparse point is not allowed: '{current}'.");
        }
    }
}