namespace UotanStudio.Services.Firmware;

/// <summary>Captures paths before an asynchronous image operation. 在异步镜像操作前固定路径。</summary>
internal sealed record ImageOperationPaths(string Source, bool IsDirectory, string Root, string OutputRoot)
{
    internal string Name => IsDirectory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Source))
        : Path.GetFileNameWithoutExtension(Source);

    internal string UnpackedDirectory
    {
        get
        {
            var path = Path.GetFullPath(IsDirectory ? Source : Path.Combine(Root, Name));
            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)),
                    StringComparison.OrdinalIgnoreCase)
                || Name is "" or "." or ".."
                || new[] { "images", "output", ".uotan", ".deleted" }.Contains(Name, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The unpacked directory must be a non-reserved child of the sub-project root.");
            return path;
        }
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