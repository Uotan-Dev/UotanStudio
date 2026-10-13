namespace UotanStudio.Services.Patching;

/// <summary>Path confinement shared by patch workspaces and publication.<para>补丁工作区与发布共用的路径限制逻辑。</para></summary>
internal static class PatchPath
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Resolve(string root, string relative)
    {
        var segments = ValidateRelative(relative);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Patch target escapes its workspace.");
        }
        RejectLinks(full);
        return full;
    }

    /// <summary>Validates relative path syntax before layout mapping, without probing the filesystem.<para>在布局映射前校验相对路径语法，不探测文件系统。</para></summary>
    /// <param name="relative">The relative path to validate.<para>要校验的相对路径。</para></param>
    /// <returns>The split path segments.<para>拆分后的路径段。</para></returns>
    internal static string[] ValidateRelative(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var segments = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || segments.Any(segment =>
            segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.AsSpan().ContainsAny(PathSearchValues.InvalidFileName)))
        {
            throw new InvalidDataException("A patch target must be a normalized relative path.");
        }
        foreach (var segment in segments)
        {
            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9')
            {
                throw new InvalidDataException("A patch path cannot contain a reserved device name.");
            }
        }
        return segments;
    }

    internal static string NormalizeFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Patch file has no filesystem root.");
        return Resolve(root, Path.GetRelativePath(root, full));
    }

    /// <summary>Rejects reparse points between the deepest existing non-link directory of a path and the path itself. Ancestors above that boundary are system-chosen and stay outside patch control.<para>在路径与其最深已存在的非链接目录之间拒绝重解析点。该边界之上的祖先目录由系统选定，不受补丁内容控制。</para></summary>
    /// <param name="path">The path to inspect.<para>要检查的路径。</para></param>
    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var boundary = DeepestExistingAncestor(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = full;
            current is not null && !string.Equals(current, boundary, comparison);
            current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A patch file path must not traverse a link or junction.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>Returns the deepest ancestor that already exists as a real directory, skipping missing components and symbolic links so the walk continues upwards.<para>返回已存在且为真实目录的最深上级目录，跳过缺失组件与符号链接并继续向上查找。</para></summary>
    /// <param name="full">The normalized full path to walk.<para>要向上遍历的规范化完整路径。</para></param>
    /// <returns>The boundary directory whose ancestors are exempt from link checks.<para>其上级目录免于链接检查的边界目录。</para></returns>
    private static string DeepestExistingAncestor(string full)
    {
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            return current;
        }
        return Path.GetPathRoot(full) ?? full;
    }
}