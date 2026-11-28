using UotanStudio.Services;
using UotanStudio.Services.Remote;

namespace UotanStudio.ViewModels.Editors.MultiColumnBrowser;

/// <summary>
/// The remote column part of a file column: browsing a remote file server (SFTP, SCP, FTP, WebDAV) shares the
/// browsing-tree machinery with device columns but routes content, transfer and file management through one
/// <see cref="IRemoteFileSystem"/> session owned by this column.
/// <para>文件列的远程部分：浏览远程文件服务器（SFTP、SCP、FTP、WebDAV）复用设备列的浏览树机制，但内容读取、"
/// "传输与文件管理都由本列持有的一个 <see cref="IRemoteFileSystem"/> 会话承担。</para>
/// </summary>
public sealed partial class CompareColumnViewModel
{
    private IRemoteFileSystem? _remoteSession;

    /// <summary>
    /// The remote connection this column browses, or null for local filesystem and device columns.
    /// <para>本列浏览的远程连接；本地文件系统与设备列为 null。</para>
    /// </summary>
    public RemoteConnectionProfile? RemoteTarget { get; private set; }

    /// <summary>
    /// Whether this column browses a remote file server.
    /// <para>本列是否正在浏览远程文件服务器。</para>
    /// </summary>
    public bool IsRemoteColumn => RemoteTarget is not null;

    /// <summary>
    /// Gets the session of this column, creating it on first use so the connection is only opened when the
    /// column actually loads content.
    /// <para>获取本列的会话；首次使用时才创建，因此只有在列真正加载内容时才会建立连接。</para>
    /// </summary>
    internal IRemoteFileSystem RemoteSession
        => _remoteSession ??= RemoteFileSystemService.Instance.CreateSession(
            RemoteTarget ?? throw new InvalidOperationException("The column has no remote target."));

    /// <summary>
    /// Loads a remote directory into this column: the column header shows the connection description and the
    /// remote path, and the body gets a browsing tree whose children load on demand.
    /// <para>把远程目录加载到本列：列头显示连接描述与远程路径，列体挂一棵按需加载子项的浏览树。</para>
    /// </summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    /// <param name="remotePath">Optional remote directory path; empty uses the profile's initial path.<para>可选远程目录路径；为空时使用档案的初始路径。</para></param>
    /// <returns>The load task.<para>加载任务。</para></returns>
    public async Task LoadRemotePathAsync(RemoteConnectionProfile profile, string? remotePath = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RemoteTarget = profile;
        DeviceTarget = null;
        Path = string.IsNullOrWhiteSpace(remotePath) ? RemoteFileSystemService.GetDefaultPath(profile) : remotePath.Trim();
        OnPropertyChanged(nameof(IsRemoteColumn));
        await LoadRemoteContentAsync(Path);
    }

    /// <summary>
    /// Reloads this column's remote directory from its current path.
    /// <para>按当前路径重新加载本列的远程目录。</para>
    /// </summary>
    /// <returns>The load task.<para>加载任务。</para></returns>
    internal Task ReloadRemoteContentAsync() => LoadRemoteContentAsync((Path ?? string.Empty).Trim());

    /// <summary>
    /// Deletes one entry in this column's remote directory.
    /// <para>删除本列远程目录中的一个条目。</para>
    /// </summary>
    /// <param name="remotePath">Remote file or directory path.<para>远程文件或目录路径。</para></param>
    /// <param name="isDirectory">Whether the entry is a directory.<para>该条目是否为目录。</para></param>
    /// <returns>The delete task.<para>删除任务。</para></returns>
    public Task DeleteRemoteEntryAsync(string remotePath, bool isDirectory)
        => RemoteSession.DeleteAsync(remotePath, isDirectory, CancellationToken.None);

    /// <summary>Renames one entry in this column's remote directory, keeping it in the same parent directory.<para>重命名本列远程目录中的一个条目，保持在原父目录下。</para></summary>
    /// <param name="remotePath">Current remote path.<para>当前远程路径。</para></param>
    /// <param name="newName">New entry name within the same parent.<para>同一父目录下的新条目名。</para></param>
    /// <returns>The rename task.<para>重命名任务。</para></returns>
    public Task RenameRemoteEntryAsync(string remotePath, string newName)
        => RemoteSession.RenameAsync(remotePath, newName, CancellationToken.None);

