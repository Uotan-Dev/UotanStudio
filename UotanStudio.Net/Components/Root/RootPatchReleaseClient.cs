using UotanStudio.Net.GitHub;

namespace UotanStudio.Net.Components.Root;

/// <summary>One upstream root project whose published packages patch an Android boot image.
/// <para>一个上游 root 项目，其发布包用于修补 Android boot 镜像。</para></summary>
/// <param name="Key">Stable key the host uses for display, storage and settings.
/// <para>宿主用于显示、存储与设置的稳定键。</para></param>
/// <param name="Owner">GitHub repository owner.<para>GitHub 仓库拥有者。</para></param>
/// <param name="Repository">GitHub repository name.<para>GitHub 仓库名。</para></param>
public sealed record RootPatchVendor(string Key, string Owner, string Repository);

/// <summary>
/// Upstream projects the boot patcher can provision packages from.
/// <para>引导修补器可从中供给补丁包的上游项目。</para>
/// </summary>
/// <remarks>
/// Every entry names the official repository of that project, so release metadata and downloads
/// always resolve to the origin the community ships from. Magisk Alpha is Magisk repackaged by its
/// own maintainer and is therefore a separate key over a different repository.
/// <para>每个条目都指向该项目的官方仓库，因此版本元数据与下载始终解析到社区实际发布所用的源站。
/// Magisk Alpha 是 Magisk 由其自有维护者重新打包的产物，因此使用独立键与独立仓库。</para>
/// </remarks>
public static class RootPatchCatalog
{
    /// <summary>Key of the official Magisk project.<para>官方 Magisk 项目的键。</para></summary>
    public const string MagiskKey = "magisk";

    /// <summary>Key of the Magisk Alpha fork.<para>Magisk Alpha 分支的键。</para></summary>
    public const string MagiskAlphaKey = "magisk-alpha";

    /// <summary>Key of the official KernelSU project.<para>官方 KernelSU 项目的键。</para></summary>
    public const string KernelSuKey = "kernelsu";

    /// <summary>Key of the SukiSU project.<para>SukiSU 项目的键。</para></summary>
    public const string SukiSuKey = "sukisu";

    /// <summary>Key of the official APatch project.<para>官方 APatch 项目的键。</para></summary>
    public const string APatchKey = "apatch";

    /// <summary>Every supported vendor, in the order the host presents them.
    /// <para>全部受支持的供应方，按宿主呈现顺序排列。</para></summary>
    /// <value>Five stable entries; the collection is never mutated after initialization.
    /// <para>五个稳定条目；初始化后不再修改该集合。</para></value>
    public static IReadOnlyList<RootPatchVendor> Vendors { get; } =
    [
        new(MagiskKey, "topjohnwu", "Magisk"),
        new(MagiskAlphaKey, "vvb2060", "Magisk"),
        new(KernelSuKey, "tiann", "KernelSU"),
        new(SukiSuKey, "SukiSU-Ultra", "SukiSU-Ultra"),
        new(APatchKey, "bmax121", "APatch"),
    ];

    /// <summary>Looks a vendor up by key.<para>按键查找供应方。</para></summary>
    /// <param name="key">Root project key such as <c>magisk-alpha</c>, compared without case sensitivity.
    /// <para>形如 <c>magisk-alpha</c> 的项目键，比对不区分大小写。</para></param>
    /// <returns>The matching vendor, or <see langword="null"/> when the key is unknown.
    /// <para>匹配的供应方；键未知时为 <see langword="null"/>。</para></returns>
    public static RootPatchVendor? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var vendor in Vendors)
        {
            if (string.Equals(vendor.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return vendor;
            }
        }

        return null;
    }
}

/// <summary>One installable package published by a root project release.
/// <para>某个 root 项目 Release 发布的一个可安装补丁包。</para></summary>
/// <param name="Key">Key of the vendor that published the package.
/// <para>发布该补丁包的供应方键。</para></param>
/// <param name="Version">Version text derived from the release tag.
/// <para>由 Release 标签推导的版本文本。</para></param>
/// <param name="Tag">Release tag exactly as published.<para>发布时的原始 Release 标签。</para></param>
/// <param name="AssetName">Release asset file name.<para>Release 资产文件名。</para></param>
/// <param name="DownloadUrl">Origin download URL; mirrors are derived from it by the downloader.
/// <para>源站下载地址；镜像由下载器据此派生。</para></param>
/// <param name="Length">Exact byte length reported by the release API.
/// <para>Release API 上报的精确字节长度。</para></param>
/// <param name="Sha256">Lowercase SHA-256 digest published by the release API, or <see langword="null"/> when that
/// release publishes none, in which case the payload must come from the origin.
/// <para>Release API 公布的小写 SHA-256 摘要；该 Release 未公布摘要时为 <see langword="null"/>，此时载荷只能取自源站。</para></param>
public sealed record RootPatchRelease(
    string Key,
    string Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long Length,
    string? Sha256);

