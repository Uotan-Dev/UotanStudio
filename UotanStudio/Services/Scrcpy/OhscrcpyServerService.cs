using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;
using UotanStudio.Net.Components.Scrcpy;
using UotanStudio.Net.Http;

namespace UotanStudio.Services.Scrcpy;

/// <summary>Where a resolved ohscrcpy device-server package came from.
/// <para>已解析的 ohscrcpy 设备端服务包来源。</para></summary>
public enum OhscrcpyServerSource
{
    /// <summary>Fetched from a GitHub release and digest-verified.
    /// <para>从 GitHub Release 获取并通过摘要校验。</para></summary>
    Provisioned,
    /// <summary>A HAP placed next to the executable by hand, used without a release manifest.
    /// <para>手工放置在可执行文件旁的 HAP，无 Release 清单即使用。</para></summary>
    SideLoaded,
}

/// <summary>Stage of the ohscrcpy update flow. <para>ohscrcpy 更新流程的阶段。</para></summary>
public enum OhscrcpyServerStage
{
    /// <summary>Reading the release metadata from the origin. <para>从源站读取版本元数据。</para></summary>
    Consulting,
    /// <summary>Streaming the HAP. <para>正在拉取 HAP。</para></summary>
    Downloading,
    /// <summary>Hashing the staged file. <para>正在计算暂存文件的哈希。</para></summary>
    Verifying,
    /// <summary>Committing the verified file and its manifest. <para>正在提交已校验的文件与其清单。</para></summary>
    Installing,
    /// <summary>Nothing to do; the wanted version is already cached. <para>无需动作；目标版本已在缓存中。</para></summary>
    UpToDate,
    /// <summary>A usable HAP is resolved. <para>已解析到可用的 HAP。</para></summary>
    Ready,
}

/// <summary>One progress sample from the ohscrcpy update flow. <para>ohscrcpy 更新流程发出的一次进度样本。</para></summary>
/// <param name="Stage">Current stage. <para>当前阶段。</para></param>
/// <param name="Version">Version being consulted or installed, when known. <para>正在查询或安装的版本，未知时为 <see langword="null"/>。</para></param>
/// <param name="Url">Currently attempted URL. <para>当前尝试的地址。</para></param>
/// <param name="BytesDownloaded">Bytes streamed so far. <para>已流入字节数。</para></param>
/// <param name="TotalBytes">Expected byte length. <para>预期字节长度。</para></param>
/// <param name="Message">Human-readable status line. <para>面向用户的状态描述。</para></param>
public sealed record OhscrcpyServerProgress(
    OhscrcpyServerStage Stage,
    string? Version,
    string? Url,
    long? BytesDownloaded,
    long? TotalBytes,
    string Message);

/// <summary>A resolved ohscrcpy device-server package ready to be installed on an HDC target.
/// <para>已解析、可安装到 HDC 目标上的 ohscrcpy 设备端服务包。</para></summary>
/// <param name="Path">Absolute HAP path. <para>HAP 绝对路径。</para></param>
/// <param name="Version">Published version, or <c>side-loaded</c> for a hand-placed package.
/// <para>已发布版本；手工放置的包为 <c>side-loaded</c>。</para></param>
/// <param name="Sha256">Lowercase SHA-256 of the file at <paramref name="Path"/>.
/// <para><paramref name="Path"/> 处文件的小写 SHA-256。</para></param>
/// <param name="Source">Which store supplied the package. <para>由哪个存储提供该包。</para></param>
public sealed record OhscrcpyServerArtifact(string Path, string Version, string Sha256, OhscrcpyServerSource Source);

/// <summary>Installation record of one provisioned ohscrcpy version.
/// <para>某个已供给 ohscrcpy 版本的安装记录。</para></summary>
/// <param name="Version">Installed version. <para>已安装版本。</para></param>
/// <param name="Sha256">Verified digest. <para>已校验的摘要。</para></param>
/// <param name="AssetName">Release asset name. <para>Release 资产名。</para></param>
/// <param name="OriginUrl">Logical release URL. <para>逻辑 Release 地址。</para></param>
/// <param name="UsedSource">Concrete source (origin or mirror) that delivered the file.
/// <para>实际交付该文件的源（源站或镜像）。</para></param>
/// <param name="InstalledAtUnixSeconds">Commit time. <para>提交时间。</para></param>
public sealed record OhscrcpyServerInstall(
    string Version,
    string Sha256,
    string AssetName,
    string OriginUrl,
    string UsedSource,
    long InstalledAtUnixSeconds);

