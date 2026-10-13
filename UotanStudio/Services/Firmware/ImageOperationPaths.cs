using System.Collections.Frozen;

namespace UotanStudio.Services.Firmware;

/// <summary>Captures paths before an asynchronous image operation.<para>在异步镜像操作前固定路径。</para></summary>
/// <param name="Source">Source file or directory path.<para>源文件或目录路径。</para></param>
/// <param name="IsDirectory">Whether the source is a directory.<para>源是否为目录。</para></param>
/// <param name="Root">Sub-project root path.<para>子项目根路径。</para></param>
/// <param name="OutputRoot">Output root path.<para>输出根路径。</para></param>
internal sealed record ImageOperationPaths(string Source, bool IsDirectory, string Root, string OutputRoot)
{
    private static readonly FrozenSet<string> ReservedNames =
        FrozenSet.ToFrozenSet(["images", "output", "delivery", ".uotan", ".deleted"], StringComparer.OrdinalIgnoreCase);

    internal string Name => IsDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Source))
        : Path.GetFileNameWithoutExtension(Source);

    internal string UnpackedDirectory
    {
        get
        {
            if (Name is "" or "." or "..")
                throw new IOException("The unpacked directory must be a non-reserved child of the sub-project root.");

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(Source));
            var parent = IsDirectory || IsUnderImagesDirectory(sourceDir, Root)
                ? Path.GetFullPath(Root)
                : sourceDir!;
            if (string.IsNullOrEmpty(parent))
                throw new IOException("The unpacked directory must be a non-reserved child of the sub-project root.");

            var path = Path.GetFullPath(IsDirectory ? Source : Path.Combine(parent, Name));
            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(parent),
                    StringComparison.OrdinalIgnoreCase)
                || ReservedNames.Contains(Name))
                throw new IOException("The unpacked directory must be a non-reserved child of the sub-project root.");
            return path;
        }
    }

    /// <summary>Determines whether the source directory is the <c>images/</c> directory or inside it.<para>判断源目录是否为 <c>images/</c> 目录或其子目录。</para></summary>
    /// <param name="sourceDirectory">Directory to test.<para>待测试的目录。</para></param>
    /// <param name="root">Sub-project root path.<para>子项目根路径。</para></param>
    /// <returns>True when the directory is inside the images directory.<para>目录位于 images 目录内时为真。</para></returns>
    private static bool IsUnderImagesDirectory(string? sourceDirectory, string root)
    {
        if (string.IsNullOrEmpty(sourceDirectory)) return false;
        var images = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "images")));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(current, images, comparison)
            || current.StartsWith(images + Path.DirectorySeparatorChar, comparison);
    }

    internal void ValidateExtraction()
    {
        if (IsDirectory || Directory.Exists(Source)) throw new IOException("Extraction requires a firmware file, not a directory.");
        if (!File.Exists(Source)) throw new FileNotFoundException("Firmware file not found.", Source);
        var target = UnpackedDirectory;
        var source = Path.GetFullPath(Source);
        if (!source.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Firmware input must belong to the active sub-project.");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Extraction output must not contain the source file.");
    }
}