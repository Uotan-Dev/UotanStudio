using System.IO.Hashing;
using System.Security.Cryptography;
using UotanStudio.Comm;
using UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

namespace UotanStudio.Services.NativeBin;

/// <summary>
/// Stage of the provisioning flow reported through <see cref="NativeBinProgress"/>.
/// <para>通过 <see cref="NativeBinProgress"/> 汇报的探测阶段。</para>
/// </summary>
public enum NativeBinStage
{
    /// <summary>Inspecting the cache and settings. <para>检查缓存与设置。</para></summary>
    Probing,
    /// <summary>Streaming the download to a temporary file. <para>将下载写入临时文件。</para></summary>
    Downloading,
    /// <summary>Computing and comparing the SHA-256 of the staged file. <para>计算并比对暂存文件的 SHA-256。</para></summary>
    Verifying,
    /// <summary>Moving the verified file to its final cache path. <para>把校验后的文件移到最终缓存路径。</para></summary>
    Installing,
    /// <summary>Cache path has been registered with <c>LibUsbRuntime.SetLibraryPath</c>. <para>缓存路径已通过 <c>LibUsbRuntime.SetLibraryPath</c> 注册。</para></summary>
    Done,
}

/// <summary>
/// One progress sample emitted during provisioning.
/// <para>探测流程中发出的一次进度样本。</para>
/// </summary>
/// <param name="Rid">Target runtime identifier. <para>目标运行时标识。</para></param>
/// <param name="Url">Currently attempted URL. <para>当前尝试的 URL。</para></param>
/// <param name="BytesDownloaded">Bytes streamed so far (may be <see langword="null"/> before download starts). <para>已流入字节数（未开始下载时为 <see langword="null"/>）。</para></param>
/// <param name="TotalBytes">Server-reported content length, when available. <para>服务器上报的内容长度，可能为 <see langword="null"/>。</para></param>
/// <param name="Stage">Current stage. <para>当前阶段。</para></param>
/// <param name="Message">Human-readable status line. <para>面向用户的状态描述。</para></param>
public sealed record NativeBinProgress(
    string Rid,
    string? Url,
    long? BytesDownloaded,
    long? TotalBytes,
    NativeBinStage Stage,
    string Message);

/// <summary>
/// Raised by <see cref="NativeBinProvisioner.EnsureAsync"/> when every configured
/// URL for a RID failed to produce a valid libusb binary.
/// <para><see cref="NativeBinProvisioner.EnsureAsync"/> 在为某个 RID 尝试所有 URL 后仍无法
/// 获得有效的 libusb 二进制时抛出。</para>
/// </summary>
public sealed class NativeRuntimeNotAvailableException : Exception
{
    /// <summary>
    /// Initializes the exception with a human-readable message.
    /// <para>使用面向用户的消息初始化异常。</para>
    /// </summary>
    /// <param name="message">Describes the failure. <para>描述失败原因。</para></param>
    public NativeRuntimeNotAvailableException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with a human-readable message and inner error.
    /// <para>使用面向用户的消息和内部异常初始化异常。</para>
    /// </summary>
    /// <param name="message">Describes the failure. <para>描述失败原因。</para></param>
    /// <param name="innerException">Last underlying exception. <para>最后一次底层异常。</para></param>
    public NativeRuntimeNotAvailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Downloads and caches the native libusb runtime for the active runtime identifier,
/// then registers the verified local file.
/// <para>为当前运行时标识下载并缓存原生 libusb 运行库，随后注册校验后的本地文件。</para>
/// </summary>
public sealed class NativeBinProvisioner
{
    private const int DownloadBufferSize = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly INativeBinSettings _settings;
    private readonly IProgress<NativeBinProgress>? _progress;
    private readonly Action<string>? _libraryPathOverride;

    /// <summary>
    /// Initializes a new provisioner.
    /// <para>创建新的探测器。</para>
    /// </summary>
    /// <param name="httpClient">HTTP client used for downloads.
    /// <para>用于下载的 HTTP 客户端。</para></param>
    /// <param name="settings">Runtime configuration source.
    /// <para>运行时配置来源。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度上报目标。</para></param>
    /// <param name="libraryPathOverride">Optional delegate taking the cached path in place
    /// of the loader registration step.
    /// <para>接收缓存路径以替代加载器注册步骤的可选委托。</para></param>
    public NativeBinProvisioner(
        HttpClient httpClient,
        INativeBinSettings? settings = null,
        IProgress<NativeBinProgress>? progress = null,
        Action<string>? libraryPathOverride = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? new NativeBinSettingsStore();
        _progress = progress;
        _libraryPathOverride = libraryPathOverride;
    }

    /// <summary>
    /// Returns the shared library file name that a RID resolves to.
    /// <para>返回某个 RID 解析到的共享库文件名。</para>
    /// </summary>
    /// <param name="rid">Runtime identifier. <para>运行时标识。</para></param>
    /// <returns>The platform library file name.<para>对应平台的库文件名。</para></returns>
    /// <exception cref="ArgumentException">The RID is not recognised. <para>RID 不受支持。</para></exception>
    public static string LibFileNameFor(string rid)
        => NativeBinCatalog.ByRid(rid)?.FileName
            ?? throw new ArgumentException($"No libusb entry for RID '{rid}'.", nameof(rid));

