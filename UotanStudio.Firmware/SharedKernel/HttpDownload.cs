using System.Net;
using System.Net.Http.Headers;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary><para>GitHub 加速镜像站点描述；BaseUrl 为空表示源站直连。</para>
/// A GitHub accelerator mirror; an empty BaseUrl means the origin.</summary>
public sealed record GitHubMirrorSite
{
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// <para>统一的 HTTP 下载入口：GitHub 链接自动枚举镜像站并对镜像与源站测速，
/// 按测速结果从快到慢依次尝试下载；非 GitHub 链接直接下载。</para>
/// A single HTTP download entry point: GitHub URLs are expanded to mirrors plus the origin,
/// speed-probed, and tried fastest-first; other URLs download directly.
/// </summary>
public static class HttpDownload
{
    /// <summary><para>镜像站测速的候选列表；源站直连始终参与测速。</para>
    /// Candidate mirrors for speed probing; the origin always participates.</summary>
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

    /// <summary><para>判断地址是否为 GitHub 文件（release/blob/raw 下载路径）。</para>
    /// Whether the URL points at a GitHub file (release, blob or raw path).</summary>
    public static bool IsGitHubUrl(Uri uri) => string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary><para>枚举一个地址的全部可用源：源站优先，GitHub 链接追加所有启用镜像。</para>
    /// Enumerates every usable source for a URL: the origin first, then all enabled mirrors for GitHub links.</summary>
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

    /// <summary><para>把 GitHub 地址改写到某个镜像；非 GitHub 地址原样返回。</para>
    /// Rewrites a GitHub URL onto a mirror; non-GitHub URLs are returned unchanged.</summary>
    internal static Uri MirrorUrl(GitHubMirrorSite site, Uri url)
    {
        var target = string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? url.ToString()
            : "https://github.com" + url.PathAndQuery;
        return new Uri(site.BaseUrl.TrimEnd('/') + "/" + target);
    }

    /// <summary><para>对镜像站与源站测速（按拉取 256 KiB 的耗时排序），返回从快到慢的源列表；
    /// 测速失败的源排在最后，测速整体失败时退化为源站优先的固定顺序。</para>
    /// Speed-probes mirrors and the origin by fetching 256 KiB each and returns sources fastest-first;
    /// failed probes sort last, and a wholly failed probe degrades to origin-first order.</summary>
    public static async Task<IReadOnlyList<Uri>> SelectSourcesBySpeedAsync(Uri url, HttpClient client, CancellationToken cancellationToken)
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
                    using var request = CreateRequest(source);
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

    /// <summary><para>下载到目标文件：先按测速选择源，再依次尝试；返回最终使用的源地址。</para>
    /// Downloads to a file: picks sources by speed probe, then tries each in order; returns the source used.</summary>
    public static async Task<Uri> DownloadAsync(HttpClient client, Uri url, string destination,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var sources = await SelectSourcesBySpeedAsync(url, client, cancellationToken).ConfigureAwait(false);
        List<string> errors = [];
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadSingleAsync(client, source, destination, progress, cancellationToken).ConfigureAwait(false);
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
        throw new IOException("无法下载 / Download failed: " + url + "\n" + string.Join("\n", errors));
    }

    /// <summary><para>从单个源下载（支持范围并行的镜像同样走此路径），目标文件以原子替换提交。</para>
    /// Downloads from one source (range-parallel where supported), committing the destination atomically.</summary>
    public static async Task DownloadSingleAsync(HttpClient client, Uri uri, string destination,
        IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var temporary = destination + $".part-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            var (length, supportsRanges) = await ProbeAsync(client, uri, cancellationToken).ConfigureAwait(false);
            if (supportsRanges && length >= ParallelDownloadThreshold)
            {
                try
                {
                    await DownloadParallelAsync(client, uri, temporary, length, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Some CDNs advertise ranges but reject concurrent requests; retain a reliable sequential fallback.
                    TryDelete(temporary);
                    await DownloadSequentialAsync(client, uri, temporary, length, progress, cancellationToken).ConfigureAwait(false);
                }
            }
            else
                await DownloadSequentialAsync(client, uri, temporary, length, progress, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task<(long Length, bool SupportsRanges)> ProbeAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
        return (length, response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is > 0);
    }

    private static async Task DownloadSequentialAsync(HttpClient client, Uri uri, string destination, long total,
        IProgress<(long Processed, long Total)>? progress, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
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
        IProgress<(long Processed, long Total)>? progress, CancellationToken cancellationToken)
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
            using var request = CreateRequest(uri);
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

    /// <summary><para>构造带浏览器式标头的 GET 请求，避免镜像站的机器人拦截。</para>
    /// Builds a GET with browser-style headers to avoid mirror bot filtering.</summary>
    public static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        return request;
    }
}
