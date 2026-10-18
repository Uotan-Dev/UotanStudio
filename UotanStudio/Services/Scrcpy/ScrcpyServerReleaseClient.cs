using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Scrcpy;

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
/// Fetches the published scrcpy server release from <c>api.github.com</c>.
/// <para>从 <c>api.github.com</c> 获取已发布的 scrcpy 服务端版本信息。</para>
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

    /// <summary>Fetches and validates the newest release.<para>获取并校验最新的 Release。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="url">Release API URL; tests inject a stand-in.
    /// <para>Release API 地址；测试注入替身。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.<para>取消请求与解析。</para></param>
    /// <returns>The described server artifact.<para>所描述的服务端产物。</para></returns>
    /// <exception cref="InvalidOperationException">The response carries no release, no standalone server asset, a tag without a dotted decimal version, or an asset without a published SHA-256 digest.
    /// <para>响应中没有 Release、没有独立服务端资产、标签不含点分十进制版本，或资产未公布 SHA-256 摘要。</para></exception>
    /// <exception cref="HttpRequestException">The origin request failed.
    /// <para>源站请求失败。</para></exception>
    public static async Task<ScrcpyServerRelease> GetLatestAsync(
        HttpClient client,
        string url = LatestReleaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await JsonSerializer.DeserializeAsync(
            content, ScrcpyReleaseJsonContext.Default.GitHubReleaseDocument, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The release API returned no document.");

        return FromDocument(document);
    }

    /// <summary>Projects a parsed release document onto the server artifact it publishes.
    /// <para>从已解析的 Release 文档中投影出它发布的服务端产物。</para></summary>
    /// <param name="document">Parsed release document. <para>已解析的 Release 文档。</para></param>
    /// <returns>The described server artifact. <para>所描述的服务端产物。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// No standalone server asset, an unusable tag, or a missing digest.
    /// <para>没有独立服务端资产、标签不可用，或缺少摘要。</para>
    /// </exception>
    public static ScrcpyServerRelease FromDocument(GitHubReleaseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var asset = document.Assets.FirstOrDefault(a => a.Name.StartsWith(ServerAssetPrefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Release '{document.TagName}' publishes no {ServerAssetPrefix} asset.");
        var version = TrimVersion(document.TagName)
            ?? throw new InvalidOperationException($"Release tag '{document.TagName}' has no dotted decimal version.");
        var digest = ParseDigest(asset.Digest)
            ?? throw new InvalidOperationException($"Asset '{asset.Name}' publishes no SHA-256 digest.");
        return new ScrcpyServerRelease(version, document.TagName, asset.Name, asset.BrowserDownloadUrl, asset.Size, digest);
    }

    /// <summary>Returns the version part of a release tag, or <see langword="null"/> when it is not dotted decimal.
    /// <para>返回 Release 标签中的版本部分；不是点分十进制时返回 <see langword="null"/>。</para></summary>
    /// <param name="tag">Release tag, typically prefixed with <c>v</c>. <para>Release 标签，通常以 <c>v</c> 开头。</para></param>
    /// <returns>The version text, or <see langword="null"/>. <para>版本文本，或 <see langword="null"/>。</para></returns>
    public static string? TrimVersion(string tag)
    {
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return IsDottedDecimal(value) ? value : null;
    }

    /// <summary>Extracts the lowercase hex of a <c>sha256:&lt;hex&gt;</c> digest field.
    /// <para>从 <c>sha256:&lt;hex&gt;</c> 摘要字段中提取小写十六进制。</para></summary>
    /// <param name="digest">Raw digest field, possibly null or empty. <para>原始摘要字段，允许为空。</para></param>
    /// <returns>64 lowercase hex characters, or <see langword="null"/>. <para>64 位小写十六进制，或 <see langword="null"/>。</para></returns>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null) return null;
        var value = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? digest[prefix.Length..] : digest;
        value = value.Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static bool IsDottedDecimal(string value)
    {
        if (value.Length is < 3 or > 16 || !value.Contains('.', StringComparison.Ordinal)) return false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) return false;
        }

        return true;
    }
}

/// <summary>Subset of the GitHub release document that is read here.
/// <para>此处读取的 GitHub Release 文档子集。</para></summary>
/// <param name="TagName">Release tag, e.g. <c>v5.0.1</c>. <para>Release 标签，例如 <c>v5.0.1</c>。</para></param>
/// <param name="Assets">Published assets. <para>已发布的资产。</para></param>
public sealed record GitHubReleaseDocument(string TagName, List<GitHubAssetDocument> Assets);

/// <summary>Subset of a GitHub release asset that is read here.
/// <para>此处读取的 GitHub Release 资产子集。</para></summary>
/// <param name="Name">Asset file name. <para>资产文件名。</para></param>
/// <param name="Size">Byte length reported by the API. <para>API 上报的字节长度。</para></param>
/// <param name="BrowserDownloadUrl">Origin download URL. <para>源站下载地址。</para></param>
/// <param name="Digest">Published digest, formatted <c>sha256:&lt;hex&gt;</c>.
/// <para>已公布的摘要，形式为 <c>sha256:&lt;hex&gt;</c>。</para></param>
public sealed record GitHubAssetDocument(string Name, long Size, string BrowserDownloadUrl, string? Digest);

/// <summary>
/// Source-generated JSON metadata for the release API document, so the fetch keeps working under NativeAOT.
/// <para>Release API 文档的源生成 JSON 元数据，使该获取在 NativeAOT 下仍然可用。</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubReleaseDocument))]
[JsonSerializable(typeof(GitHubAssetDocument))]
[JsonSerializable(typeof(List<GitHubAssetDocument>))]
internal sealed partial class ScrcpyReleaseJsonContext : JsonSerializerContext;