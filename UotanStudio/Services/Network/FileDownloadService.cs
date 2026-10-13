using System.Security.Cryptography;
using UotanStudio.Firmware.Infrastructure.Downloads;

namespace UotanStudio.Services.Network;

/// <summary>One validated download request.<para>一条已校验的下载请求。</para></summary>
/// <param name="Uri">Absolute HTTP or HTTPS address to download.<para>要下载的绝对 HTTP 或 HTTPS 地址。</para></param>
/// <param name="DestinationPath">Full path of the published file.<para>发布后文件的完整路径。</para></param>
/// <param name="UseGitHubMirror">Whether the mirrored route is requested.<para>是否请求镜像路线。</para></param>
/// <param name="RequestHeaders">Optional additional request headers.<para>可选的附加请求头。</para></param>
/// <param name="ExpectedSha256">Optional lowercase hexadecimal SHA-256 digest to verify.<para>可选的小写十六进制 SHA-256 摘要。</para></param>
/// <param name="TimeoutSeconds">Optional overall timeout in seconds.<para>可选的总体超时秒数。</para></param>
public sealed record FileDownloadRequest(
    Uri Uri,
    string DestinationPath,
    bool UseGitHubMirror = false,
    string? RequestHeaders = null,
    string? ExpectedSha256 = null,
    double? TimeoutSeconds = null);

/// <summary>Outcome of a completed download: the published path, the winning source and the byte count.<para>完成下载的结果：发布路径、胜出来源与字节数。</para></summary>
/// <param name="Path">Published destination path.<para>已发布的目标路径。</para></param>
/// <param name="Source">Address that produced the file.<para>实际产出文件的地址。</para></param>
/// <param name="Bytes">Length of the published file.<para>发布文件的长度。</para></param>
public sealed record FileDownloadResult(string Path, Uri Source, long Bytes);

/// <summary>
/// Downloads a remote artifact to a path-backed file, verifying an optional SHA-256 and publishing with
/// an atomic replace so a failed or mismatched transfer never overwrites an existing destination.
/// <para>把远程产物下载到路径文件，可选校验 SHA-256，并以原子替换发布，失败或摘要不符时不会覆盖既有目标。</para>
/// </summary>
public static class FileDownloadService
{
    /// <summary>Validates, downloads and atomically publishes one artifact.<para>校验、下载并原子发布单个产物。</para></summary>
    /// <param name="httpClient">Client performing the transfer.<para>执行传输的客户端。</para></param>
    /// <param name="request">Validated download request.<para>已校验的下载请求。</para></param>
    /// <param name="progress">Optional byte progress sink.<para>可选的字节进度上报。</para></param>
    /// <param name="cancellationToken">Token cancelling the transfer.<para>取消传输的令牌。</para></param>
    /// <returns>The published path, winning source and byte count.<para>发布路径、胜出来源与字节数。</para></returns>
    public static async Task<FileDownloadResult> DownloadAsync(
        HttpClient httpClient,
        FileDownloadRequest request,
        IProgress<(long Processed, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("URL must be an absolute HTTP or HTTPS address.");
        }
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new InvalidOperationException("Output Path is required.");
        }
        var expectedHash = request.ExpectedSha256?.Trim();
        if (!string.IsNullOrEmpty(expectedHash)
            && (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("Expected SHA256 must be 64 hexadecimal characters.");
        }
        if (request.TimeoutSeconds is { } timeoutSeconds
            && (!double.IsFinite(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 3600))
        {
            throw new InvalidDataException("Timeout Seconds must be between 1 and 3600.");
        }
        if (request.UseGitHubMirror && !HttpDownload.IsGitHubUrl(request.Uri))
        {
            throw new InvalidOperationException("GitHub Mirror source only accepts GitHub download addresses.");
        }

        var destination = Path.GetFullPath(request.DestinationPath);
        var staging = string.IsNullOrEmpty(expectedHash)
            ? destination : destination + $".verify-{Guid.NewGuid():N}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.TimeoutSeconds is { } seconds)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }
        try
        {
            Uri source;
            if (request.UseGitHubMirror)
            {
                source = await HttpDownload.DownloadAsync(httpClient, request.Uri, staging, progress,
                    timeout.Token, request.RequestHeaders).ConfigureAwait(false);
            }
            else
            {
                await HttpDownload.DownloadSingleAsync(httpClient, request.Uri, staging, progress,
                    timeout.Token, request.RequestHeaders).ConfigureAwait(false);
                source = request.Uri;
            }
            if (!string.IsNullOrEmpty(expectedHash))
            {
                await using (var input = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var actualHash = await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash)))
                    {
                        throw new InvalidDataException("Downloaded SHA256 does not match the expected value.");
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();
                File.Move(staging, destination, overwrite: true);
            }

            return new FileDownloadResult(destination, source, new FileInfo(destination).Length);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The download timed out.", error);
        }
        finally
        {
            if (!string.Equals(staging, destination, StringComparison.Ordinal) && File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}