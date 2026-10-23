using UotanStudio.Net.GitHub;

namespace UotanStudio.Net.Components.Scrcpy;

/// <summary>One published scrcpy server artifact, as described by the GitHub release API.
/// <para>GitHub Release API 描述的一个 scrcpy 服务端产物。</para></summary>
/// <param name="Version">Dotted decimal version, derived from the release tag.
/// <para>点分十进制版本，由 Release 标签推导。</para></param>
/// <param name="Tag">Release tag the artifact was published under.
/// <para>发布该产物的 Release 标签。</para></param>
/// <param name="AssetName">Release asset file name.<para>Release 资产文件名。</para></param>
/// <param name="DownloadUrl">Origin download URL; mirrors are derived from it by the downloader.
/// <para>源站下载地址；镜像由下载器据此派生。</para></param>
/// <param name="Length">Exact byte length reported by the release API.
/// <para>Release API 上报的精确字节长度。</para></param>
/// <param name="Sha256">Lowercase SHA-256 digest published by the release API.
/// <para>Release API 公布的小写 SHA-256 摘要。</para></param>
public sealed record ScrcpyServerRelease(
    string Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long Length,
    string Sha256);

/// <summary>
/// Describes the published scrcpy server release read from <c>api.github.com</c>.
/// <para>描述从 <c>api.github.com</c> 读取的已发布 scrcpy 服务端版本。</para>
/// </summary>
/// <remarks>
/// <para>
/// Metadata is read from the origin only and never from a mirror: the digest and byte length are what
/// make a mirror-served payload trustworthy, so a mirror must not be able to supply them. The
/// artifact itself is downloaded through the mirror-accelerating downloader and verified against them.
/// </para>
/// <para>
/// 元数据只读源站、绝不读镜像：摘要与字节长度正是让镜像载荷可信的依据，因此镜像不能提供它们。
/// 产物本体走带镜像加速的下载器获取，并用这两个值校验。
/// </para>
/// </remarks>
public static class ScrcpyServerReleaseClient
{
    /// <summary>Release API endpoint for the newest published scrcpy version.
    /// <para>最新已发布 scrcpy 版本的 Release API 地址。</para></summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";

    /// <summary>Prefix of the standalone server asset name.
    /// <para>独立服务端资产文件名的前缀。</para></summary>
    public const string ServerAssetPrefix = "scrcpy-server-v";

    private const string Owner = "Genymobile";
    private const string Repository = "scrcpy";

    /// <summary>Fetches and validates the newest release.<para>获取并校验最新的 Release。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.<para>取消请求与解析。</para></param>
    /// <returns>The described server artifact.<para>所描述的服务端产物。</para></returns>
    /// <exception cref="InvalidOperationException">The response carries no release, no standalone server asset, a tag without a dotted decimal version, or an asset without a published SHA-256 digest.
    /// <para>响应中没有 Release、没有独立服务端资产、标签不含点分十进制版本，或资产未公布 SHA-256 摘要。</para></exception>
    /// <exception cref="HttpRequestException">The origin request failed; the exception carries the status code.
    /// <para>源站请求失败；异常携带状态码。</para></exception>
    public static async Task<ScrcpyServerRelease> GetLatestAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var release = await GitHubReleaseClient.GetLatestAsync(client, Owner, Repository, cancellationToken)
            .ConfigureAwait(false);
        return FromRelease(release);
    }

    /// <summary>Projects a parsed release onto the server artifact it publishes.
    /// <para>从已解析的 Release 中投影出它发布的服务端产物。</para></summary>
    /// <param name="release">Parsed release description. <para>已解析的 Release 描述。</para></param>
    /// <returns>The described server artifact. <para>所描述的服务端产物。</para></returns>
    /// <exception cref="InvalidOperationException">No standalone server asset, an unusable tag, or a missing digest.
    /// <para>没有独立服务端资产、标签不可用，或缺少摘要。</para></exception>
    public static ScrcpyServerRelease FromRelease(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = release.Assets.FirstOrDefault(asset => asset.Name.StartsWith(ServerAssetPrefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Release '{release.Tag}' publishes no {ServerAssetPrefix} asset.");
        var version = TrimVersion(release.Tag)
            ?? throw new InvalidOperationException($"Release tag '{release.Tag}' has no dotted decimal version.");
        var digest = asset.Sha256
            ?? throw new InvalidOperationException($"Asset '{asset.Name}' publishes no SHA-256 digest.");
        return new ScrcpyServerRelease(version, release.Tag, asset.Name, asset.DownloadUrl, asset.Size, digest);
    }

    /// <summary>Returns the version part of a release tag, or <see langword="null"/> when it is not dotted decimal.
    /// <para>返回 Release 标签中的版本部分；不是点分十进制时返回 <see langword="null"/>。</para></summary>
    /// <param name="tag">Release tag, typically prefixed with <c>v</c>. <para>Release 标签，通常以 <c>v</c> 开头。</para></param>
    /// <returns>The version text, or <see langword="null"/>. <para>版本文本，或 <see langword="null"/>。</para></returns>
    public static string? TrimVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return IsDottedDecimal(value) ? value : null;
    }

    /// <summary>Extracts the lowercase hex of a <c>sha256:&lt;hex&gt;</c> digest field.
    /// <para>从 <c>sha256:&lt;hex&gt;</c> 摘要字段中提取小写十六进制。</para></summary>
    /// <param name="digest">Raw digest field, possibly null or empty. <para>原始摘要字段，允许为空。</para></param>
    /// <returns>64 lowercase hex characters, or <see langword="null"/>. <para>64 位小写十六进制，或 <see langword="null"/>。</para></returns>
    public static string? ParseDigest(string? digest) => GitHubReleaseClient.ParseDigest(digest);

    private static bool IsDottedDecimal(string value)
    {
        if (value.Length is < 3 or > 16 || !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }
}