/// <summary>One page of installable packages published by a root project.
/// <para>某个 root 项目发布的一页可安装补丁包。</para></summary>
/// <param name="Releases">Installable packages on this page, in release order.
/// <para>该页的可安装补丁包，按发布顺序排列。</para></param>
/// <param name="HasMore">Whether the upstream page was full, so another page may contain older releases.
/// <para>上游该页是否已满，即后续页可能还有更早的 Release。</para></param>
public sealed record RootPatchReleasePage(IReadOnlyList<RootPatchRelease> Releases, bool HasMore);

/// <summary>
/// Projects root project releases onto the package a boot patch run consumes.
/// <para>把 root 项目 Release 投影为修补流程可消费的补丁包。</para>
/// </summary>
/// <remarks>
/// Release metadata is read from the ranked source list the caller supplies: the fastest probed source first
/// with the <c>api.github.com</c> origin as a fallback, so catalogs may arrive through a mirror. The payload
/// download derives its own mirrors from the origin URL and stays digest-verified.
/// <para>版本元数据按调用方提供的源列表读取：测速最快的源在前，<c>api.github.com</c> 源站兜底，因此清单可以来自镜像。
/// 载荷下载另行从源站地址派生镜像，并继续按摘要校验。</para>
/// </remarks>
public static class RootPatchReleaseClient
{
    /// <summary>Releases requested per catalog page before asset filtering.
    /// <para>每页清单在资产过滤前请求的 Release 数量。</para></summary>
    /// <remarks>Five releases keep long release-note feeds such as KernelSU under the one-MiB response cap and let the
    /// window reveal older versions on demand. <para>5 个 Release 使 KernelSU 这类发布说明很长的源保持在 1 MiB
    /// 响应上限内，并让窗口按需再加载更早的版本。</para></remarks>
    public const int DefaultCatalogCount = 5;

    /// <summary>Lists the first page of published packages of a root project, newest release first.
    /// <para>列出 root 项目第一页的已发布补丁包，最新的 Release 在前。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="key">Root project key from <see cref="RootPatchCatalog"/>.
    /// <para><see cref="RootPatchCatalog"/> 中的项目键。</para></param>
    /// <param name="maximum">Requested release count before asset filtering; clamped by the metadata reader.
    /// <para>按资产过滤前请求的 Release 数量；由元数据读取器收敛。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <returns>Every release on the first page that publishes an installable package, in release order.
    /// <para>第一页中每个发布了可安装补丁包的 Release，按发布顺序排列。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a known root project.
    /// <para><paramref name="key"/> 不是已知的 root 项目。</para></exception>
    /// <exception cref="HttpRequestException">The origin returned a non-success status.
    /// <para>源站返回非成功状态。</para></exception>
    /// <exception cref="InvalidOperationException">The response exceeds the size cap or is not a usable release list.
    /// <para>响应超出大小上限或不是可用的 Release 列表。</para></exception>
    public static async Task<IReadOnlyList<RootPatchRelease>> ListAsync(HttpClient client, string key,
        int maximum = DefaultCatalogCount, CancellationToken cancellationToken = default)
        => (await ListPageAsync(client, key, maximum, page: 1, cancellationToken).ConfigureAwait(false)).Releases;

    /// <summary>Lists one page of published packages of a root project, newest release first.
    /// <para>列出 root 项目一页的已发布补丁包，最新的 Release 在前。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="key">Root project key from <see cref="RootPatchCatalog"/>.
    /// <para><see cref="RootPatchCatalog"/> 中的项目键。</para></param>
    /// <param name="maximum">Requested release count per page before asset filtering.
    /// <para>每页按资产过滤前请求的 Release 数量。</para></param>
    /// <param name="page">One-based page number. <para>从 1 开始的页码。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <param name="sources">Optional ranked candidate URLs of the same release-list request, fastest source
    /// first; each is tried in order until one succeeds, and <see langword="null"/> keeps the origin-only request.
    /// <para>可选的同一清单请求的候选地址列表（已按测速排序）；按顺序逐个尝试直到某个源成功，为
    /// <see langword="null"/> 时保持仅源站请求。</para></param>
    /// <returns>The installable packages on that page together with whether the upstream page was full.
    /// <para>该页的可安装补丁包，以及上游该页是否已满。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is less than 1.
    /// <para><paramref name="page"/> 小于 1。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a known root project.
    /// <para><paramref name="key"/> 不是已知的 root 项目。</para></exception>
    /// <exception cref="HttpRequestException">Every candidate source failed, or a source returned a non-success status.
    /// <para>全部候选源失败，或某个源返回非成功状态。</para></exception>
    /// <exception cref="InvalidOperationException">The response exceeds the size cap or is not a usable release list.
    /// <para>响应超出大小上限或不是可用的 Release 列表。</para></exception>
    public static async Task<RootPatchReleasePage> ListPageAsync(HttpClient client, string key,
        int maximum = DefaultCatalogCount, int page = 1, CancellationToken cancellationToken = default,
        IReadOnlyList<Uri>? sources = null)
    {
        var vendor = RootPatchCatalog.Find(key)
            ?? throw new ArgumentException($"Unknown root patch vendor '{key}'.", nameof(key));
        var releases = await GitHubReleaseClient
            .ListPageAsync(client, vendor.Owner, vendor.Repository, maximum, page, cancellationToken, sources)
            .ConfigureAwait(false);
        var pageSize = Math.Clamp(maximum, 1, GitHubReleaseClient.MaximumListCount);
        return new RootPatchReleasePage(FromReleases(vendor, releases), releases.Count == pageSize);
    }

