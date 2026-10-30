using System.Text;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.Components.Root;
using UotanStudio.Net.GitHub;
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
/// Release metadata, including the published length and digest, is read from the origin only, so those values stay
/// the trust anchor of the payload. The source probe ranks mirrors for the payload transfer, and every candidate
/// source is verified against the length and digest before the next one is tried; a release that publishes no
/// digest is fetched from the origin alone. Downloads land in the shared download resource directory, one folder
/// per upstream command, one subfolder per version, and are published atomically after verification.
/// <para>Release 元数据（含公布的长度与摘要）只从源站读取，使这些值保持为载荷的信任锚点。源测速只对载荷传输的镜像排序，
/// 每个候选源都要先通过长度与摘要校验，才会尝试下一个；未公布摘要的 Release 只从源站获取。
/// 下载产物落在共享下载资源目录下，每个上游命令一个文件夹、每个版本一个子目录，并在校验通过后原子发布。</para>
/// </remarks>
public sealed class RootPatchPackageService
{
    private readonly HttpClient _client;
    private readonly BootPatchService _patchService = new();
    private readonly bool? _useMirrorListOverride;
    private readonly Lock _catalogGate = new();
    private readonly Dictionary<RootPatchPackageSource, IReadOnlyList<RootPatchPackage>> _catalogs = [];
    private readonly Dictionary<RootPatchPackageSource, Task<IReadOnlyList<RootPatchPackage>>> _catalogLoads = [];
    private readonly Dictionary<RootPatchPackageSource, int> _catalogPages = [];
    private readonly Dictionary<RootPatchPackageSource, bool> _catalogHasMore = [];
    private readonly Dictionary<RootPatchPackageSource, Task> _catalogMoreLoads = [];
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private string? _preferredSourceAuthority;
    private Task? _warmUp;
    private bool _probeAttempted;

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

