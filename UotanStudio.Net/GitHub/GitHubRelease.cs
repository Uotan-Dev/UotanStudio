using System.Text.Json.Serialization;

namespace UotanStudio.Net.GitHub;

/// <summary>One downloadable asset published by a GitHub release.
/// <para>GitHub Release 发布的一个可下载资产。</para></summary>
/// <param name="Name">Asset file name.<para>资产文件名。</para></param>
/// <param name="DownloadUrl">Origin download URL; mirrors are derived from it by the downloader.
/// <para>源站下载地址；镜像由下载器据此派生。</para></param>
/// <param name="Size">Exact byte length reported by the release API.
/// <para>Release API 上报的精确字节长度。</para></param>
/// <param name="Sha256">Lowercase SHA-256 digest published by the release API, or <see langword="null"/> when the API exposes no valid digest.
/// <para>Release API 公布的小写 SHA-256 摘要；API 未提供有效摘要时为 <see langword="null"/>。</para></param>
public sealed record GitHubAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>A GitHub release with its tag normalized to a version string.
/// <para>标签已归一化为版本串的 GitHub Release。</para></summary>
/// <param name="Owner">Repository owner the release belongs to.<para>该 Release 所属仓库的拥有者。</para></param>
/// <param name="Repository">Repository name the release belongs to.<para>该 Release 所属的仓库名。</para></param>
/// <param name="Tag">Release tag exactly as published, for example <c>v1.2.3</c>.
/// <para>发布时的原始 Release 标签，例如 <c>v1.2.3</c>。</para></param>
/// <param name="Version">Deterministic version text derived from <paramref name="Tag"/>, with a single leading <c>v</c> removed when a digit follows it.
/// <para>由 <paramref name="Tag"/> 推导的确定性版本文本；当 <c>v</c> 后紧跟数字时去掉这一个前缀。</para></param>
/// <param name="Assets">Published assets in API order; empty when the release publishes none.
/// <para>按 API 顺序排列的已发布资产；Release 未发布资产时为空。</para></param>
public sealed record GitHubRelease(
    string Owner,
    string Repository,
    string Tag,
    string Version,
    IReadOnlyList<GitHubAsset> Assets);

/// <summary>Subset of the GitHub release document that is read from the API.
/// <para>从 API 读取的 GitHub Release 文档子集。</para></summary>
/// <param name="TagName">Published release tag, or <see langword="null"/> when the document carries none.
/// <para>已发布的 Release 标签；文档没有该字段时为 <see langword="null"/>。</para></param>
/// <param name="Assets">Published assets in API order; <see langword="null"/> when the document lists none.
/// <para>按 API 顺序排列的已发布资产；文档未列出资产时为 <see langword="null"/>。</para></param>
internal sealed record GitHubReleaseDocument(string? TagName, List<GitHubAssetDocument>? Assets)
{
    /// <summary>Whether the release is an unpublished draft.
    /// <para>该 Release 是否为未发布的草稿。</para></summary>
    /// <value><see langword="false"/> when the API omits the field. <para>API 未提供该字段时为 <see langword="false"/>。</para></value>
    public bool Draft { get; init; }

    /// <summary>Whether the release is published as a pre-release.
    /// <para>该 Release 是否以预发布形式公布。</para></summary>
    /// <value><see langword="false"/> when the API omits the field. <para>API 未提供该字段时为 <see langword="false"/>。</para></value>
    public bool Prerelease { get; init; }
}

/// <summary>Subset of a GitHub release asset that is read from the API.
/// <para>从 API 读取的 GitHub Release 资产子集。</para></summary>
internal sealed record GitHubAssetDocument(string? Name, long Size, string? BrowserDownloadUrl, string? Digest);

/// <summary>
/// Source-generated JSON metadata for the release API document, so metadata reads stay NativeAOT-safe.
/// <para>Release API 文档的源生成 JSON 元数据，使元数据读取在 NativeAOT 下可用。</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubReleaseDocument))]
[JsonSerializable(typeof(List<GitHubReleaseDocument>))]
[JsonSerializable(typeof(GitHubAssetDocument))]
[JsonSerializable(typeof(List<GitHubAssetDocument>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;