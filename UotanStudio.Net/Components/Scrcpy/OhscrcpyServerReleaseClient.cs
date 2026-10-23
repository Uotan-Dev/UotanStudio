using UotanStudio.Net.GitHub;

namespace UotanStudio.Net.Components.Scrcpy;

/// <summary>One published ohscrcpy device-server package, as described by the GitHub release API.
/// <para>GitHub Release API 描述的一个 ohscrcpy 设备端服务包。</para></summary>
/// <param name="Version">Dotted decimal version, derived from the release tag.
/// <para>点分十进制版本，由 Release 标签推导。</para></param>
/// <param name="Tag">Release tag the package was published under.
/// <para>发布该包的 Release 标签。</para></param>
/// <param name="AssetName">Release asset file name, always a <c>.hap</c>.
/// <para>Release 资产文件名，始终是 <c>.hap</c>。</para></param>
/// <param name="DownloadUrl">Origin download URL; mirrors are derived from it by the downloader.
/// <para>源站下载地址；镜像由下载器据此派生。</para></param>
/// <param name="Length">Exact byte length reported by the release API.
/// <para>Release API 上报的精确字节长度。</para></param>
/// <param name="Sha256">Lowercase SHA-256 digest published by the release API.
/// <para>Release API 公布的小写 SHA-256 摘要。</para></param>
public sealed record OhscrcpyServerRelease(
    string Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long Length,
    string Sha256);

/// <summary>
/// Describes the published ohscrcpy device-server HAP read from <c>api.github.com</c>.
/// <para>描述从 <c>api.github.com</c> 读取的已发布 ohscrcpy 设备端服务 HAP。</para>
/// </summary>
/// <remarks>
/// <para>
/// The upstream project publishes a HarmonyOS application package, not a scrcpy-style executable jar:
/// <c>OHScrcpyServer.hap</c> contains the <c>ScrcpyService</c> ServiceExtensionAbility plus the native
/// <c>libscrcpy_capture.so</c>, so the host installs it instead of pushing and executing a binary.
/// Metadata is read from the origin only; a mirror may deliver the payload but cannot supply the length
/// or digest that make it trustworthy.
/// </para>
/// <para>
/// 上游发布的是 HarmonyOS 应用包，而不是 scrcpy 风格的可执行 jar：<c>OHScrcpyServer.hap</c> 内含
/// <c>ScrcpyService</c> ServiceExtensionAbility 与原生 <c>libscrcpy_capture.so</c>，因此主机安装它而不是
/// 推送并执行二进制。版本元数据只读源站；镜像可以交付载荷，但不能提供使其可信的长度与摘要。
/// </para>
/// </remarks>
public static class OhscrcpyServerReleaseClient
{
    /// <summary>Release API endpoint for the newest published ohscrcpy version.
    /// <para>最新已发布 ohscrcpy 版本的 Release API 地址。</para></summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/guoxiucai/ohos-scrcpy-app/releases/latest";

    /// <summary>File name of the device-server package published by every release.
    /// <para>每次发布都会提供的设备端服务包文件名。</para></summary>
    public const string ServerAssetName = "OHScrcpyServer.hap";

    private const string Owner = "guoxiucai";
    private const string Repository = "ohos-scrcpy-app";

    /// <summary>Fetches and validates the newest release.<para>获取并校验最新的 Release。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.<para>取消请求与解析。</para></param>
    /// <returns>The described device-server package.<para>所描述的设备端服务包。</para></returns>
    /// <exception cref="InvalidOperationException">The response carries no release, no HAP asset, a tag
    /// without a dotted decimal version, or an asset without a published SHA-256 digest.
    /// <para>响应中没有 Release、没有 HAP 资产、标签不含点分十进制版本，或资产未公布 SHA-256 摘要。</para></exception>
    /// <exception cref="HttpRequestException">The origin request failed; the exception carries the status code.
    /// <para>源站请求失败；异常携带状态码。</para></exception>
    public static async Task<OhscrcpyServerRelease> GetLatestAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var release = await GitHubReleaseClient.GetLatestAsync(client, Owner, Repository, cancellationToken)
            .ConfigureAwait(false);
        return FromRelease(release);
    }

    /// <summary>Projects a parsed release onto the device-server package it publishes.
    /// <para>从已解析的 Release 中投影出它发布的设备端服务包。</para></summary>
    /// <param name="release">Parsed release description. <para>已解析的 Release 描述。</para></param>
    /// <returns>The described device-server package. <para>所描述的设备端服务包。</para></returns>
    /// <exception cref="InvalidOperationException">No HAP asset, an unusable tag, or a missing digest.
    /// <para>没有 HAP 资产、标签不可用，或缺少摘要。</para></exception>
    public static OhscrcpyServerRelease FromRelease(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = release.Assets.FirstOrDefault(asset => IsServerAsset(asset.Name))
            ?? throw new InvalidOperationException(
                $"Release '{release.Tag}' publishes no {ServerAssetName} asset.");
        var version = TrimVersion(release.Tag)
            ?? throw new InvalidOperationException($"Release tag '{release.Tag}' has no dotted decimal version.");
        var digest = asset.Sha256
            ?? throw new InvalidOperationException($"Asset '{asset.Name}' publishes no SHA-256 digest.");
        return new OhscrcpyServerRelease(version, release.Tag, asset.Name, asset.DownloadUrl, asset.Size, digest);
    }

    /// <summary>Whether an asset name is the device-server HAP (an optional version suffix is accepted).
    /// <para>资产名是否为设备端服务 HAP（允许带版本后缀）。</para></summary>
    /// <param name="name">Release asset file name. <para>Release 资产文件名。</para></param>
    /// <returns><see langword="true"/> for the HAP asset. <para>是 HAP 资产时为 <see langword="true"/>。</para></returns>
    public static bool IsServerAsset(string name)
        => name.StartsWith("OHScrcpyServer", StringComparison.OrdinalIgnoreCase) &&
           name.EndsWith(".hap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the version part of a release tag, or <see langword="null"/> when it is not dotted decimal.
    /// <para>返回 Release 标签中的版本部分；不是点分十进制时返回 <see langword="null"/>。</para></summary>
    /// <param name="tag">Release tag, typically prefixed with <c>v</c>. <para>Release 标签，通常以 <c>v</c> 开头。</para></param>
    /// <returns>The version text, or <see langword="null"/>. <para>版本文本，或 <see langword="null"/>。</para></returns>
    public static string? TrimVersion(string tag) => ScrcpyServerReleaseClient.TrimVersion(tag);
}