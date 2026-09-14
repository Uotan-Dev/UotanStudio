namespace UotanStudio.Services.FileManagement;

/// <summary>Entry kinds exposed by a browser session, independent of UI node types.</summary>
internal enum ImageBrowserEntryKind
{
    File,
    Directory,
    Symlink,
}

/// <summary>A flat image entry used for indexing and selected-path extraction.</summary>
/// <param name="Path">Relative path inside the image.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Kind">File, directory or symbolic link.</param>
internal sealed record ImageBrowserEntry(string Path, long Size, ImageBrowserEntryKind Kind);