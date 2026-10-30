using System.Text.Json;
using UotanStudio.Net.Http;

namespace UotanStudio.Net.GitHub;

/// <summary>Thrown when a release API response exceeds <see cref="GitHubReleaseClient.MaximumResponseBytes"/>.
/// <para>Release API 响应超过 <see cref="GitHubReleaseClient.MaximumResponseBytes"/> 时抛出。</para></summary>
/// <remarks>Derived from <see cref="InvalidOperationException"/> so existing callers stay source-compatible.
/// <para>派生自 <see cref="InvalidOperationException"/>，既有调用方保持兼容。</para></remarks>
public sealed class GitHubResponseTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads release metadata from the <c>api.github.com</c> origin, optionally through a caller-ranked source
/// list that may deliver documents from mirrors.
/// <para>从 <c>api.github.com</c> 源站读取 Release 元数据；也可按调用方排序的源列表从镜像交付文档。</para>
/// </summary>
/// <remarks>
/// Every response body is capped at one MiB and parsed through a source-generated JSON context, so the
/// metadata path stays bounded and NativeAOT-safe. The artifact itself is downloaded separately and
/// verified against the <see cref="GitHubAsset.Size"/> and <see cref="GitHubAsset.Sha256"/> reported here.
/// <para>每个响应体上限为一 MiB，并经源生成 JSON 上下文解析，因此元数据路径有界且在 NativeAOT 下可用。产物本体另行下载，
/// 并用此处上报的 <see cref="GitHubAsset.Size"/> 与 <see cref="GitHubAsset.Sha256"/> 校验。</para>
/// </remarks>
public static class GitHubReleaseClient
{
    /// <summary>Origin used for every release metadata request.
    /// <para>所有 Release 元数据请求使用的源站。</para></summary>
    public const string ApiOrigin = "https://api.github.com";

    /// <summary>Maximum accepted response body size in bytes.
    /// <para>可接受的响应体最大字节数。</para></summary>
    public const int MaximumResponseBytes = 1024 * 1024;

    /// <summary>Fetches the newest published release of a repository.
    /// <para>获取仓库最新已发布的 Release。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="owner">Repository owner; must be a single path segment.
    /// <para>仓库拥有者；必须是单个路径段。</para></param>
    /// <param name="repo">Repository name; must be a single path segment.
    /// <para>仓库名；必须是单个路径段。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <returns>The normalized release description.
    /// <para>已归一化的 Release 描述。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="repo"/> is empty or contains a path separator.
    /// <para><paramref name="owner"/> 或 <paramref name="repo"/> 为空或包含路径分隔符。</para></exception>
    /// <exception cref="HttpRequestException">The origin returned a non-success status; the exception carries the status code.
    /// <para>源站返回非成功状态；异常携带状态码。</para></exception>
    /// <exception cref="GitHubResponseTooLargeException">The response exceeds the size cap.
    /// <para>响应超出大小上限。</para></exception>
    /// <exception cref="InvalidOperationException">The response carries no document or tag, or describes an asset without a name or download URL.
    /// <para>响应没有文档或标签，或资产缺少名称或下载地址。</para></exception>
    /// <exception cref="JsonException">The response body is not valid release JSON.
    /// <para>响应体不是有效的 Release JSON。</para></exception>
    public static Task<GitHubRelease> GetLatestAsync(HttpClient client, string owner, string repo,
        CancellationToken cancellationToken)
        => GetAsync(client, BuildLatestUri(owner, repo), owner, repo, cancellationToken);

    /// <summary>Fetches one published release of a repository by tag.
    /// <para>按标签获取仓库的一个已发布 Release。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="owner">Repository owner; must be a single path segment.
    /// <para>仓库拥有者；必须是单个路径段。</para></param>
    /// <param name="repo">Repository name; must be a single path segment.
    /// <para>仓库名；必须是单个路径段。</para></param>
    /// <param name="tag">Release tag; escaped as one path segment.
    /// <para>Release 标签；按单个路径段转义。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <returns>The normalized release description.
    /// <para>已归一化的 Release 描述。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="repo"/> is empty or contains a path separator, or <paramref name="tag"/> is empty.
    /// <para><paramref name="owner"/> 或 <paramref name="repo"/> 为空或包含路径分隔符，或 <paramref name="tag"/> 为空。</para></exception>
    /// <exception cref="HttpRequestException">The origin returned a non-success status, including 404 for an unknown tag; the exception carries the status code.
    /// <para>源站返回非成功状态（未知标签为 404）；异常携带状态码。</para></exception>
    /// <exception cref="GitHubResponseTooLargeException">The response exceeds the size cap.
    /// <para>响应超出大小上限。</para></exception>
    /// <exception cref="InvalidOperationException">The response carries no document or tag, or describes an asset without a name or download URL.
    /// <para>响应没有文档或标签，或资产缺少名称或下载地址。</para></exception>
    /// <exception cref="JsonException">The response body is not valid release JSON.
    /// <para>响应体不是有效的 Release JSON。</para></exception>
    public static Task<GitHubRelease> GetByTagAsync(HttpClient client, string owner, string repo, string tag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var uri = new Uri($"{ApiOrigin}/repos/{RequireSegment(owner, nameof(owner))}/{RequireSegment(repo, nameof(repo))}/releases/tags/{Uri.EscapeDataString(tag.Trim())}");
        return GetAsync(client, uri, owner, repo, cancellationToken);
    }

