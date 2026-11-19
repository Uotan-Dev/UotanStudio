namespace UotanStudio.Services.Remote;

/// <summary>
/// One file or directory entry on a remote file server (SFTP, SCP, FTP or WebDAV).
/// <para>远程文件服务器（SFTP、SCP、FTP 或 WebDAV）上的一个文件或目录条目。</para>
/// </summary>
public sealed class RemoteFileEntry
{
    /// <summary>Entry name (file or folder name).<para>条目名称（文件或文件夹名）。</para></summary>
    public required string Name { get; init; }

    /// <summary>Absolute path on the remote server ('/'-separated).<para>远程服务器上的绝对路径（'/' 分隔）。</para></summary>
    public required string FullPath { get; init; }

    /// <summary>Whether this entry is a directory.<para>是否为目录。</para></summary>
    public required bool IsDirectory { get; init; }

    /// <summary>File size in bytes; -1 for directories.<para>文件大小（字节）；目录为 -1。</para></summary>
    public long Size { get; init; } = -1;

    /// <summary>Date and time of last modification, or null when the server does not report one.<para>最后修改时间；服务器未报告时为 null。</para></summary>
    public DateTimeOffset? ModificationTime { get; init; }
}