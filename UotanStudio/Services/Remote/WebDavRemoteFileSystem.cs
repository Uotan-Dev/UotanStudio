using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace UotanStudio.Services.Remote;

/// <summary>
/// WebDAV remote file system implemented directly on <see cref="HttpClient"/>: PROPFIND lists a collection,
/// GET streams a file down, PUT uploads one, DELETE/MOVE/MKCOL cover the file-management primitives. No
/// third-party WebDAV package is used so the NativeAOT gate stays free of new trim or AOT warnings.
/// <para>直接基于 <see cref="HttpClient"/> 实现的 WebDAV 远程文件系统：PROPFIND 列举集合、GET 流式下载、PUT 上传，"
/// "DELETE/MOVE/MKCOL 覆盖文件管理原语。不引入第三方 WebDAV 包，使 NativeAOT 门禁不出现新的裁剪/AOT 警告。</para>
/// </summary>
internal sealed class WebDavRemoteFileSystem : IRemoteFileSystem
{
    /// <summary>The WebDAV namespace used by the well-known properties.<para>标准属性使用的 WebDAV 命名空间。</para></summary>
    private static readonly XNamespace Dav = "DAV:";

    /// <summary>The PROPFIND request body returning names, sizes, timestamps and resource types.<para>PROPFIND 请求体：返回名称、大小、时间与资源类型。</para></summary>
    private static readonly string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>"
        + "<d:displayname/><d:getcontentlength/><d:getlastmodified/><d:resourcetype/>"
        + "</d:prop></d:propfind>";

    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _displayName;

    /// <summary>
    /// Creates the session from a connection profile: the encryption mode selects http or https, and basic
    /// credentials are attached for a non-anonymous profile.
    /// <para>按连接配置档案创建会话：加密模式决定 http 或 https，非匿名档案附带基本凭据。</para>
    /// </summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    public WebDavRemoteFileSystem(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var scheme = profile.Encryption == RemoteEncryptionMode.Https ? "https" : "http";
        _endpoint = new Uri($"{scheme}://{profile.Host}:{profile.Port}/");
        _displayName = $"{scheme}://{profile.Host}:{profile.Port}";
        Protocol = profile.Protocol;

        var handler = new HttpClientHandler();
        if (!profile.IsAnonymous && !string.IsNullOrWhiteSpace(profile.Username))
        {
            handler.Credentials = new NetworkCredential(profile.Username, profile.Password);
            handler.PreAuthenticate = true;
        }

