using System.Text;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.Components.Root;
using UotanStudio.Net.Http;

using UotanStudio.Services.Network;
namespace UotanStudio.Services.PatchPackages;

/// <summary>One downloadable root patch package offered for a boot patch run.
/// <para>一次 Boot 修补可用的可下载根补丁包。</para></summary>
/// <param name="Source">Root project the package belongs to.<para>该补丁包所属的 root 项目。</para></param>
/// <param name="Version">Version text shown in the version list.<para>在版本列表中显示的版本。</para></param>
/// <param name="Tag">Release tag exactly as published.<para>发布时的原始 Release 标签。</para></param>
/// <param name="AssetName">Release asset file name.<para>Release 资产文件名。</para></param>
/// <param name="DownloadUrl">Origin download URL; mirrors are derived from it by the downloader.
/// <para>源站下载地址；镜像由下载器据此派生。</para></param>
/// <param name="Length">Exact byte length reported by the release API.
/// <para>Release API 上报的精确字节长度。</para></param>
/// <param name="Sha256">Published lowercase SHA-256 digest, or <see langword="null"/> when the release publishes none.
/// <para>公布的小写 SHA-256 摘要；该 Release 未公布摘要时为 <see langword="null"/>。</para></param>
public sealed record RootPatchPackage(
    RootPatchPackageSource Source,
    string Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long Length,
    string? Sha256);

/// <summary>Where a resolved patch package came from.
/// <para>已解析补丁包的来源。</para></summary>
/// <param name="Path">Absolute local file path handed to the patcher.<para>交给修补器的本地绝对路径。</para></param>
/// <param name="Source">Concrete URL that delivered the file, or the origin URL on a cache hit.
/// <para>交付该文件的具体地址；命中缓存时为源站地址。</para></param>
/// <param name="FromCache">Whether the file was already stored and verified locally.
/// <para>该文件是否已在本地存放并通过校验。</para></param>
public sealed record RootPatchPackageArtifact(string Path, Uri Source, bool FromCache);

/// <summary>Outcome of validating one patch package file.
/// <para>单个补丁包文件的校验结果。</para></summary>
/// <param name="Path">Absolute path that was inspected.<para>被检查的绝对路径。</para></param>
/// <param name="Detection">Detected family, ABIs and options, or <see langword="null"/> when the file is not a readable package.
/// <para>检测到的类型族、ABI 与选项；文件不是可读补丁包时为 <see langword="null"/>。</para></param>
/// <param name="IsSupported">Whether the file is usable for the requested source.
/// <para>该文件是否可用于所请求的来源。</para></param>
/// <param name="Problem">Human-readable reason when <paramref name="IsSupported"/> is <see langword="false"/>.
/// <para><paramref name="IsSupported"/> 为 <see langword="false"/> 时的人类可读原因。</para></param>
public sealed record RootPatchPackageValidation(string Path, BootPatchDetection? Detection, bool IsSupported, string? Problem);

/// <summary>
/// Lists, downloads and validates root patch packages, storing them beside the scrcpy server store.
/// <para>列出、下载并校验根补丁包，把它们与 scrcpy 服务端存储并排存放。</para>
/// </summary>
/// <remarks>
/// Release metadata is read from the origin only. The payload may arrive through a mirror, and is accepted
/// only after its length and published digest match; a release that publishes no digest is fetched from the
/// origin alone. Downloads land in the shared download resource directory, one folder per upstream command,
/// one subfolder per version, and are published atomically after verification.
/// <para>版本元数据只从源站读取。载荷可以由镜像交付，但只有在长度与公布的摘要都一致后才被接受；未公布摘要的
/// Release 只从源站获取。下载产物落在共享下载资源目录下，每个上游命令一个文件夹、每个版本一个子目录，
/// 并在校验通过后原子发布。</para>
/// </remarks>
public sealed class RootPatchPackageService
{
    private readonly HttpClient _client;
    private readonly BootPatchService _patchService = new();
    private readonly bool? _useMirrorListOverride;
    private string? _preferredSourceAuthority;

