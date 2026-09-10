using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verification for the explicit repack-metadata context (T7): explicit project roots never
/// cross-fallback, explicit local mode ignores the ambient root, contexts are fixed at call
/// time, and legacy static entry points keep their historical behavior.
/// <para>显式重打包元数据上下文（T7）验证：显式工程根不交叉回退、显式本地模式忽略环境根、
/// 上下文在调用时固定，旧静态入口保持历史行为。</para>
/// </summary>
[Collection("Workspace state")]
public class RepackMetadataContextTests : IDisposable
{
    /// <summary>
    /// The smallest valid repack metadata JSON accepted by <see cref="RepackMetadata.ReadFromAbsolute"/>.
    /// <para><see cref="RepackMetadata.ReadFromAbsolute"/> 接受的最小有效重打包元数据 JSON。</para>
    /// </summary>
    private readonly string? _originalRoot = RepackMetadata.DefaultMetadataRoot;

    public void Dispose() => RepackMetadata.DefaultMetadataRoot = _originalRoot;

    private const string ValidMetadataJson = """{"schema":"UotanStudio.Firmware.Repack","version":1,"formatName":"EXT4","sourceFileName":"system.img","sourceLength":1,"sourceProperties":{},"packingOptions":{},"configuration":{}}""";

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uotan-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private static void WriteMetadata(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, RepackMetadata.FileName), ValidMetadataJson);
    }

    private static string WriteSourceFile(string directory, string fileName)
    {
        var path = System.IO.Path.Combine(directory, fileName);
        File.WriteAllText(path, "stub source image");
        return path;
    }

    [Fact]
    public void Read_ExplicitProjectRoot_FindsPartitionKeyedMetadata()
    {
        using var temp = new TempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "metadata");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        WriteMetadata(System.IO.Path.Combine(root, "system"));

        var context = RepackMetadataContext.FromProjectRoot(root);
        Assert.NotNull(RepackMetadata.Read(extraction, context));
        Assert.Equal(System.IO.Path.Combine(root, "system", RepackMetadata.FileName),
            RepackMetadata.ResolveActualMetadataPath(extraction, context));
    }

    [Fact]
    public void Read_ExplicitProjectRoot_MissingRootDoesNotFallbackToLocal()
    {
        using var temp = new TempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "missing-metadata-root");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        // Local metadata exists, but the explicit project root must not fall back to it.
        // 本地元数据存在，但显式工程根不得回退读取它。
        WriteMetadata(extraction);

        var context = RepackMetadataContext.FromProjectRoot(root);
        Assert.Null(RepackMetadata.Read(extraction, context));
        Assert.Null(RepackMetadata.ResolveActualMetadataPath(extraction, context));
    }

    [Fact]
    public void Read_ExplicitLocalDirectory_IgnoresAmbientRoot()
    {
        using var temp = new TempDirectory();
        var ambientRoot = System.IO.Path.Combine(temp.Path, "ambient");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        WriteMetadata(extraction);

        RepackMetadata.DefaultMetadataRoot = ambientRoot;
        try
        {
            // The ambient root is set but contains nothing for this partition; explicit local mode
            // must read the extraction directory and never probe the ambient root.
            // 环境根已设置但不含该分区；显式本地模式必须读解包目录，绝不探测环境根。
            var context = RepackMetadataContext.LocalDirectory;
            Assert.NotNull(RepackMetadata.Read(extraction, context));
            Assert.Equal(System.IO.Path.Combine(extraction, RepackMetadata.FileName),
                RepackMetadata.ResolveActualMetadataPath(extraction, context));
        }
        finally
        {
            RepackMetadata.DefaultMetadataRoot = null;
        }
    }

    [Fact]
    public void Read_UnspecifiedContext_MatchesLegacyBehavior()
    {
        using var temp = new TempDirectory();
        var ambientRoot = System.IO.Path.Combine(temp.Path, "ambient");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        WriteMetadata(System.IO.Path.Combine(ambientRoot, "system"));

        RepackMetadata.DefaultMetadataRoot = ambientRoot;
        try
        {
            // Unspecified keeps the legacy single-location rule: ambient root wins, no local read.
            // 未指定上下文保持旧单一位置规则：环境根优先，不读本地。
            Assert.NotNull(RepackMetadata.Read(extraction));
            Assert.Equal(System.IO.Path.Combine(ambientRoot, "system", RepackMetadata.FileName),
                RepackMetadata.ResolveActualMetadataPath(extraction));
        }
        finally
        {
            RepackMetadata.DefaultMetadataRoot = null;
        }

        // Without an ambient root the unspecified context reads the extraction directory.
        // 无环境根时未指定上下文读取解包目录。
        WriteMetadata(extraction);
        Assert.NotNull(RepackMetadata.Read(extraction));
    }

    [Fact]
    public async Task WriteAsync_ExplicitContext_IsFixedAtCallTime()
    {
        using var temp = new TempDirectory();
        var rootA = System.IO.Path.Combine(temp.Path, "root-a");
        var rootB = System.IO.Path.Combine(temp.Path, "root-b");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        var source = WriteSourceFile(extraction, "system.img");
        var handler = new StubHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRead = async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var write = RepackMetadata.WriteAsync(handler, source, extraction,
            RepackMetadataContext.FromProjectRoot(rootA), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        RepackMetadata.DefaultMetadataRoot = rootB;
        try
        {
            release.SetResult();
            await write;
            Assert.True(File.Exists(System.IO.Path.Combine(rootA, "system", RepackMetadata.FileName)),
                "The explicit project root must receive the metadata.");
            Assert.False(Directory.Exists(rootB), "The context must not follow the ambient root switched mid-operation.");
            Assert.False(File.Exists(System.IO.Path.Combine(extraction, RepackMetadata.FileName)),
                "Explicit project-root mode must not write beside the extraction directory.");
        }
        finally
        {
            RepackMetadata.DefaultMetadataRoot = null;
        }
    }

    [Fact]
    public async Task WriteAsync_ExplicitLocal_WritesBesideExtractionDespiteAmbientRoot()
    {
        using var temp = new TempDirectory();
        var ambientRoot = System.IO.Path.Combine(temp.Path, "ambient");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        var source = WriteSourceFile(extraction, "system.img");

        RepackMetadata.DefaultMetadataRoot = ambientRoot;
        try
        {
            await RepackMetadata.WriteAsync(new StubHandler(), source, extraction, RepackMetadataContext.LocalDirectory,
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(System.IO.Path.Combine(extraction, RepackMetadata.FileName)));
            Assert.False(Directory.Exists(ambientRoot), "Explicit local mode must not touch the ambient root.");
        }
        finally
        {
            RepackMetadata.DefaultMetadataRoot = null;
        }
    }

    [Fact]
    public async Task WriteAsync_ConcurrentExplicitRoots_DoNotCrossWrite()
    {
        using var temp = new TempDirectory();
        var rootA = System.IO.Path.Combine(temp.Path, "root-a");
        var rootB = System.IO.Path.Combine(temp.Path, "root-b");
        var extractionA = System.IO.Path.Combine(temp.Path, "a", "system.img");
        var extractionB = System.IO.Path.Combine(temp.Path, "b", "system.img");
        Directory.CreateDirectory(extractionA);
        Directory.CreateDirectory(extractionB);
        var sourceA = WriteSourceFile(extractionA, "system.img");
        var sourceB = WriteSourceFile(extractionB, "system.img");
        var handler = new StubHandler();

        await Task.WhenAll(
            RepackMetadata.WriteAsync(handler, sourceA, extractionA, RepackMetadataContext.FromProjectRoot(rootA), TestContext.Current.CancellationToken),
            RepackMetadata.WriteAsync(handler, sourceB, extractionB, RepackMetadataContext.FromProjectRoot(rootB), TestContext.Current.CancellationToken));

        Assert.True(File.Exists(System.IO.Path.Combine(rootA, "system", RepackMetadata.FileName)));
        Assert.True(File.Exists(System.IO.Path.Combine(rootB, "system", RepackMetadata.FileName)));
        Assert.False(File.Exists(System.IO.Path.Combine(extractionA, RepackMetadata.FileName)));
        Assert.False(File.Exists(System.IO.Path.Combine(extractionB, RepackMetadata.FileName)));
    }

    [Fact]
    public async Task WriteAsync_Cancelled_LeavesNoPartialJsonOrTempFiles()
    {
        using var temp = new TempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "root");
        var extraction = System.IO.Path.Combine(temp.Path, "system.img");
        Directory.CreateDirectory(extraction);
        var source = WriteSourceFile(extraction, "system.img");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RepackMetadata.WriteAsync(new StubHandler(), source, extraction,
                RepackMetadataContext.FromProjectRoot(root), cancellation.Token));

        Assert.False(Directory.Exists(root), "A cancelled write must not create the metadata directory.");
        Assert.Empty(Directory.GetFiles(extraction, "*.tmp-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitContext_OverwritesExistingMetadataWithUnrelatedAmbientRoot(bool project)
    {
        using var temp = new TempDirectory();
        var extraction = Path.Combine(temp.Path, "system");
        Directory.CreateDirectory(extraction);
        var source = WriteSourceFile(temp.Path, "system.img");
        var root = Path.Combine(temp.Path, "metadata");
        var target = project ? Path.Combine(root, "system") : extraction;
        WriteMetadata(target);
        var context = project ? RepackMetadataContext.FromProjectRoot(root) : RepackMetadataContext.LocalDirectory;
        RepackMetadata.DefaultMetadataRoot = Path.Combine(temp.Path, "unrelated");
        await RepackMetadata.WriteAsync(new StubHandler(), source, extraction, context, TestContext.Current.CancellationToken);
        Assert.True(RepackMetadata.IsMetadataFile(Path.Combine(target, RepackMetadata.FileName)));
        Assert.NotNull(RepackMetadata.Read(extraction, context));
        Assert.False(Directory.Exists(RepackMetadata.DefaultMetadataRoot));
    }

    [Fact]
    public void IsMetadataFile_DoesNotMistakeUnrelatedJsonForAmbientMetadata()
    {
        using var temp = new TempDirectory();
        var extraction = Path.Combine(temp.Path, "system");
        Directory.CreateDirectory(extraction);
        var root = Path.Combine(temp.Path, "metadata");
        WriteMetadata(Path.Combine(root, "system"));
        RepackMetadata.DefaultMetadataRoot = root;
        var path = Path.Combine(extraction, RepackMetadata.FileName);
        File.WriteAllText(path, "{}");
        Assert.False(RepackMetadata.IsMetadataFile(path));
    }

    [Fact]
    public async Task CancellationAfterSourceRead_PreservesExistingMetadata()
    {
        using var temp = new TempDirectory();
        WriteMetadata(temp.Path);
        var path = Path.Combine(temp.Path, RepackMetadata.FileName);
        var original = File.ReadAllBytes(path);
        var source = WriteSourceFile(temp.Path, "system.img");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler { BeforeRead = async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
        }};
        var write = RepackMetadata.WriteAsync(handler, source, temp.Path, RepackMetadataContext.LocalDirectory, cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task FailedPublication_RemovesTemporaryFileAndPreservesDestination()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, RepackMetadata.FileName);
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "keep");
        File.WriteAllText(marker, "existing destination");
        var source = WriteSourceFile(temp.Path, "system.img");
        var error = await Record.ExceptionAsync(() => RepackMetadata.WriteAsync(new StubHandler(), source,
            temp.Path, RepackMetadataContext.LocalDirectory, TestContext.Current.CancellationToken));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Publication unexpectedly succeeded.");
        Assert.Equal("existing destination", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    private sealed class StubHandler : IReadonlyFormatHandler
    {
        public string FormatName => "EXT4";

        public IReadOnlyList<string> FileExtensions => [".img"];

        public bool CanHandle(string filePath) => true;

        public Func<Task>? BeforeRead { get; set; }

        public async Task<FirmwareOpResult> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            if (BeforeRead is not null) await BeforeRead();
            return FirmwareOpResult.Ok("stub");
        }

        public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(FirmwareOpResult.Ok("stub"));
    }
}
