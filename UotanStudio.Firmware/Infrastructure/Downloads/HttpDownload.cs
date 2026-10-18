using System.Net;
using System.Net.Http.Headers;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.Infrastructure.Downloads;

/// <summary>A GitHub accelerator mirror; an empty BaseUrl means the origin.
/// <para>GitHub 加速镜像站点描述；BaseUrl 为空表示源站直连。</para></summary>
public sealed record GitHubMirrorSite
{
    /// <summary>Display name of the mirror, used in logs and error text. <para>镜像站显示名称，用于日志与错误文本。</para></summary>
    /// <value>The mandatory site label. <para>必填的站点名称。</para></value>
    public required string Name { get; init; }

    /// <summary>Prefix the GitHub URL is appended to; an empty value marks the direct-origin entry, which is never rewritten. <para>拼接在 GitHub 地址之前的前缀；为空表示直连源站条目，该条目不做地址改写。</para></summary>
    /// <value>The mandatory mirror prefix, which may be empty. <para>必填的镜像前缀，允许为空字符串。</para></value>
    public required string BaseUrl { get; init; }

    /// <summary>Whether the mirror takes part in enumeration and speed probing. <para>该镜像是否参与源枚举与测速。</para></summary>
    /// <value><see langword="true"/> unless the initializer sets otherwise. <para>除非初始化时显式设置，否则为 <see langword="true"/>。</para></value>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// A single HTTP download entry point: GitHub URLs are expanded to mirrors plus the origin,
/// speed-probed, and tried fastest-first; other URLs download directly.
/// <para>统一的 HTTP 下载入口：GitHub 链接自动枚举镜像站并对镜像与源站测速，
/// 按测速结果从快到慢依次尝试下载；非 GitHub 链接直接下载。</para>
/// </summary>
public static class HttpDownload
{
    /// <summary>Browser User-Agent sent when caller headers do not provide one. <para>调用方标头未提供 User-Agent 时发送的浏览器标识。</para></summary>
    public const string DefaultBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    /// <summary>Candidate mirrors for speed probing; the origin always participates.
    /// <para>镜像站测速的候选列表；源站直连始终参与测速。</para></summary>
    private static readonly List<GitHubMirrorSite> MirrorSites =
    [
        new() { Name = "GitHub 直连", BaseUrl = string.Empty, IsEnabled = true },
        new() { Name = "h233", BaseUrl = "https://gh.h233.eu.org/https://github.com", IsEnabled = true },
        new() { Name = "rapidgit", BaseUrl = "https://rapidgit.jjda.de5.net/https://github.com", IsEnabled = true },
        new() { Name = "ddlc", BaseUrl = "https://gh.ddlc.top/https://github.com", IsEnabled = true },
        new() { Name = "gh-proxy.org", BaseUrl = "https://gh-proxy.org/https://github.com", IsEnabled = true },
        new() { Name = "cdn.gh-proxy.org", BaseUrl = "https://cdn.gh-proxy.org/https://github.com", IsEnabled = true },
        new() { Name = "edgeone", BaseUrl = "https://edgeone.gh-proxy.org/https://github.com", IsEnabled = true },
        new() { Name = "isteed", BaseUrl = "https://cors.isteed.cc/github.com", IsEnabled = true },
        new() { Name = "ghproxy.it", BaseUrl = "https://ghproxy.it/https://github.com", IsEnabled = true },
        new() { Name = "boki.moe", BaseUrl = "https://github.boki.moe/https://github.com", IsEnabled = true },
        new() { Name = "jasonzeng", BaseUrl = "https://gh.jasonzeng.dev/https://github.com", IsEnabled = true },
        new() { Name = "monlor", BaseUrl = "https://gh.monlor.com/https://github.com", IsEnabled = true },
        new() { Name = "tbedu", BaseUrl = "https://github.tbedu.top/https://github.com", IsEnabled = true },
        new() { Name = "geekery", BaseUrl = "https://github.geekery.cn/https://github.com", IsEnabled = true },
        new() { Name = "ednovas", BaseUrl = "https://github.ednovas.xyz/https://github.com", IsEnabled = true },
        new() { Name = "geekertao", BaseUrl = "https://ghfile.geekertao.top/https://github.com", IsEnabled = true },
        new() { Name = "keleyaa", BaseUrl = "https://ghp.keleyaa.com/https://github.com", IsEnabled = true },
        new() { Name = "chjina", BaseUrl = "https://gh.chjina.com/https://github.com", IsEnabled = true },
        new() { Name = "hwinzniej", BaseUrl = "https://ghpxy.hwinzniej.top/https://github.com", IsEnabled = true },
        new() { Name = "crashmc", BaseUrl = "https://cdn.crashmc.com/https://github.com", IsEnabled = true },
        new() { Name = "yylx", BaseUrl = "https://git.yylx.win/https://github.com", IsEnabled = true },
        new() { Name = "mrhjx", BaseUrl = "https://gitproxy.mrhjx.cn/https://github.com", IsEnabled = true },
        new() { Name = "cxkpro", BaseUrl = "https://ghproxy.cxkpro.top/https://github.com", IsEnabled = true },
        new() { Name = "xxooo", BaseUrl = "https://gh.xxooo.cf/https://github.com", IsEnabled = true },
        new() { Name = "idayer", BaseUrl = "https://gh.idayer.com/https://github.com", IsEnabled = true },
        new() { Name = "npee", BaseUrl = "https://down.npee.cn/?https://github.com", IsEnabled = true },
        new() { Name = "ihtw", BaseUrl = "https://raw.ihtw.moe/github.com", IsEnabled = true },
        new() { Name = "xixu", BaseUrl = "https://xget.xi-xu.me/gh", IsEnabled = true },
        new() { Name = "zwy", BaseUrl = "https://gh.zwy.one/https://github.com", IsEnabled = true },
        new() { Name = "monkeyray", BaseUrl = "https://ghproxy.monkeyray.net/https://github.com", IsEnabled = true },
        new() { Name = "ghproxy.net", BaseUrl = "https://ghproxy.net/https://github.com", IsEnabled = true },
        new() { Name = "ghfast", BaseUrl = "https://ghfast.top/https://github.com", IsEnabled = true },
        new() { Name = "wget.la", BaseUrl = "https://wget.la/https://github.com", IsEnabled = true },
    ];

