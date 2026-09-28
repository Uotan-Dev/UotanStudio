using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Services.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

/// <summary>Exercises comparison input policies without constructing a document or UI platform.</summary>
public sealed class ComparisonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uotan-compare-input-{Guid.NewGuid():N}");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ComparisonServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Detection_PreservesFirmwareExtensionPriorityAndBinaryProbe()
    {
        var service = ComparisonService.CreateDefault();
        var plain = Write("plain.txt", "ordinary text");
        var firmware = Write("firmware.IMG", "ordinary text");
        var binary = Write("binary.txt", "text\0binary");

        Assert.True(await service.IsTextLikeFileAsync(plain, Token));
        Assert.False(await service.IsTextLikeFileAsync(firmware, Token));
        Assert.False(await service.IsTextLikeFileAsync(binary, Token));
    }

    [Fact]
    public async Task Directory_ReturnsSortedRelativeEntriesAndRawSizes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write("z.txt", "abc");
        Write("sub/a.txt", "12345");
        var entries = await Create().ReadDirectoryAsync(_root, Token);

        Assert.Equal(["sub", "sub/a.txt", "z.txt"], entries.Select(entry => entry.RelativePath));
        Assert.True(entries[0].IsDirectory);
        Assert.Equal(-1, entries[0].LeftSize);
        Assert.Equal(5, entries[1].LeftSize);
        Assert.Equal(3, entries[2].LeftSize);
        Assert.All(entries, entry =>
        {
            Assert.Equal(FolderItemStatus.LeftOnly, entry.Status);
            Assert.Equal(-1, entry.RightSize);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_PreservesBomBlankLinesAndReleasesFile(bool utf16)
    {
        var path = Path.Combine(_root, "text.txt");
        await File.WriteAllTextAsync(path, "first\r\n\r\nlast\n",
            utf16 ? Encoding.Unicode : new UTF8Encoding(true), Token);

        var lines = await Create().ReadTextAsync(path, Token);

        Assert.Equal(["first", "", "last"], lines);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task Text_RejectsOversizedFileBeforeReadingContents()
    {
        var path = Path.Combine(_root, "large.txt");
        using (var stream = File.Create(path)) stream.SetLength(ComparisonService.MaximumTextBytes + 1);

        var exception = await Assert.ThrowsAsync<FormatException>(() => Create().ReadTextAsync(path, Token));

        Assert.Contains("byte limit", exception.Message);
    }

    [Fact]
    public async Task Text_AcceptsLineLimitAndRejectsOneMoreLine()
    {
        var path = Write("lines.txt", new string('\n', ComparisonService.MaximumTextLines));
        var service = Create();
        Assert.Equal(ComparisonService.MaximumTextLines, (await service.ReadTextAsync(path, Token)).Count);
        await File.AppendAllTextAsync(path, "extra", Token);

        var exception = await Assert.ThrowsAsync<FormatException>(() => service.ReadTextAsync(path, Token));
        Assert.Contains("line limit", exception.Message);
    }

    [Fact]
    public async Task CancelledInputs_DoNotTouchMissingFilesOrFormatHandlers()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        var service = Create(detect: _ => throw new InvalidOperationException("Must not detect"));
        var missing = Path.Combine(_root, "missing");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadDirectoryAsync(missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadTextAsync(missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadMetadataAsync(missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompareTextAsync(missing, missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IsTextLikeFileAsync(missing, cancellation.Token));
    }

    [Fact]
    public async Task Metadata_AwaitsHandlerAndPreservesDataWithoutUnpacking()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<FirmwareOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MetadataHandler(async ct =>
        {
            Assert.Equal(Token, ct);
            entered.SetResult();
            return await result.Task.WaitAsync(ct);
        });
        var service = Create(_ => handler);
        var pending = service.ReadMetadataAsync("virtual-image", Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(pending.IsCompleted);
        result.SetResult(FirmwareOpResult.Ok("summary", ["entry"], new Dictionary<string, string> { ["key"] = "value" }));

        var metadata = await pending;

        Assert.Equal("Test format", metadata.FormatName);
        Assert.Equal("summary", metadata.Summary);
        Assert.Equal("value", metadata.Metadata["key"]);
        Assert.Equal(["entry"], metadata.Entries);
    }

    [Fact]
    public async Task Metadata_CancelledOldReadDoesNotAffectAnotherRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FirmwareOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new MetadataHandler(_ =>
        {
            entered.SetResult();
            return release.Task;
        });
        var current = new MetadataHandler(_ => Task.FromResult(FirmwareOpResult.Ok("current")));
        var service = Create(path => path == "old" ? old : current);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = service.ReadMetadataAsync("old", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancellation.Cancel();
        var metadata = await service.ReadMetadataAsync("current", Token);
        release.SetResult(FirmwareOpResult.Ok("stale"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("current", metadata.Summary);
    }

    [Fact]
    public async Task Metadata_ReportsDeclaredFailureButPropagatesUnexpectedExceptions()
    {
        var warnings = new List<string>();
        var failed = new MetadataHandler(_ => Task.FromResult(FirmwareOpResult.Fail("invalid header")));
        var service = Create(_ => failed, warnings.Add);

        Assert.Same(ComparisonMetadata.Unrecognized, await service.ReadMetadataAsync("broken", Token));
        Assert.Contains("invalid header", Assert.Single(warnings));
        Assert.Same(ComparisonMetadata.Unrecognized, await Create().ReadMetadataAsync("unknown", Token));

        var throwing = Create(_ => new MetadataHandler(_ => throw new InvalidDataException("bad structure")));
        await Assert.ThrowsAsync<InvalidDataException>(() => throwing.ReadMetadataAsync("broken", Token));
    }

    [Fact]
    public async Task Comparisons_ReturnTextDifferencesAndBinaryBitmap()
    {
        var left = Write("left.txt", "same\nleft\n");
        var right = Write("right.txt", "same\nright\n");
        var service = Create();

        var lines = await service.CompareTextAsync(left, right, Token);
        Assert.Contains(lines, line => line.Kind == DiffLineKind.Equal && line.LeftText == "same");
        Assert.Contains(lines, line => line.Kind != DiffLineKind.Equal);

        var bitmap = new byte[BinaryDiffService.RowBitmapLength(new FileInfo(right).Length)];
        var stats = await service.CompareBinaryAsync(left, right, bitmap, Token);
        Assert.True(stats.TotalDifferentBytes > 0);
        Assert.Equal(5, stats.FirstDiffOffset);
        Assert.NotEqual(0, bitmap[0]);
    }

    private static ComparisonService Create(Func<string, IReadonlyFormatHandler?>? detect = null, Action<string>? warning = null) =>
        new(new BinaryDiffService(), new TextDiffService(), new FolderCompareService(),
            detect ?? (_ => null), () => [], warning ?? (_ => { }));

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class MetadataHandler(Func<CancellationToken, Task<FirmwareOpResult>> read) : IReadonlyFormatHandler
    {
        public string FormatName => "Test format";
        public IReadOnlyList<string> FileExtensions => ["test"];
        public bool CanHandle(string filePath) => true;
        public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default) => read(cancellationToken);
        public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Comparison must never unpack a file.");
    }
}