    /// <summary>
    /// Ensures a verified libusb runtime is cached and registered for the
    /// supplied RID. On the happy path this returns the existing cache path
    /// without touching the network.
    /// <para>确保指定 RID 的 libusb 运行库已缓存并注册。缓存命中时不发起网络请求。</para>
    /// </summary>
    /// <param name="rid">Target runtime identifier. <para>目标运行时标识。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// Absolute path of the cached libusb file ready for loading.
    /// <para>可供加载的缓存 libusb 文件绝对路径。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The RID is not recognised by <see cref="NativeBinCatalog"/>.
    /// <para>RID 不被 <see cref="NativeBinCatalog"/> 识别。</para></exception>
    /// <exception cref="NativeRuntimeNotAvailableException">
    /// Every configured URL failed to produce a valid libusb binary.
    /// <para>所有已配置的 URL 都无法获得有效的 libusb 二进制。</para></exception>
    public async Task<string> EnsureAsync(string rid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            throw new ArgumentOutOfRangeException(nameof(rid));
        }

        var entry = NativeBinCatalog.ByRid(rid)
            ?? throw new ArgumentOutOfRangeException(nameof(rid), $"No libusb entry for RID '{rid}'.");

        var cachePath = Path.Combine(_settings.CacheDir, rid, entry.FileName);
        Report(new NativeBinProgress(rid, null, null, null, NativeBinStage.Probing, $"Probing cache for {rid}"));

        if (File.Exists(cachePath))
        {
            try
            {
                if (await FileHashMatchesAsync(cachePath, entry.ExpectedSha256, ct).ConfigureAwait(false))
                {
                    SetLibraryPath(cachePath);
                    Report(new NativeBinProgress(rid, null, null, null, NativeBinStage.Done,
                        $"Using cached libusb at {cachePath}"));
                    return cachePath;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var candidateUrls = SelectCandidateUrls(entry);
        var attempted = new List<string>(candidateUrls.Count);
        Exception? lastFailure = null;

        foreach (var url in candidateUrls)
        {
            ct.ThrowIfCancellationRequested();
            attempted.Add(url.Url);
            Report(new NativeBinProgress(rid, url.Url, null, null, NativeBinStage.Probing, $"Trying {url.Url}"));

            var tmpPath = cachePath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                var downloadResult = await DownloadAsync(rid, url.Url, tmpPath, ct).ConfigureAwait(false);
                if (downloadResult.Bytes <= 0)
                {
                    throw new InvalidDataException($"Downloaded file is empty: {url.Url}");
                }

                Report(new NativeBinProgress(rid, url.Url, downloadResult.Bytes, downloadResult.TotalBytes,
                    NativeBinStage.Verifying, $"Verifying SHA-256 for {url.Url}"));
                var actual = await HashFileAsync(tmpPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    lastFailure = new InvalidDataException(
                        $"SHA-256 mismatch from {url.Url} (expected {entry.ExpectedSha256}, got {actual}); retrying next URL.");
                    continue;
                }

                Report(new NativeBinProgress(rid, url.Url, downloadResult.Bytes, downloadResult.TotalBytes,
                    NativeBinStage.Installing, $"Installing {rid} libusb"));
                File.Move(tmpPath, cachePath, overwrite: true);
                SetLibraryPath(cachePath);
                Report(new NativeBinProgress(rid, url.Url, downloadResult.Bytes, downloadResult.TotalBytes,
                    NativeBinStage.Done, $"Installed libusb from {url.Url}"));
                return cachePath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        throw new NativeRuntimeNotAvailableException(
            $"Unable to obtain libusb runtime for RID '{rid}'. Attempted URLs: " +
            $"{string.Join(", ", attempted)}. " +
            $"Last error: {lastFailure?.Message ?? "unknown"}. " +
            "You can manually place a compatible libusb into " +
            $"'{Path.Combine(_settings.CacheDir, rid)}' and retry, or call " +
            "LibUsbRuntime.SetLibraryPath with the absolute path to a system-installed libusb.");
    }

    /// <summary>
    /// Returns the local cache path for a RID without downloading, so an absent
    /// file simply yields a path that does not exist yet.
    /// <para>返回某 RID 的本地缓存路径且不触发下载，文件缺失时返回尚未存在的路径。</para>
    /// </summary>
    /// <param name="rid">Runtime identifier. <para>运行时标识。</para></param>
    /// <returns>Absolute path the cached runtime file resolves to.<para>缓存运行库文件对应的绝对路径。</para></returns>
    /// <exception cref="ArgumentException">The RID is not recognised. <para>RID 不受支持。</para></exception>
    public string GetCachePath(string rid)
    {
        var entry = NativeBinCatalog.ByRid(rid)
            ?? throw new ArgumentException($"No libusb entry for RID '{rid}'.", nameof(rid));
        return Path.Combine(_settings.CacheDir, rid, entry.FileName);
    }

    private IReadOnlyList<NativeBinUrl> SelectCandidateUrls(NativeBinEntry entry)
    {
        if (_settings.UseMirrorList)
        {
            return entry.Urls;
        }

        var direct = entry.Urls.Where(u => !u.IsMirror).ToList();
        return direct.Count > 0 ? direct : entry.Urls.Take(1).ToList();
    }

    private async Task<(long Bytes, long? TotalBytes)> DownloadAsync(string rid, string url, string tmpPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UotanStudio/1.0");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GET {url} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var total = response.Content.Headers.ContentLength;
        using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
            DownloadBufferSize, FileOptions.SequentialScan);

        var buffer = new byte[DownloadBufferSize];
        long written = 0;
        var read = await content.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        while (read > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            Report(new NativeBinProgress(rid, url, written, total, NativeBinStage.Downloading,
                $"Downloading {url}"));
            read = await content.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        }
        return (written, total);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> FileHashMatchesAsync(string path, string expectedHex, CancellationToken ct)
    {
        var actual = await HashFileAsync(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private void SetLibraryPath(string path)
    {
        if (_libraryPathOverride is not null)
        {
            _libraryPathOverride(path);
            return;
        }

        LibUsbRuntime.SetLibraryPath(path);
    }

    private void Report(NativeBinProgress progress) => _progress?.Report(progress);
}