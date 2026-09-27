using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkLoaderProfileTests
{
    [Fact]
    public async Task FromFileAsync_ValidModernContainer_ParsesEntriesAndComputesSha256()
    {
        using var dir = new TempDirectory();
        var bytes = Container(
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)),
            Entry(0x07DE, 0x8A00, 0x0001, 1, Region(700, 8, 0x200000, 0, 2), Region(708, 16, 0x40000000, 12, 4)));
        var path = dir.WriteBytes("da.bin", bytes);

        var profile = await MtkLoaderProfile.FromFileAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), profile.Sha256Hex);
        Assert.Equal(path, profile.FilePath);
        Assert.Equal(2, profile.Entries.Count);
        Assert.Equal(MtkDaKind.V5, profile.Entries[0].Kind);
        Assert.Equal((ushort)0x07DE, profile.Entries[1].HardwareCode);
        Assert.Equal(MtkDaDirectoryLayout.Modern, profile.Container.Layout);
    }

    [Fact]
    public async Task SelectEntry_UniqueHardwareMatch_ReturnsSingleMatchWithBoundedRegionPair()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)),
            Entry(0x07DE, 0x8A00, 0x0001, 1, Region(700, 8, 0x200000, 0, 2), Region(708, 16, 0x40000000, 12, 4)));

        var selection = profile.SelectEntry(Device(0x0766));

        Assert.Equal(MtkProfileDecision.SingleMatch, selection.Decision);
        Assert.Equal(0, selection.EntryIndex);
        Assert.Equal(0, selection.StageOneRegionIndex);
        Assert.Equal(1, selection.StageTwoRegionIndex);
        Assert.Equal(profile.Sha256Hex, selection.Sha256Hex);
        var evidence = selection.ToEvidence();
        Assert.Equal("da-profile", evidence.Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence.Outcome);
        Assert.Equal(profile.Sha256Hex, evidence.ResourceSha256Hex);
    }

    [Fact]
    public async Task SelectEntry_HardwareMismatch_ReturnsNoMatchBeforeAnyUpload()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)));

        var selection = profile.SelectEntry(Device(0x07DE));

        Assert.Equal(MtkProfileDecision.NoMatch, selection.Decision);
        Assert.Null(selection.Descriptor);
        Assert.Equal(CommandOutcomeKind.Rejected, selection.ToEvidence().Outcome);
        Assert.Contains(selection.Reasons, reason => reason.Contains("0x07DE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectEntry_TwoEntriesMatchSameIdentity_ReturnsAmbiguous()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)),
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(700, 8, 0x200000, 0, 2), Region(708, 16, 0x40000000, 12, 4)));

        var selection = profile.SelectEntry(Device(0x0766));

        Assert.Equal(MtkProfileDecision.Ambiguous, selection.Decision);
        Assert.Null(selection.EntryIndex);
    }

    [Fact]
    public async Task SelectEntry_OnlyUnusableRegionGeometry_ReturnsNeedsExplicitSelection()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2,
                Region(600, 8, 0x200001, 0, 2),
                Region(608, 16, 0x40000002, 12, 4)));

        var selection = profile.SelectEntry(Device(0x0766));

        Assert.Equal(MtkProfileDecision.NeedsExplicitSelection, selection.Decision);
        Assert.Equal(CommandOutcomeKind.Unknown, selection.ToEvidence().Outcome);
        Assert.Contains(selection.Reasons, reason => reason.Contains("DA1/DA2 region pair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectEntry_NewerDeviceIdentity_DistinguishesExactFromMinimumRevisions()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0x0001, 1, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)));

        var exact = profile.SelectEntry(Device(0x0766, hardwareVersion: 0xCA01, softwareVersion: 2));
        var minimum = profile.SelectEntry(Device(0x0766, hardwareVersion: 0xCA01, softwareVersion: 2),
            MtkDaIdentityMatch.MinimumRevisions);

        Assert.Equal(MtkProfileDecision.NoMatch, exact.Decision);
        Assert.Equal(MtkProfileDecision.SingleMatch, minimum.Decision);
    }

    [Fact]
    public async Task FromFileAsync_EmptyFile_FailsStructuralValidation()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("empty.bin", []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MtkLoaderProfile.FromFileAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromFileAsync_TruncatedHeader_FailsStructuralValidation()
    {
        using var dir = new TempDirectory();
        var bytes = Container(Entry(0x0766, 0x8A00, 0xCA01, 2,
            Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)));
        var path = dir.WriteBytes("truncated.bin", bytes[..50]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            MtkLoaderProfile.FromFileAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromFileAsync_MissingFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => MtkLoaderProfile.FromFileAsync(
            Path.Combine(Path.GetTempPath(), $"uotan-missing-{Guid.NewGuid():N}.bin"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromFileAsync_PreCancelledToken_ThrowsWithoutParsing()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("da.bin", Container(Entry(0x0766, 0x8A00, 0xCA01, 2,
            Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MtkLoaderProfile.FromFileAsync(path, cancellation.Token));
    }

    [Fact]
    public async Task From_SingleMatchSelection_ProducesBootstrapOptionsWithSelectedIndices()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)));

        var options = MtkBootstrapOptions.From(profile, profile.SelectEntry(Device(0x0766)),
            MtkXFlashStorageKind.Emmc);

        Assert.Equal(profile.FilePath, options.LoaderPath);
        Assert.Equal(0, options.EntryIndex);
        Assert.Equal(0, options.StageOneRegionIndex);
        Assert.Equal(1, options.StageTwoRegionIndex);
        Assert.Equal(MtkDaRegionContent.WithoutSignatureSuffix, options.StageTwoContent);
        Assert.Equal(MtkDaIdentityMatch.Exact, options.IdentityMatch);
        Assert.Equal(MtkXFlashStorageKind.Emmc, options.StorageKind);
    }

    [Fact]
    public async Task From_NoMatchSelection_ThrowsWithoutProducingOptions()
    {
        using var dir = new TempDirectory();
        var profile = await ProfileAsync(dir,
            Entry(0x0766, 0x8A00, 0xCA01, 2, Region(600, 8, 0x200000, 0, 2), Region(608, 16, 0x40000000, 12, 4)));

        var selection = profile.SelectEntry(Device(0x07DE));

        var error = Assert.Throws<InvalidOperationException>(() =>
            MtkBootstrapOptions.From(profile, selection, MtkXFlashStorageKind.Emmc));
        Assert.Contains("NoMatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorRegionBounds_RejectOversizedUnalignedAndOverflowingShapes()
    {
        Assert.False(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 64u * 1024 * 1024 + 8, 0x200000, 0, 0)));
        Assert.False(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 9, 0x200000, 0, 0)));
        Assert.False(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 8, 0x200001, 0, 0)));
        Assert.False(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 8, 0xFFFFFFFC, 0, 0)));
        Assert.False(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 8, 0x200000, 0, 10)));
        Assert.False(MtkDaDescriptor.IsStageTwoUsable(new MtkDaRegion(4096, 0, 0x40000000, 0, 0)));
        Assert.True(MtkDaDescriptor.IsStageOneUsable(new MtkDaRegion(4096, 8, 0x200000, 0, 8)));
    }

    [Fact]
    public async Task FromFileAsync_ProvidedRealLoader_ParsesAndHashesOnlyWithoutDeviceIo()
    {
        var path = Environment.GetEnvironmentVariable("UOTAN_TEST_MTK_LOADER_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Assert.Skip("Requires a real MTK DA file provided via UOTAN_TEST_MTK_LOADER_PATH; "
                + "缺少该环境变量指向的真实 DA 文件时跳过（不连接设备）。");
        }

        var profile = await MtkLoaderProfile.FromFileAsync(path!, TestContext.Current.CancellationToken);

        Assert.NotEmpty(profile.Entries);
        Assert.Equal(64, profile.Sha256Hex.Length);
    }

    private static async Task<MtkLoaderProfile> ProfileAsync(TempDirectory dir, params DaEntrySpec[] entries)
    {
        var path = dir.WriteBytes("da.bin", Container(entries));
        return await MtkLoaderProfile.FromFileAsync(path, TestContext.Current.CancellationToken);
    }

    private static MtkDeviceInfo Device(ushort hardwareCode, ushort subCode = 0x8A00,
        ushort hardwareVersion = 0xCA01, ushort softwareVersion = 2) =>
        new(hardwareCode, subCode, hardwareVersion, softwareVersion, 0);

    private static byte[] Container(params DaEntrySpec[] entries)
    {
        var directoryEnd = 108L + entries.Length * 220;
        var length = directoryEnd;
        foreach (var entry in entries)
        {
            foreach (var region in entry.Regions)
            {
                length = Math.Max(length, (long)region.FileOffset + region.Length);
            }
        }

        var bytes = new byte[Math.Max(length, 1048)];
        "MTK_DOWNLOAD_AGENT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 0x22668899);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), (uint)entries.Length);
        for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
        {
            var entry = entries[entryIndex];
            var record = bytes.AsSpan(108 + entryIndex * 220, 220);
            ushort[] fixedPart = [0xDADA, entry.Hardware, entry.SubCode, entry.HardwareVersion,
                entry.SoftwareVersion, 0, 4096, 0, 0, (ushort)entry.Regions.Length];
            for (var word = 0; word < fixedPart.Length; word++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record[(word * 2)..], fixedPart[word]);
            }

            for (var regionIndex = 0; regionIndex < entry.Regions.Length; regionIndex++)
            {
                var data = record[(20 + regionIndex * 20)..];
                BinaryPrimitives.WriteUInt32LittleEndian(data, entry.Regions[regionIndex].FileOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(data[4..], entry.Regions[regionIndex].Length);
                BinaryPrimitives.WriteUInt32LittleEndian(data[8..], entry.Regions[regionIndex].LoadAddress);
                BinaryPrimitives.WriteUInt32LittleEndian(data[12..], entry.Regions[regionIndex].StartOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(data[16..], entry.Regions[regionIndex].SignatureLength);
            }
        }

        return bytes;
    }

    private static DaEntrySpec Entry(ushort hardware, ushort subCode, ushort hardwareVersion, ushort softwareVersion,
        params MtkDaRegion[] regions) => new(hardware, subCode, hardwareVersion, softwareVersion, regions);

    private static MtkDaRegion Region(uint fileOffset, uint length, uint loadAddress, uint startOffset, uint signature) =>
        new(fileOffset, length, loadAddress, startOffset, signature);

    private sealed record DaEntrySpec(ushort Hardware, ushort SubCode, ushort HardwareVersion,
        ushort SoftwareVersion, MtkDaRegion[] Regions);
}