    /// <summary>Process-wide instance used by the boot patch window.
    /// <para>修补 Boot 窗口使用的进程级实例。</para></summary>
    /// <value>A service over a shared HTTP client. <para>基于共享 HTTP 客户端的服务实例。</para></value>
    public static RootPatchPackageService Shared { get; } = new(SharedHttpClient.Shared);

    /// <summary>Creates the service.<para>创建服务。</para></summary>
    /// <param name="client">HTTP client shared by the metadata read and the download.
    /// <para>版本读取与下载共用的 HTTP 客户端。</para></param>
    /// <param name="resourceRoot">Override for the download resource directory, used by tests.
    /// <para>下载资源目录的覆盖值，供测试使用。</para></param>
    public RootPatchPackageService(HttpClient client, string? resourceRoot = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ResourceRoot = string.IsNullOrWhiteSpace(resourceRoot) ? DefaultResourceRoot() : Path.GetFullPath(resourceRoot);
    }

    /// <summary>Creates a service with an explicit mirror-list decision, so a test can pin downloads to the origin.
    /// <para>创建带显式镜像列表决策的服务，使测试可把下载固定到源站。</para></summary>
    /// <param name="client">HTTP client shared by the metadata read and the download.
    /// <para>版本读取与下载共用的 HTTP 客户端。</para></param>
    /// <param name="resourceRoot">Override for the download resource directory.
    /// <para>下载资源目录的覆盖值。</para></param>
    /// <param name="useMirrorList">Whether mirror URLs may deliver the payload.
    /// <para>是否允许镜像地址交付载荷。</para></param>
    internal RootPatchPackageService(HttpClient client, string? resourceRoot, bool useMirrorList)
        : this(client, resourceRoot)
        => _useMirrorListOverride = useMirrorList;

    /// <summary>Shared download resource directory that holds one folder per command.
    /// <para>共享的下载资源目录，其中每个命令一个文件夹。</para></summary>
    /// <value>Absolute directory path: the constructor override, then the persisted setting, then the parent of the
    /// configured scrcpy server store, then <c>%APPDATA%/UotanStudio</c>.
    /// <para>绝对目录路径：优先构造函数覆盖值，其次是持久化设置，再其次是已配置 scrcpy 服务端存储的父目录，
    /// 最后是 <c>%APPDATA%/UotanStudio</c>。</para></value>
    public string ResourceRoot { get; }

    /// <summary>Authority of the fastest source found by the most recent speed probe or download.
    /// <para>最近一次测速或下载所找到的最快源的站点部分。</para></summary>
    /// <value><c>scheme://host[:port]</c> of the origin or mirror, or <see langword="null"/> before any ranking.
    /// <para>源站或镜像的 <c>scheme://host[:port]</c>；尚未排序时为 <see langword="null"/>。</para></value>
    public string? PreferredSourceAuthority => _preferredSourceAuthority;

    /// <summary>Speed-probes every candidate source of a GitHub file and remembers the fastest one.
    /// <para>对某个 GitHub 文件的全部候选源测速，并记住最快的一个。</para></summary>
    /// <remarks>
    /// The probe measures the origin plus every enabled mirror with a bounded range request, and the winner
    /// is reused by later downloads, so the probe cost is paid once per window instead of once per transfer.
    /// Release metadata stays on <c>api.github.com</c>: mirrors do not proxy that API, and the byte length and
    /// digest that make a mirror-delivered payload trustworthy must not come from the mirror itself.
    /// <para>测速以有界范围请求测量源站与全部启用镜像，胜出的源被后续下载复用，使每次窗口只付一次测速成本。
    /// Release 元数据仍取自 <c>api.github.com</c>：镜像不代理该 API，而让镜像载荷可信的字节长度与摘要不能来自镜像本身。</para>
    /// </remarks>
    /// <param name="url">GitHub file URL whose sources are ranked, typically the newest release asset.
    /// <para>参与排序的 GitHub 文件地址，通常是最新发布资产。</para></param>
    /// <param name="cancellationToken">Cancels probing; the remembered source is left unchanged.
    /// <para>取消测速；已记住的源保持不变。</para></param>
    /// <returns>The winning authority, or <see langword="null"/> when no source answered.
    /// <para>胜出源的站点部分；没有任何源应答时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is <see langword="null"/>.
    /// <para><paramref name="url"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<string?> ProbeSourcesAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var sources = await HttpDownload.SelectSourcesBySpeedAsync(url, _client, cancellationToken)
            .ConfigureAwait(false);
        var fastest = sources.Count > 0 ? sources[0] : null;
        if (fastest is null)
        {
            return null;
        }

