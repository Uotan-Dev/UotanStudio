using UotanStudio.Build;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.GitHub;
using UotanStudio.Net.Http;

namespace UotanStudio.Services.Update;

/// <summary>
/// The kind of installer artifact an update package carries, which decides how it is handed to the platform.
/// <para>更新包携带的安装产物类型，决定它如何交给平台安装器。</para>
/// </summary>
public enum AppUpdatePackageKind
{
    /// <summary>An MSIX package installed through the Windows app installer. <para>通过 Windows 应用安装程序安装的 MSIX 包。</para></summary>
    Msix,

    /// <summary>A macOS disk image holding the application bundle. <para>承载应用包的 macOS 磁盘映像。</para></summary>
    Dmg,

    /// <summary>A Debian package installed by the system package manager. <para>由系统包管理器安装的 Debian 包。</para></summary>
    Deb,

    /// <summary>An RPM package installed by the system package manager. <para>由系统包管理器安装的 RPM 包。</para></summary>
    Rpm,

    /// <summary>An Alpine apk package installed by the system package manager. <para>由系统包管理器安装的 Alpine apk 包。</para></summary>
    Apk,

    /// <summary>An Arch pacman package installed by the system package manager. <para>由系统包管理器安装的 Arch pacman 包。</para></summary>
    Pacman,

    /// <summary>A plain archive used when no installer package is published. <para>未发布安装包时使用的普通归档。</para></summary>
    Archive,
}

/// <summary>
/// One release asset that can be downloaded as an application update; the sort order of
/// <see cref="AppUpdatePackageKind"/> is the preference order when several kinds exist.
/// <para>一个可作为应用更新下载的 Release 资产；<see cref="AppUpdatePackageKind"/> 的排序即多种产物并存时的优先顺序。</para>
/// </summary>
/// <param name="Kind">The installer artifact kind. <para>安装产物类型。</para></param>
/// <param name="Name">Published asset file name. <para>已发布的资产文件名。</para></param>
/// <param name="DownloadUrl">Origin download URL of the asset. <para>资产的源站下载地址。</para></param>
/// <param name="Size">Exact byte length, or zero when the release API reported none. <para>精确字节长度；API 未上报时为 0。</para></param>
/// <param name="Sha256">Lowercase SHA-256 published by the API, or null when absent. <para>API 公布的小写 SHA-256；缺失时为 null。</para></param>
public sealed record AppUpdatePackage(
    AppUpdatePackageKind Kind,
    string Name,
    string DownloadUrl,
    long Size,
    string? Sha256)
{
    /// <summary>
    /// Whether this package is a platform installer rather than a plain archive.
    /// <para>该包是否为平台安装器而非普通归档。</para>
    /// </summary>
    /// <value>True for every kind except <see cref="AppUpdatePackageKind.Archive"/>.
    /// <para>除 <see cref="AppUpdatePackageKind.Archive"/> 之外的任何类型都为真。</para></value>
    public bool IsInstaller => Kind != AppUpdatePackageKind.Archive;
}

/// <summary>
/// The outcome of one update check: either the running build is current, or a newer release with a
/// usable package for the current platform was found.
/// <para>一次更新检查的结果：运行中的构建已是最新，或找到了带有当前平台可用包的新版本。</para>
/// </summary>
/// <param name="Release">The checked release. <para>被检查的 Release。</para></param>
/// <param name="IsNewer">Whether the release is newer than the running build. <para>该 Release 是否比运行中的构建更新。</para></param>
/// <param name="Package">The selected package, or null when the release publishes nothing usable. <para>选中的包；Release 没有可用产物时为 null。</para></param>
/// <param name="ReleaseNotes">Release notes text, or null when the release carries none. <para>更新日志文本；Release 未携带时为 null。</para></param>
public sealed record AppUpdateCheckResult(
    GitHubRelease Release,
    bool IsNewer,
    AppUpdatePackage? Package,
    string? ReleaseNotes);

/// <summary>
/// One progress sample of an update download.
/// <para>更新下载的一次进度样本。</para>
/// </summary>
/// <param name="Processed">Bytes written so far. <para>已写入的字节数。</para></param>
/// <param name="Total">Total bytes, or zero when the server exposes no length. <para>总字节数；服务器未给出长度时为 0。</para></param>
public sealed record AppUpdateDownloadProgress(long Processed, long Total);

