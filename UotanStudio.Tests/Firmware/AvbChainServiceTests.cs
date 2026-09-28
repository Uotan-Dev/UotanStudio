using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbChainServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "uotan-avb-chain-service-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly RSA _childKey = RSA.Create(2048);
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public AvbChainServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task VerifyAvbChainAsync_TrustedChain_ReturnsPerImagePartitionAndRollbackResults()
    {
        await CreateChainAsync();

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey), new Dictionary<uint, ulong> { [0] = 8, [2] = 6 }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.RootKeyIsTrusted);
        Assert.True(result.RollbackIndexesAreValid);
        Assert.Equal(2, result.Images.Count);
        Assert.All(result.Images, image => Assert.True(image.SignatureIsValid));
        Assert.Equal(2U, result.Images[1].RollbackIndexLocation);
        Assert.True(result.Images[1].ExpectedKeyMatches);
        var partition = Assert.Single(result.Partitions);
        Assert.Equal("vbmeta_system", partition.MetadataPartition);
        Assert.Equal("system", partition.PartitionName);
        Assert.True(partition.Content.ContentIsValid);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_NoExternalPolicy_LeavesTrustAndRollbackUnverified()
    {
        await CreateChainAsync();

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Null(result.RootKeyIsTrusted);
        Assert.Null(result.RollbackIndexesAreValid);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_MissingMappedPartition_ReturnsStructuredFailure()
    {
        await CreateChainAsync();
        _paths.Remove("system");

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.RootKeyIsTrusted);
        Assert.Contains("explicitly mapped", Assert.Single(result.Errors));
        Assert.Empty(result.Partitions);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_TamperedPartition_ReturnsContentFailure()
    {
        await CreateChainAsync();
        await File.WriteAllBytesAsync(_paths["system"], [9, 8, 7, 6], TestContext.Current.CancellationToken);

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.RootKeyIsTrusted);
        Assert.False(Assert.Single(result.Partitions).Content.ContentIsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_WrongExternalRoot_RejectsBeforeFollowingDescriptors()
    {
        await CreateChainAsync();
        using var wrongKey = RSA.Create(2048);

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(wrongKey)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.RootKeyIsTrusted);
        Assert.Single(result.Images);
        Assert.Empty(result.Partitions);
        Assert.True(result.Images[0].SignatureIsValid);
        Assert.False(result.Images[0].ExpectedKeyMatches);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_StaleRollbackMinimum_FailsClosed()
    {
        await CreateChainAsync();

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey), new Dictionary<uint, ulong> { [0] = 8, [2] = 8 }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.RollbackIndexesAreValid);
        Assert.Contains(result.Errors, error => error.Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAvbChainAsync_Canceled_PropagatesCancellationBeforeOpeningImages()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FirmwareService.Instance.VerifyAvbChainAsync(
            Options(), cancellation.Token));
    }

    [Fact]
    public async Task VerifyAvbChainAsync_SparseMetadataAndPartition_VerifiesLogicalContent()
    {
        await CreateChainAsync();
        await MakeSparseAsync("vbmeta");
        await MakeSparseAsync("system");

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey), new Dictionary<uint, ulong> { [0] = 8, [2] = 6 }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.RootKeyIsTrusted);
        Assert.True(Assert.Single(result.Partitions).Content.ContentIsValid);
    }

    [Fact]
    public async Task VerifyAvbChainAsync_TruncatedSparseInput_ReturnsDiagnosticWithoutTrustClaim()
    {
        await CreateChainAsync();
        await MakeSparseAsync("vbmeta");
        await File.WriteAllBytesAsync(_paths["vbmeta"], [0x3a, 0xff, 0x26, 0xed], TestContext.Current.CancellationToken);

        var result = await FirmwareService.Instance.VerifyAvbChainAsync(Options(
            AvbRsaPublicKeyCodec.Export(_rootKey)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.RootKeyIsTrusted);
        Assert.Single(result.Errors);
        Assert.Empty(result.Partitions);
    }

    private AvbChainVerifyOptions Options(byte[]? trustedRoot = null, IReadOnlyDictionary<uint, ulong>? rollback = null) => new()
    {
        RootPartitionName = "vbmeta",
        PartitionPaths = _paths,
        TrustedRootPublicKey = trustedRoot,
        MinimumRollbackIndexes = rollback
    };

    private async Task CreateChainAsync()
    {
        byte[] data = [1, 2, 3, 4];
        var dataPath = Path.Combine(_directory, "system.img");
        await File.WriteAllBytesAsync(dataPath, data, TestContext.Current.CancellationToken);
        _paths["system"] = dataPath;

        await SignAsync("vbmeta_system", _childKey,
        [
            new AvbHashDescriptor
            {
                PartitionName = "system",
                ImageSize = (ulong)data.Length,
                Salt = [],
                Digest = SHA256.HashData(data)
            }
        ], rollback: 7, location: 29);
        await SignAsync("vbmeta", _rootKey,
        [
            new AvbChainPartitionDescriptor
            {
                PartitionName = "vbmeta_system",
                RollbackIndexLocation = 2,
                PublicKey = AvbRsaPublicKeyCodec.Export(_childKey)
            }
        ], rollback: 9, location: 0);
    }

    private async Task SignAsync(string name, RSA key, object[] descriptors, ulong rollback, uint location)
    {
        var header = new byte[256];
        "AVB0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(112, 8), rollback);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(124, 4), location);
        var template = Path.Combine(_directory, name + ".template");
        var output = Path.Combine(_directory, name + ".img");
        await File.WriteAllBytesAsync(template, header, TestContext.Current.CancellationToken);
        var privateKey = (RsaPrivateCrtKeyParameters)DotNetUtilities.GetRsaKeyPair(key).Private;
        var signed = await AvbSigner.SignVbmetaAsync<object>(template, privateKey, output, descriptors,
            TestContext.Current.CancellationToken);
        Assert.True(signed.Success, signed.ErrorMessage);
        _paths[name] = output;
    }

    private async Task MakeSparseAsync(string name)
    {
        var sparsePath = _paths[name] + ".sparse";
        using (var raw = SparseReader.FromRawFile(_paths[name], blockSize: 4))
        {
            await using var output = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            SparseWriter.WriteToStream(raw, output, includeCrc: true);
            await output.FlushAsync(TestContext.Current.CancellationToken);
        }
        _paths[name] = sparsePath;
    }

    public void Dispose()
    {
        _rootKey.Dispose();
        _childKey.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}