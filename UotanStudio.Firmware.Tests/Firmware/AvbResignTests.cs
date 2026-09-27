using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Build;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbResignTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-avb-resign-" + Guid.NewGuid().ToString("N"));

    public AvbResignTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(512, 4096, 8192)]
    [InlineData(4096, 512, 4608)]
    public void Build_DifferentBlockSizes_MatchesExplicitTwoLevelTree(int dataBlockSize, int hashBlockSize, int leafSize)
    {
        var input = Enumerable.Range(0, 129 * dataBlockSize).Select(i => (byte)(i * 37)).ToArray();
        byte[] salt = [5, 4, 3, 2, 1];
        var leaves = new byte[leafSize];
        for (var block = 0; block < 129; block++)
        {
            var salted = salt.Concat(input.AsSpan(block * dataBlockSize, dataBlockSize).ToArray()).ToArray();
            SHA256.HashData(salted).CopyTo(leaves, block * 32);
        }
        var top = new byte[hashBlockSize];
        for (var block = 0; block < leafSize / hashBlockSize; block++)
        {
            var salted = salt.Concat(leaves.AsSpan(block * hashBlockSize, hashBlockSize).ToArray()).ToArray();
            SHA256.HashData(salted).CopyTo(top, block * 32);
        }

        using var tree = AvbHashtreeBuilder.Build(input, salt, dataBlockSize, hashBlockSize);

        Assert.Equal(top.Concat(leaves).ToArray(), tree.TreeBytes);
        Assert.Equal(SHA256.HashData(salt.Concat(top).ToArray()), tree.RootDigest);
        Assert.Equal(leafSize + hashBlockSize, tree.TreeSize);
    }

    [Theory]
    [InlineData(AvbResignMode.Hash, false)]
    [InlineData(AvbResignMode.Hashtree, false)]
    [InlineData(AvbResignMode.Hash, true)]
    public async Task ResignImageAsync_SparseInput_VerifiesLogicalContentAndRejectsBadCrc(AvbResignMode mode, bool corruptCrc)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var signed = Path.Combine(_root, "signed.img");
        var sparsePath = Path.Combine(_root, "sparse.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 17001).Select(i => (byte)(i * 31)).ToArray(), token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        var options = new AvbResignOptions { Mode = mode, PartitionName = "system", Salt = [] };
        var first = await AvbResigner.ResignImageAsync(source, signed, key, options, token);
        Assert.True(first.Success, first.ErrorMessage);
        using (var sparse = SparseReader.FromRawFile(signed))
        await using (var destination = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            SparseWriter.WriteToStream(sparse, destination, includeCrc: true);
        }
        if (corruptCrc)
        {
            await using var stream = new FileStream(sparsePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = stream.Length - 4;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 1));
        }
        await File.WriteAllBytesAsync(output, [9, 8, 7], token);

        var result = await AvbResigner.ResignImageAsync(sparsePath, output, key, options, token);

        Assert.Equal(!corruptCrc, result.Success);
        Assert.Equal(corruptCrc ? new byte[] { 9, 8, 7 } : await File.ReadAllBytesAsync(signed, token),
            await File.ReadAllBytesAsync(output, token));
        using var exclusive = new FileStream(sparsePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Theory]
    [InlineData(AvbResignMode.Hash)]
    [InlineData(AvbResignMode.Hashtree)]
    public async Task ResignImageAsync_CanceledBeforeStart_PreservesDestination(AvbResignMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(output, [7, 6, 5], token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AvbResigner.ResignImageAsync(
            Path.Combine(_root, "missing.img"), output, key,
            new AvbResignOptions { Mode = mode, PartitionName = "boot" }, cancellation.Token));

        Assert.Equal(new byte[] { 7, 6, 5 }, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory]
    [InlineData(AvbResignMode.Hash, 4096, 4096)]
    [InlineData(AvbResignMode.Hashtree, 512, 4096)]
    [InlineData(AvbResignMode.Hashtree, 4096, 512)]
    [InlineData(AvbResignMode.Hashtree, 4096, 4096)]
    public async Task ResignImageAsync_ExplicitOptions_PreservesContentAndSerializesEveryParameter(
        AvbResignMode mode, int dataBlockSize, int hashBlockSize)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        var data = Enumerable.Range(0, 129 * dataBlockSize - 13).Select(i => (byte)(i * 19)).ToArray();
        await File.WriteAllBytesAsync(source, data, token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        var options = new AvbResignOptions
        {
            Mode = mode,
            PartitionName = "system",
            Salt = [1, 2, 3, 4],
            RollbackIndex = 987,
            RollbackIndexLocation = 31,
            Flags = 3,
            DoNotUseAb = true,
            DataBlockSize = dataBlockSize,
            HashBlockSize = hashBlockSize,
            PartitionSize = 1048576
        };

        var result = await AvbResigner.ResignImageAsync(source, output, key, options, token);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1048576, result.OutputSize);
        var bytes = await File.ReadAllBytesAsync(output, token);
        Assert.Equal(data, bytes[..data.Length]);
        var footer = AvbFooter.TryLoad(bytes)!.Value;
        Assert.Equal((ulong)data.Length, footer.OriginalImageSize);
        var metadata = bytes.AsSpan((int)footer.VbmetaOffset, (int)footer.VbmetaSize).ToArray();
        var header = AvbVBMetaImageHeader.FromBytes(metadata);
        Assert.Equal(987UL, header.RollbackIndex);
        Assert.Equal(31U, header.RollbackIndexLocation);
        Assert.Equal(3U, header.Flags);
        Assert.Equal(2U, header.RequiredLibavbVersionMinor);
        Assert.True(AvbSignatureVerifier.Verify(metadata).SignatureIsValid);
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(metadata));
        Assert.Equal(options.Salt, descriptor.Salt);
        Assert.Equal(1U, descriptor.Flags);
        if (mode == AvbResignMode.Hashtree)
        {
            Assert.Equal((uint)dataBlockSize, descriptor.DataBlockSize);
            Assert.Equal((uint)hashBlockSize, descriptor.HashBlockSize);
        }
        using var image = new MemoryStream(bytes, writable: false);
        var verified = await AvbPartitionVerifier.VerifyAsync(image, descriptor, token);
        Assert.True(verified.ContentIsValid, verified.ErrorMessage);

        var repeated = Path.Combine(_root, "repeated.img");
        var second = await AvbResigner.ResignImageAsync(output, repeated, key, options, token);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(repeated, token));
        Assert.Equal(data, await File.ReadAllBytesAsync(source, token));
    }

    [Theory]
    [InlineData(AvbResignMode.Hash)]
    [InlineData(AvbResignMode.Hashtree)]
    public async Task ResignImageAsync_ExactMinimumCapacity_SucceedsAndRejectsOneBlockLess(AvbResignMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[4097], token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;
        var first = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { Mode = mode, PartitionName = "boot", Salt = [] }, token);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal(0, first.OutputSize % 4096);
        var expected = await File.ReadAllBytesAsync(output, token);
        var exact = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { Mode = mode, PartitionName = "boot", Salt = [], PartitionSize = first.OutputSize }, token);
        Assert.True(exact.Success, exact.ErrorMessage);
        Assert.Equal(expected, await File.ReadAllBytesAsync(output, token));

        var rejected = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { Mode = mode, PartitionName = "boot", PartitionSize = first.OutputSize - 4096 }, token);
        Assert.False(rejected.Success);
        Assert.Equal(expected, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory]
    [InlineData(0, 32, 0, 4096)]
    [InlineData(0, 0, 4, 4096)]
    [InlineData(12345, 0, 0, 4096)]
    [InlineData(-4096, 0, 0, 4096)]
    [InlineData(0, 0, 0, 32)]
    [InlineData(0, 0, 0, 768)]
    [InlineData(0, 0, 0, 2097152)]
    public async Task ResignImageAsync_InvalidOptions_PreservesExistingOutput(
        long capacity, uint slot, uint flags, int blockSize)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        await File.WriteAllBytesAsync(source, new byte[8192], token);
        await File.WriteAllBytesAsync(output, [9, 8, 7], token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;

        var result = await AvbResigner.ResignImageAsync(source, output, key, new AvbResignOptions
        {
            Mode = AvbResignMode.Hashtree,
            PartitionName = "system",
            RollbackIndexLocation = slot,
            Flags = flags,
            DataBlockSize = blockSize,
            PartitionSize = capacity == 0 ? null : capacity
        }, token);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(output, token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task ResignImageAsync_InvalidExistingFooter_RejectsInsteadOfSigningMetadataAsContent()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source.img");
        var output = Path.Combine(_root, "output.img");
        var bytes = new byte[8192];
        "AVBf"u8.CopyTo(bytes.AsSpan(bytes.Length - 64));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 60), 1);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(bytes.Length - 44), ulong.MaxValue);
        await File.WriteAllBytesAsync(source, bytes, token);
        await File.WriteAllBytesAsync(output, [1], token);
        using var rsa = SigningTestKeys.Create();
        var key = (RsaKeyParameters)DotNetUtilities.GetRsaKeyPair(rsa).Private;

        var result = await AvbResigner.ResignImageAsync(source, output, key,
            new AvbResignOptions { PartitionName = "boot" }, token);

        Assert.False(result.Success);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(output, token));
    }

    [Fact]
    public void Build_CanceledDuringTreeRead_RestoresInputPosition()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelingStream(new byte[1048576], cancellation);
        stream.Position = 7;

        Assert.Throws<OperationCanceledException>(() =>
            AvbHashtreeBuilder.Build(stream, [], 4096, 512, cancellation.Token));

        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CancelingStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }
}