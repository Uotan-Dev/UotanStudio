using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbChainVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_SparseMetadataAndPartition_VerifiesLogicalBytesAndChecksums(bool corruptCrc)
    {
        using var fixture = new Fixture();
        var data = new byte[4096];
        await fixture.DataAsync("system", data);
        await fixture.MetadataAsync("vbmeta", [Fixture.Hash("system", data)]);
        await fixture.SparseAsync("vbmeta");
        await fixture.SparseAsync("system", corruptCrc);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.Equal(!corruptCrc, result.Success);
        if (corruptCrc)
        {
            Assert.Contains("CRC32", Assert.Single(result.Errors));
        }
        else
        {
            Assert.True(Assert.Single(result.Partitions).Content.ContentIsValid);
        }
    }

    [Fact]
    public async Task VerifyAsync_SparseFooterImage_FindsFooterAtLogicalEnd()
    {
        using var fixture = new Fixture();
        await fixture.FooterAsync("boot", new byte[4096]);
        await fixture.SparseAsync("boot");

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(root: "boot"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(Assert.Single(result.Partitions).Content.ContentIsValid);
    }

    [Fact]
    public async Task VerifyAsync_FooterProtectedContentStartsWithAvbMagic_UsesFooterMetadata()
    {
        using var fixture = new Fixture();
        var data = new byte[512];
        "AVB0"u8.CopyTo(data);
        await fixture.FooterAsync("boot", data);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(root: "boot"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.RootKeyIsTrusted);
        Assert.Equal(1U, result.Images[0].Header!.Value.AlgorithmType);
        Assert.True(Assert.Single(result.Partitions).Content.ContentIsValid);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(32U)]
    public async Task VerifyAsync_InvalidChainRollbackSlot_RejectsBeforeChildRead(uint slot)
    {
        using var fixture = new Fixture();
        await fixture.MetadataAsync("vbmeta", [fixture.Chain("missing", slot)]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Single(result.Images);
        Assert.Contains("rollback", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData(4, 2UL)]
    [InlineData(8, 4UL)]
    [InlineData(12, 16777280UL)]
    [InlineData(20, ulong.MaxValue)]
    public async Task VerifyAsync_InvalidMetadataHeader_ReturnsDiagnosticWithoutThrowing(int offset, ulong value)
    {
        using var fixture = new Fixture();
        var bytes = new byte[256];
        "AVB0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 1);
        if (offset < 12)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), (uint)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset, 8), value);
        }
        await fixture.DataAsync("vbmeta", bytes);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Empty(result.Partitions);
    }

    [Fact]
    public async Task VerifyAsync_TrustedChain_ChecksDataDelegatedKeyAndParentRollbackSlot()
    {
        using var fixture = new Fixture();
        await fixture.SimpleGraphAsync();

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(rollback: new Dictionary<uint, ulong> { [0] = 8, [2] = 6 }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.RootKeyIsTrusted);
        Assert.True(result.RollbackIndexesAreValid);
        Assert.Equal(2, result.Images.Count);
        Assert.All(result.Images, image => Assert.True(image.SignatureIsValid));
        Assert.Equal(29U, result.Images[1].Header!.Value.RollbackIndexLocation);
        Assert.Equal(2U, result.Images[1].RollbackIndexLocation);
        Assert.True(result.Images[1].ExpectedKeyMatches);
        var partition = Assert.Single(result.Partitions);
        Assert.Equal("vbmeta_system", partition.MetadataPartition);
        Assert.Equal("system", partition.PartitionName);
        Assert.True(partition.Content.ContentIsValid);
    }

    [Fact]
    public async Task VerifyAsync_WithoutExternalPolicy_LeavesTrustAndRollbackUnverified()
    {
        using var fixture = new Fixture();
        await fixture.SimpleGraphAsync();

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(trust: false), TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Null(result.RootKeyIsTrusted);
        Assert.Null(result.RollbackIndexesAreValid);
        Assert.All(result.Images, image => Assert.Null(image.RollbackIndexIsValid));
    }

    [Fact]
    public async Task VerifyAsync_WrongRootPin_DoesNotTraverseUntrustedDescriptors()
    {
        using var fixture = new Fixture();
        await fixture.SimpleGraphAsync();
        using var wrong = SigningTestKeys.Create(alternate: true);
        var options = new AvbChainVerifyOptions
        {
            RootPartitionName = "vbmeta",
            PartitionPaths = fixture.Paths,
            TrustedRootPublicKey = AvbRsaPublicKeyCodec.Export(wrong)
        };

        var result = await AvbChainVerifier.VerifyAsync(options, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.RootKeyIsTrusted);
        Assert.Single(result.Images);
        Assert.Empty(result.Partitions);
        Assert.True(result.Images[0].SignatureIsValid);
        Assert.False(result.Images[0].ExpectedKeyMatches);
    }

    [Fact]
    public async Task VerifyAsync_WrongDelegatedSigner_RejectsEvenWhenChildSignatureIsValid()
    {
        using var fixture = new Fixture();
        await fixture.SimpleGraphAsync(alternateChild: true);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.RootKeyIsTrusted);
        Assert.True(result.Images[1].SignatureIsValid);
        Assert.False(result.Images[1].ExpectedKeyMatches);
        Assert.Empty(result.Partitions);
    }

    [Theory]
    [InlineData(0, 10UL)]
    [InlineData(2, 8UL)]
    [InlineData(0, 0UL)]
    public async Task VerifyAsync_MissingOrStaleRollbackPolicy_Rejects(int slot, ulong minimum)
    {
        using var fixture = new Fixture();
        await fixture.SimpleGraphAsync();
        var policy = new Dictionary<uint, ulong> { [(uint)slot] = minimum };

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(rollback: policy), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(result.RollbackIndexesAreValid);
        Assert.Contains(result.Errors, error => error.Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_MissingPartitionOrUnmappedPathLikeName_FailsClosed(bool pathLike)
    {
        using var fixture = new Fixture();
        var target = pathLike ? "../system" : "system";
        await fixture.MetadataAsync("vbmeta", [Fixture.Hash(target, [1, 2, 3])]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("explicitly mapped", Assert.Single(result.Errors));
        Assert.Empty(result.Partitions);
    }

    [Theory]
    [InlineData(0U, 0U)]
    [InlineData(1U, 0U)]
    [InlineData(0U, 1U)]
    [InlineData(1U, 1U)]
    public async Task VerifyAsync_SlotSuffixAndDoNotUseAb_ResolveExactPhysicalMappings(uint chainFlags, uint hashFlags)
    {
        using var fixture = new Fixture();
        byte[] data = [1, 2, 3, 4];
        var child = chainFlags == 0 ? "vbmeta_system_a" : "vbmeta_system";
        var system = hashFlags == 0 ? "system_a" : "system";
        await fixture.DataAsync(system, data);
        await fixture.MetadataAsync(child, [Fixture.Hash("system", data, hashFlags)]);
        await fixture.MetadataAsync("vbmeta_a", [fixture.Chain("vbmeta_system", 1, chainFlags)]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(root: "vbmeta_a", suffix: "_a"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(child, result.Images[1].PartitionName);
        Assert.Equal(system, Assert.Single(result.Partitions).PartitionName);
        if (chainFlags == 1)
        {
            Assert.Equal(3U, result.Images[0].Header!.Value.RequiredLibavbVersionMinor);
        }
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(2U)]
    [InlineData(4U)]
    public async Task VerifyAsync_NonzeroChildFlags_Rejects(uint flags)
    {
        using var fixture = new Fixture();
        await fixture.MetadataAsync("child", [], flags: flags);
        await fixture.MetadataAsync("vbmeta", [fixture.Chain("child", 1)]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("flags", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(2U)]
    public async Task VerifyAsync_DisabledRootFlags_DoNotBypassTamperedContent(uint flags)
    {
        using var fixture = new Fixture();
        await fixture.DataAsync("system", [0, 0]);
        await fixture.MetadataAsync("vbmeta", [Fixture.Hash("system", [1, 2])], flags: flags);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(flags, result.RootFlags);
        Assert.False(Assert.Single(result.Partitions).Content.ContentIsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_NestedChain_RequiresExplicitOfflineInspection(bool inspect)
    {
        using var fixture = new Fixture();
        await fixture.MetadataAsync("grandchild", []);
        await fixture.MetadataAsync("child", [fixture.Chain("grandchild", 2)]);
        await fixture.MetadataAsync("vbmeta", [fixture.Chain("child", 1)]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(nested: inspect), TestContext.Current.CancellationToken);

        Assert.Equal(inspect, result.Success);
        Assert.True(result.ContainsNestedChains);
        Assert.Equal(inspect ? 3 : 2, result.Images.Count);
    }

    [Fact]
    public async Task VerifyAsync_CycleThroughAlias_RejectsAndReleasesFiles()
    {
        using var fixture = new Fixture();
        await fixture.MetadataAsync("child", [fixture.Chain("alias", 2)]);
        await fixture.MetadataAsync("vbmeta", [fixture.Chain("child", 1)]);
        fixture.Paths["alias"] = fixture.Paths["vbmeta"];

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(nested: true), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cycle", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
        foreach (var path in fixture.Paths.Values.Distinct())
        {
            using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(reopened.CanWrite);
        }
    }

    [Fact]
    public async Task VerifyAsync_ExcessiveDepth_StopsAtConfiguredBoundary()
    {
        using var fixture = new Fixture();
        for (var index = 16; index >= 0; index--)
        {
            await fixture.MetadataAsync("node" + index, index == 16 ? [] : [fixture.Chain("node" + (index + 1), (uint)(index + 1))]);
        }

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(root: "node0", nested: true), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(16, result.Images.Count);
        Assert.Contains("depth", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task VerifyAsync_ReusedRollbackSlot_RejectsConflictingGraph()
    {
        using var fixture = new Fixture();
        await fixture.MetadataAsync("first", []);
        await fixture.MetadataAsync("second", []);
        await fixture.MetadataAsync("vbmeta", [fixture.Chain("first", 1), fixture.Chain("second", 1)]);

        var result = await AvbChainVerifier.VerifyAsync(fixture.Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("reused", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task VerifyAsync_CancelledBeforeTraversal_ThrowsWithoutOpeningImages()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var options = new AvbChainVerifyOptions { RootPartitionName = "missing", PartitionPaths = new Dictionary<string, string>() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AvbChainVerifier.VerifyAsync(options, cancellation.Token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private string _root => _temp.Path;
        private readonly RSA _key = SigningTestKeys.Create();
        public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);

        public AvbChainVerifyOptions Options(string root = "vbmeta", string suffix = "", bool trust = true, bool nested = false,
            IReadOnlyDictionary<uint, ulong>? rollback = null) => new()
            {
                RootPartitionName = root,
                PartitionPaths = Paths,
                SlotSuffix = suffix,
                InspectNestedChains = nested,
                TrustedRootPublicKey = trust ? AvbRsaPublicKeyCodec.Export(_key) : null,
                MinimumRollbackIndexes = rollback
            };

        public AvbChainPartitionDescriptor Chain(string name, uint slot, uint flags = 0)
            => new() { PartitionName = name, RollbackIndexLocation = slot, PublicKey = AvbRsaPublicKeyCodec.Export(_key), Flags = flags };

        public static AvbHashDescriptor Hash(string name, byte[] data, uint flags = 0)
            => new() { PartitionName = name, ImageSize = (ulong)data.Length, Salt = [], Digest = SHA256.HashData(data), Flags = flags };

        public async Task DataAsync(string name, byte[] bytes)
        {
            var path = Path.Combine(_root, name + ".img");
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            Paths[name] = path;
        }

        public async Task MetadataAsync(string name, object[] descriptors, ulong rollback = 7, uint location = 0, uint flags = 0, bool alternate = false)
        {
            var header = new byte[256];
            "AVB0"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(112, 8), rollback);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(120, 4), flags);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(124, 4), location);
            var template = Path.Combine(_root, "template.img");
            var output = Path.Combine(_root, name + ".img");
            await File.WriteAllBytesAsync(template, header, TestContext.Current.CancellationToken);
            using var otherKey = alternate ? SigningTestKeys.Create(alternate: true) : null;
            var privateKey = otherKey ?? _key;
            var result = await AvbSigner.SignVbmetaAsync(template, privateKey, output, descriptors, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage);
            Paths[name] = output;
        }

        public async Task SimpleGraphAsync(bool alternateChild = false)
        {
            byte[] data = [4, 5, 6, 7];
            await DataAsync("system", data);
            await MetadataAsync("vbmeta_system", [Hash("system", data)], location: 29, alternate: alternateChild);
            await MetadataAsync("vbmeta", [Chain("vbmeta_system", 2)], rollback: 9);
        }

        public async Task FooterAsync(string name, byte[] data)
        {
            var input = Path.Combine(_root, name + ".raw");
            var output = Path.Combine(_root, name + ".img");
            await File.WriteAllBytesAsync(input, data, TestContext.Current.CancellationToken);
            var privateKey = _key;
            var result = await AvbSigner.AddHashFooterAsync(input, name, privateKey, output, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage);
            Paths[name] = output;
        }

        public async Task SparseAsync(string name, bool corruptCrc = false)
        {
            var path = Paths[name] + ".sparse";
            using (var sparse = SparseReader.FromRawFile(Paths[name], blockSize: 4))
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                SparseWriter.WriteToStream(sparse, output, includeCrc: true);
                await output.FlushAsync(TestContext.Current.CancellationToken);
            }
            if (corruptCrc)
            {
                await using var output = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                output.Position = output.Length - 1;
                var value = output.ReadByte();
                output.Position--;
                output.WriteByte((byte)(value ^ 1));
            }
            Paths[name] = path;
        }

        public void Dispose()
        {
            _key.Dispose();
            _temp.Dispose();
        }
    }
}