        _client = new HttpClient(handler)
        {
            // Large firmware pulls must not hit a client-side deadline; every call carries its own token.
            // <para>大文件下载不能被客户端超时打断；每次调用都携带自己的取消令牌。</para>
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Gets the protocol of the underlying connection.<para>获取底层连接的协议。</para></summary>
    public RemoteProtocol Protocol { get; }

    /// <summary>Gets a human readable connection description for logs and diagnostics.<para>获取用于日志与诊断的连接描述。</para></summary>
    public string DisplayName => _displayName;

    /// <summary>
    /// Enumerates one remote directory with a depth-one PROPFIND; the collection's own entry is skipped.
    /// <para>以 depth=1 的 PROPFIND 枚举一个远程目录；集合自身的条目会被跳过。</para>
    /// </summary>
    public async Task<IReadOnlyList<RemoteFileEntry>> EnumerateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var self = NormalizePath(directoryPath);
        var entries = new List<RemoteFileEntry>();
        foreach (var entry in await PropFindAsync(directoryPath, depthOne: true, cancellationToken).ConfigureAwait(false))
        {
            // Skip the collection's own entry and any response with no usable name: an empty name would make a
            // recursive download re-enter the same directory and never terminate.
            // <para>跳过集合自身的条目，以及任何没有可用名称的响应：空名会让递归下载重新进入同一目录而无法终止。</para>
            if (string.IsNullOrEmpty(entry.Name)
                || string.Equals(entry.FullPath.TrimEnd('/'), self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Returns the entry at a remote path, or null when it does not exist.<para>返回路径对应的条目；不存在时返回 null。</para></summary>
    public async Task<RemoteFileEntry?> TryGetEntryAsync(string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var entry in await PropFindAsync(remotePath, depthOne: false, cancellationToken).ConfigureAwait(false))
            {
                return entry;
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return null;
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
        var self = NormalizePath(remotePath);
        foreach (var child in await EnumerateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            // Guard against a server echoing the parent as its own child: recursing into it would loop forever.
            // <para>防御服务器把父目录当作自己的子项返回：递归进入它会无限循环。</para>
            if (string.Equals(NormalizePath(child.FullPath), self, StringComparison.OrdinalIgnoreCase))
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
        await using var content = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(remoteFile))
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Deletes one remote file or collection; WebDAV deletes collections recursively.<para>删除一个远程文件或集合；WebDAV 会递归删除集合。</para></summary>
    public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(remotePath));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Remote entry not found: {remotePath}", remotePath);
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Renames one remote entry by moving it within its parent directory.<para>通过在父目录内移动来重命名一个远程条目。</para></summary>
    public async Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken)
    {
        var destination = RemoteFileSystemService.CombinePath(RemoteFileSystemService.GetParentPath(remotePath), newName);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), BuildUri(remotePath));
        request.Headers.Add("Destination", BuildUri(destination).AbsoluteUri);
        // Refuse to replace an existing entry: the file manager never silently overwrites.
        // <para>拒绝覆盖已存在条目：文件管理器从不静默覆盖。</para>
        request.Headers.Add("Overwrite", "F");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException($"The remote destination already exists: {destination}");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates one remote collection; an already existing collection counts as success.<para>创建一个远程集合；集合已存在时视为成功。</para></summary>
    public async Task CreateDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildUri(directoryPath));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Disposes the HTTP transport.<para>释放 HTTP 传输。</para></summary>
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Runs one PROPFIND and parses the multistatus response into entries; a 404 becomes
    /// <see cref="FileNotFoundException"/>.
    /// <para>执行一次 PROPFIND 并把 multistatus 响应解析为条目；404 转为 <see cref="FileNotFoundException"/>。</para>
    /// </summary>
    private async Task<IReadOnlyList<RemoteFileEntry>> PropFindAsync(string remotePath, bool depthOne, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildUri(remotePath))
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depthOne ? "1" : "0");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Remote path not found: {remotePath}", remotePath);
        }

        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<RemoteFileEntry>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return entries;
        }

        var document = XDocument.Parse(xml);
        foreach (var element in document.Descendants(Dav + "response"))
        {
            if (ParseResponse(element) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Parses one multistatus response element into an entry.<para>把 multistatus 中的一个响应元素解析为条目。</para></summary>
    private static RemoteFileEntry? ParseResponse(XElement response)
    {
        var href = response.Element(Dav + "href")?.Value;
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(href);
        if (Uri.TryCreate(decoded, UriKind.Absolute, out var absolute))
        {
            decoded = absolute.AbsolutePath;
        }

        var fullPath = NormalizePath(decoded.StartsWith('/') ? decoded : "/" + decoded);
        var propstat = response.Elements(Dav + "propstat")
            .FirstOrDefault(element => (string?)element.Element(Dav + "status") is { } status
                && status.Contains(" 200 ", StringComparison.Ordinal));
        var prop = propstat?.Element(Dav + "prop");
        var isCollection = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
        return new RemoteFileEntry
        {
            Name = RemoteFileSystemService.GetEntryName(fullPath),
            FullPath = fullPath,
            IsDirectory = isCollection,
            Size = isCollection ? -1 : ParseLength(prop?.Element(Dav + "getcontentlength")?.Value),
            ModificationTime = ParseDate(prop?.Element(Dav + "getlastmodified")?.Value),
        };
    }

    /// <summary>Downloads one remote file to a local file path.<para>把一个远程文件下载到本地文件路径。</para></summary>
    private async Task DownloadFileAsync(string remoteFilePath, string localFilePath, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(BuildUri(remoteFilePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Remote file not found: {remoteFilePath}", remoteFilePath);
        }

        response.EnsureSuccessStatusCode();
        var fileInfo = new FileInfo(localFilePath);
        fileInfo.Directory?.Create();
        await using var localStream = fileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the absolute URI of a remote path beneath the endpoint.<para>构造远程路径在端点下的绝对 URI。</para></summary>
    private Uri BuildUri(string remotePath)
    {
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(_endpoint.AbsoluteUri);
        foreach (var segment in segments)
        {
            builder.Append(Uri.EscapeDataString(segment)).Append('/');
        }

        return new Uri(builder.ToString());
    }

    /// <summary>Normalizes a remote path for comparisons: rooted and without a trailing slash.<para>把远程路径规范成用于比较的形态：以 '/' 开头且无尾斜杠。</para></summary>
    private static string NormalizePath(string remotePath)
    {
        var trimmed = remotePath.Trim().TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>Parses a byte length, returning -1 when absent or malformed.<para>解析字节长度；缺失或格式错误时返回 -1。</para></summary>
    private static long ParseLength(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : -1;

    /// <summary>Parses an HTTP-date timestamp, returning null when absent or malformed.<para>解析 HTTP 日期；缺失或格式错误时返回 null。</para></summary>
    private static DateTimeOffset? ParseDate(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}