/// <summary>
/// Checks for application updates from the project's GitHub releases and downloads the matching
/// package for the current platform.
/// <para>从项目的 GitHub Release 检查应用更新，并下载当前平台对应的安装包。</para>
/// </summary>
/// <remarks>
/// Release metadata and the artifact both come from <c>UotanStudio.Net</c>; this service only decides
/// which asset belongs to the running platform and channel, stages the download under
/// <c>AppPaths.Root/updates</c>, and verifies the reported length and SHA-256 before publishing it.
/// Every network call carries the caller's <see cref="CancellationToken"/>.
/// <para>Release 元数据与产物都来自 <c>UotanStudio.Net</c>；本服务只决定哪个资产属于当前平台与通道，
/// 把下载暂存在 <c>AppPaths.Root/updates</c> 下，并在发布前校验上报的长度与 SHA-256。所有网络调用贯穿调用方的
/// <see cref="CancellationToken"/>。</para>
/// </remarks>
public sealed class AppUpdateService
{
    /// <summary>
    /// The repository owner read from <see cref="BuildInfo.RepositoryUrl"/>'s fixed coordinates.
    /// <para>由 <see cref="BuildInfo.RepositoryUrl"/> 固定坐标得到的仓库拥有者。</para>
    /// </summary>
    private const string Owner = "Uotan-Dev";

    /// <summary>
    /// The repository name read from <see cref="BuildInfo.RepositoryUrl"/>'s fixed coordinates.
    /// <para>由 <see cref="BuildInfo.RepositoryUrl"/> 固定坐标得到的仓库名。</para>
    /// </summary>
    private const string Repository = "UotanStudio";

    private readonly HttpClient _client;

