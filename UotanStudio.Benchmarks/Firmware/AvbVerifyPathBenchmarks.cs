using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// AVB hashtree verification throughput over one 256 MiB raw image, measured next to a sequential digest
/// of the same file as the pure read-and-hash lower bound.
/// <para>在同一个 256 MiB 原始镜像上度量 AVB 哈希树校验吞吐，并以同一文件的顺序摘要作为纯读取加哈希下限。</para>
/// </summary>
[MemoryDiagnoser]
public class AvbVerifyPathBenchmarks
{
    [Params(256)] public int MegaBytes;

    private string _root = "";
    private string _image = "";
    private AvbDescriptorInfo _descriptor = new();

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "avb-verify-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _image = Path.Combine(_root, "system.img");
        var bytes = new byte[MegaBytes * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(_image, bytes);
        byte[] salt = [1, 2, 3, 4];
        using var tree = AvbHashtreeBuilder.Build(_image, salt, 4096, 4096);
        using (var output = new FileStream(_image, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            output.Write(tree.TreeBytes);
        }

        _descriptor = new AvbDescriptorInfo
        {
            Tag = 1,
            PartitionName = "system",
            HashAlgorithm = "sha256",
            Salt = salt,
            RootDigest = tree.RootDigest,
            ImageSize = (ulong)tree.ImageSize,
            TreeOffset = (ulong)tree.ImageSize,
            TreeSize = (ulong)tree.TreeSize,
            DataBlockSize = 4096,
            HashBlockSize = 4096,
            DmVerityVersion = 1
        };
        using var warm = OpenImage();
        warm.CopyTo(Stream.Null, 1024 * 1024);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public async Task<bool> VerifyCurrent()
    {
        await using var stream = OpenImage();
        var result = await AvbPartitionVerifier.VerifyAsync(stream, _descriptor, CancellationToken.None);
        if (!result.ContentIsValid)
        {
            throw new InvalidOperationException(result.ErrorMessage);
        }

        return result.StoredTreeIsValid == true;
    }

    [Benchmark]
    public async Task<byte> SequentialSha256()
    {
        await using var stream = OpenImage();
        var digest = await SHA256.HashDataAsync(stream);
        return digest[0];
    }

    [Benchmark]
    public async Task<long> SequentialRead()
    {
        await using var stream = OpenImage();
        await stream.CopyToAsync(Stream.Null, 1024 * 1024);
        return stream.Length;
    }

    private FileStream OpenImage()
        => new(_image, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
}