    /// <summary>Projects already parsed releases onto the installable packages they publish.
    /// <para>把已解析的 Release 投影为它们发布的可用补丁包。</para></summary>
    /// <param name="vendor">Vendor the releases belong to. <para>这些 Release 所属的供应方。</para></param>
    /// <param name="releases">Parsed releases in release order. <para>按发布顺序排列的已解析 Release。</para></param>
    /// <returns>One package per release that publishes an installable asset, in the same order.
    /// <para>每个发布了可安装资产的 Release 对应一个补丁包，顺序与输入一致。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="vendor"/> or <paramref name="releases"/> is <see langword="null"/>.
    /// <para><paramref name="vendor"/> 或 <paramref name="releases"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<RootPatchRelease> FromReleases(RootPatchVendor vendor, IReadOnlyList<GitHubRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(releases);
        List<RootPatchRelease> packages = [];
        foreach (var release in releases)
        {
            if (FromRelease(vendor, release) is { } package)
            {
                packages.Add(package);
            }
        }

        return packages;
    }

    /// <summary>Projects a single parsed release onto the package it publishes.
    /// <para>把单个已解析的 Release 投影为它发布的补丁包。</para></summary>
    /// <param name="vendor">Vendor the release belongs to. <para>该 Release 所属的供应方。</para></param>
    /// <param name="release">Parsed release description. <para>已解析的 Release 描述。</para></param>
    /// <returns>The installable package, or <see langword="null"/> when the release publishes no APK or ZIP asset.
    /// <para>可安装的补丁包；Release 未发布 APK 或 ZIP 资产时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="vendor"/> or <paramref name="release"/> is <see langword="null"/>.
    /// <para><paramref name="vendor"/> 或 <paramref name="release"/> 为 <see langword="null"/>。</para></exception>
    public static RootPatchRelease? FromRelease(RootPatchVendor vendor, GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(release);
        if (SelectPackageAsset(release.Assets) is not { } asset)
        {
            return null;
        }

        return new RootPatchRelease(vendor.Key, release.Version, release.Tag, asset.Name, asset.DownloadUrl,
            asset.Size, asset.Sha256);
    }

    /// <summary>Selects the asset a patch run installs from, preferring a release-marked APK and avoiding
    /// legacy or spoofed variants. <para>选出修补流程可安装的资产，优先选择标记为发行构建的 APK，并避开 legacy
    /// 与 spoofed 变体。</para></summary>
    /// <param name="assets">Assets of one release, in API order. <para>单个 Release 的资产，按 API 顺序。</para></param>
    /// <returns>The preferred package asset, or <see langword="null"/> when the release publishes no APK or ZIP.
    /// <para>首选的补丁包资产；Release 未发布 APK 或 ZIP 时为 <see langword="null"/>。</para></returns>
    public static GitHubAsset? SelectPackageAsset(IReadOnlyList<GitHubAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        GitHubAsset? selected = null;
        var selectedRank = int.MaxValue;
        foreach (var asset in assets)
        {
            var rank = Rank(asset.Name);
            if (rank < selectedRank)
            {
                selected = asset;
                selectedRank = rank;
            }
        }

        return selected;
    }

    /// <summary>Ranks one asset name for the package picker; lower wins.
    /// <para>为资产挑选器给单个资产名打分；分数越低越优先。</para></summary>
    /// <remarks>
    /// Release-marked APKs win over plain APKs, then ZIPs; debug, sources and stub-app builds never win.
    /// <para>标记发行版的 APK 优先于普通 APK，其次 ZIP；debug、sources 与 stub 应用壳永远不入选。</para>
    /// </remarks>
    private static int Rank(string name)
    {
        var extension = Path.GetExtension(name);
        var isApk = extension.Equals(".apk", StringComparison.OrdinalIgnoreCase);
        var isZip = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (!isApk && !isZip)
        {
            return int.MaxValue;
        }

        if (name.Contains("debug", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sources", StringComparison.OrdinalIgnoreCase)
            || name.Contains("stub", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var releaseMarked = name.Contains("release", StringComparison.OrdinalIgnoreCase);
        var variantPenalty = name.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("spoofed", StringComparison.OrdinalIgnoreCase)
            ? 4
            : 0;
        return (isApk, releaseMarked) switch
        {
            (true, true) => variantPenalty,
            (true, false) => 1 + variantPenalty,
            (false, true) => 2 + variantPenalty,
            _ => 3 + variantPenalty,
        };
    }
}