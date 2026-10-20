using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Hash.Xxh32;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pins the EROFS feature declaration channel: an explicit declaration is the authority for both superblock
/// feature words, a layout option may not demand a bit the declaration omits, and the upstream feature table
/// resolves alias names, read-only bits and the oldest mounting kernel.
/// <para>钉住 EROFS 特性声明通道：显式声明是两个超级块特性字的权威，布局选项不得要求声明缺失的位，上游特性表
/// 解析别名、只读位与最老可挂载内核。</para>
/// </summary>
public sealed class ErofsFeatureDeclarationTests
{
    private const int BlockSize = 4096;

    [Theory]
    [InlineData(ErofsFeature.Lz4ZeroPadding, 0x00000001u, true, false, false, false, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster, 0x00000003u, false, false, false, false, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.CompressionConfigs, 0x00000003u, false, false, false, false, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster | ErofsFeature.Ztailpacking,
        0x00000013u, false, true, false, false, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster | ErofsFeature.Fragments | ErofsFeature.Dedupe,
        0x00000023u, false, false, true, false, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster | ErofsFeature.Metabox,
        0x00000103u, false, false, false, true, 0)]
    [InlineData(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster | ErofsFeature.DeviceTable,
        0x0000000Bu, false, false, false, false, 1)]
    public void Build_ExplicitDeclaration_WritesExactlyTheDeclaredIncompatWord(ErofsFeature declared,
        uint expectedIncompat, bool legacy, bool compressedTailPacking, bool fragments, bool metabox, int extraDevices)
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x53, 20 * 1024).ToArray();
        var tail = Enumerable.Repeat((byte)0x41, 1000).ToArray();
        source.WriteBytes("data.bin", payload);
        source.WriteBytes("tail.bin", tail);
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = legacy,
            CompressedTailPacking = compressedTailPacking,
            Fragments = fragments,
            Dedup = fragments,
            Metabox = metabox,
            ExtraDeviceCount = extraDevices,
            ExtraDeviceImages = DeviceDescriptors(extraDevices),
            Features = Declare(declared),
        };

        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(expectedIncompat, superblock.FeatureIncompat);
        Assert.Equal(ErofsSuperblock.CompatSbChecksum, superblock.FeatureCompat);
        Assert.True(options.FeaturesAuthoritative);
        Assert.Equal(expectedIncompat, options.Features.IncompatBits);
        Assert.Equal(0u, options.AddedIncompatBits);
        Assert.Equal(0u, options.DroppedCompatBits);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        Assert.Equal(tail, parser.ReadFileBytes("tail.bin"));
    }

    [Fact]
    public void Build_DeclaredZtailpackingWithoutInlineTail_KeepsBitAndLeavesLayoutUnchanged()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x41, 4 * BlockSize).ToArray();
        source.WriteBytes("whole.bin", payload);
        var withoutBit = Declare(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster);
        var withBit = withoutBit.With(ErofsFeature.Ztailpacking);

        var without = BuildWholeBlockImage(source.Path, withoutBit);
        var with = BuildWholeBlockImage(source.Path, withBit);

        Assert.Equal(0x00000003u, ErofsSuperblock.FromImage(without).FeatureIncompat);
        Assert.Equal(0x00000013u, ErofsSuperblock.FromImage(with).FeatureIncompat);
        var reference = (byte[])without.Clone();
        with.AsSpan(ErofsSuperblock.SuperblockOffset + 4, 4)
            .CopyTo(reference.AsSpan(ErofsSuperblock.SuperblockOffset + 4, 4));
        with.AsSpan(ErofsSuperblock.SuperblockOffset + 80, 4)
            .CopyTo(reference.AsSpan(ErofsSuperblock.SuperblockOffset + 80, 4));
        Assert.Equal(with, reference);
        using var parser = new ErofsParser(with);
        Assert.Equal(payload, parser.ReadFileBytes("whole.bin"));
    }

    [Fact]
    public void Build_MetaboxLayoutWithUndeclaredFeature_ThrowsNamingMetabox()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 12 * 1024).ToArray());
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Compress = false,
            Metabox = true,
            Features = ErofsFeatureSet.FromWords(0, 0),
        };

        var error = Assert.Throws<NotSupportedException>(() => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("metabox", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ErofsOptions.Metabox), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DeviceTableLayoutWithUndeclaredFeature_ThrowsNamingDeviceTable()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 12 * 1024).ToArray());
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            ExtraDeviceCount = 1,
            ExtraDeviceImages = DeviceDescriptors(1),
            Features = Declare(ErofsFeature.Lz4ZeroPadding | ErofsFeature.BigPcluster),
        };

        var error = Assert.Throws<NotSupportedException>(() => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("device_table", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ErofsOptions.ExtraDeviceCount), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DeclaredFortyEightBit_WritesWideBlockCountAndRootNid()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 40 * 1024).ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseExtentIndexes = ErofsTriStateFeature.Enabled,
            Features = Declare(ErofsFeature.Lz4ZeroPadding).With(ErofsFeature.FortyEightBit),
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureIncompat & ErofsSuperblock.Incompat48Bit) != 0);
        Assert.Equal(superblock.Blocks, (uint)superblock.BlockCount64);
        Assert.Equal(superblock.BlocksHi, checked((ushort)(superblock.BlockCount64 >> 32)));
        Assert.Equal(superblock.RootNid8b, superblock.RootNid64);
    }

    [Fact]
    public void Build_DeclaredFortyEightBitWithNoWideField_Throws()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x42, 8 * 1024).ToArray());
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Features = Declare(ErofsFeature.Lz4ZeroPadding).With(ErofsFeature.FortyEightBit),
        };

        var error = Assert.Throws<NotSupportedException>(() => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("48bit", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ErofsOptions.UseExtentIndexes), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DeclaredXattrPrefixesWithNoPrefixTable_Throws()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x42, 8 * 1024).ToArray());
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Features = Declare(ErofsFeature.Lz4ZeroPadding).With(ErofsFeature.XattrPrefixes),
        };

        var error = Assert.Throws<NotSupportedException>(() => new ErofsBuilder().Build(source.Path, options));

        Assert.Contains("xattr_prefixes", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ErofsOptions.SourceXattrPrefixes), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ErofsOptions.IshareXattrPrefix), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LongXattrName_WritesPrefixTableThatReadsBackTheFullName()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x42, 1024).ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["data.bin"] =
                [
                    new ErofsXattrEntry { Name = "vendor.example.long", Value = [0x01, 0x02] },
                ],
            },
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureIncompat & ErofsSuperblock.IncompatXattrPrefixes) != 0);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatPlainXattrPfx) != 0);
        Assert.True(superblock.XattrPrefixCount > 0);

        var bytes = image.ToArray();
        using var parser = new ErofsParser(new MemoryStream(bytes));
        var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith("data.bin", StringComparison.Ordinal));
        var xattrs = file.Xattrs ?? throw new Xunit.Sdk.XunitException("The rebuilt file carries no xattrs.");
        Assert.Contains(xattrs, x => x.Name == "vendor.example.long" && x.Value.SequenceEqual(new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public void Build_IshareXattrs_PacksRepeatedSetsIntoSharedAreaThatReadsBack()
    {
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", Enumerable.Repeat((byte)0x11, 512).ToArray());
        source.WriteBytes("b.bin", Enumerable.Repeat((byte)0x22, 512).ToArray());
        IReadOnlyList<ErofsXattrEntry> shared =
        [
            new ErofsXattrEntry { Name = "user.role", Value = "system"u8.ToArray() },
        ];
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            IshareXattrs = true,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["a.bin"] = shared,
                ["b.bin"] = shared,
            },
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.NotEqual(0u, superblock.XattrBlkAddr);

        using var parser = new ErofsParser(new MemoryStream(image));
        var files = parser.EnumerateFiles();
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            var file = files.Single(entry => entry.Path.EndsWith(name, StringComparison.Ordinal));
            var xattrs = file.Xattrs ?? throw new Xunit.Sdk.XunitException($"{name} carries no xattrs.");
            var role = Assert.Single(xattrs, x => x.Name == "user.role");
            Assert.Equal("system", System.Text.Encoding.UTF8.GetString(role.Value));
        }
    }

    [Fact]
    public void Build_SharedEaInMetabox_HostsTheSharedAreaInTheMetaboxContainer()
    {
        using var source = new TempDirectory();
        source.WriteBytes("a.bin", Enumerable.Repeat((byte)0x11, 512).ToArray());
        source.WriteBytes("b.bin", Enumerable.Repeat((byte)0x22, 512).ToArray());
        IReadOnlyList<ErofsXattrEntry> shared =
        [
            new ErofsXattrEntry { Name = "user.role", Value = "system"u8.ToArray() },
        ];
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Metabox = true,
            MetaboxRequired = true,
            IshareXattrs = true,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["a.bin"] = shared,
                ["b.bin"] = shared,
            },
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox) != 0,
            "a metabox build must advertise that the shared area lives in the container it wrote.");
        Assert.NotEqual(0u, superblock.XattrBlkAddr);
        Assert.NotEqual(0u, superblock.MetaBoxNid);

        using var parser = new ErofsParser(new MemoryStream(image));
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith(name, StringComparison.Ordinal));
            var role = Assert.Single(file.Xattrs ?? [], x => x.Name == "user.role");
            Assert.Equal("system", System.Text.Encoding.UTF8.GetString(role.Value));
        }
    }

    [Fact]
    public void Build_IshareFingerprintPrefix_WritesDigestAndNamesThePrefixInTheSuperblock()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x77, 3000).ToArray();
        source.WriteBytes("data.bin", payload);
        const string prefix = "erofs.fingerprint.v1";
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            IshareXattrs = true,
            IshareXattrPrefix = prefix,
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatIshareXattrs) != 0);
        Assert.True(superblock.XattrPrefixCount > 0);
        Assert.True(superblock.IshareXattrPrefixId < superblock.XattrPrefixCount);
        Assert.Equal(0, superblock.IshareXattrPrefixId & ErofsXattr.LongPrefixFlag);

        using var parser = new ErofsParser(new MemoryStream(image));
        var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith("data.bin", StringComparison.Ordinal));
        var fingerprint = Assert.Single(file.Xattrs ?? [], x => x.Name == prefix);
        var expected = new byte[7 + 32];
        "sha256:"u8.CopyTo(expected);
        System.Security.Cryptography.SHA256.HashData(payload).CopyTo(expected, 7);
        Assert.Equal(expected, fingerprint.Value);
    }

    [Fact]
    public void Build_XattrNameFilter_WritesHeaderWordAndPublishesTheCompatBit()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x31, 4096).ToArray());
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Features = ErofsFeatureSet.Auto
                .With(ErofsFeature.Lz4ZeroPadding)
                .With(ErofsFeature.XattrFilter),
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["data.bin"] = [new ErofsXattrEntry { Name = "user.role", Value = "system"u8.ToArray() }],
            },
        });

        var superblock = ReadSuperblock(image);
        Assert.True((superblock.FeatureCompat & ErofsSuperblock.CompatXattrFilter) != 0);

        using var parser = new ErofsParser(new MemoryStream(image));
        var file = parser.EnumerateFiles().Single(entry => entry.Path.EndsWith("data.bin", StringComparison.Ordinal));
        Assert.Contains(file.Xattrs ?? [], x => x.Name == "user.role");
    }

    [Fact]
    public void ComputeNameFilter_MatchesUpstreamXxh32Vectors()
    {
        Assert.Equal(0x02CC5D05u, Xxh32.Compute([], 0));
        Assert.Equal(0x550D7456u, Xxh32.Compute("a"u8, 0));
        Assert.Equal(0x32D153FFu, Xxh32.Compute("abc"u8, 0));
    }

    private static ErofsSuperblock ReadSuperblock(byte[] image)
    {
        var superblock = new byte[ErofsSuperblock.SuperblockSize + ErofsSuperblock.ExtSlotSize];
        Array.Copy(image, ErofsSuperblock.SuperblockOffset, superblock, 0, superblock.Length);
        return ErofsSuperblock.FromBytes(superblock);
    }

    [Fact]
    public void Build_DeclaredCompatBitsBeyondLayout_WritesIntersectionAndReportsDroppedBit()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x63, 12 * 1024).ToArray();
        source.WriteBytes("data.bin", payload);
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            SourceMtimeFeature = true,
            Features = ErofsFeatureSet.Auto
                .With(ErofsFeature.Lz4ZeroPadding)
                .With(ErofsFeature.Mtime)
                .With(ErofsFeature.XattrFilter),
        };

        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(ErofsSuperblock.CompatMtime, superblock.FeatureCompat);
        Assert.True(superblock.HasCompatMtime);
        Assert.False(superblock.HasCompatChecksum);
        Assert.False(superblock.HasCompatXattrFilter);
        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(ErofsSuperblock.CompatSbChecksum | ErofsSuperblock.CompatXattrFilter, options.DroppedCompatBits);
        Assert.Equal(0u, options.AddedIncompatBits);
        Assert.True(options.FeaturesAuthoritative);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void With_FragmentsThenDedupe_SetsOneSharedBitAndPrintsThePairedName()
    {
        var declared = ErofsFeatureSet.Auto.With(ErofsFeature.Fragments).With(ErofsFeature.Dedupe);

        Assert.Equal(ErofsSuperblock.IncompatFragments, declared.IncompatBits);
        Assert.Equal(ErofsSuperblock.IncompatDedupe, ErofsFeatureTable.BitOf(ErofsFeature.Fragments));
        Assert.True(declared.Contains(ErofsFeature.Fragments));
        Assert.True(declared.Contains(ErofsFeature.Dedupe));
        Assert.Equal("fragments/dedupe", declared.ToString());
        var described = ErofsSuperblock.DescribeIncompat(declared.IncompatBits);
        Assert.Contains("fragments", described, StringComparison.Ordinal);
        Assert.Contains("dedupe", described, StringComparison.Ordinal);
        Assert.False(declared.Without(ErofsFeature.Fragments).Contains(ErofsFeature.Dedupe));
        Assert.Equal(0u, declared.Without(ErofsFeature.Dedupe).IncompatBits);
    }

    [Theory]
    [InlineData("big_pcluster", 0x00000000u, 0x00000001u, 0x00000000u, 0x00000003u)]
    [InlineData("BIG_PCLUSTER", 0x00000000u, 0x00000001u, 0x00000000u, 0x00000003u)]
    [InlineData("!big_pcluster", 0x00000000u, 0x00000003u, 0x00000000u, 0x00000001u)]
    [InlineData("ztailpacking,!fragments", 0x00000000u, 0x00000021u, 0x00000000u, 0x00000011u)]
    [InlineData("mtime,xattr_filter", 0x00000000u, 0x00000000u, 0x00000006u, 0x00000000u)]
    [InlineData("!mtime", 0x00000006u, 0x00000000u, 0x00000004u, 0x00000000u)]
    [InlineData("metabox,lz4_0padding", 0x00000000u, 0x00000000u, 0x00000000u, 0x00000101u)]
    public void TryApplyEdits_UpstreamTokens_SetAndClearBaselineBits(string text, uint baseCompat,
        uint baseIncompat, uint expectedCompat, uint expectedIncompat)
    {
        var applied = ErofsFeatureTable.TryApplyEdits(text, baseCompat, baseIncompat, out var compat,
            out var incompat, out var error);

        Assert.True(applied);
        Assert.Null(error);
        Assert.Equal(expectedCompat, compat);
        Assert.Equal(expectedIncompat, incompat);
    }

    [Fact]
    public void TryApplyEdits_AutoToken_ReturnsBaselineWords()
    {
        var applied = ErofsFeatureTable.TryApplyEdits("big_pcluster,auto", 0x00000006u, 0x00000021u, out var compat,
            out var incompat, out var error);

        Assert.True(applied);
        Assert.Null(error);
        Assert.Equal(0x00000006u, compat);
        Assert.Equal(0x00000021u, incompat);
    }

    [Theory]
    [InlineData("no_such_feature", "no_such_feature")]
    [InlineData("big_pcluster,!not_a_feature", "not_a_feature")]
    [InlineData("!", "!")]
    public void TryApplyEdits_UnknownToken_FailsAndReportsTheRejectedToken(string text, string expectedToken)
    {
        var applied = ErofsFeatureTable.TryApplyEdits(text, 0x00000000u, 0x00000001u, out var compat,
            out var incompat, out var error);

        Assert.False(applied);
        Assert.Contains(expectedToken, error ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0x00000000u, compat);
    }

    [Fact]
    public void MinimumKernel_MetaboxDeclaration_OverridesLowerBigPclusterFloor()
    {
        var declared = ErofsFeatureSet.Auto
            .With(ErofsFeature.Lz4ZeroPadding)
            .With(ErofsFeature.BigPcluster)
            .With(ErofsFeature.Metabox);

        Assert.Equal(6, declared.MinimumKernelMajor);
        Assert.Equal(17, declared.MinimumKernelMinor);
        Assert.Equal("6.17", ErofsFeatureTable.DescribeKernel(ErofsFeature.Metabox));
        var modern = ErofsFeatureSet.Auto.With(ErofsFeature.Lz4ZeroPadding).With(ErofsFeature.CompressionConfigs);
        Assert.Equal(5, modern.MinimumKernelMajor);
        Assert.Equal(13, modern.MinimumKernelMinor);
        Assert.Equal("5.13", ErofsFeatureTable.DescribeKernel(ErofsFeature.BigPcluster));
    }

    [Fact]
    public void MinimumKernel_CompatOnlyDeclaration_IsZeroAndAutoPrintsAuto()
    {
        var declared = ErofsFeatureSet.Auto.With(ErofsFeature.Mtime).With(ErofsFeature.XattrFilter);

        Assert.Equal(0, declared.MinimumKernelMajor);
        Assert.Equal(0, declared.MinimumKernelMinor);
        Assert.Equal("mtime,xattr_filter", declared.ToString());
        Assert.True(ErofsFeatureSet.Auto.IsAuto);
        Assert.Equal("auto", ErofsFeatureSet.Auto.ToString());
        Assert.Equal(0u, ErofsFeatureSet.Auto.IncompatBits);
        Assert.Equal("any", ErofsFeatureTable.DescribeKernel(ErofsFeature.Mtime));
        Assert.True(ErofsFeatureTable.IsCompat(ErofsFeature.XattrFilter));
        Assert.False(ErofsFeatureTable.IsCompat(ErofsFeature.Metabox));
    }

    private static ErofsFeatureSet Declare(ErofsFeature declared)
    {
        var set = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, 0u);
        foreach (var feature in Enum.GetValues<ErofsFeature>())
        {
            if (feature != ErofsFeature.None && declared.HasFlag(feature))
            {
                set = set.With(feature);
            }
        }

        return set;
    }

    private static IReadOnlyList<ErofsDeviceSpec>? DeviceDescriptors(int extraDevices)
    {
        IReadOnlyList<ErofsDeviceSpec>? descriptors = extraDevices > 0
            ? [new ErofsDeviceSpec("other.img", 1L << 20)]
            : null;
        return descriptors;
    }

    private static byte[] BuildWholeBlockImage(string directory, ErofsFeatureSet features)
        => new ErofsBuilder().Build(directory, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            BuildTime = 1_700_000_000UL,
            Uuid = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            Features = features,
        });
}