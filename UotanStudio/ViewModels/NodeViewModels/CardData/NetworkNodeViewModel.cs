using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using UotanStudio.Firmware.Infrastructure.Downloads;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Downloads a remote artifact to a path-backed, atomically published file.</summary>
public sealed class NetworkNodeViewModel : FlowNodeViewModel
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;

    public NetworkNodeViewModel(string title, HttpClient? httpClient = null) : base(title, NodeCategory.Data)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public static NetworkNodeViewModel CreateDownloadNode(HttpClient? httpClient = null)
        => Create("Network.File.Download", "Download", httpClient);

    private static NetworkNodeViewModel Create(string typeKey, string title, HttpClient? httpClient)
    {
        var node = new NetworkNodeViewModel(title, httpClient)
        {
            Location = new Point(100, 100),
            TypeKey = typeKey
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "URL", PortId = "url", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Source",
            PortId = "source",
            PortType = NodePortType.Text,
            InitialValue = "Direct",
            Choices = new[] { "Direct", "GitHub Mirror" }.Select(item => new NodePortChoice(item)).ToArray()
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Output Path",
            PortId = "output-path",
            PortType = NodePortType.Path,
            SuggestedOutputName = "download.bin",
            OutputExtension = "bin"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Request Headers",
            PortId = "user-agent",
            PortType = NodePortType.Text,
            InitialValue = "User-Agent: " + HttpDownload.DefaultBrowserUserAgent
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Expected SHA256",
            PortId = "expected-sha256",
            PortType = NodePortType.Text
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Timeout Seconds",
            PortId = "timeout-seconds",
            PortType = NodePortType.Number
        });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortId = "path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Source URL", PortId = "source-url", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortId = "bytes", PortType = NodePortType.Number });
        return node;
    }

    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        var urlText = Convert.ToString(ReadInput("URL"), CultureInfo.InvariantCulture)?.Trim();
        var outputPath = Convert.ToString(ReadInput("Output Path"), CultureInfo.InvariantCulture)?.Trim();
        var requestHeaders = Convert.ToString(ReadInput("Request Headers"), CultureInfo.InvariantCulture)?.Trim();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("URL must be an absolute HTTP or HTTPS address.");
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("Output Path is required.");
        }
        var expectedHash = Convert.ToString(ReadInput("Expected SHA256"), CultureInfo.InvariantCulture)?.Trim();
        if (!string.IsNullOrEmpty(expectedHash)
            && (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("Expected SHA256 must be 64 hexadecimal characters.");
        }
        var timeoutText = Convert.ToString(ReadInput("Timeout Seconds"), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(timeoutText)
            && (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds < 1 || seconds > 3600))
        {
            throw new InvalidDataException("Timeout Seconds must be between 1 and 3600.");
        }

        var useMirrors = string.Equals(Convert.ToString(ReadInput("Source"), CultureInfo.InvariantCulture), "GitHub Mirror", StringComparison.Ordinal);
        if (useMirrors && !HttpDownload.IsGitHubUrl(uri))
        {
            throw new InvalidOperationException("GitHub Mirror source only accepts GitHub download addresses.");
        }

        var progress = new DownloadProgress(value =>
        {
            if (value.Total > 0)
            {
                context.ReportProgress((double)value.Processed / value.Total);
            }
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(timeoutText))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(double.Parse(timeoutText, CultureInfo.InvariantCulture)));
        }
        var destination = Path.GetFullPath(outputPath);
        var staging = string.IsNullOrEmpty(expectedHash)
            ? destination : destination + $".verify-{Guid.NewGuid():N}";
        try
        {
            Uri source;
            if (useMirrors)
            {
                source = await HttpDownload.DownloadAsync(_httpClient, uri, staging, progress,
                    timeout.Token, requestHeaders).ConfigureAwait(false);
            }
            else
            {
                await HttpDownload.DownloadSingleAsync(_httpClient, uri, staging, progress,
                    timeout.Token, requestHeaders).ConfigureAwait(false);
                source = uri;
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

            var length = new FileInfo(destination).Length;
            WriteOutput("Path", destination);
            WriteOutput("Source URL", source.ToString());
            WriteOutput("Bytes", (double)length);
            context.ReportProgress(1);
            context.Log.Write(LogLevel.Info, Title, $"Downloaded {length} bytes from '{source}'.");
        }
        catch (OperationCanceledException error) when (!context.CancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(LanguageService.Instance.GetString("Node_Network_Timeout"), error);
        }
        finally
        {
            if (!string.Equals(staging, destination, StringComparison.Ordinal) && File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private sealed class DownloadProgress(Action<(long Processed, long Total)> report)
        : IProgress<(long Processed, long Total)>
    {
        public void Report((long Processed, long Total) value) => report(value);
    }
}