    private const int SpeedProbeBytes = 256 * 1024;
    private static readonly TimeSpan SpeedProbeTimeout = TimeSpan.FromSeconds(5);

    private const int TransferBufferBytes = 1024 * 1024;
    private const int ParallelDownloadThreshold = 8 * 1024 * 1024;
    private const int ParallelChunkBytes = 16 * 1024 * 1024;
    private const int ParallelWorkers = 8;

    /// <summary>Whether the URL points at a GitHub file (release, blob or raw path).
    /// <para>判断地址是否为 GitHub 文件（release/blob/raw 下载路径）。</para></summary>
    /// <param name="uri">Absolute URL whose host is compared. <para>用于比对主机名的绝对地址。</para></param>
    /// <returns>
    /// <see langword="true"/> for <c>github.com</c>, <c>raw.githubusercontent.com</c> or <c>objects.githubusercontent.com</c>, compared without case sensitivity.
    /// <para>主机名为 <c>github.com</c>、<c>raw.githubusercontent.com</c> 或 <c>objects.githubusercontent.com</c> 时返回 <see langword="true"/>，比对不区分大小写。</para>
    /// </returns>
    public static bool IsGitHubUrl(Uri uri) => string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enumerates every usable source for a URL: the origin first, then all enabled mirrors for GitHub links.
    /// <para>枚举一个地址的全部可用源：源站优先，GitHub 链接追加所有启用镜像。</para></summary>
    /// <param name="url">The requested URL. <para>请求的目标地址。</para></param>
    /// <returns>
    /// The origin followed by mirror URLs built from enabled sites with a non-empty <see cref="GitHubMirrorSite.BaseUrl"/>;
    /// non-GitHub URLs yield only the origin. Evaluation is lazy, so the sequence reflects the mirror list as it stands when iterated.
    /// <para>先返回源站，再按启用且 <see cref="GitHubMirrorSite.BaseUrl"/> 非空的镜像生成镜像地址；非 GitHub 地址只返回源站。枚举为惰性求值。</para>
    /// </returns>
    public static IEnumerable<Uri> EnumerateSources(Uri url)
    {
        yield return url;
        if (!IsGitHubUrl(url)) yield break;
        foreach (var site in MirrorSites)
        {
            if (!site.IsEnabled || site.BaseUrl.Length == 0) continue;
            yield return MirrorUrl(site, url);
        }
    }

