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

/// <summary>
/// Projects root project releases onto the package a boot patch run consumes.
/// <para>把 root 项目 Release 投影为修补流程可消费的补丁包。</para>
/// </summary>
/// <remarks>
/// Release metadata, byte length and digest are read from <c>api.github.com</c> only and never from a
/// mirror; the downloader derives mirrors from the origin URL for the payload alone.
/// <para>版本元数据、字节长度与摘要只从 <c>api.github.com</c> 读取，绝不来自镜像；下载器仅对载荷本体
/// 从源站地址派生镜像。</para>
/// </remarks>
public static class RootPatchReleaseClient
{
    /// <summary>Lists published packages of a root project, newest release first.
    /// <para>列出 root 项目的已发布补丁包，最新的 Release 在前。</para></summary>
    /// <param name="client">HTTP client used for the single origin request.
    /// <para>用于这次源站请求的 HTTP 客户端。</para></param>
    /// <param name="key">Root project key from <see cref="RootPatchCatalog"/>.
    /// <para><see cref="RootPatchCatalog"/> 中的项目键。</para></param>
    /// <param name="maximum">Requested release count before asset filtering; clamped by the metadata reader.
    /// <para>按资产过滤前请求的 Release 数量；由元数据读取器收敛。</para></param>
    /// <param name="cancellationToken">Cancels the request and the parse.
    /// <para>取消请求与解析。</para></param>
    /// <returns>Every release that publishes an installable package, in release order.
    /// <para>按发布顺序排列的、发布了可安装补丁包的 Release。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a known root project.
    /// <para><paramref name="key"/> 不是已知的 root 项目。</para></exception>
    /// <exception cref="HttpRequestException">The origin returned a non-success status.
    /// <para>源站返回非成功状态。</para></exception>
    /// <exception cref="InvalidOperationException">The response exceeds the size cap or is not a usable release list.
    /// <para>响应超出大小上限或不是可用的 Release 列表。</para></exception>
    public static async Task<IReadOnlyList<RootPatchRelease>> ListAsync(HttpClient client, string key,
        int maximum = GitHubReleaseClient.DefaultListCount, CancellationToken cancellationToken = default)
    {
        var vendor = RootPatchCatalog.Find(key)
            ?? throw new ArgumentException($"Unknown root patch vendor '{key}'.", nameof(key));
        var releases = await GitHubReleaseClient
            .ListAsync(client, vendor.Owner, vendor.Repository, maximum, cancellationToken)
            .ConfigureAwait(false);
        return FromReleases(vendor, releases);
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

    /// <summary>Selects the asset a patch run installs from, preferring an APK that is marked as a release build.
    /// <para>选出修补流程可安装的资产，优先选择标记为发行构建的 APK。</para></summary>
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
            || name.Contains("sources", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var releaseMarked = name.Contains("release", StringComparison.OrdinalIgnoreCase);
        return (isApk, releaseMarked) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => 2,
            _ => 3,
        };
    }
}