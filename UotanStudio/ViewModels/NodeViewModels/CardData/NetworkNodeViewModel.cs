using System.Globalization;
using Avalonia;
using UotanStudio.Firmware.Infrastructure.Downloads;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Network;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Downloads a remote artifact to a path-backed, atomically published file.<para>下载远程构件到由路径支撑、原子发布的文件。</para></summary>
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
            Choices = [.. new[] { "Direct", "GitHub Mirror" }.Select(item => new NodePortChoice(item))]
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
        var expectedHash = Convert.ToString(ReadInput("Expected SHA256"), CultureInfo.InvariantCulture)?.Trim();
        var timeoutText = Convert.ToString(ReadInput("Timeout Seconds"), CultureInfo.InvariantCulture);
        double? timeoutSeconds = null;
        if (!string.IsNullOrWhiteSpace(timeoutText))
        {
            if (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds < 1 || seconds > 3600)
            {
                throw new InvalidDataException("Timeout Seconds must be between 1 and 3600.");
            }
            timeoutSeconds = seconds;
        }

        var useMirrors = string.Equals(Convert.ToString(ReadInput("Source"), CultureInfo.InvariantCulture),
            "GitHub Mirror", StringComparison.Ordinal);
        var progress = new DownloadProgress(value =>
        {
            if (value.Total > 0)
            {
                context.ReportProgress((double)value.Processed / value.Total);
            }
        });
        var request = new FileDownloadRequest(uri, outputPath ?? string.Empty, useMirrors, requestHeaders,
            expectedHash, timeoutSeconds);
        try
        {
            var result = await FileDownloadService.DownloadAsync(_httpClient, request, progress,
                context.CancellationToken).ConfigureAwait(false);
            WriteOutput("Path", result.Path);
            WriteOutput("Source URL", result.Source.ToString());
            WriteOutput("Bytes", (double)result.Bytes);
            context.ReportProgress(1);
            context.Log.Write(LogLevel.Info, Title, $"Downloaded {result.Bytes} bytes from '{result.Source}'.");
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(LanguageService.Instance.GetString("Node_Network_Timeout"), error);
        }
    }

    private sealed class DownloadProgress(Action<(long Processed, long Total)> report)
        : IProgress<(long Processed, long Total)>
    {
        public void Report((long Processed, long Total) value) => report(value);
    }
}