    /// <summary>Creates a directory next to or inside this column's remote entry.<para>在本列远程条目之内或同级创建一个目录。</para></summary>
    /// <param name="remotePath">Remote entry the new folder is created for.<para>新建目录所对应的远程条目。</para></param>
    /// <param name="isDirectory">Whether <paramref name="remotePath"/> is a directory.<para><paramref name="remotePath"/> 是否为目录。</para></param>
    /// <param name="folderName">New folder name.<para>新目录名。</para></param>
    /// <returns>The creation task.<para>创建任务。</para></returns>
    public Task CreateRemoteDirectoryAsync(string remotePath, bool isDirectory, string folderName)
    {
        var parent = isDirectory ? remotePath : RemoteFileSystemService.GetParentPath(remotePath);
        return RemoteSession.CreateDirectoryAsync(RemoteFileSystemService.CombinePath(parent, folderName), CancellationToken.None);
    }

    /// <summary>Copies one local path up to this column's remote directory.<para>把一个本地路径上传到本列的远程目录。</para></summary>
    /// <param name="localPath">Local file or directory to upload.<para>待上传的本地文件或目录。</para></param>
    /// <param name="remoteDestination">Remote directory or file path; a trailing slash uploads inside it.<para>远程目录或文件路径；以斜杠结尾时上传入该目录内。</para></param>
    /// <returns>The upload task.<para>上传任务。</para></returns>
    public Task UploadToRemoteAsync(string localPath, string remoteDestination)
        => UploadToRemoteAsync(localPath, remoteDestination, CancellationToken.None);

    /// <summary>Copies one local path up to this column's remote directory.<para>把一个本地路径上传到本列的远程目录。</para></summary>
    /// <param name="localPath">Local file or directory to upload.<para>待上传的本地文件或目录。</para></param>
    /// <param name="remoteDestination">Remote directory or file path; a trailing slash uploads inside it.<para>远程目录或文件路径；以斜杠结尾时上传入该目录内。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The upload task.<para>上传任务。</para></returns>
    public Task UploadToRemoteAsync(string localPath, string remoteDestination, CancellationToken cancellationToken)
        => RemoteSession.UploadAsync(localPath, remoteDestination, cancellationToken);

    /// <summary>Copies one remote entry of this column down to a local directory.<para>把本列的一个远程条目下载到本地目录。</para></summary>
    /// <param name="remotePath">Remote file or directory to download.<para>待下载的远程文件或目录。</para></param>
    /// <param name="localDestination">Local directory or file path; an existing directory receives the entry name.<para>本地目录或文件路径；已存在的目录按条目名接收。</para></param>
    /// <returns>The download task.<para>下载任务。</para></returns>
    public Task DownloadFromRemoteAsync(string remotePath, string localDestination)
        => DownloadFromRemoteAsync(remotePath, localDestination, CancellationToken.None);

    /// <summary>Copies one remote entry of this column down to a local directory.<para>把本列的一个远程条目下载到本地目录。</para></summary>
    /// <param name="remotePath">Remote file or directory to download.<para>待下载的远程文件或目录。</para></param>
    /// <param name="localDestination">Local directory or file path; an existing directory receives the entry name.<para>本地目录或文件路径；已存在的目录按条目名接收。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The download task.<para>下载任务。</para></returns>
    public Task DownloadFromRemoteAsync(string remotePath, string localDestination, CancellationToken cancellationToken)
    {
        var target = Directory.Exists(localDestination)
            ? System.IO.Path.Combine(localDestination, RemoteFileSystemService.GetEntryName(remotePath))
            : localDestination;
        return RemoteSession.DownloadAsync(remotePath, target, cancellationToken);
    }