    /// <summary>Default number of releases returned by <see cref="ListAsync"/>.
    /// <para><see cref="ListAsync"/> 默认返回的 Release 数量。</para></summary>
    public const int DefaultListCount = 20;

    /// <summary>Upper bound accepted for the release count of one <see cref="ListAsync"/> request.
    /// <para>单次 <see cref="ListAsync"/> 请求可接受的 Release 数量上限。</para></summary>
    public const int MaximumListCount = 100;

    /// <summary>Fetches published releases of a repository, newest first, without drafts.
    /// <para>获取仓库的已发布 Release（由新到旧），不含草稿。</para></summary>
    /// <remarks>When the document exceeds the response cap, the count is halved and the request is retried, so a
    /// repository with long release notes still yields its newest releases. <para>响应文档超过上限时会把数量减半后重试，
    /// 使发布说明很长的仓库仍能取到最新 Release。</para></remarks>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="owner">Repository owner; must be a single path segment.
    /// <para>仓库拥有者；必须是单个路径段。</para></param>
    /// <param name="repo">Repository name; must be a single path segment.
    /// <para>仓库名；必须是单个路径段。</para></param>
    /// <param name="maximum">Requested release count, clamped to 1 through <see cref="MaximumListCount"/>; reduced on
    /// retry when the document exceeds the cap. <para>请求的 Release 数量，收敛到 1 至 <see cref="MaximumListCount"/>；
    /// 文档超过上限重试时会减少。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <returns>Published releases in API order, drafts removed; pre-releases stay listed.
    /// <para>按 API 顺序的已发布 Release，草稿已移除；预发布仍保留在列表中。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="repo"/> is empty or contains a path separator.
    /// <para><paramref name="owner"/> 或 <paramref name="repo"/> 为空或包含路径分隔符。</para></exception>
    /// <exception cref="HttpRequestException">The origin returned a non-success status; the exception carries the status code.
    /// <para>源站返回非成功状态；异常携带状态码。</para></exception>
    /// <exception cref="GitHubResponseTooLargeException">Even a single release exceeds the size cap.
    /// <para>即使只请求一个 Release 也超出大小上限。</para></exception>
    /// <exception cref="InvalidOperationException">The response carries no document list, or describes a release without a tag or an asset without a name or download URL.
    /// <para>响应没有文档列表，或描述了缺少标签的 Release、缺少名称或下载地址的资产。</para></exception>
    /// <exception cref="JsonException">The response body is not a valid release JSON array.
    /// <para>响应体不是有效的 Release JSON 数组。</para></exception>
    public static Task<IReadOnlyList<GitHubRelease>> ListAsync(HttpClient client, string owner, string repo,
        int maximum = DefaultListCount, CancellationToken cancellationToken = default)
        => ListPageAsync(client, owner, repo, maximum, page: 1, cancellationToken);