/// <summary>
/// Resolves and refreshes the ohscrcpy device-server HAP that the HDC mirror session installs on the device.
/// <para>解析并更新 HDC 投屏会话安装到设备上的 ohscrcpy 设备端服务 HAP。</para>
/// </summary>
/// <remarks>
/// <para>
/// The upstream artifact is a HarmonyOS application package, so this store keeps the <c>.hap</c> itself
/// rather than a pushed binary. Release metadata (version, byte length, SHA-256) is read from the origin
/// only; the payload may come from a mirror and is accepted only when it matches both the published length
/// and digest.
/// </para>
/// <para>
/// 上游产物是 HarmonyOS 应用包，因此本存储保存 <c>.hap</c> 本身，而不是被推送的二进制。版本元数据（版本、
/// 字节长度、SHA-256）只从源站读取；载荷可以来自镜像，只有长度与摘要都与公布值一致时才接受。
/// </para>
/// <para>
/// Unlike the scrcpy store there is no package shipped inside the application payload. When neither the
/// cache nor the release feed yields a HAP, <see cref="Resolve"/> and <see cref="EnsureLatestAsync"/> fail
/// with a message naming the store root and the missing asset instead of silently starting a broken session.
/// </para>
/// <para>
/// 与 scrcpy 存储不同，应用载荷中不随包附带该 HAP。缓存与 Release 源都无法给出 HAP 时，
/// <see cref="Resolve"/> 与 <see cref="EnsureLatestAsync"/> 会以指明存储根与缺失资产的消息失败，而不是静默
/// 启动一个坏掉的会话。
/// </para>
/// </remarks>
public sealed class OhscrcpyServerService
{
    /// <summary>File name the provisioned HAP is stored under, inside a per-version directory.
    /// <para>已供给 HAP 的存储文件名，位于按版本分的目录内。</para></summary>
    public const string HapFileName = OhscrcpyServerReleaseClient.ServerAssetName;

