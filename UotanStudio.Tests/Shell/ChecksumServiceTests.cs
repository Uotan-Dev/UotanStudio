using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using UotanStudio.Services.Checksum;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class ChecksumServiceTests
{
    private static readonly ChecksumAlgorithms All = new(Md5: true, Sha1: true, Sha256: true, Crc32: true);

    [Fact]
    public async Task ComputeAsync_KnownVectors_ProducesStandardDigests()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");

        var result = await ChecksumService.ComputeAsync(path, All, TestContext.Current.CancellationToken);

        // RFC 1326 / FIPS 180-4 / IEEE 802.3 参考向量。
        // Reference vectors from RFC 1326, FIPS 180-4 and IEEE 802.3.
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", result.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", result.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", result.Sha256);
        Assert.Equal("352441c2", result.Crc32);
    }

    [Fact]
    public async Task ComputeAsync_EmptyFile_ProducesStandardDigests()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("empty.bin", []);

        var result = await ChecksumService.ComputeAsync(path, All, TestContext.Current.CancellationToken);

        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", result.Md5);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", result.Sha1);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sha256);
        Assert.Equal("00000000", result.Crc32);
    }

    [Fact]
    public async Task ComputeAsync_UnselectedAlgorithms_ReturnEmptyStrings()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("only-sha256.txt", "abc");
        var onlySha256 = new ChecksumAlgorithms(Md5: false, Sha1: false, Sha256: true, Crc32: false);

        var result = await ChecksumService.ComputeAsync(path, onlySha256, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Md5);
        Assert.Equal(string.Empty, result.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", result.Sha256);
        Assert.Equal(string.Empty, result.Crc32);
    }

    [Fact]
    public async Task ComputeAsync_MultiMegabyteFile_HandlesChunkedReads()
    {
        using var directory = new TempDirectory();
        // 3 字节对齐 + 跨越多个 1MB 缓冲块，验证分块读取与整读结果一致。
        // 3-byte payload repeated across several 1MB buffer chunks; chunked reads must match a whole read.
        var payload = Encoding.ASCII.GetBytes("abc");
        var data = new byte[3 * 1024 * 1024 + 1];
        // 逐个字节填充，避免最后一个不完整块（3 字节对齐多出 1 字节）溢出 Span。
        // Fill byte-by-byte so the trailing partial chunk (1 byte past the 3-byte alignment) does not overflow.
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = payload[i % payload.Length];
        }

        var path = directory.WriteBytes("large.bin", data);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        // CRC32 同样由 data 自算（与 SHA256 一致），保证「分块读取 == 整读」的一致性断言与数据同源。
        // The CRC32 is likewise computed from data (like the SHA256) so the chunked-vs-whole assertion
        // stays consistent with the actual payload.
        var crc = new Crc32();
        crc.Append(data);
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crc.GetCurrentHash()).ToString("x8");

        var result = await ChecksumService.ComputeAsync(path, All, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Sha256);
        Assert.Equal(expectedCrc, result.Crc32);
    }

    [Fact]
    public async Task ComputeAsync_CancelledToken_ThrowsWithoutReturningPartialResult()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("abc.txt", "abc");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChecksumService.ComputeAsync(path, All, cts.Token));
    }

    [Fact]
    public async Task ComputeAsync_MissingFile_Throws()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "missing.bin");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ChecksumService.ComputeAsync(path, All, TestContext.Current.CancellationToken));
    }
}