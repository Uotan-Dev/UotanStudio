namespace UotanStudio.Services.FileManagement;

/// <summary>Entry kinds exposed by a browser session.<para>浏览器会话暴露的条目类型。</para></summary>
internal enum ImageBrowserEntryKind
{
    File,
    Directory,
    Symlink,
}

/// <summary>A flat image entry used for indexing and selected-path extraction.<para>用于索引和选定路径提取的扁平镜像条目。</para></summary>
/// <param name="Path">Relative path inside the image.<para>镜像内的相对路径。</para></param>
/// <param name="Size">File size in bytes.<para>以字节计的文件大小。</para></param>
/// <param name="Kind">File, directory or symbolic link.<para>文件、目录或符号链接。</para></param>
internal sealed record ImageBrowserEntry(string Path, long Size, ImageBrowserEntryKind Kind);