    /// <summary>File name of the installation manifest inside the cache root.
    /// <para>缓存根目录中安装清单的文件名。</para></summary>
    public const string ManifestFileName = "install.json";

    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    /// <summary>Process-wide instance used by the mirror editor and the startup check.
    /// <para>投屏编辑器与启动检查共用的进程级实例。</para></summary>
    /// <value>A service over a shared HTTP client. <para>基于共享 HTTP 客户端的服务实例。</para></value>
    public static OhscrcpyServerService Shared { get; } = new(new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", HttpDownload.DefaultBrowserUserAgent } },
    });

    private readonly HttpClient _client;
    private readonly IProgress<OhscrcpyServerProgress>? _progress;
    private readonly bool? _useMirrorListOverride;

    /// <summary>Creates the service. <para>创建服务。</para></summary>
    /// <param name="client">HTTP client shared by the metadata read and the download.
    /// <para>版本读取与下载共用的 HTTP 客户端。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度上报目标。</para></param>
    /// <param name="cacheRoot">Override for the store root, used by tests and portable installs.
    /// <para>存储根目录的覆盖值，供测试与便携安装使用。</para></param>
    public OhscrcpyServerService(HttpClient client, IProgress<OhscrcpyServerProgress>? progress = null,
        string? cacheRoot = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _progress = progress;
        CacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? DefaultCacheRoot() : Path.GetFullPath(cacheRoot);
    }

    /// <summary>Creates a service with an explicit mirror-list decision, so a test can pin the payload
    /// to the origin without reading the persisted setting or probing mirrors.
    /// <para>创建带显式镜像列表决策的服务，使测试无需读取持久化设置或探测镜像即可把载荷固定到源站。</para>
    /// </summary>
    /// <param name="client">HTTP client shared by the metadata read and the download.
    /// <para>版本读取与下载共用的 HTTP 客户端。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度上报目标。</para></param>
    /// <param name="cacheRoot">Override for the store root. <para>存储根目录的覆盖值。</para></param>
    /// <param name="useMirrorList">Whether mirror URLs may deliver the payload.
    /// <para>是否允许镜像地址交付载荷。</para></param>
    internal OhscrcpyServerService(HttpClient client, IProgress<OhscrcpyServerProgress>? progress, string? cacheRoot,
        bool useMirrorList)
        : this(client, progress, cacheRoot)
    {
        _useMirrorListOverride = useMirrorList;
    }

    /// <summary>Store root holding the manifest and the per-version HAPs.
    /// <para>保存清单与按版本存放的 HAP 的存储根目录。</para></summary>
    /// <value>Absolute directory path: the constructor override, then the persisted setting, then
    /// <c>%APPDATA%/UotanStudio/OhscrcpyServer</c>. <para>绝对目录路径：优先构造函数覆盖值，其次是持久化设置，
    /// 最后是 <c>%APPDATA%/UotanStudio/OhscrcpyServer</c>。</para></value>
    public string CacheRoot { get; }

    private static string DefaultCacheRoot()
    {
        var configured = OhscrcpyServerSettingsStore.CacheDir;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio", "OhscrcpyServer");
    }

    /// <summary>Candidate paths of a HAP placed next to the application by hand, in lookup order.
    /// <para>手工放置于应用旁的 HAP 候选路径，按查找顺序。</para></summary>
    /// <value>Two absolute paths; the first existing one is used. <para>两个绝对路径，使用首个存在的那个。</para></value>
    public static IReadOnlyList<string> SideLoadedCandidates { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "Assets", HapFileName),
        Path.Combine(AppContext.BaseDirectory, HapFileName),
    ];

    /// <summary>Returns the provisioned artifact, or a hand-placed HAP when nothing valid is cached.
    /// <para>返回已供给的产物；缓存中没有有效文件时返回手工放置的 HAP。</para></summary>
    /// <remarks>
    /// <para> A provisioned package is accepted only when its bytes still hash to the digest recorded at install time, so a half-written or hand-edited cache file falls back to the side-loaded package instead of breaking every launch. </para>
    /// <para> 已供给的包只有在其字节仍能哈希到安装时记录的摘要时才被接受，因此写坏一半或被手工改过的缓存文件 会回落到手工放置的包，而不是让每次启动都失败。 </para></remarks>
    /// <returns>The HAP to install and its version.<para>要安装的 HAP 及其版本。</para></returns>
    /// <exception cref="FileNotFoundException">Neither a valid provisioned HAP nor a side-loaded HAP exists.
    /// <para>既没有有效的已供给 HAP，也没有手工放置的 HAP。</para></exception>
    public OhscrcpyServerArtifact Resolve()
    {
        var installed = ReadInstall();
        if (installed is not null)
        {
            var path = HapPathFor(installed.Version);
            if (File.Exists(path) && HashMatches(path, installed.Sha256))
            {
                return new OhscrcpyServerArtifact(path, installed.Version, installed.Sha256,
                    OhscrcpyServerSource.Provisioned);
            }
        }

        foreach (var candidate in SideLoadedCandidates)
        {
            if (File.Exists(candidate))
            {
                return new OhscrcpyServerArtifact(candidate, "side-loaded", Sha256Hex(candidate),
                    OhscrcpyServerSource.SideLoaded);
            }
        }

        throw new FileNotFoundException(
            $"No ohscrcpy server HAP is available: {CacheRoot} holds no verified version and no side-loaded "
            + $"HAP was found at {string.Join(", ", SideLoadedCandidates)}. The GitHub release feed supplies "
            + $"{OhscrcpyServerReleaseClient.ServerAssetName} when the network is reachable.");
    }

    /// <summary>Reads the installation manifest. <para>读取安装清单。</para></summary>
    /// <returns>The recorded install, or <see langword="null"/> when absent or unreadable.
    /// <para>记录的安装信息；不存在或不可读时为 <see langword="null"/>。</para></returns>
    public OhscrcpyServerInstall? ReadInstall()
    {
        var path = Path.Combine(CacheRoot, ManifestFileName);
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, OhscrcpyServerJsonContext.Default.OhscrcpyServerInstall);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Absolute path a provisioned version is stored at. <para>已供给版本的绝对存储路径。</para></summary>
    /// <param name="version">Server version. <para>服务端版本。</para></param>
    /// <returns>The per-version HAP path. <para>按版本存放的 HAP 路径。</para></returns>
    public string HapPathFor(string version) => Path.Combine(CacheRoot, version, HapFileName);

    /// <summary>Fetches the newest release and reports whether it differs from what is cached.
    /// <para>获取最新发布版本，并报告它与已缓存内容是否不同。</para></summary>
    /// <param name="cancellationToken">Cancels the request. <para>取消请求。</para></param>
    /// <returns>The published release, or <see langword="null"/> when the release API is unreachable.
    /// <para>已发布版本；Release API 不可达时返回 <see langword="null"/>。</para></returns>
    public async Task<OhscrcpyServerRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Consulting, null,
            OhscrcpyServerReleaseClient.LatestReleaseUrl, null, null, "Reading the ohscrcpy release feed."));
        try
        {
            return await OhscrcpyServerReleaseClient.GetLatestAsync(_client, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Consulting, null, null, null, null,
                $"Release feed unavailable: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Downloads the newest release unless the cache already holds it.
    /// <para>下载最新发布版本，除非缓存中已经有它。</para></summary>
    /// <param name="cancellationToken">Cancels the metadata read and the transfer. <para>取消元数据读取与传输。</para></param>
    /// <returns>The artifact to use: the newly provisioned release or an unchanged cache hit.
    /// <para>应使用的产物：新供给的版本或未变化的缓存命中。</para></returns>
    /// <exception cref="FileNotFoundException">No local HAP exists and the release feed could not be read.
    /// <para>本地没有 HAP 且无法读取 Release 源。</para></exception>
    public async Task<OhscrcpyServerArtifact> EnsureLatestAsync(CancellationToken cancellationToken = default)
    {
        OhscrcpyServerArtifact? current = null;
        try
        {
            current = Resolve();
        }
        catch (FileNotFoundException)
        {
        }

        var release = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            if (current is not null) return current;
            throw new FileNotFoundException(
                $"No ohscrcpy server HAP is available under {CacheRoot} and the GitHub release feed could not "
                + "be reached. Fetch " + OhscrcpyServerReleaseClient.ServerAssetName + " once while online.");
        }

        if (current is not null
            && current.Source == OhscrcpyServerSource.Provisioned
            && string.Equals(current.Version, release.Version, StringComparison.Ordinal)
            && string.Equals(current.Sha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Report(new OhscrcpyServerProgress(OhscrcpyServerStage.UpToDate, release.Version, null, null, null,
                $"ohscrcpy server {release.Version} is already installed."));
            return current;
        }

        try
        {
            return await InstallAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (current is not null &&
            ex is IOException or InvalidDataException or HttpRequestException)
        {
            Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Installing, release.Version, null, null, null,
                $"Update to {release.Version} failed ({ex.Message}); continuing with {current.Version}."));
            return current;
        }
    }

    /// <summary>Resolves a usable HAP, fetching and installing the latest release when the local cache and
    /// the side-loaded copy are both absent. Intended for synchronous callers already on a background thread.
    /// <para>解析可用的 HAP；本地缓存与手工放置副本都不存在时拉取并安装最新发布。供已在后台线程上的同步调用方使用。</para>
    /// </summary>
    /// <returns>The resolved or freshly installed artifact. <para>已解析或刚安装的产物。</para></returns>
    /// <exception cref="FileNotFoundException">No local HAP exists and the release feed is unreachable.
    /// <para>本地没有 HAP 且 Release 源不可达。</para></exception>
    public OhscrcpyServerArtifact ResolveOrProvision()
    {
        try
        {
            return Resolve();
        }
        catch (FileNotFoundException)
        {
            return EnsureLatestAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>Resolves a usable HAP, fetching and installing the latest release when the local cache and
    /// the side-loaded copy are both absent.
    /// <para>解析可用的 HAP；本地缓存与手工放置副本都不存在时拉取并安装最新发布。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the metadata read and the download.
    /// <para>取消元数据读取与下载。</para></param>
    /// <returns>The resolved or freshly installed artifact. <para>已解析或刚安装的产物。</para></returns>
    /// <exception cref="FileNotFoundException">No local HAP exists and the release feed is unreachable.
    /// <para>本地没有 HAP 且 Release 源不可达。</para></exception>
    public async Task<OhscrcpyServerArtifact> ResolveOrProvisionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Resolve();
        }
        catch (FileNotFoundException)
        {
            return await EnsureLatestAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Downloads one released HAP and commits it after length and digest verification.
    /// <para>下载一个已发布的 HAP，并在长度与摘要校验后提交。</para></summary>
    /// <param name="release">Release description to fetch.<para>要获取的版本描述。</para></param>
    /// <param name="cancellationToken">Cancels the transfer; the staged file is removed before the exception escapes.
    /// <para>取消传输；异常抛出前会先删除暂存文件。</para></param>
    /// <returns>The provisioned artifact.<para>已供给的产物。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="release"/> is <see langword="null"/>.
    /// <para><paramref name="release"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The delivered file does not have the published length or SHA-256.
    /// <para>交付的文件与公布的长度或 SHA-256 不符。</para></exception>
    /// <exception cref="IOException">The file could not be staged or committed.
    /// <para>无法暂存或提交该文件。</para></exception>
    public async Task<OhscrcpyServerArtifact> InstallAsync(OhscrcpyServerRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        await UpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staged = Path.Combine(CacheRoot, $"{release.Version}.part-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HapPathFor(release.Version))!);
            var target = HapPathFor(release.Version);
            var origin = new Uri(release.DownloadUrl);
            var downloadProgress = new Progress<(long Processed, long Total)>(sample =>
                Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Downloading, release.Version, null,
                    sample.Processed, sample.Total, $"Downloading ohscrcpy server {release.Version}.")));
            Uri used;
            if (_useMirrorListOverride ?? OhscrcpyServerSettingsStore.UseMirrorList)
            {
                used = await HttpDownload.DownloadAsync(_client, origin, staged, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                used = origin;
                await HttpDownload.DownloadSingleAsync(_client, origin, staged, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var info = new FileInfo(staged);
            if (info.Length != release.Length)
            {
                throw new InvalidDataException(
                    $"ohscrcpy server {release.Version} is {release.Length} bytes but {used.Host} delivered {info.Length}.");
            }

            Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Verifying, release.Version, used.ToString(),
                info.Length, release.Length, $"Verifying SHA-256 of ohscrcpy server {release.Version}."));
            var digest = Sha256Hex(staged);
            if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"ohscrcpy server {release.Version} digest mismatch from {used.Host}: "
                    + $"expected {release.Sha256}, got {digest}.");
            }

            AtomicPublish.Publish(staged, target);
            var install = new OhscrcpyServerInstall(release.Version, digest, release.AssetName,
                release.DownloadUrl, used.ToString(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            WriteInstall(install);
            Report(new OhscrcpyServerProgress(OhscrcpyServerStage.Ready, release.Version, used.ToString(),
                release.Length, release.Length, $"ohscrcpy server {release.Version} installed from {used.Host}."));
            return new OhscrcpyServerArtifact(target, release.Version, digest, OhscrcpyServerSource.Provisioned);
        }
        finally
        {
            TryDelete(staged);
            UpdateGate.Release();
        }
    }

    /// <summary>Refreshes the cache in the background when the setting allows it.
    /// <para>在设置允许时于后台更新缓存。</para></summary>
    /// <remarks> <para> Failures are reported through <see cref="LogService"/> only: a launch must never
    /// depend on a reachable release feed, and a previously cached HAP keeps mirroring working meanwhile.
    /// </para><para> 失败只经由 <see cref="LogService"/> 上报：启动绝不能依赖可达的 Release 源，期间已缓存的 HAP
    /// 仍可投屏。 </para> </remarks>
    /// <param name="startupDelay">Delay before the first request.<para>首次请求前的延迟。</para></param>
    /// <param name="cancellationToken">Cancels the wait and the update.<para>取消等待与更新。</para></param>
    /// <returns>A task that completes once the check round has finished or been skipped.
    /// <para>检查轮次完成或被跳过后完成的任务。</para></returns>
    public async Task RunBackgroundCheckAsync(TimeSpan startupDelay, CancellationToken cancellationToken = default)
    {
        if (!OhscrcpyServerSettingsStore.AutoUpdate) return;
        try
        {
            await Task.Delay(startupDelay, cancellationToken).ConfigureAwait(false);
            var artifact = await EnsureLatestAsync(cancellationToken).ConfigureAwait(false);
            LogService.Instance.Write(LogLevel.Info, "ScreenMirror",
                $"ohscrcpy server ready: {artifact.Version} ({artifact.Source}).");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ScreenMirror",
                $"ohscrcpy server update skipped: {ex.Message}");
        }
    }

    private void WriteInstall(OhscrcpyServerInstall install)
    {
        var path = Path.Combine(CacheRoot, ManifestFileName);
        var staged = path + $".part-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(CacheRoot);
            using (var stream = File.Open(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, install, OhscrcpyServerJsonContext.Default.OhscrcpyServerInstall);
            }

            AtomicPublish.Publish(staged, path);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            return string.Equals(Sha256Hex(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(Sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private void Report(OhscrcpyServerProgress progress)
    {
        _progress?.Report(progress);
        LogService.Instance.Write(LogLevel.Info, "ScreenMirror", $"ohscrcpy server: {progress.Message}");
    }
}

/// <summary>
/// Settings adapter for the ohscrcpy store, read on demand so a change applies to the next attempt.
/// <para>ohscrcpy 存储的设置适配器，按需读取以便变更在下一次尝试时生效。</para>
/// </summary>
public static class OhscrcpyServerSettingsStore
{
    /// <summary>Whether a background refresh of the cached HAP is allowed.
    /// <para>是否允许后台刷新缓存 HAP。</para></summary>
    /// <value><see langword="true"/> unless persisted otherwise. <para>除非持久化设置了其他值，否则为 <see langword="true"/>。</para></value>
    public static bool AutoUpdate => ReadFlag("ohscrcpyServer.autoUpdate", true);

    /// <summary>Whether non-official GitHub mirrors may deliver the payload.
    /// <para>是否允许非官方 GitHub 镜像交付载荷。</para></summary>
    /// <value><see langword="true"/> unless persisted otherwise. <para>除非持久化设置了其他值，否则为 <see langword="true"/>。</para></value>
    public static bool UseMirrorList => ReadFlag("ohscrcpyServer.useMirrorList", true);

    /// <summary>Configured cache root, or empty for the default location.
    /// <para>已配置的缓存根目录；为空表示使用默认位置。</para></summary>
    /// <value>An absolute or empty directory path. <para>绝对或为空的目录路径。</para></value>
    public static string CacheDir
    {
        get
        {
            if (SettingsPersistenceService.Instance.LoadSettings().AdditionalSettings is not { } extra) return string.Empty;
            if (!extra.TryGetValue("ohscrcpyServer.cacheDir", out var element)) return string.Empty;
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        }
    }

    private static bool ReadFlag(string key, bool fallback)
    {
        if (SettingsPersistenceService.Instance.LoadSettings().AdditionalSettings is not { } extra) return fallback;
        if (!extra.TryGetValue(key, out var element)) return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }
}

/// <summary>
/// Source-generated JSON metadata for the installation manifest, so the cache read keeps working under NativeAOT.
/// <para>安装清单的源生成 JSON 元数据，使缓存读取在 NativeAOT 下仍然可用。</para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OhscrcpyServerInstall))]
internal sealed partial class OhscrcpyServerJsonContext : JsonSerializerContext;