using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class SelinuxPolicyParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-selinux-policy-" + Guid.NewGuid().ToString("N"));
    public static bool UpstreamSampleAvailable =>
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_SELINUX_POLICY_SAMPLE"));

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
        Assert.Equal("header-and-body-boundary-only", metadata.CapabilityStatus);
        Assert.Equal(32, metadata.BodyOffset);
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
    }

    [Fact(Skip = "Set UOTAN_SELINUX_POLICY_SAMPLE to a real SELinux binary policy sample.",
        SkipUnless = nameof(UpstreamSampleAvailable))]
    public async Task ReadMetadataAsync_UpstreamLibsepolSample_ReportsOnlyValidatedHeaderMetadata()
    {
        var metadata = await SelinuxPolicyParser.ReadMetadataAsync(
            Environment.GetEnvironmentVariable("UOTAN_SELINUX_POLICY_SAMPLE")!,
            TestContext.Current.CancellationToken);

        Assert.Equal("kernel", metadata.PolicyType);
        Assert.Equal((uint)32, metadata.Version);
        Assert.True(metadata.MlsEnabled);
        Assert.Equal((uint)8, metadata.SymbolTableCount);
        Assert.Equal((uint)9, metadata.ObjectContextTableCount);
        Assert.Equal(32, metadata.BodyOffset);
        Assert.Equal(1520, metadata.BodyLength);
        Assert.False(metadata.RuleEnumerationSupported);
        Assert.Equal("header-and-body-boundary-only", metadata.CapabilityStatus);
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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
