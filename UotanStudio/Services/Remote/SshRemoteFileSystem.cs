using Renci.SshNet;
using UotanStudio.Services;

namespace UotanStudio.Services.Remote;

/// <summary>
/// SSH-backed remote file system for SFTP and SCP profiles. Browsing and transfer go through the SSH
/// SFTP subsystem, which OpenSSH servers provide alongside the SCP command; operations are serialized
/// because one <see cref="SftpClient"/> serves one session at a time.
/// <para>基于 SSH 的远程文件系统，服务 SFTP 与 SCP 档案。浏览与传输经由 SSH SFTP 子系统完成（OpenSSH 服务器在提供 SCP 命令的同时也提供该子系统）；"
/// "由于一个 <see cref="SftpClient"/> 同一时间只服务一个会话，操作会串行执行。</para>
/// </summary>
internal sealed class SshRemoteFileSystem : IRemoteFileSystem
{
    private readonly RemoteConnectionProfile _profile;

    /// <summary>Serializes connect and data operations; <see cref="SftpClient"/> is not safe for concurrent use.</summary>
    /// <para>串行化连接与数据操作；<see cref="SftpClient"/> 不支持并发使用。</para>
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private SftpClient? _client;
    private bool _disposed;

    /// <summary>Creates the session for the given connection profile.<para>按连接配置档案创建会话。</para></summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    public SshRemoteFileSystem(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>Gets the protocol of the underlying connection.<para>获取底层连接的协议。</para></summary>
    public RemoteProtocol Protocol => _profile.Protocol;

    /// <summary>Gets a human readable connection description for logs and diagnostics.<para>获取用于日志与诊断的连接描述。</para></summary>
    public string DisplayName => $"{(_profile.Protocol == RemoteProtocol.Scp ? "scp" : "sftp")}://{_profile.Host}:{_profile.Port}";

    /// <summary>Enumerates one remote directory through the SFTP subsystem.<para>经 SFTP 子系统枚举一个远程目录。</para></summary>
    public async Task<IReadOnlyList<RemoteFileEntry>> EnumerateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = new List<RemoteFileEntry>();
            await foreach (var item in client.ListDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name is "." or ".." || string.IsNullOrEmpty(item.Name))
                {
                    continue;
                }

                entries.Add(new RemoteFileEntry
                {
                    Name = item.Name,
                    FullPath = RemoteFileSystemService.CombinePath(directoryPath, item.Name),
                    IsDirectory = item.IsDirectory,
                    Size = item.IsDirectory ? -1 : item.Length,
                    ModificationTime = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
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
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var attributes = await client.GetAttributesAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return new RemoteFileEntry
            {
                Name = RemoteFileSystemService.GetEntryName(remotePath),
                FullPath = remotePath,
                IsDirectory = attributes.IsDirectory,
                Size = attributes.IsDirectory ? -1 : attributes.Size,
                ModificationTime = new DateTimeOffset(attributes.LastWriteTimeUtc, TimeSpan.Zero),
            };
        }
        finally
        {
            _operationLock.Release();
        }
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
        var self = RemoteFileSystemService.GetParentPath(remotePath) == remotePath
            ? remotePath
            : remotePath.TrimEnd('/');
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
            await using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var remoteStream = client.OpenWrite(remoteFile);
            await localStream.CopyToAsync(remoteStream, cancellationToken).ConfigureAwait(false);
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
                client.DeleteFile(remotePath);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Renames one remote entry within its parent directory.<para>在原父目录内重命名一个远程条目。</para></summary>
    public async Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken)
    {
        var destination = RemoteFileSystemService.CombinePath(RemoteFileSystemService.GetParentPath(remotePath), newName);
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            client.RenameFile(remotePath, destination);
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
            client.CreateDirectory(directoryPath);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Closes the SSH connection and releases the lock.<para>关闭 SSH 连接并释放锁。</para></summary>
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

    /// <summary>Connects lazily and reuses the established client across operations.<para>惰性连接，并在多个操作间复用已建立的客户端。</para></summary>
    private async Task<SftpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
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
            var client = new SftpClient(_profile.Host, _profile.Port, username, password)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                OperationTimeout = TimeSpan.FromSeconds(30),
            };
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!client.IsConnected)
            {
                client.Dispose();
                throw new InvalidOperationException($"SSH connection to {_profile.Host}:{_profile.Port} was not established.");
            }

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
            await using var localStream = fileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await using var remoteStream = client.OpenRead(remoteFilePath);
            await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Deletes a remote directory recursively; SFTP has no recursive delete of its own.<para>递归删除远程目录；SFTP 自身没有递归删除。</para></summary>
    private static async Task DeleteRemoteDirectoryAsync(SftpClient client, string directoryPath, CancellationToken cancellationToken)
    {
        await foreach (var child in client.ListDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false))
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            if (child.IsDirectory)
            {
                await DeleteRemoteDirectoryAsync(client, child.FullName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                client.DeleteFile(child.FullName);
            }
        }

        client.DeleteDirectory(directoryPath);
    }
}