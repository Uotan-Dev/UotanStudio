using System.Collections.Frozen;

namespace UotanStudio.Services.Firmware;

/// <summary>Captures paths before an asynchronous image operation. 在异步镜像操作前固定路径。</summary>
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

            // images/ 内的文件解包到子项目根目录（与 images/ 同级）；其余位置一律本地解包到源文件所在目录。
            // Files under images/ unpack to the sub-project root (beside images/); anything else unpacks
            // locally into the directory containing the source file.
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

    /// <summary>Determines whether the source directory is the sub-project <c>images/</c> directory or inside it. 判断源目录是否为子项目 <c>images/</c> 目录或其子目录。</summary>
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