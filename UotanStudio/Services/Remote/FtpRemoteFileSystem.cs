using FluentFTP;
using UotanStudio.Services;

namespace UotanStudio.Services.Remote;

/// <summary>
/// FTP/FTPS remote file system backed by <c>FluentFTP</c>: the profile's encryption mode selects plain FTP,
/// explicit FTPS or implicit FTPS; operations are serialized because one client serves one session.
/// <para>基于 <c>FluentFTP</c> 的 FTP/FTPS 远程文件系统：配置档案的加密模式决定明文 FTP、显式 FTPS 或隐式 FTPS；"
/// "由于一个客户端同一时间只服务一个会话，操作会串行执行。</para>
/// </summary>
internal sealed class FtpRemoteFileSystem : IRemoteFileSystem
{
    private readonly RemoteConnectionProfile _profile;

    /// <summary>Serializes connect and data operations; the client is not safe for concurrent use.</summary>
    /// <para>串行化连接与数据操作；客户端不支持并发使用。</para>
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private AsyncFtpClient? _client;
    private bool _disposed;

    /// <summary>Creates the session for the given connection profile.<para>按连接配置档案创建会话。</para></summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    public FtpRemoteFileSystem(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>Gets the protocol of the underlying connection.<para>获取底层连接的协议。</para></summary>
    public RemoteProtocol Protocol => RemoteProtocol.Ftp;

    /// <summary>Gets a human readable connection description for logs and diagnostics.<para>获取用于日志与诊断的连接描述。</para></summary>
    public string DisplayName => $"ftp://{_profile.Host}:{_profile.Port}";

