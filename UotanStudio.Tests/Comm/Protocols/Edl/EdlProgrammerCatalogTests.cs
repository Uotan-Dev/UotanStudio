using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlProgrammerCatalogTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string EmptyStreamSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Select_UniqueHardwareMatch_ReturnsMatchedWithPositiveReason()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([
            Descriptor("prog_a", HashA, hardwareId: 0x339),
            Descriptor("prog_b", HashB, hardwareId: 0x100)
        ]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x339, "d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"), catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.Matched, selection.Decision);
        Assert.Equal(HashA, selection.Descriptor?.Sha256Hex);
        Assert.True(selection.IsUploadEligible);
        Assert.Equal(2, selection.PerCandidateReasons.Count);
        Assert.Equal(EdlProgrammerCandidateOutcome.Rejected, selection.PerCandidateReasons[1].Outcome);
        Assert.Contains("HWID", selection.PerCandidateReasons[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_HardwareMaskMatchesBothCandidates_ReturnsAmbiguousWithoutDescriptor()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([
            Descriptor("prog_a", HashA, hardwareId: 0x0339, hardwareIdMask: 0x0FFF),
            Descriptor("prog_b", HashB, hardwareId: 0x0339, hardwareIdMask: 0xFFFF)
        ]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x0339, "d41d8cd9"), catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.Ambiguous, selection.Decision);
        Assert.Null(selection.Descriptor);
        Assert.All(selection.PerCandidateReasons, reason =>
            Assert.Equal(EdlProgrammerCandidateOutcome.Matched, reason.Outcome));
    }

    [Fact]
    public void Select_PublicKeyHashContradictsDescriptor_RejectsWithoutUploadEligibility()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([
            new EdlProgrammerDescriptor
            {
                FileName = "prog_a",
                Sha256Hex = HashA,
                OemPublicKeyHashes = new HashSet<string>(["1111111111111111111111111111111111111111111111111111111111111111"])
            }
        ]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x339, "D41D8CD98F00B204E9800998ECF8427E00000000000000000000000000000000"), catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NoMatch, selection.Decision);
        Assert.Equal(EdlProgrammerCandidateOutcome.Rejected, selection.PerCandidateReasons.Single().Outcome);
        Assert.Contains("OEM public-key hash", selection.PerCandidateReasons.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_PublicKeyHashMatchesCaseInsensitively_ReturnsMatched()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([
            new EdlProgrammerDescriptor
            {
                FileName = "prog_a",
                Sha256Hex = HashA,
                OemPublicKeyHashes = new HashSet<string>(["d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"])
            }
        ]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x339, "D41D8CD98F00B204E9800998ECF8427E00000000000000000000000000000000"), catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.Matched, selection.Decision);
    }

    [Fact]
    public void Select_MissingIdentity_ReturnsNeedsExplicitSelectionForEveryCandidate()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([Descriptor("prog_a", HashA, hardwareId: 0x339)]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(null, catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NeedsExplicitSelection, selection.Decision);
        Assert.Null(selection.Descriptor);
        Assert.Equal(EdlProgrammerCandidateOutcome.InsufficientEvidence, selection.PerCandidateReasons.Single().Outcome);
    }

    [Fact]
    public void Select_DescriptorWithoutIdentityConstraints_ReturnsNeedsExplicitSelection()
    {
        var catalog = EdlProgrammerCatalog.FromDescriptors([Descriptor("prog_a", HashA)]);

        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x339, "d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"), catalog,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NeedsExplicitSelection, selection.Decision);
        Assert.Equal(EdlProgrammerCandidateOutcome.InsufficientEvidence, selection.PerCandidateReasons.Single().Outcome);
    }

    [Fact]
    public void Select_CancellationRequested_ThrowsBeforeEvaluatingCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var catalog = EdlProgrammerCatalog.FromDescriptors([Descriptor("prog_a", HashA, hardwareId: 0x339)]);

        Assert.ThrowsAny<OperationCanceledException>(() => EdlRuleProgrammerSelector.Instance.Select(
            Identity(0x339, "d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"), catalog, cancellation.Token));
    }

    [Fact]
    public void FromDescriptors_DuplicateSha256_ThrowsBeforeAnySelection()
    {
        Assert.Throws<ArgumentException>(() => EdlProgrammerCatalog.FromDescriptors([
            Descriptor("first", HashA),
            Descriptor("second", HashA)
        ]));
    }

    [Fact]
    public void FromDescriptors_MalformedHash_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => EdlProgrammerCatalog.FromDescriptors([
            new EdlProgrammerDescriptor { FileName = "bad", Sha256Hex = "ABC" }
        ]));
    }

    [Fact]
    public void Validate_MaskWithoutHardwareId_ThrowsArgumentException()
    {
        var descriptor = new EdlProgrammerDescriptor { FileName = "prog", Sha256Hex = HashA, HardwareIdMask = 0xFFF };

        Assert.Throws<ArgumentException>(descriptor.Validate);
    }

    [Fact]
    public void Validate_ProvenanceHashContradiction_ThrowsArgumentException()
    {
        var descriptor = new EdlProgrammerDescriptor
        {
            FileName = "prog",
            Sha256Hex = HashA,
            Provenance = new UotanStudio.Comm.Protocols.Common.Evidence.ResourceProvenance
            {
                Origin = UotanStudio.Comm.Protocols.Common.Evidence.ResourceOrigin.UserSupplied,
                Sha256Hex = HashB
            }
        };

        Assert.Throws<ArgumentException>(descriptor.Validate);
    }

    [Fact]
    public async Task ComputeSha256Hex_EmptyStream_ReturnsStandardDigestAndRestoresPosition()
    {
        await using var stream = new MemoryStream();
        stream.Position = 0;

        var digest = EdlProgrammerHash.ComputeSha256Hex(stream, TestContext.Current.CancellationToken);

        Assert.Equal(EmptyStreamSha256, digest);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ComputeSha256Hex_NonSeekableStream_ThrowsBeforeConsumingBytes()
    {
        await using var stream = new NonSeekableStream("abc"u8.ToArray());

        Assert.Throws<ArgumentException>(() => EdlProgrammerHash.ComputeSha256Hex(stream,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.ConsumedBytes);
    }

    [Fact]
    public void Select_EmptyCatalog_ReturnsNeedsExplicitSelectionWithoutDescriptor()
    {
        var selection = EdlRuleProgrammerSelector.Instance.Select(Identity(0x339, "d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"),
            EdlProgrammerCatalog.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(EdlProgrammerMatchDecision.NeedsExplicitSelection, selection.Decision);
        Assert.Empty(selection.PerCandidateReasons);
    }

    private static SaharaDeviceIdentity Identity(ulong hardwareId, string publicKeyHash) =>
        new(42, hardwareId, 1, publicKeyHash);

    private static EdlProgrammerDescriptor Descriptor(string fileName, string sha256Hex, ulong? hardwareId = null,
        ulong? hardwareIdMask = null) => new()
        {
            FileName = fileName,
            Sha256Hex = sha256Hex,
            HardwareId = hardwareId,
            HardwareIdMask = hardwareIdMask
        };

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public int ConsumedBytes { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            ConsumedBytes += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}