    /// <summary>Rewrites a GitHub URL onto a mirror; non-GitHub URLs are returned unchanged.
    /// <para>把 GitHub 地址改写到某个镜像；非 GitHub 地址原样返回。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <see cref="GitHubMirrorSite.BaseUrl"/> entry in the list already carries the part of the
    /// origin it substitutes for, either <c>…&#47;https://github.com</c> or a short alias such as
    /// <c>…&#47;gh</c> or <c>…&#47;github.com</c>. A <c>github.com</c> URL therefore contributes only its
    /// path and query, and appending the whole URL on top of that prefix would repeat the origin
    /// (<c>…&#47;https://github.com&#47;https://github.com&#47;…</c>), which no such proxy serves.
    /// </para>
    /// <para>
    /// 列表中每个 <see cref="GitHubMirrorSite.BaseUrl"/> 已经自带它要替换的源站片段，形式为
    /// <c>…&#47;https://github.com</c>，或 <c>…&#47;gh</c>、<c>…&#47;github.com</c> 这类短别名。因此
    /// <c>github.com</c> 的地址只贡献路径与查询串；若把完整地址再拼到该前缀之后就会出现两次源站
    /// （<c>…&#47;https://github.com&#47;https://github.com&#47;…</c>），没有这类代理会响应该形式。
    /// </para>
    /// <para>
    /// Other GitHub hosts keep the full URL, because their path is not valid under the rewritten
    /// <c>github.com</c> namespace; a prefix ending in the <c>https://github.com</c> marker has that
    /// marker replaced rather than extended.
    /// </para>
    /// <para>
    /// 其他 GitHub 主机保留完整地址，因为其路径在被改写的 <c>github.com</c> 命名空间下并不成立；
    /// 以 <c>https://github.com</c> 结尾的前缀会把这个标记整段替换，而不是在其后追加。
    /// </para>
    /// </remarks>
    internal static Uri MirrorUrl(GitHubMirrorSite site, Uri url)
    {
        if (!IsGitHubUrl(url)) return url;
        var prefix = site.BaseUrl.TrimEnd('/');
        if (string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(prefix + url.PathAndQuery);
        }

        const string marker = "https://github.com";
        if (prefix.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(prefix[..^marker.Length].TrimEnd('/') + "/" + url);
        }

        return new Uri(prefix + "/" + url);
    }