    /// <summary>Enumerates one remote directory through a LIST command.<para>通过 LIST 命令枚举一个远程目录。</para></summary>
    public async Task<IReadOnlyList<RemoteFileEntry>> EnumerateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await client.GetListing(directoryPath, cancellationToken).ConfigureAwait(false);
            var entries = new List<RemoteFileEntry>(items.Length);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Name))
                {
                    continue;
                }

                entries.Add(new RemoteFileEntry
                {
                    Name = item.Name,
                    FullPath = RemoteFileSystemService.CombinePath(directoryPath, item.Name),
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    Size = item.Type == FtpObjectType.Directory ? -1 : item.Size,
                    ModificationTime = item.RawModified == DateTime.MinValue ? null : new DateTimeOffset(item.RawModified, TimeSpan.Zero),
                });
            }

            return entries;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Returns the entry at a remote path, or null when it does not exist.<para>返回路径对应的条目；不存在时返回 null。</para></summary>
    public async Task<RemoteFileEntry?> TryGetEntryAsync(string remotePath, CancellationToken cancellationToken)
    {
        var parent = RemoteFileSystemService.GetParentPath(remotePath);
        var name = RemoteFileSystemService.GetEntryName(remotePath);
        var items = await EnumerateDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Downloads one remote file or directory tree to a local destination path.<para>把一个远程文件或目录树下载到本地目标路径。</para></summary>
    public async Task DownloadAsync(string remotePath, string localDestination, CancellationToken cancellationToken)
    {
        var entry = await TryGetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Remote entry not found: {remotePath}", remotePath);
        if (!entry.IsDirectory)
        {
            await DownloadFileAsync(remotePath, localDestination, cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(localDestination);
        var self = remotePath.TrimEnd('/');
        foreach (var child in await EnumerateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            // Guard against a server echoing the parent as its own child: recursing into it would loop forever.
            // <para>防御服务器把父目录当作自己的子项返回：递归进入它会无限循环。</para>
            if (string.Equals(child.FullPath.TrimEnd('/'), self, StringComparison.Ordinal))
            {
                continue;
            }

            await DownloadAsync(child.FullPath, Path.Combine(localDestination, child.Name), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Downloads one remote file into a fresh temporary directory and returns its local path.<para>把一个远程文件下载到新的临时目录并返回本地路径。</para></summary>
    public async Task<string> DownloadToTempAsync(string remoteFilePath, CancellationToken cancellationToken)
    {
        var fileName = RemoteFileSystemService.GetEntryName(remoteFilePath);
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = "downloaded_file";
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "UotanStudio", "RemoteTemp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var localPath = Path.Combine(tempDirectory, fileName);
        await DownloadFileAsync(remoteFilePath, localPath, cancellationToken).ConfigureAwait(false);
        return localPath;
    }

    /// <summary>Uploads one local file or directory tree; directories are created remotely before their children.<para>上传一个本地文件或目录树；目录会先远程创建再上传子项。</para></summary>
    public async Task UploadAsync(string localPath, string remoteDestination, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var endsWithSeparator = remoteDestination.EndsWith('/');
        if (Directory.Exists(localPath))
        {
            var targetDirectory = endsWithSeparator ? RemoteFileSystemService.CombinePath(remoteDestination, name) : remoteDestination;
            await CreateDirectoryAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
            {
                await UploadAsync(child, targetDirectory + "/", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var remoteFile = endsWithSeparator ? RemoteFileSystemService.CombinePath(remoteDestination, name) : remoteDestination;
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.UploadFile(localPath, remoteFile, FtpRemoteExists.Overwrite, createRemoteDir: false, FtpVerify.None, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Deletes one remote file or directory (directories are deleted recursively).<para>删除一个远程文件或目录（目录递归删除）。</para></summary>
    public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isDirectory)
            {
                await DeleteRemoteDirectoryAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFile(remotePath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Renames one remote entry by moving it within its parent directory.<para>通过在父目录内移动来重命名一个远程条目。</para></summary>
    public async Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken)
    {
        var destination = RemoteFileSystemService.CombinePath(RemoteFileSystemService.GetParentPath(remotePath), newName);
        var entry = await TryGetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry is { IsDirectory: true })
            {
                await client.MoveDirectory(remotePath, destination, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.MoveFile(remotePath, destination, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Creates one remote directory; missing parents are created on the way.<para>创建一个远程目录；缺失的父目录会一并创建。</para></summary>
    public async Task CreateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.CreateDirectory(directoryPath, force: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Closes the FTP connection and releases the lock.<para>关闭 FTP 连接并释放锁。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationLock.Dispose();
        _client?.Dispose();
        _client = null;
    }

    /// <summary>Connects lazily with the profile's encryption mode and reuses the established client.<para>按配置档案的加密模式惰性连接，并复用已建立的客户端。</para></summary>
    private async Task<AsyncFtpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is { IsConnected: true } connected)
        {
            return connected;
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true } existing)
            {
                return existing;
            }

            var username = _profile.IsAnonymous || string.IsNullOrWhiteSpace(_profile.Username) ? "anonymous" : _profile.Username;
            var password = _profile.IsAnonymous ? string.Empty : _profile.Password;
            var client = new AsyncFtpClient(_profile.Host, username, password, _profile.Port)
            {
                Config =
                {
                    EncryptionMode = _profile.Encryption switch
                    {
                        RemoteEncryptionMode.TlsImplicit => FtpEncryptionMode.Implicit,
                        RemoteEncryptionMode.TlsExplicit => FtpEncryptionMode.Explicit,
                        _ => FtpEncryptionMode.None,
                    },
                    DataConnectionEncryption = _profile.Encryption is not RemoteEncryptionMode.None,
                },
            };
            await client.Connect(cancellationToken).ConfigureAwait(false);
            _client = client;
            return client;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Downloads one remote file, creating its local parent directories on the way.<para>下载一个远程文件，并按需创建本地父目录。</para></summary>
    private async Task DownloadFileAsync(string remoteFilePath, string localFilePath, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fileInfo = new FileInfo(localFilePath);
            fileInfo.Directory?.Create();
            await client.DownloadFile(localFilePath, remoteFilePath, FtpLocalExists.Overwrite, FtpVerify.None, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Deletes a remote directory recursively.<para>递归删除远程目录。</para></summary>
    private static async Task DeleteRemoteDirectoryAsync(AsyncFtpClient client, string directoryPath, CancellationToken cancellationToken)
    {
        foreach (var child in await client.GetListing(directoryPath, cancellationToken).ConfigureAwait(false))
        {
            if (child.Type == FtpObjectType.Directory)
            {
                await DeleteRemoteDirectoryAsync(client, child.FullName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFile(child.FullName, cancellationToken).ConfigureAwait(false);
            }
        }

        await client.DeleteDirectory(directoryPath, cancellationToken).ConfigureAwait(false);
    }
}