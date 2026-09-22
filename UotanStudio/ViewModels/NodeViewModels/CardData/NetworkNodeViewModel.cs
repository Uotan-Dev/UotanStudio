using System.Globalization;
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
        => Create("Network.Download", "Download", httpClient);

    public static NetworkNodeViewModel CreateGitHubDownloadNode(HttpClient? httpClient = null)
        => Create("Network.GitHubDownload", "GitHub Mirror Download", httpClient);

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
            Title = "Output Path",
            PortId = "output-path",
            PortType = NodePortType.Path,
            SuggestedOutputName = "download.bin",
            OutputExtension = "bin"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "User Agent",
            PortId = "user-agent",
            PortType = NodePortType.Text,
            InitialValue = "UotanStudio/1.0"
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
        var userAgent = Convert.ToString(ReadInput("User Agent"), CultureInfo.InvariantCulture)?.Trim();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("URL must be an absolute HTTP or HTTPS address.");
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("Output Path is required.");
        }

        var useMirrors = string.Equals(TypeKey, "Network.GitHubDownload", StringComparison.Ordinal);
        if (useMirrors && !HttpDownload.IsGitHubUrl(uri))
        {
            throw new InvalidOperationException("GitHub Mirror Download only accepts GitHub download addresses.");
        }

        var progress = new DownloadProgress(value =>
        {
            if (value.Total > 0)
            {
                context.ReportProgress((double)value.Processed / value.Total);
            }
        });
        Uri source;
        if (useMirrors)
        {
            source = await HttpDownload.DownloadAsync(_httpClient, uri, outputPath, progress,
                context.CancellationToken, userAgent).ConfigureAwait(false);
        }
        else
        {
            await HttpDownload.DownloadSingleAsync(_httpClient, uri, outputPath, progress,
                context.CancellationToken, userAgent).ConfigureAwait(false);
            source = uri;
        }

        var length = new FileInfo(outputPath).Length;
        WriteOutput("Path", Path.GetFullPath(outputPath));
        WriteOutput("Source URL", source.ToString());
        WriteOutput("Bytes", (double)length);
        context.ReportProgress(1);
        context.Log.Write(LogLevel.Info, Title, $"Downloaded {length} bytes from '{source}'.");
    }

    private sealed class DownloadProgress(Action<(long Processed, long Total)> report)
        : IProgress<(long Processed, long Total)>
    {
        public void Report((long Processed, long Total) value) => report(value);
    }
}