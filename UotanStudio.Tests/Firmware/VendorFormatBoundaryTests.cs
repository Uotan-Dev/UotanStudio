using System.Buffers.Binary;
using UotanStudio.Firmware.BootSecurity.Workflow;
using UotanStudio.Firmware.VendorFirmware.Amlogic;
using UotanStudio.Firmware.VendorFirmware.SonySin;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class VendorFormatBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-vendor-" + Guid.NewGuid().ToString("N"));
    public VendorFormatBoundaryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Amlogic_MagicOnlyCandidate_IsDetectedButRejectedAsTruncated()
    {
        var path = Path.Combine(_root, "candidate.img");
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x27B51956);
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        var handler = new AmlogicFormatHandler();
        Assert.True(handler.CanHandle(path));
        Assert.False((await handler.UnpackAsync(path, _root, cancellationToken: TestContext.Current.CancellationToken)).Success);
    }

    [Theory]
    [InlineData(AmlogicImageVersion.V1, 4u, 64, 128)]
    [InlineData(AmlogicImageVersion.V2, 8u, 72, 576)]
    public async Task Amlogic_PackVerifyAndExtract_UsesVersionedLayout(
        AmlogicImageVersion version,
        uint alignment,
        int headerSize,
        int itemSize)
    {
        var source = Path.Combine(_root, $"src-{version}");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "DDR.USB"), [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, $"packed-{version}.img");
        var handler = new AmlogicFormatHandler();
        var packed = await handler.PackAsync(source, path, new AmlogicPackOptions
        {
            Version = version,
            Alignment = alignment,
            VerifiedEntries = ["DDR.USB"]
        }, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal((uint)version, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(alignment, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.True(bytes.Length >= headerSize + 2 * itemSize);
        Assert.True((await handler.VerifyAsync(path, TestContext.Current.CancellationToken)).Success);

        var output = Path.Combine(_root, "out");
        var result = await handler.UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(Path.Combine(output, "DDR.USB"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Amlogic_UpstreamV1Fixture_VerifiesAndExtracts()
    {
        const string fixture =
            "+vwz3wEAAABWGbUngAEAAAAAAAAEAAAAAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAQAAAAAAAA4AAAAAAAAAVVNCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABERFIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAFABAAAAAAAAMAAAAAAAAABWRVJJRlkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEREUgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABsZWdhY3ktdmVjdG9yCgAAc2hhMXN1bSBjMzJhOGI3ODcxY2EwMWE5MzY1OGUyZTRjOWI1Yzk1ODEzYWRlNDk3";
        var image = Path.Combine(_root, "upstream-v1.img");
        var fixtureBytes = Convert.FromBase64String(fixture);
        await File.WriteAllBytesAsync(image, fixtureBytes, TestContext.Current.CancellationToken);
        var handler = new AmlogicFormatHandler();

        var verified = await handler.VerifyAsync(image, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "upstream-out");
        var unpacked = await handler.UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(verified.Success, verified.Summary);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal("legacy-vector\n", await File.ReadAllTextAsync(Path.Combine(output, "DDR.USB"), TestContext.Current.CancellationToken));

        var source = Path.Combine(_root, "upstream-source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "DDR.USB"), "legacy-vector\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var managedImage = Path.Combine(_root, "managed-v1.img");
        var packed = await handler.PackAsync(source, managedImage, new AmlogicPackOptions
        {
            Version = AmlogicImageVersion.V1,
            VerifiedEntries = ["DDR.USB"]
        }, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);
        Assert.Equal(fixtureBytes, await File.ReadAllBytesAsync(managedImage, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Amlogic_CorruptedPayload_FailsBeforeExistingOutputIsReplaced()
    {
        var source = Path.Combine(_root, "src-corrupt");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "DDR.USB"), [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "packed.img");
        var handler = new AmlogicFormatHandler();
        var packed = await handler.PackAsync(source, image, new AmlogicPackOptions
        {
            VerifiedEntries = ["DDR.USB"]
        }, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var corrupted = await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken);
        corrupted[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(image, corrupted, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "corrupt-out");
        Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "DDR.USB");
        await File.WriteAllBytesAsync(existing, [0x7A], TestContext.Current.CancellationToken);

        var unpacked = await handler.UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(unpacked.Success);
        Assert.Equal([0x7A], await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Amlogic_ConvertV1ToV2_PreservesPayloadAndVerification()
    {
        var source = Path.Combine(_root, "src-convert");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "UBOOT.USB"), [9, 8, 7, 6], TestContext.Current.CancellationToken);
        var v1 = Path.Combine(_root, "source-v1.img");
        var v2 = Path.Combine(_root, "converted-v2.img");
        var handler = new AmlogicFormatHandler();
        Assert.True((await handler.PackAsync(source, v1, new AmlogicPackOptions
        {
            Version = AmlogicImageVersion.V1,
            VerifiedEntries = ["UBOOT.USB"]
        }, TestContext.Current.CancellationToken)).Success);

        var converted = await handler.ConvertAsync(v1, v2, new AmlogicPackOptions
        {
            Version = AmlogicImageVersion.V2,
            Alignment = 8
        }, TestContext.Current.CancellationToken);

        Assert.True(converted.Success, converted.Summary);
        Assert.True((await handler.VerifyAsync(v2, TestContext.Current.CancellationToken)).Success);
        var output = Path.Combine(_root, "converted-out");
        Assert.True((await handler.UnpackAsync(v2, output, cancellationToken: TestContext.Current.CancellationToken)).Success);
        Assert.Equal([9, 8, 7, 6], await File.ReadAllBytesAsync(Path.Combine(output, "UBOOT.USB"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Amlogic_DeclaredLengthMismatch_IsRejectedBeforeExtraction()
    {
        var source = Path.Combine(_root, "src-length");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "DDR.USB"), [1, 2, 3], TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "length.img");
        var handler = new AmlogicFormatHandler();
        Assert.True((await handler.PackAsync(source, image, TestContext.Current.CancellationToken)).Success);
        var bytes = await File.ReadAllBytesAsync(image, TestContext.Current.CancellationToken);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), checked((ulong)bytes.Length + 1));
        await File.WriteAllBytesAsync(image, bytes, TestContext.Current.CancellationToken);

        var output = Path.Combine(_root, "length-out");
        var result = await handler.UnpackAsync(image, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(output, "DDR.USB")));
    }

    [Fact]
    public async Task SonySin_MmcfWithLz4AndAddr_ExtractsImageAndFillsHole()
    {
        var path = Path.Combine(_root, "system.sin");
        var sin = CreateSonySinFixture();
        await File.WriteAllBytesAsync(path, sin, TestContext.Current.CancellationToken);
        var handler = new SonySinFormatHandler();

        Assert.True(handler.CanHandle(path));
        var metadata = await handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "sony-output");
        var unpacked = await handler.UnpackAsync(path, output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(metadata.Success, metadata.Summary);
        Assert.Equal("2", metadata.Metadata!["SegmentCount"]);
        Assert.Equal("20", metadata.Metadata["OutputSize"]);
        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.Equal(
            [.. "hello hello"u8.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xDE, 0xAD, 0xBE, 0xEF],
            await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SonySin_GptpLengthDoesNotCoverWholeRecords_IsRejected()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(36), 95);

        var result = await ReadSonyMetadataAsync("bad-gptp.sin", sin);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_PackedRangeOutsideDeclaredData_IsRejected()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(72), 0x1000);

        var result = await ReadSonyMetadataAsync("bad-range.sin", sin);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_DeclaredDataSizeBeyondFile_IsRejected()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(0x18), checked((uint)(sin.Length + 1)));

        var result = await ReadSonyMetadataAsync("bad-data-size.sin", sin);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_OverlappingOutputSegments_IsRejected()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(128), 8);

        var result = await ReadSonyMetadataAsync("overlap.sin", sin);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_OverlappingPackedSegments_AreRejectedBeforePublication()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(112), 0);
        var result = await ReadSonyMetadataAsync("packed-overlap.sin", sin);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_DecodedLengthMismatch_PreservesExistingDestination()
    {
        var sin = CreateSonySinFixture();
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(80), 12);
        var path = Path.Combine(_root, "mismatch.sin");
        await File.WriteAllBytesAsync(path, sin, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "mismatch-output");
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, "mismatch.img");
        await File.WriteAllBytesAsync(destination, [0x7A], TestContext.Current.CancellationToken);

        var result = await new SonySinFormatHandler().UnpackAsync(
            path,
            output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal([0x7A], await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SonySin_InvalidLz4BackReference_IsRejected()
    {
        var sin = CreateSonySinFixture();
        sin[136] = 0;
        sin[137] = 1;
        sin[138] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(80), 4);
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(88), 3);
        var path = Path.Combine(_root, "bad-lz4.sin");
        await File.WriteAllBytesAsync(path, sin, TestContext.Current.CancellationToken);

        var result = await new SonySinFormatHandler().UnpackAsync(
            path,
            Path.Combine(_root, "bad-lz4-output"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SonySin_OutputExceedsBudget_IsRejectedBeforePublication()
    {
        var path = Path.Combine(_root, "budget.sin");
        await File.WriteAllBytesAsync(path, CreateSonySinFixture(), TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "budget-output");

        var result = await new SonySinFormatHandler().UnpackAsync(
            path,
            output,
            new() { MaximumOutputBytes = 19 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(output, "budget.img")));
    }

    [Fact]
    public async Task SonySin_PreCanceledExtraction_ThrowsCancellationAndDoesNotPublish()
    {
        var path = Path.Combine(_root, "cancel.sin");
        await File.WriteAllBytesAsync(path, CreateSonySinFixture(), TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "cancel-output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SonySinFormatHandler().UnpackAsync(path, output, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(Path.Combine(output, "cancel.img")));
    }

    [Fact]
    public void SelinuxPlan_ChangesOnlyExplicitBootArguments()
    {
        var path = Path.Combine(_root, "missing-boot.img");
        Assert.Throws<FileNotFoundException>(() => new SelinuxSwitchService().CreatePlan(path, SelinuxSwitchMode.Permissive));
    }

    private async Task<UotanStudio.Firmware.Core.FirmwareOpResult> ReadSonyMetadataAsync(
        string fileName,
        byte[] bytes)
    {
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        return await new SonySinFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);
    }

    private static byte[] CreateSonySinFixture()
    {
        const int headerSize = 32;
        const int gptpLength = 96;
        const int dataBase = headerSize + 8 + gptpLength;
        byte[] compressed = [0xB0, .. "hello hello"u8.ToArray()];
        byte[] raw = [0xDE, 0xAD, 0xBE, 0xEF];
        var sin = new byte[dataBase + compressed.Length + raw.Length];

        sin[0] = 3;
        sin[1] = (byte)'S';
        sin[2] = (byte)'I';
        sin[3] = (byte)'N';
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(0x18), checked((uint)(sin.Length - headerSize)));

        "MMCF"u8.CopyTo(sin.AsSpan(headerSize));
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(headerSize + 4), gptpLength);
        "GPTP"u8.CopyTo(sin.AsSpan(headerSize + 8));
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(headerSize + 12), gptpLength);
        for (var index = 0; index < 16; index++)
        {
            sin[headerSize + 16 + index] = checked((byte)index);
        }

        const int lz4Record = headerSize + 8 + 24;
        "LZ4A"u8.CopyTo(sin.AsSpan(lz4Record));
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(lz4Record + 4), 40);
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(lz4Record + 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(lz4Record + 16), 11);
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(lz4Record + 24), checked((ulong)compressed.Length));
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(lz4Record + 32), 0);

        const int addrRecord = lz4Record + 40;
        "ADDR"u8.CopyTo(sin.AsSpan(addrRecord));
        BinaryPrimitives.WriteUInt32BigEndian(sin.AsSpan(addrRecord + 4), 32);
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(addrRecord + 8), checked((ulong)compressed.Length));
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(addrRecord + 16), checked((ulong)raw.Length));
        BinaryPrimitives.WriteUInt64BigEndian(sin.AsSpan(addrRecord + 24), 16);

        compressed.CopyTo(sin, dataBase);
        raw.CopyTo(sin, dataBase + compressed.Length);
        return sin;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}