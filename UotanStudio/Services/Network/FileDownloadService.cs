using System.Security.Cryptography;
using UotanStudio.Firmware.Infrastructure.Downloads;

namespace UotanStudio.Services.Network;

/// <summary>One validated download request shared by the download node and the script tool surface.</summary>
public sealed record FileDownloadRequest(
    Uri Uri,
    string DestinationPath,
    bool UseGitHubMirror = false,
    string? RequestHeaders = null,
    string? ExpectedSha256 = null,
    double? TimeoutSeconds = null);

/// <summary>Outcome of a completed download: the published path, the winning source and the byte count.</summary>
public sealed record FileDownloadResult(string Path, Uri Source, long Bytes);

/// <summary>
/// Downloads a remote artifact to a path-backed file, verifying an optional SHA-256 and publishing with
/// an atomic replace so a failed or mismatched transfer never overwrites an existing destination.
/// </summary>
public static class FileDownloadService
{
    /// <summary>Validates, downloads and atomically publishes one artifact.</summary>
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