    /// <summary>Fetches one page of published releases of a repository, newest first, without drafts.
    /// <para>获取仓库一页已发布的 Release（由新到旧），不含草稿。</para></summary>
    /// <remarks>When the document exceeds the response cap, the count is halved and the request is retried for the
    /// same page. <para>响应文档超过上限时会把数量减半并对同一页重试。</para></remarks>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="owner">Repository owner; must be a single path segment.
    /// <para>仓库拥有者；必须是单个路径段。</para></param>
    /// <param name="repo">Repository name; must be a single path segment.
    /// <para>仓库名；必须是单个路径段。</para></param>
    /// <param name="maximum">Requested release count per page, clamped to 1 through <see cref="MaximumListCount"/>.
    /// <para>每页请求的 Release 数量，收敛到 1 至 <see cref="MaximumListCount"/>。</para></param>
    /// <param name="page">One-based page number. <para>从 1 开始的页码。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <param name="sources">Optional ranked candidate URLs of the same release-list request, fastest source
    /// first; each is tried in order until one returns a usable document, and <see langword="null"/> keeps the
    /// origin-only request. <para>可选的同一清单请求的候选地址列表（已按测速排序）；按顺序逐个尝试直到某个源返回可用
    /// 文档，为 <see langword="null"/> 时保持仅源站请求。</para></param>
    /// <returns>Published releases on that page in API order, drafts removed; pre-releases stay listed.
    /// <para>该页按 API 顺序的已发布 Release，草稿已移除；预发布仍保留在列表中。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is less than 1.
    /// <para><paramref name="page"/> 小于 1。</para></exception>
    /// <exception cref="GitHubResponseTooLargeException">Even a single release exceeds the size cap.
    /// <para>即使只请求一个 Release 也超出大小上限。</para></exception>
    /// <exception cref="HttpRequestException">Every candidate source failed, or a source returned a non-success status.
    /// <para>全部候选源失败，或某个源返回非成功状态。</para></exception>
    /// <exception cref="InvalidOperationException">The response carries no document list, or describes a release
    /// without a tag or an asset without a name or download URL.
    /// <para>响应没有文档列表，或描述了缺少标签的 Release、缺少名称或下载地址的资产。</para></exception>
    public static async Task<IReadOnlyList<GitHubRelease>> ListPageAsync(HttpClient client, string owner, string repo,
        int maximum = DefaultListCount, int page = 1, CancellationToken cancellationToken = default,
        IReadOnlyList<Uri>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var count = Math.Clamp(maximum, 1, MaximumListCount);
        while (true)
        {
            try
            {
                var payload = await ReadReleasesAsync(client, owner, repo, count, page, sources, cancellationToken)
                    .ConfigureAwait(false);
                return ParseReleases(owner, repo, payload);
            }
            catch (GitHubResponseTooLargeException) when (count > 1)
            {
                count = Math.Max(1, count / 2);
            }
        }
    }

    private static async Task<byte[]> ReadReleasesAsync(HttpClient client, string owner, string repo, int count,
        int page, IReadOnlyList<Uri>? sources, CancellationToken cancellationToken)
    {
        if (sources is not { Count: > 0 })
        {
            return await ReadAsync(client, BuildListUri(owner, repo, count, page), cancellationToken).ConfigureAwait(false);
        }

        List<string> errors = [];
        GitHubResponseTooLargeException? tooLarge = null;
        var tooLargeCount = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadAsync(client, source, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubResponseTooLargeException ex)
            {
                tooLarge = ex;
                tooLargeCount++;
                errors.Add(source.Host + ": " + ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                errors.Add(source.Host + ": " + ex.Message);
            }
        }

        if (tooLarge is not null && tooLargeCount == errors.Count) throw tooLarge;
        throw new HttpRequestException(
            $"The GitHub release feed of {owner}/{repo} failed for every candidate source:\n"
            + string.Join("\n", errors));
    }

