using System.Text.Json;
using UotanStudio.Net.Http;

namespace UotanStudio.Net.GitHub;

/// <summary>
/// Reads release metadata from the <c>api.github.com</c> origin only; mirrors never supply versions, lengths or digests.
/// <para>只从 <c>api.github.com</c> 源站读取 Release 元数据；镜像绝不提供版本、长度或摘要。</para>
/// </summary>
/// <remarks>
/// <para>
/// Every response body is capped at one MiB and parsed through a source-generated JSON context, so the
/// metadata path stays bounded and NativeAOT-safe. The artifact itself is downloaded separately and
/// verified against the <see cref="GitHubAsset.Size"/> and <see cref="GitHubAsset.Sha256"/> reported here.
/// </para>
/// <para>
/// 每个响应体上限为一 MiB，并经源生成 JSON 上下文解析，因此元数据路径有界且在 NativeAOT 下可用。产物本体另行下载，
/// 并用此处上报的 <see cref="GitHubAsset.Size"/> 与 <see cref="GitHubAsset.Sha256"/> 校验。
/// </para>
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
    /// <exception cref="InvalidOperationException">The response exceeds the size cap, carries no document or tag, or describes an asset without a name or download URL.
    /// <para>响应超出大小上限、没有文档或标签，或资产缺少名称或下载地址。</para></exception>
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
    /// <exception cref="InvalidOperationException">The response exceeds the size cap, carries no document or tag, or describes an asset without a name or download URL.
    /// <para>响应超出大小上限、没有文档或标签，或资产缺少名称或下载地址。</para></exception>
    /// <exception cref="JsonException">The response body is not valid release JSON.
    /// <para>响应体不是有效的 Release JSON。</para></exception>
    public static Task<GitHubRelease> GetByTagAsync(HttpClient client, string owner, string repo, string tag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var uri = new Uri($"{ApiOrigin}/repos/{RequireSegment(owner, nameof(owner))}/{RequireSegment(repo, nameof(repo))}/releases/tags/{Uri.EscapeDataString(tag.Trim())}");
        return GetAsync(client, uri, owner, repo, cancellationToken);
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

        var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ParseRelease(owner, repository, payload);
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
            throw new InvalidOperationException(
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
                throw new InvalidOperationException(
                    $"The release API response exceeds the {MaximumResponseBytes}-byte cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Uri BuildLatestUri(string owner, string repository)
        => new($"{ApiOrigin}/repos/{RequireSegment(owner, nameof(owner))}/{RequireSegment(repository, nameof(repository))}/releases/latest");

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