    /// <summary>
    /// Creates the update service over the supplied HTTP client.
    /// <para>围绕给定的 HTTP 客户端创建更新服务。</para>
    /// </summary>
    /// <param name="client">Client used for metadata reads and downloads. <para>用于元数据读取与下载的客户端。</para></param>
    public AppUpdateService(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Directory where staged update packages live, under the application data root.
    /// <para>暂存更新包的目录，位于应用数据根目录下。</para>
    /// </summary>
    /// <value>The update cache directory. <para>更新缓存目录。</para></value>
    public static string UpdateDirectory => Path.Combine(AppPaths.Root, "updates");

    /// <summary>
    /// Fetches the newest published release and decides whether it upgrades the running build.
    /// <para>获取最新已发布的 Release，并判断它是否升级运行中的构建。</para>
    /// </summary>
    /// <param name="skippedVersion">A version the user asked to skip, or null when nothing is skipped.
    /// <para>用户要求跳过的版本；没有跳过时为 null。</para></param>
    /// <param name="cancellationToken">Cancels the metadata request. <para>取消元数据请求。</para></param>
    /// <returns>The check result describing the release, whether it is newer, and the chosen package.
    /// <para>描述该 Release、是否更新以及选中包的检查结果。</para></returns>
    /// <exception cref="HttpRequestException">The release feed could not be read. <para>无法读取 Release 源。</para></exception>
    public async Task<AppUpdateCheckResult> CheckAsync(string? skippedVersion,
        CancellationToken cancellationToken = default)
    {
        var release = await SelectReleaseAsync(cancellationToken).ConfigureAwait(false);
        var isNewer = IsNewerVersion(release.Version, BuildInfo.Version)
            && !string.Equals(Normalize(release.Version), Normalize(skippedVersion), StringComparison.OrdinalIgnoreCase);
        var package = isNewer ? SelectPackage(release) : null;
        return new AppUpdateCheckResult(release, isNewer, package, ReleaseNotesOf(release));
    }

    /// <summary>
    /// Reads the newest release that matches the running build's channel.
    /// <para>读取与运行中构建通道匹配的最新 Release。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the request. <para>取消请求。</para></param>
    /// <returns>The matching release. <para>匹配的 Release。</para></returns>
    /// <remarks>A release build ignores pre-releases; a beta build accepts them, so a beta user is never
    /// offered a build from another channel. <para>正式版构建忽略预发布；Beta 构建接受预发布，使 Beta 用户不会被
    /// 提示其他通道的构建。</para></remarks>
    private async Task<GitHubRelease> SelectReleaseAsync(CancellationToken cancellationToken)
    {
        var releases = await GitHubReleaseClient
            .ListAsync(_client, Owner, Repository, GitHubReleaseClient.DefaultListCount, cancellationToken)
            .ConfigureAwait(false);
        GitHubRelease? best = null;
        foreach (var release in releases)
        {
            if (BuildInfo.Channel == BuildChannel.Release && IsPrereleaseVersion(release.Version))
            {
                continue;
            }

            if (best is null || IsNewerVersion(release.Version, best.Version))
            {
                best = release;
            }
        }

        return best ?? throw new InvalidOperationException(
            "The release feed published no release usable by this build channel.");
    }

    /// <summary>
    /// Picks the best published asset for the current platform, preferring the native installer and
    /// falling back to a plain archive.
    /// <para>为当前平台挑选最合适的已发布资产，优先安装器，缺失时回退到普通归档。</para>
    /// </summary>
    /// <param name="release">The release whose assets are examined. <para>要检查资产列表的 Release。</para></param>
    /// <returns>The preferred package, or null when nothing usable is published. <para>最优先的包；没有可用产物时为 null。</para></returns>
    public static AppUpdatePackage? SelectPackage(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var rid = CurrentRuntimeIdentifier();
        var preferred = PreferredInstallerKind();
        AppUpdatePackage? best = null;
        foreach (var asset in release.Assets)
        {
            if (!MatchesPlatform(asset.Name, rid))
            {
                continue;
            }

            var candidate = new AppUpdatePackage(ClassifyAsset(asset.Name), asset.Name,
                asset.DownloadUrl, asset.Size, asset.Sha256);
            if (best is null || Rank(candidate.Kind, preferred) < Rank(best.Kind, preferred))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Downloads the selected package into the update directory and verifies its length and digest.
    /// <para>把选中的包下载到更新目录，并校验其长度与摘要。</para>
    /// </summary>
    /// <param name="package">The package to download. <para>要下载的包。</para></param>
    /// <param name="progress">Receives byte progress samples, or null to ignore progress.
    /// <para>接收字节进度样本；为 null 时忽略进度。</para></param>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消传输。</para></param>
    /// <returns>The absolute path of the verified downloaded package. <para>已校验下载包的绝对路径。</para></returns>
    /// <exception cref="InvalidDataException">The delivered length or digest does not match the release metadata.
    /// <para>交付的长度或摘要与 Release 元数据不一致。</para></exception>
    /// <exception cref="IOException">Every candidate source failed. <para>全部候选源失败。</para></exception>
    public async Task<string> DownloadAsync(AppUpdatePackage package,
        IProgress<AppUpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        Directory.CreateDirectory(UpdateDirectory);
        var target = Path.Combine(UpdateDirectory, package.Name);
        var staged = Path.Combine(UpdateDirectory, package.Name + ".part-" + Guid.NewGuid().ToString("N"));
        var relay = progress is null
            ? null
            : new Progress<(long Processed, long Total)>(sample =>
                progress.Report(new AppUpdateDownloadProgress(sample.Processed, sample.Total)));
        try
        {
            await HttpDownload
                .DownloadAsync(_client, new Uri(package.DownloadUrl), staged, relay, cancellationToken)
                .ConfigureAwait(false);

            var info = new FileInfo(staged);
            if (package.Size > 0 && info.Length != package.Size)
            {
                throw new InvalidDataException(
                    $"Update {package.Name} is {package.Size} bytes but the download delivered {info.Length}.");
            }

            if (package.Sha256 is { Length: 64 } expected)
            {
                var digest = Sha256Hex(staged);
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Update {package.Name} digest mismatch: expected {expected}, got {digest}.");
                }
            }

            AtomicPublish.Publish(staged, target);
            return target;
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>
    /// Compares two version strings numerically segment by segment, treating a pre-release suffix as older.
    /// <para>按段比较两个版本串，并把预发布后缀视为更旧。</para>
    /// </summary>
    /// <param name="candidate">The version being considered. <para>待判断的版本。</para></param>
    /// <param name="current">The reference version. <para>参照版本。</para></param>
    /// <returns>True when <paramref name="candidate"/> is newer than <paramref name="current"/>.
    /// <para><paramref name="candidate"/> 比 <paramref name="current"/> 更新时为真。</para></returns>
    public static bool IsNewerVersion(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var left = ParseVersion(candidate);
        var right = ParseVersion(current);
        for (var index = 0; index < Math.Max(left.Numbers.Length, right.Numbers.Length); index++)
        {
            var a = index < left.Numbers.Length ? left.Numbers[index] : 0;
            var b = index < right.Numbers.Length ? right.Numbers[index] : 0;
            if (a != b)
            {
                return a > b;
            }
        }

        if (left.IsPrerelease != right.IsPrerelease)
        {
            return right.IsPrerelease;
        }

        return left.IsPrerelease && ComparePrerelease(left.Suffix, right.Suffix) > 0;
    }

    /// <summary>
    /// Compares two pre-release suffixes segment by segment, so <c>beta.10</c> ranks above <c>beta.9</c>.
    /// <para>按段比较两个预发布后缀，使 <c>beta.10</c> 高于 <c>beta.9</c>。</para>
    /// </summary>
    /// <param name="candidate">The candidate suffix. <para>候选后缀。</para></param>
    /// <param name="current">The reference suffix. <para>参照后缀。</para></param>
    /// <returns>A negative, zero, or positive value describing the ordering.
    /// <para>描述排序的负数、零或正数。</para></returns>
    private static int ComparePrerelease(string candidate, string current)
    {
        var left = candidate.Split('.', '-', '+');
        var right = current.Split('.', '-', '+');
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var a = index < left.Length ? left[index] : string.Empty;
            var b = index < right.Length ? right[index] : string.Empty;
            var numericA = long.TryParse(a, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var na);
            var numericB = long.TryParse(b, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var nb);
            var comparison = numericA && numericB
                ? na.CompareTo(nb)
                : string.CompareOrdinal(a, b);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a version string carries a pre-release suffix such as <c>-beta.2</c>.
    /// <para>版本串是否携带 <c>-beta.2</c> 这类预发布后缀。</para>
    /// </summary>
    /// <param name="version">The version text. <para>版本文本。</para></param>
    /// <returns>True when a suffix is present. <para>存在后缀时为真。</para></returns>
    public static bool IsPrereleaseVersion(string version) => ParseVersion(version).IsPrerelease;

    /// <summary>
    /// The runtime identifier of the running process, used to match release assets.
    /// <para>运行进程的运行时标识符，用于匹配 Release 资产。</para>
    /// </summary>
    /// <returns>The runtime identifier. <para>运行时标识符。</para></returns>
    public static string CurrentRuntimeIdentifier() => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    private static AppUpdatePackageKind PreferredInstallerKind()
    {
        if (OperatingSystem.IsWindows())
        {
            return AppUpdatePackageKind.Msix;
        }

        if (OperatingSystem.IsMacOS())
        {
            return AppUpdatePackageKind.Dmg;
        }

        return AppUpdatePackageKind.Deb;
    }

    /// <summary>
    /// Orders asset kinds so the platform's own installer wins over other installers, which win over a
    /// plain archive.
    /// <para>给产物类型排序：本平台安装器优先于其他安装器，其他安装器优先于普通归档。</para>
    /// </summary>
    /// <param name="kind">The candidate kind. <para>候选类型。</para></param>
    /// <param name="preferred">The running platform's installer kind. <para>运行平台的安装器类型。</para></param>
    /// <returns>A small rank; lower values are preferred. <para>排序值，越小越优先。</para></returns>
    private static int Rank(AppUpdatePackageKind kind, AppUpdatePackageKind preferred)
        => kind == preferred ? 0 : kind == AppUpdatePackageKind.Archive ? 2 : 1;

    private static AppUpdatePackageKind ClassifyAsset(string name)
    {
        if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Msix;
        }

        if (name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Dmg;
        }

        if (name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Deb;
        }

        if (name.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Rpm;
        }

        if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Apk;
        }

        if (name.EndsWith(".pkg.tar.zst", StringComparison.OrdinalIgnoreCase))
        {
            return AppUpdatePackageKind.Pacman;
        }

        return AppUpdatePackageKind.Archive;
    }

    private static bool MatchesPlatform(string name, string rid)
    {
        if (!name.StartsWith("uotanstudio-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Contains(rid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return name.Contains("win-", StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return name.Contains("osx-", StringComparison.OrdinalIgnoreCase);
        }

        return name.Contains("linux-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReleaseNotesOf(GitHubRelease release) => release.ReleaseNotes;

    private static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(Sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string Normalize(string? version) => version?.Trim() ?? string.Empty;

    private static (long[] Numbers, bool IsPrerelease, string Suffix) ParseVersion(string? version)
    {
        var text = Normalize(version);
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var suffixIndex = text.IndexOfAny(['-', '+']);
        var numeric = suffixIndex >= 0 ? text[..suffixIndex] : text;
        var suffix = suffixIndex >= 0 ? text[(suffixIndex + 1)..] : string.Empty;
        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new long[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            _ = long.TryParse(parts[index], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out numbers[index]);
        }

        return (numbers, suffix.Length > 0, suffix);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}