    /// <summary>Copies one remote file of this column into a local temporary file and returns its path.<para>把本列的一个远程文件复制到本地临时文件并返回其路径。</para></summary>
    /// <param name="remotePath">Remote file path.<para>远程文件路径。</para></param>
    /// <returns>The local temporary file path.<para>本地临时文件路径。</para></returns>
    public Task<string> DownloadRemoteEntryToTempAsync(string remotePath)
        => RemoteSession.DownloadToTempAsync(remotePath, CancellationToken.None);

    /// <summary>
    /// Reads a remote text file's lines; returns <c>null</c> when the file is not textual so the caller falls back
    /// to the metadata view.
    /// <para>读取一个远程文本文件的行；文件不是文本时返回 <c>null</c>，调用方改用元数据视图。</para>
    /// </summary>
    /// <param name="remotePath">Remote file path.<para>远程文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The file's lines, or null for a binary file.<para>文件行内容；二进制文件为 null。</para></returns>
    internal async Task<IReadOnlyList<string>?> ReadRemoteTextLinesAsync(string remotePath, CancellationToken cancellationToken)
    {
        var localPath = await RemoteSession.DownloadToTempAsync(remotePath, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _owner.ReadTextLinesAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTempFile(localPath);
        }
    }

    /// <summary>
    /// Releases the remote session, closing its connection.
    /// <para>释放远程会话并关闭其连接。</para>
    /// </summary>
    private void DisposeRemoteSession()
    {
        _remoteSession?.Dispose();
        _remoteSession = null;
    }

    /// <summary>Loads a remote directory into the browsing tree and rebuilds the visible rows.<para>把远程目录加载进浏览树并重建可见行。</para></summary>
    private async Task LoadRemoteContentAsync(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var ct = BeginLoad();
        IsBusy = true;
        try
        {
            var session = RemoteSession;
            Task<IReadOnlyList<CompareTreeNodeViewModel>> LoadChildrenAsync(CompareTreeNodeViewModel parentNode, CancellationToken token)
                => LoadRemoteChildrenAsync(session, parentNode.FullPath, token);

            var rootName = GetRemoteRootName(remotePath);
            var root = CompareTreeNodeViewModel.ForRemote(rootName, remotePath, LoadChildrenAsync, ct);
            TreeRoot = root;
            await root.EnsureChildrenLoadedAsync();
            ct.ThrowIfCancellationRequested();
            _ownRows = null;
            _isDirty = false;
            _hasEditableRows = false;
            IsTextEditorVisible = false;
            Rows = BuildBrowseRows();
            OnPropertyChanged(nameof(HasPath));
            NotifyContentChanged();
            _owner.ClearError();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            TreeRoot = null;
            Rows = null;
            _owner.ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }

            NotifyContentChanged();
        }
    }

    /// <summary>Enumerates one remote directory into tree nodes, giving directories a lazy child loader.<para>枚举一个远程目录并生成树节点，目录节点带上惰性子项加载器。</para></summary>
    private static async Task<IReadOnlyList<CompareTreeNodeViewModel>> LoadRemoteChildrenAsync(
        IRemoteFileSystem session,
        string remoteDirectoryPath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entries = await session.EnumerateDirectoryAsync(remoteDirectoryPath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        Task<IReadOnlyList<CompareTreeNodeViewModel>> ChildLoader(CompareTreeNodeViewModel parentNode, CancellationToken childToken)
            => LoadRemoteChildrenAsync(session, parentNode.FullPath, childToken);

        var results = new List<CompareTreeNodeViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            results.Add(CompareTreeNodeViewModel.ForRemoteEntry(
                entry,
                entry.IsDirectory ? ChildLoader : null,
                token));
        }

        return results;
    }

    /// <summary>Returns the display name of a remote root path ('/' for the server root).<para>取远程根路径的显示名（服务器根目录为 '/'）。</para></summary>
    private static string GetRemoteRootName(string remotePath)
    {
        var name = RemoteFileSystemService.GetEntryName(remotePath);
        return string.IsNullOrEmpty(name) ? "/" : name;
    }

    /// <summary>Best-effort removal of a downloaded temporary file.<para>尽力删除已下载的临时文件。</para></summary>
    private static void TryDeleteTempFile(string localPath)
    {
        try
        {
            File.Delete(localPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}