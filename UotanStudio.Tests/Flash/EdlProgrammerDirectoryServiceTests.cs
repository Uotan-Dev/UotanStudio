using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class EdlProgrammerDirectoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "uotan-edl-programmer-dir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MatchCore_FolderHoldingNoConventionShapedFile_ReportsNoCandidatesAndNoDescriptor()
    {
        var folder = NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "no_separator.bin"), [4, 5, 6]);

        var result = EdlProgrammerDirectoryService.MatchCore(folder, Identity(0x1234ABCD00000001),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(EdlProgrammerMatchDecision.NeedsExplicitSelection, result.Decision);
        Assert.False(result.IsUploadEligible);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public void MatchCore_OneRowDeclaringTheDeviceHardwareId_ReportsTheUniqueMatch()
    {
        var folder = NewFolder();
        var name = WriteLoader(folder, "1234abcd00000001", "fedcba9876543210", "loader-one");
        var identity = Identity(0x1234ABCD00000001);

        var result = EdlProgrammerDirectoryService.MatchCore(folder, identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(EdlProgrammerMatchDecision.Matched, result.Decision);
        Assert.True(result.IsUploadEligible);
        Assert.Equal(name, result.Descriptor?.FileName);
    }

    [Fact]
    public void MatchCore_TwoRowsDeclaringTheSameHardwareId_ReportsAmbiguityAndWithholdsTheDescriptor()
    {
        var folder = NewFolder();
        WriteLoader(folder, "1234abcd00000001", "fedcba9876543210", "loader-one");
        WriteLoader(folder, "1234abcd00000001", "0123456789abcdef", "loader-two");

        var result = EdlProgrammerDirectoryService.MatchCore(folder, Identity(0x1234ABCD00000001),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(EdlProgrammerMatchDecision.Ambiguous, result.Decision);
        Assert.False(result.IsUploadEligible);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public void MatchCore_RowsDeclaringOtherHardwareIds_ReportNoMatch()
    {
        var folder = NewFolder();
        WriteLoader(folder, "00000000aaaaaaaa", "fedcba9876543210", "loader-other");
        WriteLoader(folder, "00000000bbbbbbbb", "fedcba9876543210", "loader-also-other");

        var result = EdlProgrammerDirectoryService.MatchCore(folder, Identity(0x1234ABCD00000001),
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NoMatch, result.Decision);
        Assert.False(result.IsUploadEligible);
        Assert.Null(result.Descriptor);
        Assert.Equal(2, result.CandidateReasons.Count);
        Assert.All(result.CandidateReasons, reason =>
            Assert.Equal(EdlProgrammerCandidateOutcome.Rejected, reason.Outcome));
    }

    [Fact]
    public void MatchCore_DeviceWithoutIdentity_NeverClaimsAMatchEvenWithExactlyOneRow()
    {
        var folder = NewFolder();
        WriteLoader(folder, "1234abcd00000001", "fedcba9876543210", "loader-one");

        var result = EdlProgrammerDirectoryService.MatchCore(folder, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NeedsExplicitSelection, result.Decision);
        Assert.False(result.IsUploadEligible);
        Assert.Null(result.Descriptor);
        Assert.Equal(EdlProgrammerCandidateOutcome.InsufficientEvidence,
            Assert.Single(result.CandidateReasons).Outcome);
    }

    [Fact]
    public void MatchCore_MatchedRow_CarriesTheHashTheScanMeasuredFromTheBytes()
    {
        var folder = NewFolder();
        var name = WriteLoader(folder, "1234abcd00000001", "fedcba9876543210", "loader-one");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, name))))
            .ToLowerInvariant();

        var result = EdlProgrammerDirectoryService.MatchCore(folder, Identity(0x1234ABCD00000001),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Descriptor?.Sha256Hex);
    }

    [Fact]
    public void MatchCore_RowWhoseBytesCollideWithAnotherRow_KeepsOneRowAndReportsTheDuplicateAsSkipped()
    {
        var folder = NewFolder();
        var first = WriteLoader(folder, "1234abcd00000001", "fedcba9876543210", "loader-one");
        var bytes = File.ReadAllBytes(Path.Combine(folder, first));
        File.WriteAllBytes(Path.Combine(folder, "1234abcd00000001_ffffffffffffffff_loader-two.bin"), bytes);

        var result = EdlProgrammerDirectoryService.MatchCore(folder, Identity(0x1234ABCD00000001),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(EdlProgrammerMatchDecision.Matched, result.Decision);
        Assert.Equal(first, result.Descriptor?.FileName);
    }

    [Fact]
    public void ScanAsync_FolderHoldingNoConventionShapedFile_ReportsEveryFileAsSkipped()
    {
        var folder = NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1, 2, 3]);

        var result = EdlProgrammerDirectoryService.ScanAsync(folder, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.False(result.IsUploadEligible);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public void ScanAsync_MissingFolder_RefusesBeforeAnyFileIsOpened()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() =>
            EdlProgrammerDirectoryService.ScanAsync(missing, TestContext.Current.CancellationToken));
    }

    private string NewFolder()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    private static string WriteLoader(string folder, string hardwareHex, string hashPrefixHex, string tag)
    {
        var name = $"{hardwareHex}_{hashPrefixHex}_{tag}.bin";
        File.WriteAllBytes(Path.Combine(folder, name),
            [.. System.Text.Encoding.ASCII.GetBytes(name).Select(character => (byte)character)]);
        return name;
    }

    private static SaharaDeviceIdentity Identity(ulong hardwareId)
        => new(0x1122334455667788, hardwareId, 2, "0000000000000000000000000000000000000000");
}