    /// <summary>Speed-probes mirrors and the origin by fetching 256 KiB each and returns sources fastest-first; failed probes sort last, and a wholly failed probe degrades to origin-first order.
    /// <para>对镜像站与源站测速（按拉取 256 KiB 的耗时排序），返回从快到慢的源列表； 测速失败的源排在最后，测速整体失败时退化为源站优先的固定顺序。</para></summary>
    /// <param name="url">Requested URL whose sources are ranked.<para>需要排序候选源的请求地址。</para></param>
    /// <param name="client">Shared HTTP client used for the probe requests.
    /// <para>用于测速请求的共享 HTTP 客户端。</para></param>
    /// <param name="cancellationToken">Cancels probing; an already-cancelled token aborts before any request is sent.
    /// <para>取消测速；令牌已取消时不会发出任何请求。</para></param>
    /// <param name="requestHeaders">Optional newline-separated <c>Name: Value</c> headers applied to each probe.
    /// <para>可选的按行分隔 <c>Name: Value</c> 标头，应用于每次测速请求。</para></param>
    /// <returns>Every candidate source ordered by probe latency, sources whose probe failed last; ties keep the enumeration order. A single-source URL is returned unprobed, and a probe round where every source failed degrades to origin-first order.
    /// <para>按测速耗时排序的全部候选源，测速失败的源排在最后，耗时相同则保持枚举顺序。只有单一源时不测速直接返回；所有源均测速失败时退化为源站优先的顺序。</para></returns>
    /// <exception cref="OperationCanceledException">The probe round was cancelled through <paramref name="cancellationToken"/>.
    /// <para>测速过程因 <paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task<IReadOnlyList<Uri>> SelectSourcesBySpeedAsync(Uri url, HttpClient client,
        CancellationToken cancellationToken, string? requestHeaders = null)
    {
        var sources = EnumerateSources(url).ToArray();
        if (sources.Length == 1) return sources;
        try
        {
            var probes = sources.Select(async source =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SpeedProbeTimeout);
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    using var request = CreateRequest(source, requestHeaders);
                    request.Headers.Range = new RangeHeaderValue(0, SpeedProbeBytes - 1);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var buffer = new byte[64 * 1024];
                    long readTotal = 0;
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    while (readTotal < SpeedProbeBytes
                        && (readTotal += await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, SpeedProbeBytes - readTotal)), timeout.Token).ConfigureAwait(false)) > 0) { }
                    stopwatch.Stop();
                    return (Source: source, Score: stopwatch.Elapsed.Ticks + (readTotal > 0 ? 0 : TimeSpan.TicksPerDay));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (Source: source, Score: long.MaxValue);
                }
                catch (Exception)
                {
                    return (Source: source, Score: long.MaxValue);
                }
            }).ToArray();
            var results = await Task.WhenAll(probes).ConfigureAwait(false);
            if (results.All(r => r.Score == long.MaxValue)) return sources;
            return [.. results.OrderBy(r => r.Score).ThenBy(r => Array.IndexOf(sources, r.Source)).Select(r => r.Source)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return sources;
        }
    }

    /// <summary>Downloads to a file: picks sources by speed probe, then tries each in order; returns the source used.
    /// <para>下载到目标文件：先按测速选择源，再依次尝试；返回最终使用的源地址。</para></summary>
    /// <example>
    /// <code>
    /// using var client = new HttpClient();
    /// var used = await HttpDownload.DownloadAsync(client,
    /// new Uri("https://github.com/example/project/releases/download/v1/tool.zip"),
    /// @"C:\downloads\tool.zip");
    /// </code>
    /// </example>
    /// <param name="client">HTTP client used for probing and the transfer.
    /// <para>用于测速与传输的 HTTP 客户端。</para></param>
    /// <param name="url">Logical URL to download; the reported result may be a mirror derived from it.
    /// <para>要下载的逻辑地址；返回的实际源可能是由其推导出的镜像地址。</para></param>
    /// <param name="destination">Destination file path, replaced atomically by each attempt.
    /// <para>目标文件路径；每次尝试都以原子替换方式写入。</para></param>
    /// <param name="progress">Receives processed/total byte pairs from the winning attempt; reports from failed attempts may already have been emitted.
    /// <para>接收成功那次的已处理/总字节数；失败尝试也可能已经上报过进度。</para></param>
    /// <param name="cancellationToken">Cancels selection and transfer; cancellation propagates instead of falling through to the next source.
    /// <para>取消选源与传输；取消会直接向上抛出，不再尝试后续源。</para></param>
    /// <param name="requestHeaders">Optional newline-separated headers applied to every request.
    /// <para>可选的按行分隔标头，应用于所有请求。</para></param>
    /// <returns>The source that produced the completed file.<para>最终完成下载所使用的源地址。</para></returns>
    /// <exception cref="IOException">Every candidate source failed; the message lists each source host with its error.
    /// <para>所有候选源均失败；消息中按主机列出各源的错误。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static async Task<Uri> DownloadAsync(HttpClient client, Uri url, string destination,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default,
        string? requestHeaders = null)
    {
        var sources = await SelectSourcesBySpeedAsync(url, client, cancellationToken, requestHeaders).ConfigureAwait(false);
        List<string> errors = [];
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadSingleAsync(client, source, destination, progress, cancellationToken, requestHeaders).ConfigureAwait(false);
                return source;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or EndOfStreamException)
            {
                errors.Add(source.Host + ": " + ex.Message);
            }
        }
        throw new IOException("Download failed: " + url + "\n" + string.Join("\n", errors));
    }

    /// <summary>Downloads from one source (range-parallel where supported), committing the destination atomically.
    /// <para>从单个源下载（支持范围并行的镜像同样走此路径），目标文件以原子替换提交。</para></summary>
    /// <param name="client">HTTP client used for probing and the transfer.
    /// <para>用于探测与传输的 HTTP 客户端。</para></param>
    /// <param name="uri">Concrete source URL; no mirror selection happens here.
    /// <para>具体源地址；此处不再做镜像选源。</para></param>
    /// <param name="destination">Destination file path, replaced only after the whole payload has been verified as complete.
    /// <para>目标文件路径；仅在整份载荷完整写出后才被替换。</para></param>
    /// <param name="progress">Receives processed/total byte pairs, where total is <c>0</c> when the server exposes no length.
    /// <para>接收已处理/总字节数；服务器未给出长度时总量为 <c>0</c>。</para></param>
    /// <param name="cancellationToken">Cancels the request and the file writes; the temporary file is deleted before the exception escapes.
    /// <para>取消请求与文件写入；异常抛出前会先删除临时文件。</para></param>
    /// <param name="requestHeaders">Optional newline-separated headers applied to every request.
    /// <para>可选的按行分隔标头，应用于所有请求。</para></param>
    /// <returns>A task that completes once the destination file has been replaced by the fully written temporary file.
    /// <para>在临时文件完整写出并原子替换目标文件后完成的任务。</para></returns>
    /// <exception cref="HttpRequestException">A response status indicates failure.
    /// <para>响应状态表示失败。</para></exception>
    /// <exception cref="IOException">The destination or temporary file cannot be created or moved.
    /// <para>无法创建或移动目标/临时文件。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static async Task DownloadSingleAsync(HttpClient client, Uri uri, string destination,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default,
        string? requestHeaders = null)
    {
        var temporary = destination + $".part-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            var (length, supportsRanges) = await ProbeAsync(client, uri, cancellationToken, requestHeaders).ConfigureAwait(false);
            if (supportsRanges && length >= ParallelDownloadThreshold)
            {
                try
                {
                    await DownloadParallelAsync(client, uri, temporary, length, progress, cancellationToken, requestHeaders).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    TryDelete(temporary);
                    await DownloadSequentialAsync(client, uri, temporary, length, progress, cancellationToken, requestHeaders).ConfigureAwait(false);
                }
            }
            else
                await DownloadSequentialAsync(client, uri, temporary, length, progress, cancellationToken, requestHeaders).ConfigureAwait(false);

            AtomicPublish.Publish(temporary, destination);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task<(long Length, bool SupportsRanges)> ProbeAsync(HttpClient client, Uri uri,
        CancellationToken cancellationToken, string? requestHeaders)
    {
        using var request = CreateRequest(uri, requestHeaders);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
        return (length, response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is > 0);
    }

    private static async Task DownloadSequentialAsync(HttpClient client, Uri uri, string destination, long total,
        IProgress<(long Processed, long Total)>? progress, CancellationToken cancellationToken, string? requestHeaders)
    {
        using var request = CreateRequest(uri, requestHeaders);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        total = total > 0 ? total : response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferBytes, FileOptions.SequentialScan);
        var buffer = new byte[TransferBufferBytes];
        long processed = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            processed += read;
            progress?.Report((processed, total));
        }
    }

    private static async Task DownloadParallelAsync(HttpClient client, Uri uri, string destination, long total,
        IProgress<(long Processed, long Total)>? progress, CancellationToken cancellationToken, string? requestHeaders)
    {
        await using (var preallocate = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, TransferBufferBytes, FileOptions.SequentialScan))
            preallocate.SetLength(total);

        var ranges = Enumerable.Range(0, checked((int)((total + ParallelChunkBytes - 1) / ParallelChunkBytes)))
            .Select(index =>
            {
                var start = checked((long)index * ParallelChunkBytes);
                return (Start: start, End: Math.Min(total - 1, start + ParallelChunkBytes - 1));
            })
            .ToArray();
        long processed = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = ParallelWorkers, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(ranges, options, async (range, token) =>
        {
            using var request = CreateRequest(uri, requestHeaders);
            request.Headers.Range = new RangeHeaderValue(range.Start, range.End);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent
                || response.Content.Headers.ContentRange?.From != range.Start
                || response.Content.Headers.ContentRange?.To != range.End)
                throw new InvalidOperationException("The remote server returned an invalid HTTP range response.");
            var expectedLength = checked(range.End - range.Start + 1);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedLength)
                throw new InvalidOperationException("The remote server returned an incomplete HTTP range response.");

            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, TransferBufferBytes, FileOptions.RandomAccess);
            output.Position = range.Start;
            var buffer = new byte[TransferBufferBytes];
            long rangeProcessed = 0;
            while (rangeProcessed < range.End - range.Start + 1)
            {
                var requested = (int)Math.Min(buffer.Length, range.End - range.Start + 1 - rangeProcessed);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The remote server closed a range response early.");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                rangeProcessed += read;
                var current = Interlocked.Add(ref processed, read);
                progress?.Report((current, total));
            }
        }).ConfigureAwait(false);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>Builds a GET with browser-style headers to avoid mirror bot filtering.
    /// <para>构造带浏览器式标头的 GET 请求，避免镜像站的机器人拦截。</para></summary>
    /// <param name="uri">Request target; its scheme and authority become the referrer.
    /// <para>请求目标；其协议与主机部分将作为 Referer。</para></param>
    /// <param name="requestHeaders">Optional headers, one <c>Name: Value</c> pair per line, at most 16 KiB and 64 entries. A lone line containing <c>/</c> is treated as a User-Agent. <c>Range</c> is reserved by the downloader.
    /// <para>可选标头，每行一个 <c>Name: Value</c>，总量不超过 16 KiB 且最多 64 条。仅一行且包含 <c>/</c> 时按 User-Agent 处理。<c>Range</c> 由下载器保留。</para></param>
    /// <returns>A GET request carrying the parsed headers, defaulted to <see cref="DefaultBrowserUserAgent"/> when no User-Agent was given.
    /// <para>已带上解析后标头的 GET 请求；未提供 User-Agent 时使用 <see cref="DefaultBrowserUserAgent"/>。</para></returns>
    /// <exception cref="ArgumentException">A header is malformed, spans lines, exceeds the count or size limits, uses <c>Range</c>, or cannot be applied to a GET request.
    /// <para>标头格式错误、跨行、超出条数或大小上限、使用 <c>Range</c>，或无法用于 GET 请求。</para></exception>
    public static HttpRequestMessage CreateRequest(Uri uri, string? requestHeaders = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in ParseRequestHeaders(requestHeaders))
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new ArgumentException($"'{name}' is not valid for a GET request.", nameof(requestHeaders));
            }
        }

        if (!request.Headers.UserAgent.Any()) request.Headers.TryAddWithoutValidation("User-Agent", DefaultBrowserUserAgent);
        if (request.Headers.Referrer is null) request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        if (!request.Headers.Contains("Accept-Encoding")) request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!request.Headers.Contains("Sec-Fetch-Dest")) request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        if (!request.Headers.Contains("Sec-Fetch-Mode")) request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        if (!request.Headers.Contains("Sec-Fetch-Site")) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        return request;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseRequestHeaders(string? requestHeaders)
    {
        if (string.IsNullOrWhiteSpace(requestHeaders)) return [];
        if (requestHeaders.Length > 16 * 1024)
        {
            throw new ArgumentException("Request headers must not exceed 16 KiB.", nameof(requestHeaders));
        }

        List<KeyValuePair<string, string>> headers = [];
        foreach (var line in requestHeaders.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                if (headers.Count == 0 && line.Contains('/', StringComparison.Ordinal)
                    && !line.Contains('\r', StringComparison.Ordinal))
                {
                    headers.Add(new KeyValuePair<string, string>("User-Agent", line.Trim()));
                    continue;
                }
                throw new ArgumentException("Each request header must use the 'Name: Value' format.", nameof(requestHeaders));
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0 || !name.All(IsHeaderNameCharacter)
                || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            {
                throw new ArgumentException("Request headers must use valid single-line names and values.", nameof(requestHeaders));
            }
            if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The Range request header is managed by the downloader.", nameof(requestHeaders));
            }
            headers.Add(new KeyValuePair<string, string>(name, value));
            if (headers.Count > 64) throw new ArgumentException("At most 64 request headers are allowed.", nameof(requestHeaders));
        }
        return headers;
    }

    private static bool IsHeaderNameCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}