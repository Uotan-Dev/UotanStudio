using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.Programmer;

public sealed class EdlProgrammerFolderScannerTests
{
    private const string ConventionName = "007b30e100000000_9ad772705cab4511_fhprg.bin";
    private const string ConventionHardwareHex = "007b30e100000000";
    private const string ConventionHashPrefix = "9ad772705cab4511";
    private const string SecondName = "00b830e110002900_cc3153a80293939b_fhprg_peek.bin";
    private const string ThirdName = "00cf00e100000000_99c60886e90d4bb0_fhprg.elf";
    private const string FirstBlob = "first-programmer-blob";
    private const string SecondBlob = "second-programmer-blob";
    private const string ThirdBlob = "third-programmer-blob";
    private const string PaddedUpperHexReply =
        "D41D8CD98F00B204E9800998ECF8427E00000000000000000000000000000000";

    [Theory]
    [InlineData("007b30e100000000.bin", "OEM public-key-hash prefix is missing")]
    [InlineData("007b30e10000000g_9ad772705cab4511_fhprg.bin", "not a hexadecimal digit")]
    [InlineData("07b30e10000000_9ad772705cab4511_fhprg.bin", "exactly 16 hexadecimal characters")]
    [InlineData("001060e10042004f_99c60886e90d4bb0c_fhprg.bin", "exactly 16 hexadecimal characters")]
    [InlineData("0016F0E100000000_d9357db88795b5a8_fhprg.bin", "upper-case hexadecimal")]
    [InlineData("007b30e100000000_9AD772705CAB4511_fhprg.bin", "upper-case hexadecimal")]
    [InlineData("007b30e100000000_9ad772705cab4511_fhprg.txt", "programmer extensions")]
    [InlineData("007b30e100000000_9ad772705cab4511_a_b_c_d_e_f_g.bin", "segments the loader naming convention allows")]
    [InlineData("007b30e100000000__9ad772705cab4511_fhprg.bin", "empty segment at position 2")]
    [InlineData("vendor/007b30e100000000_9ad772705cab4511_fhprg.bin", "not a bare file name")]
    public void TryParseProgrammerName_NameOutsideConvention_RefusesWithNamedReason(string fileName, string expectedFragment)
    {
        var parsed = EdlProgrammerFolderScanner.TryParseProgrammerName(fileName, out var identity, out var refusalReason);

        Assert.False(parsed);
        Assert.Null(identity);
        Assert.NotNull(refusalReason);
        Assert.Contains(expectedFragment, refusalReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseProgrammerName_ConventionShapedName_ReturnsHardwareIdAndHashPrefixFields()
    {
        var parsed = EdlProgrammerFolderScanner.TryParseProgrammerName(ConventionName, out var identity, out var refusalReason);

        Assert.True(parsed);
        Assert.Null(refusalReason);
        Assert.NotNull(identity);
        Assert.Equal(ConventionName, identity.FileName);
        Assert.Equal(Convert.ToUInt64(ConventionHardwareHex, 16), identity.HardwareId);
        Assert.Equal("007b30e1", identity.ChipIdentifierHex);
        Assert.Equal("00000000", identity.DeviceIdentifierHex);
        Assert.Equal(ConventionHashPrefix, identity.OemPublicKeyHashPrefixHex);
    }

    [Fact]
    public void TryParseProgrammerName_SwappedHexadecimalFields_KeepsFirstSegmentAsHardwareId()
    {
        var swapped = $"{ConventionHashPrefix}_{ConventionHardwareHex}_fhprg.bin";

        var parsed = EdlProgrammerFolderScanner.TryParseProgrammerName(swapped, out var identity, out var refusalReason);

        Assert.True(parsed);
        Assert.Null(refusalReason);
        Assert.NotNull(identity);
        Assert.Equal(Convert.ToUInt64(ConventionHashPrefix, 16), identity.HardwareId);
        Assert.Equal(ConventionHardwareHex, identity.OemPublicKeyHashPrefixHex);
    }

    [Fact]
    public async Task Scan_MixedFolder_ReturnsParseableRowsAndReportsEveryJunkFileAsSkipped()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), SecondBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "not a programmer", token);
            await File.WriteAllTextAsync(Path.Combine(root, "not_a_loader.bin"), "junk", token);
            Directory.CreateDirectory(Path.Combine(root, "vendor"));
            await File.WriteAllTextAsync(Path.Combine(root, "vendor", ThirdName), ThirdBlob, token);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            Assert.Equal(3, result.Descriptors.Count);
            Assert.Equal($"{ConventionName},{SecondName},{Path.Combine("vendor", ThirdName)}",
                string.Join(',', result.Descriptors.Select(row => RelativePath(root, row))));
            Assert.Equal(Encoding.UTF8.GetByteCount(FirstBlob)
                + Encoding.UTF8.GetByteCount(ThirdBlob)
                + Encoding.UTF8.GetByteCount(SecondBlob), result.TotalScannedBytes);
            var junk = Assert.Single(result.Skipped, skip => skip.FileName == "notes.txt");
            Assert.Contains("programmer extension", junk.Reason, StringComparison.Ordinal);
            var unparsed = Assert.Single(result.Skipped, skip => skip.FileName == "not_a_loader.bin");
            Assert.Contains("exactly 16 hexadecimal characters", unparsed.Reason, StringComparison.Ordinal);
            Assert.Equal(2, result.Skipped.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_EmptyDirectory_ReturnsEmptyResultWithoutReportingAnError()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            Assert.Empty(result.Descriptors);
            Assert.Empty(result.Skipped);
            Assert.Equal(0L, result.TotalScannedBytes);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_MissingDirectory_ThrowsDirectoryNotFoundBeforeOpeningAnyFile()
    {
        var token = TestContext.Current.CancellationToken;
        var missing = Path.Combine(Path.GetTempPath(), "uotan-edl-programmer-scan-missing-" + Guid.NewGuid().ToString("N"));

        var failure = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Task.Run(() => EdlProgrammerFolderScanner.Scan(missing, token), token));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_BlankOrNullDirectoryPath_ThrowsBeforeAnyHashing()
    {
        var token = TestContext.Current.CancellationToken;

        var absent = Path.Combine(Path.GetTempPath(), "uotan-edl-programmer-scan-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => EdlProgrammerFolderScanner.Scan("   ", token));
        Assert.Throws<ArgumentNullException>(() => EdlProgrammerFolderScanner.Scan(null!, token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EdlProgrammerFolderScanner.Scan(absent, 0, EdlProgrammerFolderScanner.DefaultMaximumProgrammerFileBytes, token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EdlProgrammerFolderScanner.Scan(absent, 4, 0, token));
    }

    [Fact]
    public async Task Scan_OversizedCandidate_RefusesWithoutOpeningAndNamesSizeBound()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, ConventionName);
            await File.WriteAllTextAsync(path, FirstBlob, token);
            await using var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = await Task.Run(
                () => EdlProgrammerFolderScanner.Scan(root, EdlProgrammerFolderScanner.DefaultMaximumCandidateFiles, 4, token),
                token);

            Assert.Empty(result.Descriptors);
            var skip = Assert.Single(result.Skipped);
            Assert.Equal(ConventionName, skip.FileName);
            Assert.Equal(Encoding.UTF8.GetByteCount(FirstBlob), new FileInfo(path).Length);
            Assert.Contains("exceeds the 4 byte per-file programmer limit", skip.Reason, StringComparison.Ordinal);
            Assert.Contains("refused without being opened", skip.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be read", skip.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_MoreCandidatesThanCeiling_RefusesExcessWithoutOpeningAndNamesCountBound()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), SecondBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, ThirdName), ThirdBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, 2, 1024, token), token);

            Assert.Equal(2, result.Descriptors.Count);
            Assert.Equal($"{ConventionName},{SecondName}",
                string.Join(',', result.Descriptors.Select(row => row.FileName)));
            var skip = Assert.Single(result.Skipped);
            Assert.Equal(ThirdName, skip.FileName);
            Assert.Contains("stopped at 2 candidate files", skip.Reason, StringComparison.Ordinal);
            Assert.Contains("refused without being opened", skip.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_TwoFilesWithIdenticalBytes_AdmitOneRowAndReportDuplicateHashAsSkipped()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), FirstBlob, token);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            var row = Assert.Single(result.Descriptors);
            Assert.Equal(ConventionName, row.FileName);
            Assert.Equal(Sha256Hex(FirstBlob), row.Sha256Hex);
            var skip = Assert.Single(result.Skipped);
            Assert.Equal(SecondName, skip.FileName);
            Assert.Contains(Sha256Hex(FirstBlob), skip.Reason, StringComparison.Ordinal);
            Assert.Contains("Duplicate programmer identity", skip.Reason, StringComparison.Ordinal);
            var accepted = Assert.Single(EdlProgrammerCatalog.FromDescriptors(result.Descriptors).Descriptors);
            Assert.Equal(Sha256Hex(FirstBlob), accepted.Sha256Hex);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CatalogContract_TwoDescriptorsSharingOneSha256_ThrowsArgumentException()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ConventionName), FirstBlob);
            var scanned = EdlProgrammerFolderScanner.Scan(root, token);
            var admitted = scanned.Descriptors.Single();
            var twin = admitted with { FileName = "copied_loader.bin" };

            Assert.Throws<ArgumentException>(() => EdlProgrammerCatalog.FromDescriptors([admitted, twin]));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_SameFolderTwice_ProduceIdenticallyOrderedRowsAndSkips()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), SecondBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "junk", token);

            var first = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);
            var second = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            Assert.Equal(
                string.Join(',', first.Descriptors.Select(row => row.FileName)),
                string.Join(',', second.Descriptors.Select(row => row.FileName)));
            Assert.Equal(
                string.Join(',', first.Skipped.Select(skip => skip.Path)),
                string.Join(',', second.Skipped.Select(skip => skip.Path)));
            Assert.Equal($"{ConventionName},{SecondName}",
                string.Join(',', first.Descriptors.Select(row => row.FileName)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_PreCancelledToken_SurfacesOperationCanceledWithoutScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Run(() => EdlProgrammerFolderScanner.Scan(root, cancellation.Token), CancellationToken.None));

            Assert.NotNull(failure);
            Assert.True(File.Exists(Path.Combine(root, ConventionName)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_UnreadableCandidate_ReportsReadFailureAndStillAdmitsSiblings()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var blocked = Path.Combine(root, ConventionName);
            await File.WriteAllTextAsync(blocked, FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), SecondBlob, token);
            await using var blocker = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                using var probe = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.Skip("This test host does not enforce file sharing modes, so no candidate can be made unreadable.");
            }
            catch (IOException)
            {
            }

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            var row = Assert.Single(result.Descriptors);
            Assert.Equal(SecondName, row.FileName);
            var skip = Assert.Single(result.Skipped);
            Assert.Equal(ConventionName, skip.FileName);
            Assert.Contains("could not be read for hashing", skip.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_SymbolicLinkCandidate_ReportsLinkAsSkippedWithoutFollowingIt()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var real = Path.Combine(root, ConventionName);
            await File.WriteAllTextAsync(real, FirstBlob, token);
            var link = Path.Combine(root, SecondName);
            try
            {
                File.CreateSymbolicLink(link, real);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
            {
                Assert.Skip("The reparse-point branch needs a symbolic link, which this test host refuses: " + ex.Message);
            }

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            var row = Assert.Single(result.Descriptors);
            Assert.Equal(ConventionName, row.FileName);
            var skip = Assert.Single(result.Skipped);
            Assert.Equal(SecondName, skip.FileName);
            Assert.Contains("reparse point", skip.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_AdmittedRow_CarriesProvenanceWhoseHashEqualsComputedSha256()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, ConventionName);
            await File.WriteAllTextAsync(path, FirstBlob, token);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            var row = Assert.Single(result.Descriptors);
            Assert.Equal(Sha256Hex(FirstBlob), row.Sha256Hex);
            Assert.NotNull(row.Provenance);
            Assert.Equal(row.Sha256Hex, row.Provenance.Sha256Hex);
            Assert.Equal(ResourceOrigin.UserSupplied, row.Provenance.Origin);
            Assert.Equal(path, row.Provenance.Source);
            Assert.Null(row.Provenance.LicenseTag);
            Assert.Equal(ConventionName, row.FileName);
            Assert.Equal(Encoding.UTF8.GetByteCount(FirstBlob), result.TotalScannedBytes);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_AdmittedRow_DeclaresHardwareIdOnlyAndLeavesHashAxisUndeclared()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);

            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);

            var row = Assert.Single(result.Descriptors);
            Assert.Equal(Convert.ToUInt64(ConventionHardwareHex, 16), row.HardwareId);
            Assert.Null(row.HardwareIdMask);
            Assert.Null(row.OemPublicKeyHashes);
            Assert.Null(row.SupportedStorageTypes);
            Assert.Null(row.SupportedSaharaImageIds);
            Assert.True(row.HasIdentityEvidence);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_CatalogFromScannedRows_MatchesDetectedHardwareIdUniquely()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, SecondName), SecondBlob, token);
            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);
            var catalog = EdlProgrammerCatalog.FromDescriptors(result.Descriptors);
            var identity = new SaharaDeviceIdentity(7, Convert.ToUInt64(ConventionHardwareHex, 16), 1, PaddedUpperHexReply);

            var selection = EdlRuleProgrammerSelector.Instance.Select(identity, catalog, token);

            Assert.Equal(EdlProgrammerMatchDecision.Matched, selection.Decision);
            Assert.True(selection.IsUploadEligible);
            Assert.Equal(ConventionName, selection.Descriptor?.FileName);
            Assert.Contains("declared HWID mask matched",
                selection.PerCandidateReasons.Single(reason => reason.Outcome == EdlProgrammerCandidateOutcome.Matched).Reason,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Scan_TwoScannedLoadersSharingHardwareId_SelectorReportsAmbiguous()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ConventionName), FirstBlob, token);
            await File.WriteAllTextAsync(Path.Combine(root, "007b30e100000000_d9357db88795b5a8_fhprg_peek.bin"), SecondBlob, token);
            var result = await Task.Run(() => EdlProgrammerFolderScanner.Scan(root, token), token);
            var catalog = EdlProgrammerCatalog.FromDescriptors(result.Descriptors);
            var identity = new SaharaDeviceIdentity(7, Convert.ToUInt64(ConventionHardwareHex, 16), 1, PaddedUpperHexReply);

            var selection = EdlRuleProgrammerSelector.Instance.Select(identity, catalog, token);

            Assert.Equal(2, catalog.Descriptors.Count);
            Assert.Equal(EdlProgrammerMatchDecision.Ambiguous, selection.Decision);
            Assert.Null(selection.Descriptor);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "uotan-edl-programmer-scan-" + Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RelativePath(string root, EdlProgrammerDescriptor row)
    {
        var source = row.Provenance?.Source ?? string.Empty;
        return source.StartsWith(root, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, source).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            : row.FileName;
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}