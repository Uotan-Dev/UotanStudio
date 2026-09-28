using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux.Models;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class SelinuxPolicyParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-selinux-policy-" + Guid.NewGuid().ToString("N"));
    public static bool UpstreamSampleAvailable => RealSampleIndex.IsAvailable(RealSampleIndex.SelinuxPolicyVariable);

    public SelinuxPolicyParserTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadMetadataAsync_KernelHeader_ReturnsBoundedSectionMetadata()
    {
        var path = Path.Combine(_root, "sepolicy");
        await File.WriteAllBytesAsync(path, BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 1, 8, 7, [0xAA, 0xBB]),
            TestContext.Current.CancellationToken);

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("kernel", metadata.PolicyType);
        Assert.Equal((uint)30, metadata.Version);
        Assert.True(metadata.MlsEnabled);
        Assert.Equal((uint)8, metadata.SymbolTableCount);
        Assert.Equal((uint)7, metadata.ObjectContextTableCount);
        Assert.False(metadata.RulesAreMutable);
        Assert.True(metadata.HasBody);
        Assert.Equal(2, metadata.BodyLength);
        Assert.False(metadata.RuleEnumerationSupported);
        Assert.Equal("header-and-bounded-section-cursor", metadata.CapabilityStatus);
        Assert.Equal(32, metadata.BodyOffset);
        Assert.Equal("policycaps", metadata.Sections[0].Name);
        Assert.Equal(SelinuxSectionStatus.Truncated, metadata.Sections[0].Status);
        Assert.Equal(32, metadata.Sections[0].StartOffset);
        Assert.Equal(34, metadata.Sections[0].EndOffset);
        Assert.Equal(SelinuxSectionStatus.PhaseLimited, metadata.Sections[^1].Status);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(2, metadata.UnconsumedTailLength);
        AssertContiguousSections(metadata);
    }

    [Fact]
    public async Task ReadMetadataAsync_ModuleHeader_RecognizesSupportedModuleType()
    {
        await using var stream = new MemoryStream(
            BuildModulePolicy(moduleType: 2, version: 21, configuration: 0, body: [0x01]));

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal("module", metadata.PolicyType);
        Assert.Equal((uint)21, metadata.Version);
        Assert.False(metadata.MlsEnabled);
        Assert.Equal("module-name", metadata.Sections[0].Name);
        Assert.Equal(SelinuxSectionStatus.Truncated, metadata.Sections[0].Status);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(1, metadata.UnconsumedTailLength);
    }

    [Fact]
    public async Task ReadMetadataAsync_KernelPolicyCapabilities_ReportsConsumedSectionAndPhaseLimitedTail()
    {
        byte[] body = [(byte)64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC, 0xCC, 0xCC, 0xCC];
        await using var stream = new MemoryStream(
            BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 22, 1, 8, 7, body));

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal(2, metadata.Sections.Count);
        var policyCaps = metadata.Sections[0];
        Assert.Equal("policycaps", policyCaps.Name);
        Assert.Equal(SelinuxSectionStatus.Consumed, policyCaps.Status);
        Assert.Equal(32, policyCaps.StartOffset);
        Assert.Equal(44, policyCaps.EndOffset);
        Assert.Equal((uint)0, policyCaps.DeclaredCount);
        var tail = metadata.Sections[^1];
        Assert.Equal("symbol-tables-and-later", tail.Name);
        Assert.Equal(SelinuxSectionStatus.PhaseLimited, tail.Status);
        Assert.Equal(44, tail.StartOffset);
        Assert.Equal(48, tail.EndOffset);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(4, metadata.UnconsumedTailLength);
        AssertContiguousSections(metadata);
    }

    [Fact]
    public async Task ReadMetadataAsync_InvalidBitmapMapSize_RecordsRejectedSectionWithoutThrowing()
    {
        byte[] body = [(byte)65, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC, 0xCC];
        await using var stream = new MemoryStream(
            BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 22, 1, 8, 7, body));

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            stream, TestContext.Current.CancellationToken);

        var policyCaps = Assert.Single(metadata.Sections, section => section.Name == "policycaps");
        Assert.Equal(SelinuxSectionStatus.Rejected, policyCaps.Status);
        Assert.Equal(32, policyCaps.StartOffset);
        Assert.Equal(44, policyCaps.EndOffset);
        Assert.Equal((uint)0, policyCaps.DeclaredCount);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(14, metadata.UnconsumedTailLength);
    }

    [Fact]
    public async Task ReadMetadataAsync_HostileBitmapNodeCount_RecordsRejectedBeforeAllocatingNodes()
    {
        byte[] body = [(byte)64, 0, 0, 0, 128, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xCC, 0xCC];
        await using var stream = new MemoryStream(
            BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 22, 1, 8, 7, body));

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            stream, TestContext.Current.CancellationToken);

        var policyCaps = Assert.Single(metadata.Sections, section => section.Name == "policycaps");
        Assert.Equal(SelinuxSectionStatus.Rejected, policyCaps.Status);
        Assert.Equal(uint.MaxValue, policyCaps.DeclaredCount);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(14, metadata.UnconsumedTailLength);
    }

    [Fact]
    public async Task ReadMetadataAsync_ModuleNameVersionAndCapabilities_ReportsThreeConsumedSections()
    {
        var body = new List<byte>();
        WriteUInt32(body, 5);
        body.AddRange("demo\0"u8.ToArray());
        WriteUInt32(body, 4);
        body.AddRange("1.0\0"u8.ToArray());
        body.AddRange([(byte)64, 0, 0, 0, 64, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
        body.AddRange([0xCC, 0xCC]);

        await using var stream = new MemoryStream(
            BuildModulePolicy(moduleType: 2, version: 21, configuration: 0, body: [.. body]));

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal(4, metadata.Sections.Count);
        string[] names = [.. metadata.Sections.Select(section => section.Name)];
        Assert.Equal(new[] { "module-name", "module-version", "policycaps", "symbol-tables-and-later" }, names);
        Assert.All(metadata.Sections.Take(3), section =>
            Assert.Equal(SelinuxSectionStatus.Consumed, section.Status));
        Assert.Equal(43, metadata.Sections[0].StartOffset);
        Assert.Equal(52, metadata.Sections[0].EndOffset);
        Assert.Equal((uint)5, metadata.Sections[0].DeclaredCount);
        Assert.Equal(52, metadata.Sections[1].StartOffset);
        Assert.Equal(60, metadata.Sections[1].EndOffset);
        Assert.Equal((uint)4, metadata.Sections[1].DeclaredCount);
        Assert.Equal(60, metadata.Sections[2].StartOffset);
        Assert.Equal(84, metadata.Sections[2].EndOffset);
        Assert.Equal((uint)1, metadata.Sections[2].DeclaredCount);
        Assert.Equal(SelinuxSectionStatus.PhaseLimited, metadata.Sections[^1].Status);
        Assert.Equal(84, metadata.Sections[^1].StartOffset);
        Assert.Equal(86, metadata.Sections[^1].EndOffset);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(2, metadata.UnconsumedTailLength);
        AssertContiguousSections(metadata);
    }

    [Fact]
    public async Task ReadMetadataAsync_CancellationInsideBody_PropagatesWithoutReportingMetadata()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var stream = new CancellingReadStream(
            BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 22, 1, 8, 7,
                [(byte)64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC]),
            cancellation,
            cancelAfterReads: 7);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(stream, cancellation.Token));
    }

    [Fact(Skip = "Set UOTAN_SELINUX_POLICY_SAMPLE to a real SELinux binary policy sample.",
        SkipUnless = nameof(UpstreamSampleAvailable))]
    public async Task ReadMetadataAsync_UpstreamLibsepolSample_ReportsOnlyValidatedHeaderMetadata()
    {
        await RealSampleIndex.VerifyIdentityAsync(RealSampleIndex.GetPath(RealSampleIndex.SelinuxPolicyVariable),
            RealSampleIndex.SelinuxPolicyVariable, RealSampleIndex.SelinuxPolicySha256,
            TestContext.Current.CancellationToken);

        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            RealSampleIndex.GetPath(RealSampleIndex.SelinuxPolicyVariable),
            TestContext.Current.CancellationToken);

        Assert.Equal("kernel", metadata.PolicyType);
        Assert.Equal((uint)32, metadata.Version);
        Assert.True(metadata.MlsEnabled);
        Assert.Equal((uint)8, metadata.SymbolTableCount);
        Assert.Equal((uint)9, metadata.ObjectContextTableCount);
        Assert.Equal(32, metadata.BodyOffset);
        Assert.Equal(1520, metadata.BodyLength);
        Assert.False(metadata.RuleEnumerationSupported);
        Assert.Equal("header-and-bounded-section-cursor", metadata.CapabilityStatus);
        Assert.Equal("policycaps", metadata.Sections[0].Name);
        Assert.Equal(SelinuxSectionStatus.Consumed, metadata.Sections[0].Status);
        Assert.Equal(32, metadata.Sections[0].StartOffset);
        Assert.Equal(44, metadata.Sections[0].EndOffset);
        Assert.Equal((uint)0, metadata.Sections[0].DeclaredCount);
        Assert.Equal("permissive-map", metadata.Sections[1].Name);
        Assert.Equal(SelinuxSectionStatus.Consumed, metadata.Sections[1].Status);
        Assert.Equal(44, metadata.Sections[1].StartOffset);
        Assert.Equal(56, metadata.Sections[1].EndOffset);
        Assert.Equal((uint)0, metadata.Sections[1].DeclaredCount);
        Assert.Equal("symbol-tables-and-later", metadata.Sections[^1].Name);
        Assert.Equal(SelinuxSectionStatus.PhaseLimited, metadata.Sections[^1].Status);
        Assert.Equal(56, metadata.Sections[^1].StartOffset);
        Assert.Equal(1552, metadata.Sections[^1].EndOffset);
        Assert.False(metadata.BodyFullyConsumed);
        Assert.Equal(1496, metadata.UnconsumedTailLength);
        AssertContiguousSections(metadata);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65u)]
    public async Task ReadMetadataAsync_UnsupportedVersion_RejectsWithoutRuleMutation(uint version)
    {
        await using var stream = new MemoryStream(
            BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", version, 0, 0, 0));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_MalformedLengthAndSectionCount_Rejects()
    {
        var malformed = BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 0, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(4), 33);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(new MemoryStream(malformed), TestContext.Current.CancellationToken));

        var oversized = BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 0, 1025, 7);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(new MemoryStream(oversized), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_MismatchedCompatibilitySectionCounts_Rejects()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(
                new MemoryStream(BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 0, 8, 6)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_HeaderOnlyInput_RejectsWithoutClaimingRuleSupport()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(
                new MemoryStream(BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 0, 0, 0, [])),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_Cancellation_IsObservedBeforeReadingBody()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SelinuxPolicyParser.ReadMetadataAsync(
                new MemoryStream(BuildPolicy(SelinuxPolicyParser.SelinuxMagic, "SE Linux", 30, 0, 0, 0)),
                cancellation.Token));
    }

    [Fact]
    public async Task VerifyIdentityAsync_MismatchedPinnedDigest_FailsWithProvenanceInstruction()
    {
        var path = Path.Combine(_root, "identity-mismatch.bin");
        await File.WriteAllBytesAsync(path, "abc"u8.ToArray(), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RealSampleIndex.VerifyIdentityAsync(path, RealSampleIndex.SelinuxPolicyVariable,
                new string('0', 64), TestContext.Current.CancellationToken));

        Assert.Contains("re-confirm sample provenance", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RealSampleIndex.SelinuxPolicyVariable, exception.Message);
        Assert.Contains(path, exception.Message);
        Assert.Contains(new string('0', 64), exception.Message);
    }

    [Fact]
    public async Task VerifyIdentityAsync_KnownDigestVector_ReturnsUppercaseHexDigest()
    {
        var path = Path.Combine(_root, "identity-known.bin");
        await File.WriteAllBytesAsync(path, "abc"u8.ToArray(), TestContext.Current.CancellationToken);

        var digest = await RealSampleIndex.VerifyIdentityAsync(path, RealSampleIndex.SelinuxPolicyVariable,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", digest);

        var unpinned = await RealSampleIndex.VerifyIdentityAsync(path, RealSampleIndex.ElfFirehoseVariable,
            RealSampleIndex.ElfFirehoseSha256, TestContext.Current.CancellationToken);

        Assert.Equal(digest, unpinned);
    }

    private static void AssertContiguousSections(SelinuxPolicyMetadata metadata)
    {
        var bodyEnd = metadata.BodyOffset + metadata.BodyLength;
        var cursor = metadata.BodyOffset;
        foreach (var section in metadata.Sections)
        {
            Assert.Equal(cursor, section.StartOffset);
            Assert.InRange(section.StartOffset, metadata.BodyOffset, bodyEnd);
            if (section.EndOffset is { } end)
            {
                Assert.InRange(end, section.StartOffset, bodyEnd);
                cursor = end;
            }
        }
        Assert.Equal(bodyEnd, cursor);
    }

    private static byte[] BuildPolicy(
        uint magic, string target, uint version, uint configuration, uint symbols, uint objectContexts,
        byte[]? body = null)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, magic);
        var targetBytes = Encoding.ASCII.GetBytes(target);
        WriteUInt32(stream, (uint)targetBytes.Length);
        stream.Write(targetBytes);
        WriteUInt32(stream, version);
        WriteUInt32(stream, configuration);
        WriteUInt32(stream, symbols);
        WriteUInt32(stream, objectContexts);
        stream.Write(body ?? [0x00]);
        return stream.ToArray();
    }

    private static byte[] BuildModulePolicy(uint moduleType, uint version, uint configuration, byte[]? body = null)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, SelinuxPolicyParser.SelinuxModuleMagic);
        var target = Encoding.ASCII.GetBytes("SE Linux Module");
        WriteUInt32(stream, (uint)target.Length);
        stream.Write(target);
        WriteUInt32(stream, moduleType);
        WriteUInt32(stream, version);
        WriteUInt32(stream, configuration);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        stream.Write(body ?? [0x00]);
        return stream.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(List<byte> target, uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CancellingReadStream(
        byte[] data, CancellationTokenSource cancellation, int cancelAfterReads) : MemoryStream(data)
    {
        private int _reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) > cancelAfterReads)
            {
                cancellation.Cancel();
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}