using System.Collections.Concurrent;
using System.Text;
using UotanStudio.Assets;
using UotanStudio.Compress;
using UotanStudio.Hash.Digests;
using Xunit;

namespace UotanStudio.Tests.Assets;

/// <summary>
/// Exercises the store against an in-memory payload source: every read decodes a zstd frame and checks it against the
/// manifest, materialization reuses one cache directory and stops re-reading frames, extraction writes verified bytes,
/// and each documented failure surfaces as its own exception type.
/// <para>用内存负载源驱动存储层：每次读取都解码一个 zstd 帧并对照清单校验，物化复用一个缓存目录且不再重读帧，
/// 抽取写出已校验字节，各类已记录的失败路径各自抛出对应异常类型。</para>
/// </summary>
public sealed class AssetStoreExtractionTests : IDisposable
{
    private const string BundleKey = "demo/bundle";

    private const string Header = "# <bundle>\t<path>\t<length>\t<sha256>\t<encoding>\t<origin>";

    private readonly string _root;

    public AssetStoreExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "UotanStudio.Tests.Assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Read_RegisteredFrame_ReturnsTheOriginalPayload()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", payload));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(payload, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        var decoded = store.Read(bundle["alpha.bin"]);

        Assert.Equal(payload, decoded);
        Assert.Equal(1, source.ReadCount(FrameName("alpha.bin")));
    }

    [Fact]
    public void Read_FrameMissing_ThrowsInvalidDataException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", payload));
        var store = CreateStore(new RecordingPayloadSource(), bundle);

        Assert.Throws<InvalidDataException>(() =>
        {
            store.Read(bundle["alpha.bin"]);
        });
    }

    [Fact]
    public void Read_FrameThatIsNotZstd_ThrowsInvalidDataException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", payload));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), Encoding.UTF8.GetBytes("this frame is not a zstd frame at all"));
        var store = CreateStore(source, bundle);

        Assert.Throws<InvalidDataException>(() =>
        {
            store.Read(bundle["alpha.bin"]);
        });
    }

    [Fact]
    public void Read_DeclaredLengthShorterThanDecoded_ThrowsInvalidDataException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(("alpha.bin", payload.LongLength - 1, Sha256.ComputeHexLower(payload)));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(payload, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        Assert.Throws<InvalidDataException>(() =>
        {
            store.Read(bundle["alpha.bin"]);
        });
    }

    [Fact]
    public void Read_DeclaredDigestDiffersFromDecodedBytes_ThrowsInvalidDataException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var otherDigest = Sha256.ComputeHexLower(Encoding.UTF8.GetBytes("some-other-payload"));
        var bundle = DeclareBundle(("alpha.bin", payload.LongLength, otherDigest));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(payload, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        Assert.Throws<InvalidDataException>(() =>
        {
            store.Read(bundle["alpha.bin"]);
        });
    }

    [Fact]
    public void Materialize_CalledTwice_ReusesTheCacheDirectoryWithoutRereadingFrames()
    {
        var token = TestContext.Current.CancellationToken;
        var alpha = Encoding.UTF8.GetBytes("alpha-payload");
        var beta = Encoding.UTF8.GetBytes("beta-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", alpha), Declare("beta.bin", beta));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(alpha, CompressionFormat.Zstd));
        source.Register(FrameName("beta.bin"), CompressionService.Compress(beta, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        var first = store.Materialize(bundle, token);
        var readsAfterFirst = source.TotalReadCount;
        var second = store.Materialize(bundle, token);

        Assert.Equal(first, second);
        Assert.Equal(Path.Combine(CacheRoot, "demo", "bundle", bundle.Digest[..16]), first);
        Assert.Equal(2, readsAfterFirst);
        Assert.Equal(readsAfterFirst, source.TotalReadCount);
        Assert.Equal(alpha, File.ReadAllBytes(Path.Combine(first, "alpha.bin")));
        Assert.Equal(beta, File.ReadAllBytes(Path.Combine(first, "beta.bin")));
    }

    [Fact]
    public async Task Materialize_ConcurrentCalls_ReturnTheSameCacheDirectory()
    {
        var token = TestContext.Current.CancellationToken;
        var alpha = Encoding.UTF8.GetBytes("alpha-payload");
        var beta = Encoding.UTF8.GetBytes("beta-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", alpha), Declare("beta.bin", beta));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(alpha, CompressionFormat.Zstd));
        source.Register(FrameName("beta.bin"), CompressionService.Compress(beta, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        var directories = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.Materialize(bundle, token), token)));

        Assert.All(directories, directory =>
        {
            Assert.Equal(directories[0], directory);
        });
        Assert.Equal(2, source.TotalReadCount);
        Assert.Equal(alpha, File.ReadAllBytes(Path.Combine(directories[0], "alpha.bin")));
        Assert.Equal(beta, File.ReadAllBytes(Path.Combine(directories[0], "beta.bin")));
    }

    [Fact]
    public void Extract_ExistingDirectory_WritesVerifiedBytesAndReturnsManifestOrder()
    {
        var alpha = Encoding.UTF8.GetBytes("alpha-payload");
        var beta = Encoding.UTF8.GetBytes("beta-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", alpha), Declare("beta.bin", beta));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(alpha, CompressionFormat.Zstd));
        source.Register(FrameName("beta.bin"), CompressionService.Compress(beta, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);
        var destination = Path.Combine(_root, "extract");
        Directory.CreateDirectory(destination);

        var names = store.Extract(bundle, destination, TestContext.Current.CancellationToken);

        Assert.Equal(["alpha.bin", "beta.bin"], names.ToArray());
        Assert.Equal(alpha, File.ReadAllBytes(Path.Combine(destination, "alpha.bin")));
        Assert.Equal(beta, File.ReadAllBytes(Path.Combine(destination, "beta.bin")));
        Assert.False(File.Exists(Path.Combine(destination, "alpha.bin.tmp")));
        Assert.False(File.Exists(Path.Combine(destination, "beta.bin.tmp")));
    }

    [Fact]
    public void Extract_MissingDestinationDirectory_ThrowsDirectoryNotFoundException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", payload));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(payload, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        Assert.Throws<DirectoryNotFoundException>(() =>
        {
            store.Extract(bundle, Path.Combine(_root, "absent"), TestContext.Current.CancellationToken);
        });
        Assert.Equal(0, source.TotalReadCount);
    }

    [Fact]
    public void Materialize_BundleWithoutEntries_ThrowsInvalidOperationException()
    {
        var bundle = AssetBundle.Empty("empty");
        var store = CreateStore(new RecordingPayloadSource(), bundle);

        Assert.Throws<InvalidOperationException>(() =>
        {
            store.Materialize(bundle, TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public void Materialize_CancelledToken_ThrowsOperationCanceledException()
    {
        var payload = Encoding.UTF8.GetBytes("uotan-asset-payload");
        var bundle = DeclareBundle(Declare("alpha.bin", payload));
        var source = new RecordingPayloadSource();
        source.Register(FrameName("alpha.bin"), CompressionService.Compress(payload, CompressionFormat.Zstd));
        var store = CreateStore(source, bundle);

        Assert.Throws<OperationCanceledException>(() =>
        {
            store.Materialize(bundle, new CancellationToken(canceled: true));
        });
    }

    private string CacheRoot => Path.Combine(_root, "cache");

    private AssetStoreInstance CreateStore(IAssetPayloadSource source, params AssetBundle[] bundles)
    {
        var map = bundles.ToDictionary(static bundle => bundle.Key, static bundle => bundle, StringComparer.Ordinal);
        return new AssetStoreInstance(map, source, CacheRoot);
    }

    private static AssetBundle DeclareBundle(params (string Path, long Length, string Sha256)[] rows)
    {
        var lines = new List<string> { Header };
        foreach (var (path, length, sha256) in rows)
        {
            lines.Add($"{BundleKey}\t{path}\t{length}\t{sha256}\tzstd\tsource");
        }

        return AssetManifest.Parse(Encoding.UTF8.GetBytes(string.Join('\n', lines)))[BundleKey];
    }

    private static (string Path, long Length, string Sha256) Declare(string path, byte[] payload) =>
        (path, payload.LongLength, Sha256.ComputeHexLower(payload));

    private static string FrameName(string path) => $"assets/{BundleKey}/{path}.zst";

    private sealed class RecordingPayloadSource : IAssetPayloadSource
    {
        private readonly ConcurrentDictionary<string, byte[]> _frames = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

        private int _totalReadCount;

        internal int TotalReadCount => _totalReadCount;

        internal void Register(string resourceName, byte[] frame)
        {
            _frames[resourceName] = frame;
        }

        internal int ReadCount(string resourceName) => _reads.TryGetValue(resourceName, out var count) ? count : 0;

        public byte[] Read(string resourceName)
        {
            _reads.AddOrUpdate(resourceName, 1, static (_, existing) => existing + 1);
            Interlocked.Increment(ref _totalReadCount);
            if (!_frames.TryGetValue(resourceName, out var frame))
            {
                throw new InvalidDataException(
                    $"The asset resource '{resourceName}' is missing from the test source. "
                    + $"测试源中缺少资源 '{resourceName}'。");
            }

            return frame;
        }
    }
}