    /// <summary>Speed-probes every candidate source of a GitHub URL and remembers the fastest one.
    /// <para>对某个 GitHub 地址的全部候选源测速，并记住最快的一个。</para></summary>
    /// <remarks>
    /// The probe measures the origin plus every enabled mirror with a bounded range request, and the winner
    /// is reused by later payload downloads, so the probe cost is paid once per panel instead of once per
    /// transfer. Catalog reads stay on the origin. Mirrors that cannot serve the probed URL fail the probe and
    /// keep the origin-first fallback order.
    /// <para>测速以有界范围请求测量源站与全部启用镜像，胜出的源被后续载荷下载复用，使每个面板只付一次测速成本。
    /// 清单读取保持在源站。无法代理被测地址的镜像会在测速中失败并保持源站兜底的顺序。</para>
    /// </remarks>
    /// <param name="url">GitHub URL whose sources are ranked, the release API at panel warm-up or a release asset.
    /// <para>参与排序的 GitHub 地址；面板预热时为 Release API，其他场景为发布资产。</para></param>
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
        if (_probeAttempted) return _preferredSourceAuthority;
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_probeAttempted) return _preferredSourceAuthority;
            var sources = await HttpDownload.SelectSourcesBySpeedAsync(url, _client, cancellationToken)
                .ConfigureAwait(false);
            _probeAttempted = true;
            if (sources.Count == 0 || sources[0] is not { } fastest)
            {
                LogService.Instance.Write(LogLevel.Warn, "BootPatch",
                    $"No source answered the speed probe for {url.Host}; downloads keep the origin order.");
                return null;
            }

            _preferredSourceAuthority = fastest.GetLeftPart(UriPartial.Authority);
            LogService.Instance.Write(LogLevel.Info, "BootPatch",
                $"Fastest source for {url.Host}: {_preferredSourceAuthority}.");
            return _preferredSourceAuthority;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>Reads every provisionable catalog and pins the fastest source once, so later reads and downloads are free.
    /// <para>一次性读取全部可供给清单并只固定一次最快源，使后续读取与下载不再联网探测。</para></summary>
    /// <remarks>The source probe runs first against the release API and ranks the payload mirrors; all five
    /// catalogs are then fetched simultaneously from the origin, so switching to any package source afterwards
    /// reads from the cache instead of waiting on the network.
    /// <para>先对 Release API 做源测速并为载荷镜像排序；随后五个清单同时从源站并行拉取，使之后切换任何补丁包来源
    /// 都直接读缓存而不再等待网络。</para></remarks>
    /// <param name="cancellationToken">Stops waiting for the catalogs and the probe; completed reads stay cached.
    /// <para>停止等待清单与测速；已完成的读取仍会缓存。</para></param>
    /// <returns>A task that completes after every catalog is cached and the source probe has settled.
    /// <para>全部清单已缓存且源测速已结束后完成的任务。</para></returns>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        lock (_catalogGate)
        {
            if (_warmUp is { IsCompletedSuccessfully: false }) _warmUp = null;
            return _warmUp ??= WarmUpCoreAsync(cancellationToken);
        }
    }

    /// <summary>Reports whether one source's catalog is already cached.
    /// <para>报告某个来源的清单是否已缓存。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns><see langword="true"/> after a successful read of that source.
    /// <para>该来源成功读取后为 <see langword="true"/>。</para></returns>
    public bool CatalogLoaded(RootPatchPackageSource source)
    {
        lock (_catalogGate)
        {
            return _catalogs.ContainsKey(source);
        }
    }

    /// <summary>Returns a cached catalog without any network work.
    /// <para>不联网地返回已缓存的清单。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns>The cached releases, or <see langword="null"/> when this source was not read yet.
    /// <para>已缓存的发布版；该来源尚未读取时为 <see langword="null"/>。</para></returns>
    public IReadOnlyList<RootPatchPackage>? TryGetCatalog(RootPatchPackageSource source)
    {
        lock (_catalogGate)
        {
            return _catalogs.GetValueOrDefault(source);
        }
    }

    private async Task WarmUpCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (UseMirrorSources())
            {
                try
                {
                    await ProbeSourcesAsync(CatalogProbeUrl(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Warn, "BootPatch",
                        $"Root patch source probe skipped: {DescribeLogDetail(ex.Message)}");
                }
            }

            await Task.WhenAll(RootPatchSources.All.Select(async source =>
            {
                try
                {
                    return await ListPackagesAsync(source, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Warn, "BootPatch",
                        $"Root patch catalog {source.CommandFolderName()} skipped: {DescribeLogDetail(ex.Message)}");
                    return (IReadOnlyList<RootPatchPackage>)Array.Empty<RootPatchPackage>();
                }
            }).ToArray()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_catalogGate)
            {
                _warmUp = null;
            }
        }
        catch (Exception ex)
        {
            lock (_catalogGate)
            {
                _warmUp = null;
            }

            LogService.Instance.Write(LogLevel.Warn, "BootPatch",
                $"Root patch catalog warm-up skipped: {DescribeLogDetail(ex.Message)}");
        }
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

    /// <summary>Reads the published packages of one source, newest release first, once per session.
    /// <para>读取某个来源的已发布补丁包，最新的 Release 在前；每个会话只读取一次。</para></summary>
    /// <remarks>
    /// The first read of a source is cached; later reads and concurrent callers share that result, so switching
    /// versions or sources never re-queries the release feed.
    /// <para>一个来源的首次读取会被缓存，后续读取与并发调用共享同一结果，因此切换版本或来源都不会重复查询发布源。</para>
    /// </remarks>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <param name="cancellationToken">Stops waiting for the shared read; the read itself completes and is cached for
    /// later callers. <para>停止等待共享读取；读取本身会完成并供后续调用复用。</para></param>
    /// <returns>Every release that publishes an installable package. <para>每个发布了可安装补丁包的 Release。</para></returns>
    /// <exception cref="HttpRequestException">The release feed could not be read. <para>无法读取 Release 源。</para></exception>
    /// <exception cref="InvalidOperationException">The release feed returned no usable document.
    /// <para>Release 源没有返回可用文档。</para></exception>
    public async Task<IReadOnlyList<RootPatchPackage>> ListPackagesAsync(RootPatchPackageSource source,
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<RootPatchPackage>> load;
        lock (_catalogGate)
        {
            if (_catalogs.TryGetValue(source, out var cached)) return cached;
            if (!_catalogLoads.TryGetValue(source, out var pending))
            {
                pending = LoadCatalogAsync(source);
                _catalogLoads[source] = pending;
            }

            load = pending;
        }

        var catalog = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_catalogGate)
        {
            return _catalogs.TryGetValue(source, out var ready) ? ready : catalog;
        }
    }

    /// <summary>Reports whether the upstream feed may hold releases older than the cached page.
    /// <para>报告上游是否可能还有比已缓存页更早的 Release。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns><see langword="true"/> when the last loaded page was full; <see langword="false"/> before any read.
    /// <para>最后加载的一页已满时为 <see langword="true"/>；尚未读取时为 <see langword="false"/>。</para></returns>
    public bool HasMore(RootPatchPackageSource source)
    {
        lock (_catalogGate)
        {
            return _catalogHasMore.GetValueOrDefault(source);
        }
    }

    /// <summary>Appends the next release page to the cached catalog and returns the cumulative list.
    /// <para>把下一页 Release 追加到已缓存清单，并返回累积列表。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <param name="cancellationToken">Stops waiting for the shared page read; the read itself completes and is cached.
    /// <para>停止等待共享分页读取；读取本身会完成并缓存。</para></param>
    /// <returns>The cumulative releases after the page is merged; the cached list is returned unchanged when no older
    /// page exists. <para>合并后的累积 Release；没有更早页时原样返回已缓存列表。</para></returns>
    /// <exception cref="HttpRequestException">The release feed could not be read.
    /// <para>无法读取 Release 源。</para></exception>
    /// <exception cref="InvalidOperationException">The release feed returned no usable document.
    /// <para>Release 源没有返回可用文档。</para></exception>
    public async Task<IReadOnlyList<RootPatchPackage>> LoadMoreAsync(RootPatchPackageSource source,
        CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_catalogGate)
        {
            if (!_catalogs.TryGetValue(source, out var catalog) || !_catalogHasMore.GetValueOrDefault(source))
            {
                return catalog ?? Array.Empty<RootPatchPackage>();
            }

            if (!_catalogMoreLoads.TryGetValue(source, out var pending))
            {
                pending = LoadMoreCoreAsync(source, _catalogPages.GetValueOrDefault(source) + 1);
                _catalogMoreLoads[source] = pending;
            }

            load = pending;
        }

        await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_catalogGate)
        {
            return _catalogs.GetValueOrDefault(source) ?? Array.Empty<RootPatchPackage>();
        }
    }

    private async Task LoadMoreCoreAsync(RootPatchPackageSource source, int page)
    {
        try
        {
            var result = await ListCatalogPageAsync(source, page).ConfigureAwait(false);
            lock (_catalogGate)
            {
                if (!_catalogs.TryGetValue(source, out var existing)) return;
                var known = existing.Select(package => package.Tag)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<RootPatchPackage> appended = [];
                foreach (var release in result.Releases)
                {
                    if (known.Add(release.Tag))
                    {
                        appended.Add(ToPackage(source, release));
                    }
                }

                if (appended.Count > 0)
                {
                    _catalogs[source] = [.. existing, .. appended];
                }

                _catalogPages[source] = page;
                _catalogHasMore[source] = result.HasMore;
            }
        }
        finally
        {
            lock (_catalogGate)
            {
                _catalogMoreLoads.Remove(source);
            }
        }
    }

    private static RootPatchPackage ToPackage(RootPatchPackageSource source, RootPatchRelease release)
        => new(source, release.Version, release.Tag, release.AssetName, release.DownloadUrl,
            release.Length, release.Sha256);

    private async Task<IReadOnlyList<RootPatchPackage>> LoadCatalogAsync(RootPatchPackageSource source)
    {
        try
        {
            var page = await ListCatalogPageAsync(source, page: 1).ConfigureAwait(false);
            IReadOnlyList<RootPatchPackage> catalog = [.. page.Releases.Select(release => ToPackage(source, release))];
            lock (_catalogGate)
            {
                _catalogs[source] = catalog;
                _catalogPages[source] = 1;
                _catalogHasMore[source] = page.HasMore;
            }

            return catalog;
        }
        finally
        {
            lock (_catalogGate)
            {
                _catalogLoads.Remove(source);
            }
        }
    }

    /// <summary>Builds the lightweight API URL the warm-up probe ranks payload mirrors against.
    /// <para>构造预热测速用于为载荷镜像排序的轻量 API 地址。</para></summary>
    private static Uri CatalogProbeUrl()
    {
        var vendor = RootPatchCatalog.Vendors[0];
        return new Uri($"{GitHubReleaseClient.ApiOrigin}/repos/{vendor.Owner}/{vendor.Repository}/releases?per_page=1&page=1");
    }

    /// <summary>Reads one catalog page from the origin only, because the published length and digest are the trust
    /// anchor of the payload download.
    /// <para>只从源站读取一页清单，因为公布的长度与摘要是载荷下载的信任锚点。</para></summary>
    private Task<RootPatchReleasePage> ListCatalogPageAsync(RootPatchPackageSource source, int page)
        => RootPatchReleaseClient.ListPageAsync(_client, source.Key(),
            RootPatchReleaseClient.DefaultCatalogCount, page, cancellationToken: default);

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
    /// <exception cref="InvalidDataException">The only candidate source delivered a file that does not match the
    /// published length or digest. <para>唯一候选源交付的文件与公布的长度或摘要不符。</para></exception>
    /// <exception cref="IOException">Every candidate source failed, including length or digest mismatches, or the
    /// file could not be staged or committed.
    /// <para>所有候选源均失败（含长度或摘要不符），或无法暂存/提交该文件。</para></exception>
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
            IReadOnlyList<Uri> candidates = UseMirrorSources() && package.Sha256 is not null
                ? OrderSources(origin)
                : [origin];
            Uri used = origin;
            List<string> failures = [];
            var downloaded = false;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await HttpDownload.DownloadSingleAsync(_client, candidate, staged, transfer, cancellationToken)
                        .ConfigureAwait(false);
                    var length = new FileInfo(staged).Length;
                    if (package.Length > 0 && length != package.Length)
                    {
                        throw new InvalidDataException(
                            $"Root patch package {package.Source.CommandFolderName()} {package.Version} is {package.Length} bytes but {candidate.Host} delivered {length}.");
                    }

                    var digest = await Sha256HexAsync(staged, cancellationToken).ConfigureAwait(false);
                    if (package.Sha256 is { } expected && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Root patch package {package.Source.CommandFolderName()} {package.Version} digest mismatch from {candidate.Host}: expected {expected}, got {digest}.");
                    }

                    used = candidate;
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                {
                    if (candidates.Count == 1) throw;
                    failures.Add(candidate.Host + ": " + ex.Message);
                    TryDelete(staged);
                }
            }

            if (!downloaded)
            {
                throw new IOException("Download failed: " + origin + "\n" + string.Join("\n", failures));
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
                    detection.UnsupportedReason
                    ?? "The selected file is not a supported Magisk, KernelSU, APatch or KernelPatch package.");
            }

            if (expected is { } source && !source.Accepts(detection.Kind))
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

    /// <summary>Whether non-official mirrors may deliver payloads and catalog documents.
    /// <para>非官方镜像是否可以交付载荷与清单文档。</para></summary>
    private bool UseMirrorSources() => _useMirrorListOverride ?? RootPatchPackageSettingsStore.UseMirrorList;

    /// <summary>
    /// Removes the ASCII period the transport layer appends after a message that already ends with a CJK
    /// sentence stop, so log lines do not carry a doubled "。." ending.
    /// <para>去除传输层在以中文句末标点结尾的消息后追加的英文句点，避免日志出现 "。." 的重复收尾。</para></summary>
    internal static string DescribeLogDetail(string message)
        => message.Length >= 2 && message[^1] == '.' && message[^2] is '。' or '！' or '？'
            ? message[..^1]
            : message;

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