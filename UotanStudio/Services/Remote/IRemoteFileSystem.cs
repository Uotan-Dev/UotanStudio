using UotanStudio.Services;

namespace UotanStudio.Services.Remote;

/// <summary>
/// A session against one remote file server: directory listing, download/upload and the file-management
/// primitives the multi-column file browser needs. Implementations serialize their operations and must be
/// disposed once the owning column closes.
/// <para>与一台远程文件服务器的会话：目录枚举、下载/上传以及多列文件浏览器所需的文件管理原语。"
/// "实现内部串行化操作，所属列关闭时必须释放。</para>
/// </summary>
public interface IRemoteFileSystem : IDisposable
{
    /// <summary>Gets the protocol of the underlying connection.<para>获取底层连接的协议。</para></summary>
    RemoteProtocol Protocol { get; }

    /// <summary>Gets a human readable connection description for logs and diagnostics.<para>获取用于日志与诊断的连接描述。</para></summary>
    string DisplayName { get; }

    /// <summary>
    /// Enumerates one remote directory. Hidden entries are included; "." and ".." are never returned.
    /// <para>枚举一个远程目录；包含隐藏条目，但不返回 "." 与 ".."。</para>
    /// </summary>
    /// <param name="directoryPath">Absolute remote directory path.<para>远程目录绝对路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The directory's entries.<para>该目录的条目列表。</para></returns>
    Task<IReadOnlyList<RemoteFileEntry>> EnumerateDirectoryAsync(string directoryPath, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the entry at a remote path, or null when it does not exist.
    /// <para>返回路径对应的条目；不存在时返回 null。</para>
    /// </summary>
    /// <param name="remotePath">Absolute remote path.<para>远程绝对路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The entry, or null when missing.<para>条目；缺失时为 null。</para></returns>
    Task<RemoteFileEntry?> TryGetEntryAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one remote file or directory tree to a local destination path; directories are recreated
    /// recursively beneath it.
    /// <para>把一个远程文件或目录树下载到本地目标路径；目录会递归重建在其下。</para>
    /// </summary>
    /// <param name="remotePath">Absolute remote file or directory path.<para>远程文件或目录绝对路径。</para></param>
    /// <param name="localDestination">Local destination file or directory path.<para>本地目标文件或目录路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The download task.<para>下载任务。</para></returns>
    Task DownloadAsync(string remotePath, string localDestination, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one remote file into a fresh temporary directory beneath <c>%TEMP%/UotanStudio/RemoteTemp</c>
    /// and returns the local file path.
    /// <para>把一个远程文件下载到 <c>%TEMP%/UotanStudio/RemoteTemp</c> 之下的全新临时目录并返回本地文件路径。</para>
    /// </summary>
    /// <param name="remoteFilePath">Absolute remote file path.<para>远程文件绝对路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The local temporary file path.<para>本地临时文件路径。</para></returns>
    Task<string> DownloadToTempAsync(string remoteFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads one local file or directory tree. A remote destination ending with '/' receives the entry
    /// inside itself; otherwise the destination is taken as the entry's target path.
    /// <para>上传一个本地文件或目录树。远程目标以 '/' 结尾时条目放入其内；否则目标即条目的目标路径。</para>
    /// </summary>
    /// <param name="localPath">Local file or directory path.<para>本地文件或目录路径。</para></param>
    /// <param name="remoteDestination">Remote destination directory or path.<para>远程目标目录或路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The upload task.<para>上传任务。</para></returns>
    Task UploadAsync(string localPath, string remoteDestination, CancellationToken cancellationToken);

    /// <summary>Deletes one remote file or directory (directories are deleted recursively).<para>删除一个远程文件或目录（目录递归删除）。</para></summary>
    /// <param name="remotePath">Absolute remote path.<para>远程绝对路径。</para></param>
    /// <param name="isDirectory">Whether the entry is a directory.<para>该条目是否为目录。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The delete task.<para>删除任务。</para></returns>
    Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken);

    /// <summary>Renames one remote entry within its parent directory.<para>在原父目录内重命名一个远程条目。</para></summary>
    /// <param name="remotePath">Current absolute remote path.<para>当前远程绝对路径。</para></param>
    /// <param name="newName">New entry name within the same parent.<para>同一父目录下的新条目名。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The rename task.<para>重命名任务。</para></returns>
    Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken);

    /// <summary>Creates one remote directory; underlying protocols may create missing parents.<para>创建一个远程目录；底层协议按需创建缺失的父目录。</para></summary>
    /// <param name="directoryPath">Absolute remote directory path.<para>远程目录绝对路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The creation task.<para>创建任务。</para></returns>
    Task CreateDirectoryAsync(string directoryPath, CancellationToken cancellationToken);
}