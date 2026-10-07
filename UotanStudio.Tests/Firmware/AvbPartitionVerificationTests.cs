using System.Security.Cryptography;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbPartitionVerificationTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task VerifyAsync_SingleSha1Block_UsesContentDigestWithoutStoredTree(int blockSize)
    {
        var data = new byte[blockSize];
        var descriptor = new AvbDescriptorInfo
        {
            Tag = 1,
            HashAlgorithm = "sha1",
            Salt = [],
            RootDigest = SHA1.HashData(data),
            ImageSize = (ulong)blockSize,
            TreeOffset = (ulong)blockSize,
            DataBlockSize = (uint)blockSize,
            HashBlockSize = (uint)blockSize,
            DmVerityVersion = 1
        };
        using var stream = new MemoryStream(data, writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
        Assert.True(result.StoredTreeIsValid);
    }

    [Theory]
    [InlineData(512, 4096)]
    [InlineData(4096, 512)]
    public async Task VerifyAsync_DifferentDataAndHashBlockSizes_VerifiesAlignedTree(int dataBlockSize, int hashBlockSize)
    {
        var data = Enumerable.Range(0, 2 * dataBlockSize).Select(i => (byte)(i * 11)).ToArray();
        var tree = new byte[hashBlockSize];
        SHA256.HashData(data.AsSpan(0, dataBlockSize)).CopyTo(tree, 0);
        SHA256.HashData(data.AsSpan(dataBlockSize, dataBlockSize)).CopyTo(tree, 32);
        var treeOffset = ((data.Length + hashBlockSize - 1) / hashBlockSize) * hashBlockSize;
        var image = new byte[treeOffset + tree.Length];
        data.CopyTo(image, 0);
        tree.CopyTo(image, treeOffset);
        var descriptor = new AvbDescriptorInfo
        {
            Tag = 1,
            HashAlgorithm = "sha256",
            Salt = [],
            RootDigest = SHA256.HashData(tree),
            ImageSize = (ulong)data.Length,
            TreeOffset = (ulong)treeOffset,
            TreeSize = (ulong)tree.Length,
            DataBlockSize = (uint)dataBlockSize,
            HashBlockSize = (uint)hashBlockSize,
            DmVerityVersion = 1
        };
        using var stream = new MemoryStream(image, writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
        Assert.True(result.StoredTreeIsValid);
    }

    [Fact]
    public async Task VerifyAsync_EmptyHashContent_VerifiesSaltOnly()
    {
        byte[] salt = [1, 2, 3];
        using var stream = new MemoryStream([9, 8, 7], writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, HashDescriptor(0, SHA256.HashData(salt), salt), TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("unknown", 32, 0)]
    [InlineData("blake2b-256", 32, 0)]
    [InlineData("sha256", 31, 0)]
    [InlineData("sha256", 32, 2)]
    public async Task VerifyAsync_InvalidHashParameters_RejectsBeforeReading(string algorithm, int digestLength, uint flags)
    {
        using var stream = new ReadGuardStream(new byte[4096], rejectReads: true);
        var descriptor = new AvbDescriptorInfo { Tag = 2, HashAlgorithm = algorithm, ImageSize = 4096, Salt = [], Digest = new byte[digestLength], Flags = flags };

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    [InlineData("sha512")]
    public async Task VerifyAsync_HashDescriptor_ChecksOnlyProtectedPrefixAndRestoresPosition(string algorithm)
    {
        var data = Enumerable.Range(0, 150001).Select(i => (byte)i).ToArray();
        byte[] salt = [1, 2, 3];
        using var hasher = IncrementalHash.CreateHash(new HashAlgorithmName(algorithm.ToUpperInvariant()));
        hasher.AppendData(salt);
        hasher.AppendData(data);
        var descriptor = HashDescriptor((ulong)data.Length, hasher.GetHashAndReset(), salt, algorithm);
        using var stream = new MemoryStream([.. data, 0xff, 0x55], writable: false);
        stream.Position = 37;

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
        Assert.Null(result.StoredTreeIsValid);
        Assert.False(result.FecPresent);
        Assert.Null(result.FecIsValid);
        Assert.Equal(37, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(131071)]
    public async Task VerifyAsync_TamperedHashContent_FailsAtAnyProtectedOffset(int offset)
    {
        var data = new byte[131072];
        var descriptor = HashDescriptor((ulong)data.Length, SHA256.HashData(data));
        data[offset] = 1;
        using var stream = new MemoryStream(data, writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.Contains("mismatch", result.ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(257)]
    public async Task VerifyAsync_Hashtree_ValidatesAllLevelsAndRoot(int blocks)
    {
        var (image, descriptor) = TreeFixture(blocks);
        using var stream = new MemoryStream(image, writable: false);
        stream.Position = 5;

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
        Assert.True(result.StoredTreeIsValid);
        Assert.Equal(5, stream.Position);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("padding")]
    [InlineData("zeroed")]
    public async Task VerifyAsync_TamperedHashtreeContent_RejectsDataTreeAndPadding(string region)
    {
        var (image, descriptor) = TreeFixture(129);
        var offset = region switch
        {
            "data" => 4096,
            "top" => (int)descriptor.TreeOffset,
            "bottom" => (int)descriptor.TreeOffset + 4096,
            "padding" => image.Length - 1,
            _ => (int)descriptor.TreeOffset
        };
        image[offset] ^= 1;
        if (region == "zeroed")
        {
            "ZeRoHaSH"u8.CopyTo(image.AsSpan(offset));
        }
        using var stream = new MemoryStream(image, writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.False(result.StoredTreeIsValid);
    }

    [Fact]
    public async Task VerifyAsync_WrongRoot_SeparatesStoredTreeFromContentDigest()
    {
        var (image, descriptor) = TreeFixture(129);
        descriptor.RootDigest![0] ^= 1;
        using var stream = new MemoryStream(image, writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.True(result.StoredTreeIsValid);
        Assert.Contains("root", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_FecPresent_DoesNotClaimParityValidation()
    {
        var (image, original) = TreeFixture(2);
        var descriptor = TreeDescriptor(original, fecRoots: 2, fecOffset: (ulong)image.Length, fecSize: 4096);
        using var stream = new MemoryStream([.. image, .. new byte[4096]], writable: false);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.True(result.ContentIsValid, result.ErrorMessage);
        Assert.True(result.FecPresent);
        Assert.Null(result.FecIsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task VerifyAsync_InvalidTreeGeometry_RejectsBeforeReading(int scenario)
    {
        var (image, original) = TreeFixture(2);
        var descriptor = scenario switch
        {
            0 => TreeDescriptor(original, treeSize: ulong.MaxValue),
            1 => TreeDescriptor(original, treeOffset: 0),
            2 => TreeDescriptor(original, dataBlockSize: uint.MaxValue),
            3 => TreeDescriptor(original, hashBlockSize: 768),
            4 => TreeDescriptor(original, treeSize: 0),
            5 => TreeDescriptor(original, fecRoots: 2, fecOffset: 4096, fecSize: 4096),
            _ => TreeDescriptor(original, imageSize: ulong.MaxValue)
        };
        using var stream = new ReadGuardStream(image, rejectReads: true);

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VerifyAsync_MissingPersistentDigest_FailsWithoutReading(int tag)
    {
        using var stream = new ReadGuardStream(new byte[4096], rejectReads: true);
        var descriptor = new AvbDescriptorInfo { Tag = (ulong)tag, HashAlgorithm = "sha256", ImageSize = 4096, Salt = [], Digest = [], RootDigest = [] };

        var result = await AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken);

        Assert.False(result.ContentIsValid);
        Assert.Contains("Persistent", result.ErrorMessage);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_CancellationDuringRead_PropagatesAndRestoresStream(bool tree)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var (image, descriptor) = tree ? TreeFixture(129)
            : (new byte[131072], HashDescriptor(131072, SHA256.HashData(new byte[131072])));
        using var stream = new ReadGuardStream(image, cancellation: cancellation);
        stream.Position = 7;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AvbPartitionVerifier.VerifyAsync(stream, descriptor, cancellation.Token));

        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
        Assert.True(stream.BytesRead > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_TruncatedRead_ThrowsAndRestoresStream(bool tree)
    {
        var (image, descriptor) = tree ? TreeFixture(2)
            : (new byte[8192], HashDescriptor(8192, SHA256.HashData(new byte[8192])));
        using var stream = new ReadGuardStream(image, truncate: true);
        stream.Position = 9;

        await Assert.ThrowsAsync<EndOfStreamException>(() => AvbPartitionVerifier.VerifyAsync(stream, descriptor, TestContext.Current.CancellationToken));

        Assert.Equal(9, stream.Position);
    }

    internal static (byte[] Image, AvbDescriptorInfo Descriptor) TreeFixture(int blocks)
    {
        var data = Enumerable.Range(0, blocks * 4096).Select(i => (byte)(i * 17)).ToArray();
        byte[] salt = [1, 3, 5, 7];
        using var tree = AvbHashtreeBuilder.Build(data, salt);
        return ([.. data, .. tree.TreeBytes], new AvbDescriptorInfo
        {
            Tag = 1,
            PartitionName = "system",
            HashAlgorithm = "sha256",
            Salt = salt,
            RootDigest = tree.RootDigest,
            ImageSize = (ulong)data.Length,
            TreeOffset = (ulong)data.Length,
            TreeSize = (ulong)tree.TreeSize,
            DataBlockSize = 4096,
            HashBlockSize = 4096,
            DmVerityVersion = 1
        });
    }

    private static AvbDescriptorInfo HashDescriptor(ulong size, byte[] digest, byte[]? salt = null, string algorithm = "sha256") => new()
    {
        Tag = 2,
        PartitionName = "boot",
        HashAlgorithm = algorithm,
        Salt = salt ?? [],
        Digest = digest,
        ImageSize = size
    };

    private static AvbDescriptorInfo TreeDescriptor(AvbDescriptorInfo source, ulong? imageSize = null, ulong? treeOffset = null,
        ulong? treeSize = null, uint? dataBlockSize = null, uint? hashBlockSize = null,
        uint fecRoots = 0, ulong fecOffset = 0, ulong fecSize = 0) => new()
        {
            Tag = 1,
            PartitionName = source.PartitionName,
            HashAlgorithm = source.HashAlgorithm,
            Salt = source.Salt,
            RootDigest = source.RootDigest,
            ImageSize = imageSize ?? source.ImageSize,
            TreeOffset = treeOffset ?? source.TreeOffset,
            TreeSize = treeSize ?? source.TreeSize,
            DataBlockSize = dataBlockSize ?? source.DataBlockSize,
            HashBlockSize = hashBlockSize ?? source.HashBlockSize,
            DmVerityVersion = source.DmVerityVersion,
            FecNumRoots = fecRoots,
            FecOffset = fecOffset,
            FecSize = fecSize
        };

    private sealed class ReadGuardStream(byte[] data, bool rejectReads = false, CancellationTokenSource? cancellation = null, bool truncate = false)
        : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.False(rejectReads);
            Assert.InRange(buffer.Length, 1, 1024 * 1024);
            cancellationToken.ThrowIfCancellationRequested();
            var read = truncate ? 0 : base.Read(buffer.Span[..Math.Min(buffer.Length, 257)]);
            BytesRead += read;
            cancellation?.Cancel();
            return ValueTask.FromResult(read);
        }
    }
}