        _preferredSourceAuthority = fastest.GetLeftPart(UriPartial.Authority);
        LogService.Instance.Write(LogLevel.Info, "BootPatch",
            $"Fastest source for {url.Host}: {_preferredSourceAuthority}.");
        return _preferredSourceAuthority;
    }

    /// <summary>Absolute folder holding one source's downloaded packages.
    /// <para>存放某个来源已下载补丁包的绝对文件夹。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns>The command folder under <see cref="ResourceRoot"/>. <para><see cref="ResourceRoot"/> 下的命令文件夹。</para></returns>
    public string CommandFolderFor(RootPatchPackageSource source)
        => Path.Combine(ResourceRoot, source.CommandFolderName());

    /// <summary>Absolute path a package version is stored at: command folder, version folder, asset name.
    /// <para>某个版本补丁包的绝对存储路径：命令文件夹、版本文件夹、资产名。</para></summary>
    /// <param name="package">Package description. <para>补丁包描述。</para></param>
    /// <returns>The per-version file path. <para>按版本存放的文件路径。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public string PackagePathFor(RootPatchPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Path.Combine(CommandFolderFor(package.Source), SanitizeVersion(package.Version),
            SanitizeAssetName(package.AssetName));
    }

    /// <summary>Reports whether this installation already stores a byte-length-compatible copy of a package.
    /// <para>报告本机是否已存放该补丁包的一份长度相符的副本。</para></summary>
    /// <param name="package">Package to look for. <para>要查找的补丁包。</para></param>
    /// <returns><see langword="true"/> when the per-version file exists with the published length; the digest is
    /// re-verified when the package is actually used. <para>按版本存放的文件存在且长度与公布值一致时为
    /// <see langword="true"/>；摘要在实际使用该补丁包时重新校验。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public bool HasStoredPackage(RootPatchPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        try
        {
            var info = new FileInfo(PackagePathFor(package));
            return info.Exists && (package.Length <= 0 || info.Length == package.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads the published packages of one source, newest release first.
    /// <para>读取某个来源的已发布补丁包，最新的 Release 在前。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <param name="cancellationToken">Cancels the metadata read. <para>取消版本元数据读取。</para></param>
    /// <returns>Every release that publishes an installable package. <para>每个发布了可安装补丁包的 Release。</para></returns>
    /// <exception cref="HttpRequestException">The release feed could not be read. <para>无法读取 Release 源。</para></exception>
    /// <exception cref="InvalidOperationException">The release feed returned no usable document.
    /// <para>Release 源没有返回可用文档。</para></exception>
    public async Task<IReadOnlyList<RootPatchPackage>> ListPackagesAsync(RootPatchPackageSource source,
        CancellationToken cancellationToken = default)
    {
        var releases = await RootPatchReleaseClient.ListAsync(_client, source.Key(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return [.. releases.Select(release => new RootPatchPackage(source, release.Version, release.Tag,
            release.AssetName, release.DownloadUrl, release.Length, release.Sha256))];
    }

    /// <summary>Resolves a verified local file for a package, downloading it when it is not cached yet.
    /// <para>解析补丁包对应的已校验本地文件；尚未缓存时先下载。</para></summary>
    /// <param name="package">Package to resolve. <para>要解析的补丁包。</para></param>
    /// <param name="progress">Receives fractional progress from 0 to 1 while the payload is transferred.
    /// <para>载荷传输期间接收 0 到 1 的小数进度。</para></param>
    /// <param name="cancellationToken">Cancels the transfer; the staged file is removed before the exception escapes.
    /// <para>取消传输；异常抛出前会先删除暂存文件。</para></param>
    /// <returns>The local file together with the source that delivered it.
    /// <para>本地文件以及交付它的地址。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The delivered file does not match the published length or digest.
    /// <para>交付的文件与公布的长度或摘要不符。</para></exception>
    /// <exception cref="IOException">Every candidate source failed, or the file could not be staged or committed.
    /// <para>所有候选源均失败，或无法暂存/提交该文件。</para></exception>
    public async Task<RootPatchPackageArtifact> EnsurePackageAsync(RootPatchPackage package,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        var target = PackagePathFor(package);
        if (await MatchesStoredAsync(target, package, cancellationToken).ConfigureAwait(false))
        {
            return new RootPatchPackageArtifact(target, new Uri(package.DownloadUrl), FromCache: true);
        }

        var origin = new Uri(package.DownloadUrl);
        var staged = target + $".part-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var transfer = progress is null
                ? null
                : new Progress<(long Processed, long Total)>(sample => progress.Report(sample.Total > 0
                    ? Math.Clamp((double)sample.Processed / sample.Total, 0d, 1d)
                    : 0d));
            Uri used;
            if ((_useMirrorListOverride ?? RootPatchPackageSettingsStore.UseMirrorList) && package.Sha256 is not null)
            {
                used = await HttpDownload.DownloadAsync(_client, origin, staged, transfer, cancellationToken,
                    sources: OrderSources(origin)).ConfigureAwait(false);
            }
            else
            {
                used = origin;
                await HttpDownload.DownloadSingleAsync(_client, origin, staged, transfer, cancellationToken)
                    .ConfigureAwait(false);
            }

            var length = new FileInfo(staged).Length;
            if (package.Length > 0 && length != package.Length)
            {
                throw new InvalidDataException(
                    $"Root patch package {package.Source.CommandFolderName()} {package.Version} is {package.Length} bytes but {used.Host} delivered {length}.");
            }

            var digest = await Sha256HexAsync(staged, cancellationToken).ConfigureAwait(false);
            if (package.Sha256 is { } expected && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Root patch package {package.Source.CommandFolderName()} {package.Version} digest mismatch from {used.Host}: expected {expected}, got {digest}.");
            }

            AtomicPublish.Publish(staged, target);
            _preferredSourceAuthority = used.GetLeftPart(UriPartial.Authority);
            LogService.Instance.Write(LogLevel.Info, "BootPatch",
                $"Stored root patch package {package.Source.CommandFolderName()} {package.Version} from {used.Host}.");
            return new RootPatchPackageArtifact(target, used, FromCache: false);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>Validates a package file and checks it belongs to the expected source family.
    /// <para>校验补丁包文件，并检查它属于期望的来源类型族。</para></summary>
    /// <param name="path">Package file to inspect. <para>要检查的补丁包文件。</para></param>
    /// <param name="expected">Expected source, or <see langword="null"/> for a user-chosen file of any supported family.
    /// <para>期望的来源；自选文件不限定类型族时为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the package analysis. <para>取消包解析。</para></param>
    /// <returns>The detection together with the verdict; an unreadable or unrelated file reports why.
    /// <para>检测结果与结论；不可读或类型族不符时给出原因。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty. <para><paramref name="path"/> 为空。</para></exception>
    public async Task<RootPatchPackageValidation> ValidateAsync(string path, RootPatchPackageSource? expected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return new RootPatchPackageValidation(full, null, false,
                $"The patch package '{full}' does not exist.");
        }

        try
        {
            var detection = await _patchService.DetectAsync(full, cancellationToken).ConfigureAwait(false);
            if (!detection.IsSupported)
            {
                return new RootPatchPackageValidation(full, detection, false,
                    "The selected file is not a supported Magisk, KernelSU or APatch package.");
            }

            if (expected is { } source && detection.Kind != source.ExpectedKind())
            {
                return new RootPatchPackageValidation(full, detection, false,
                    $"The selected file is a {detection.Kind} package, but {source.CommandFolderName()} requires {source.ExpectedKind()}.");
            }

            return new RootPatchPackageValidation(full, detection, true, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new RootPatchPackageValidation(full, null, false, ex.Message);
        }
    }

    /// <summary>Orders the candidate sources of one asset, keeping the fastest probed source first.
    /// <para>排序列出某个资产的全部候选源，把测速胜出的源放在最前。</para></summary>
    /// <param name="origin">Origin asset URL. <para>源站资产地址。</para></param>
    /// <returns>Every enumerable source, the remembered authority first when it is one of them.
    /// <para>全部可枚举源；记住的源若在列则排在最前。</para></returns>
    private IReadOnlyList<Uri> OrderSources(Uri origin)
    {
        var sources = HttpDownload.EnumerateSources(origin).ToArray();
        if (_preferredSourceAuthority is not { Length: > 0 } authority || sources.Length <= 1
            || !sources.Any(source => HasAuthority(source, authority)))
        {
            return sources;
        }

        List<Uri> ordered = [.. sources.Where(source => HasAuthority(source, authority))];
        ordered.AddRange(sources.Where(source => !HasAuthority(source, authority)));
        return ordered;
    }

    private static bool HasAuthority(Uri source, string authority)
        => string.Equals(source.GetLeftPart(UriPartial.Authority), authority, StringComparison.OrdinalIgnoreCase);

    private static string DefaultResourceRoot()
    {
        var configured = RootPatchPackageSettingsStore.DownloadDir;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var scrcpyStore = Scrcpy.ScrcpyServerSettingsStore.CacheDir;
        if (!string.IsNullOrWhiteSpace(scrcpyStore)
            && Path.GetDirectoryName(Path.GetFullPath(scrcpyStore)) is { Length: > 0 } parent)
        {
            return parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio");
    }

    private static async Task<bool> MatchesStoredAsync(string path, RootPatchPackage package,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            if (package.Length > 0 && info.Length != package.Length)
            {
                return false;
            }

            return package.Sha256 is null
                || string.Equals(await Sha256HexAsync(path, cancellationToken).ConfigureAwait(false), package.Sha256,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await Sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string SanitizeVersion(string version) => SanitizeSegment(version, "unknown");

    private static string SanitizeAssetName(string assetName)
        => SanitizeSegment(Path.GetFileName(assetName), "package.bin");

    private static string SanitizeSegment(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var segment = builder.ToString().Trim();
        return segment.Length == 0 || segment.All(static character => character == '.') ? fallback : segment;
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
    }
}

/// <summary>
/// Settings adapter for the root patch package store, read on demand so a change applies to the next attempt.
/// <para>根补丁包存储的设置适配器，按需读取以便变更在下一次尝试时生效。</para>
/// </summary>
public static class RootPatchPackageSettingsStore
{
    /// <summary>Whether non-official GitHub mirrors may deliver payloads that publish a digest.
    /// <para>是否允许非官方 GitHub 镜像交付已公布摘要的载荷。</para></summary>
    /// <value><see langword="true"/> unless persisted otherwise. <para>除非持久化设置了其他值，否则为 <see langword="true"/>。</para></value>
    public static bool UseMirrorList => ReadFlag("patchPackage.useMirrorList", true);

    /// <summary>Configured download resource directory, or empty for the default location.
    /// <para>已配置的下载资源目录；为空表示使用默认位置。</para></summary>
    /// <value>An absolute directory path, or empty when unset. <para>绝对目录路径；未设置时为空。</para></value>
    public static string DownloadDir
    {
        get
        {
            if (SettingsPersistenceService.Instance.LoadSettings().AdditionalSettings is not { } extra)
            {
                return string.Empty;
            }

            if (!extra.TryGetValue("patchPackage.downloadDir", out var element))
            {
                return string.Empty;
            }

            return element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        }
    }

    private static bool ReadFlag(string key, bool fallback)
    {
        if (SettingsPersistenceService.Instance.LoadSettings().AdditionalSettings is not { } extra)
        {
            return fallback;
        }

        if (!extra.TryGetValue(key, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => fallback,
        };
    }
}