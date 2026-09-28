using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class RepackMetadataProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-repack-provider-" + Guid.NewGuid().ToString("N"));

    public RepackMetadataProviderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WriteAsync_CustomProvider_PublishesConfigurationAfterCapture()
    {
        var source = CreateSource();
        var extraction = Path.Combine(_root, "extracted");
        var metadataRoot = Path.Combine(_root, "metadata");
        var destination = Path.Combine(metadataRoot, "system");
        var token = TestContext.Current.CancellationToken;
        var handler = new Provider
        {
            Capture = (metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken) =>
            {
                Assert.Equal(source, sourcePath);
                Assert.Equal(extraction, outputDirectory);
                Assert.Equal(destination, metadataDirectory);
                Assert.Equal(token, cancellationToken);
                Assert.False(File.Exists(Path.Combine(metadataDirectory, RepackMetadata.FileName)));
                Assert.Equal("4096", metadata.SourceProperties["BlockSize"]);
                metadata.Configuration["captured"] = true;
                return Task.CompletedTask;
            }
        };

        await RepackMetadata.WriteAsync(handler, source, extraction,
            RepackMetadataContext.FromProjectRoot(metadataRoot), token,
            new Dictionary<string, string> { ["BlockSize"] = "4096" });

        var saved = RepackMetadata.Read(extraction, RepackMetadataContext.LocalDirectory);
        Assert.Null(saved);
        saved = RepackMetadata.ReadFromAbsolute(Path.Combine(destination, RepackMetadata.FileName));
        Assert.NotNull(saved);
        Assert.Equal("Custom Firmware", saved.FormatName);
        Assert.True(saved.CanPackDirectory);
        Assert.True(saved.Configuration["captured"]!.GetValue<bool>());
        Assert.Equal("4096", saved.PackingOptions["BlockSize"]);
        Assert.EndsWith(" Custom format settings.", saved.PackingNotes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CaptureFails_PreservesExistingMetadata(bool cancel)
    {
        var source = CreateSource();
        var destination = Path.Combine(_root, "metadata", "system");
        await new RepackMetadata { FormatName = "original" }.SaveAsync(destination, TestContext.Current.CancellationToken);
        var metadataPath = Path.Combine(destination, RepackMetadata.FileName);
        var original = File.ReadAllBytes(metadataPath);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new Provider
        {
            Capture = async (_, _, _, _, token) =>
            {
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }
                throw new IOException("Capture failed.");
            }
        };

        var error = await Record.ExceptionAsync(() => RepackMetadata.WriteAsync(handler, source, _root,
            RepackMetadataContext.FromProjectRoot(Path.Combine(_root, "metadata")), cancellation.Token));

        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<IOException>(error);
        }
        Assert.Equal(original, File.ReadAllBytes(metadataPath));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp-*"));
    }

    [Fact]
    public async Task WriteAsync_HandlerWithoutProvider_DoesNotInferRebuildSupportFromName()
    {
        var source = CreateSource();

        await RepackMetadata.WriteAsync(new Handler(), source, _root,
            RepackMetadataContext.LocalDirectory, TestContext.Current.CancellationToken);

        var metadata = RepackMetadata.Read(_root, RepackMetadataContext.LocalDirectory);
        Assert.NotNull(metadata);
        Assert.Equal("EXT4", metadata.FormatName);
        Assert.False(metadata.CanPackDirectory);
        Assert.Empty(metadata.Configuration);
        Assert.Empty(metadata.PackingOptions);
        Assert.False(Directory.Exists(Path.Combine(_root, ".filesystem")));
    }

    private string CreateSource()
    {
        var source = Path.Combine(_root, "system.img");
        File.WriteAllBytes(source, [1, 2, 3]);
        return source;
    }

    private class Handler(string formatName = "EXT4") : IReadonlyFormatHandler
    {
        public string FormatName => formatName;
        public IReadOnlyList<string> FileExtensions => ["img"];
        public bool CanHandle(string filePath) => true;
        public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(FirmwareOpResult.Ok("source", metadata: new Dictionary<string, string> { ["BlockSize"] = "1024" }));
        public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Provider() : Handler("Custom Firmware"), IRepackMetadataProvider
    {
        public required Func<RepackMetadata, string, string, string, CancellationToken, Task> Capture { get; init; }

        public void ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        {
            metadata.CanPackDirectory = true;
            metadata.PackingOptions["BlockSize"] = metadata.SourceProperties["BlockSize"];
            metadata.PackingNotes = " Custom format settings.";
        }

        public Task CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
            string metadataDirectory, CancellationToken cancellationToken = default)
            => Capture(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}