    /// <summary>Parses a release list API document without performing any network request.
    /// <para>不发起任何网络请求地解析 Release 列表 API 文档。</para></summary>
    /// <param name="owner">Repository owner recorded on every result.
    /// <para>记录到每个结果上的仓库拥有者。</para></param>
    /// <param name="repository">Repository name recorded on every result.
    /// <para>记录到每个结果上的仓库名。</para></param>
    /// <param name="json">Release list API document text.
    /// <para>Release 列表 API 文档文本。</para></param>
    /// <returns>Published releases in document order with drafts removed, so pre-releases stay listed.
    /// <para>按文档顺序的已发布 Release（草稿已移除），预发布仍保留在列表中。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.
    /// <para><paramref name="json"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">A listed release carries no tag or describes an asset without a name or download URL.
    /// <para>列表中的某个 Release 没有标签，或描述了缺少名称或下载地址的资产。</para></exception>
    /// <exception cref="JsonException">The text is not a valid release JSON array.
    /// <para>文本不是有效的 Release JSON 数组。</para></exception>
    public static IReadOnlyList<GitHubRelease> ParseReleases(string owner, string repository, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseReleases(owner, repository, System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Normalizes a release tag into deterministic version text.
    /// <para>把 Release 标签归一化为确定性的版本文本。</para></summary>
    /// <param name="tag">Release tag, for example <c>v1.2.3</c> or <c>v1.2.3-beta.1</c>.
    /// <para>Release 标签，例如 <c>v1.2.3</c> 或 <c>v1.2.3-beta.1</c>。</para></param>
    /// <returns>The trimmed tag with one leading <c>v</c> removed when a digit follows it; surrounding whitespace is always removed.
    /// <para>去除首尾空白后的标签；当 <c>v</c> 后紧跟数字时去掉这一个前缀。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is empty or whitespace.
    /// <para><paramref name="tag"/> 为空或只有空白。</para></exception>
    public static string NormalizeVersion(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var value = tag.Trim();
        if (value.Length > 1 && value[0] is 'v' or 'V' && char.IsAsciiDigit(value[1]))
        {
            value = value[1..];
        }

        return value;
    }

    /// <summary>Parses a release API document without performing any network request.
    /// <para>不发起任何网络请求地解析 Release API 文档。</para></summary>
    /// <param name="owner">Repository owner recorded on the result.
    /// <para>记录到结果上的仓库拥有者。</para></param>
    /// <param name="repository">Repository name recorded on the result.
    /// <para>记录到结果上的仓库名。</para></param>
    /// <param name="json">Release API document text.
    /// <para>Release API 文档文本。</para></param>
    /// <returns>The normalized release description.
    /// <para>已归一化的 Release 描述。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.
    /// <para><paramref name="json"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The document carries no tag or describes an asset without a name or download URL.
    /// <para>文档没有标签，或资产缺少名称或下载地址。</para></exception>
    /// <exception cref="JsonException">The text is not valid release JSON.
    /// <para>文本不是有效的 Release JSON。</para></exception>
    public static GitHubRelease ParseRelease(string owner, string repository, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseRelease(owner, repository, System.Text.Encoding.UTF8.GetBytes(json));
    }

    internal static async Task<GitHubRelease> GetAsync(HttpClient client, Uri url, string owner, string repository,
        CancellationToken cancellationToken)
    {
        var payload = await ReadAsync(client, url, cancellationToken).ConfigureAwait(false);
        return ParseRelease(owner, repository, payload);
    }

    private static async Task<byte[]> ReadAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        using var request = HttpDownload.CreateRequest(url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub release request '{url}' failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        return await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    internal static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var value = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? digest[prefix.Length..] : digest;
        value = value.Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static GitHubRelease ParseRelease(string owner, string repository, ReadOnlySpan<byte> json)
    {
        var document = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GitHubReleaseDocument)
            ?? throw new InvalidOperationException("The release API returned no document.");
        return Project(owner, repository, document);
    }

    private static IReadOnlyList<GitHubRelease> ParseReleases(string owner, string repository, ReadOnlySpan<byte> json)
    {
        var documents = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.ListGitHubReleaseDocument)
            ?? throw new InvalidOperationException("The release API returned no document list.");
        List<GitHubRelease> releases = [];
        foreach (var document in documents)
        {
            if (document is null || document.Draft)
            {
                continue;
            }

            releases.Add(Project(owner, repository, document));
        }

        return releases;
    }

    private static GitHubRelease Project(string owner, string repository, GitHubReleaseDocument document)
    {
        var tag = document.TagName?.Trim();
        if (string.IsNullOrEmpty(tag))
        {
            throw new InvalidOperationException("The release API returned no tag_name.");
        }

        List<GitHubAsset> assets = [];
        foreach (var assetDocument in document.Assets ?? [])
        {
            if (string.IsNullOrWhiteSpace(assetDocument.Name) || string.IsNullOrWhiteSpace(assetDocument.BrowserDownloadUrl))
            {
                throw new InvalidOperationException("The release API returned an asset without a name or download URL.");
            }

            assets.Add(new GitHubAsset(assetDocument.Name, assetDocument.BrowserDownloadUrl, assetDocument.Size,
                ParseDigest(assetDocument.Digest)));
        }

        return new GitHubRelease(owner, repository, tag, NormalizeVersion(tag), assets);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > MaximumResponseBytes)
        {
            throw new GitHubResponseTooLargeException(
                $"The release API response exceeds the {MaximumResponseBytes}-byte cap.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new GitHubResponseTooLargeException(
                    $"The release API response exceeds the {MaximumResponseBytes}-byte cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Uri BuildLatestUri(string owner, string repository)
        => new($"{ApiOrigin}/repos/{RequireSegment(owner, nameof(owner))}/{RequireSegment(repository, nameof(repository))}/releases/latest");

    private static Uri BuildListUri(string owner, string repository, int count, int page)
        => new($"{ApiOrigin}/repos/{RequireSegment(owner, nameof(owner))}/{RequireSegment(repository, nameof(repository))}/releases?per_page={count}&page={page}");

    private static string RequireSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var segment = value.Trim();
        if (segment.Contains('/') || segment.Contains('\\') || segment.Contains('?') || segment.Contains('#'))
        {
            throw new ArgumentException("The value must be a single repository path segment.", parameterName);
        }

        return segment;
    }
}