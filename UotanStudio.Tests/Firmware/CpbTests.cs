using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Coolpad.Cpb;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class CpbTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public CpbTests() => FirmwareFormatRegistry.RegisterAll();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReadMetadataAsync_ValidContainer_ReportsHeaderAndImageDirectory()
    {
        var bytes = CpbContainerBuilder.Build(
        [
            ("system.img", BuildPayload(0x10, 4096)),
            ("boot.img", BuildPayload(0x20, 2048)),
        ]);
        var path = _temp.WriteBytes("firmware.cpb", bytes);

        var result = await new CpbFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("Coolpad CPB 1.7: 2 images", result.Summary);
        Assert.Equal<string>(["system.img", "boot.img"], result.Entries);
        Assert.Equal("1.7", result.Metadata["CpbVersion"]);
        Assert.Equal("8730L", result.Metadata["Model"]);
        Assert.Equal("P3", result.Metadata["HardwareVersion"]);
        Assert.Equal("4.3.066.P3.140417.8730L", result.Metadata["Version"]);
        Assert.Equal("D:\\image", result.Metadata["RomFrom"]);
        Assert.Equal("2", result.Metadata["FileCount"]);
        Assert.Equal("Verified", result.Metadata["ChecksumStatus"]);
    }

    [Fact]
    public async Task ReadMetadataAsync_MaskedProducerChecksum_IsStillVerified()
    {
        var bytes = CpbContainerBuilder.Build(
            [("system.img", BuildPayload(0x11, 512))],
            new CpbContainerOptions { StoreMaskedChecksum = true });
        var path = _temp.WriteBytes("masked.cpb", bytes);

        var result = await new CpbFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("Verified", result.Metadata["ChecksumStatus"]);
    }

    [Fact]
    public void Build_ChecksumLow16_MatchesBitwiseCcitt()
    {
        var payload = BuildPayload(0x33, 700);
        var bytes = CpbContainerBuilder.Build([("system.img", payload)], null, out var plainHeaders);
        var header = bytes.AsSpan(0, CpbContainerBuilder.HeaderSize).ToArray();
        var expected = CpbContainerBuilder.BitwiseCrc16(header[..(CpbContainerBuilder.HeaderSize - 4)], plainHeaders);

        var stored = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(CpbContainerBuilder.HeaderSize - 4));

        Assert.Equal((ushort)(expected + 0x07), (ushort)stored);
    }

    [Fact]
    public async Task UnpackAsync_ValidContainer_WritesDecodedImages()
    {
        var system = BuildPayload(0x40, 5000);
        var boot = BuildPayload(0x50, 33);
        var bytes = CpbContainerBuilder.Build([("system.img", system), ("boot.img", boot)]);
        var path = _temp.WriteBytes("firmware.cpb", bytes);
        var output = Path.Combine(_temp.Path, "out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal<string>(["system.img", "boot.img"], result.Entries);
        Assert.Equal(system, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
        Assert.Equal(boot, await File.ReadAllBytesAsync(Path.Combine(output, "boot.img"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("failed checksum", result.Summary, StringComparison.Ordinal);
        Assert.Equal([5000L, 33L], result.Components.Select(component => (long)component.SizeBytes).ToArray());
    }

    [Fact]
    public async Task UnpackAsync_CorruptPayload_KeepsFileAndReportsChecksumMismatch()
    {
        var payload = BuildPayload(0x60, 1024);
        var bytes = CpbContainerBuilder.Build([("system.img", payload)]);
        bytes[CpbContainerBuilder.HeaderSize + CpbContainerBuilder.ImageHeaderSize + 16] ^= 0xFF;
        var path = _temp.WriteBytes("corrupt.cpb", bytes);
        var output = Path.Combine(_temp.Path, "corrupt-out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("1 image(s) failed checksum: system.img", result.Summary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task UnpackAsync_HeaderChecksumMismatch_FailsWithoutWritingFiles()
    {
        var bytes = CpbContainerBuilder.Build([("system.img", BuildPayload(0x70, 128))]);
        bytes[CpbContainerBuilder.HeaderSize - 4] ^= 0xFF;
        var path = _temp.WriteBytes("tampered.cpb", bytes);
        var output = Path.Combine(_temp.Path, "tampered-out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CPB checksum mismatch", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task UnpackAsync_TruncatedPayload_FailsAndLeavesNoStagingFile()
    {
        var bytes = CpbContainerBuilder.Build([("system.img", BuildPayload(0x80, 4096))]);
        var path = _temp.WriteBytes("truncated.cpb", bytes[..^64]);
        var output = Path.Combine(_temp.Path, "truncated-out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("outside the container", result.Summary, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(output) ? Directory.GetFiles(output) : []);
    }

    [Fact]
    public async Task UnpackAsync_SelectedEntries_ExtractsOnlyRequestedImages()
    {
        var bytes = CpbContainerBuilder.Build(
        [
            ("system.img", BuildPayload(0x90, 256)),
            ("boot.img", BuildPayload(0xA0, 256)),
            ("modem.img", BuildPayload(0xB0, 256)),
        ]);
        var path = _temp.WriteBytes("select.cpb", bytes);
        var output = Path.Combine(_temp.Path, "select-out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { SelectedEntries = ["boot.img"] }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal<string>(["boot.img"], result.Entries);
        Assert.Equal<string>(["boot.img"], Directory.GetFiles(output).Select(file => Path.GetFileName(file)!));
    }

    [Fact]
    public async Task UnpackAsync_UnsafeNames_AreSanitizedInsideOutputDirectory()
    {
        var bytes = CpbContainerBuilder.Build(
        [
            ("system.img", BuildPayload(0xC0, 64)),
            ("system.img", BuildPayload(0xC1, 64)),
            ("../../evil.bin", BuildPayload(0xC2, 64)),
            (string.Empty, BuildPayload(0xC3, 64)),
        ]);
        var path = _temp.WriteBytes("names.cpb", bytes);
        var output = Path.Combine(_temp.Path, "names-out");

        var result = await new CpbFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal<string>(["system.img", "1_system.img", "evil.bin", "image-3.bin"], result.Entries);
        Assert.Equal<string>([.. result.Entries.Order(StringComparer.Ordinal)],
            [.. Directory.GetFiles(output).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)]);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.bin")));
    }

    [Fact]
    public async Task UnpackAsync_CancelledBeforeStart_Throws()
    {
        var bytes = CpbContainerBuilder.Build([("system.img", BuildPayload(0xD0, 64))]);
        var path = _temp.WriteBytes("cancel.cpb", bytes);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CpbFormatHandler().UnpackAsync(path, Path.Combine(_temp.Path, "cancel-out"), null, source.Token));
    }

    [Theory]
    [InlineData(0x01, 0x07, true)]
    [InlineData(0x01, 0x06, false)]
    [InlineData(0x02, 0x00, false)]
    public void CanHandle_VersionBytes_DecideAcceptance(byte major, byte minor, bool expected)
    {
        var bytes = CpbContainerBuilder.Build(
            [("system.img", BuildPayload(0xE0, 64))],
            new CpbContainerOptions { VersionMajor = major, VersionMinor = minor });
        var path = _temp.WriteBytes("version.cpb", bytes);

        Assert.Equal(expected, new CpbFormatHandler().CanHandle(path));
    }

    [Fact]
    public void CanHandle_ForeignMagic_Rejects()
    {
        var bytes = CpbContainerBuilder.Build([("system.img", BuildPayload(0xF0, 64))]);
        "CQ"u8.CopyTo(bytes);
        var path = _temp.WriteBytes("foreign.cpb", bytes);

        Assert.False(new CpbFormatHandler().CanHandle(path));
    }

    [Fact]
    public async Task ReadMetadataAsync_TooShortFile_Fails()
    {
        var path = _temp.WriteBytes("short.cpb", "CP\u0001\u0007"u8.ToArray());

        var result = await new CpbFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("truncated", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectDetailed_CpbMagic_ReportsStrongMagic()
    {
        var bytes = CpbContainerBuilder.Build([("system.img", BuildPayload(0x13, 128))]);
        var path = _temp.WriteBytes("detect.cpb", bytes);

        var result = FormatDetector.DetectDetailed(path);

        Assert.NotNull(result);
        Assert.Equal("Coolpad CPB Package", result!.Handler.FormatName);
        Assert.Equal(DetectionConfidence.StrongMagic, result.Confidence);
        Assert.Contains("Coolpad CPB", result.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveKey_VendorCorrectionWords_ProducesTheDocumentedSeed()
    {
        var header = CpbContainerBuilder.Build([("system.img", BuildPayload(0x12, 16))])
            .AsSpan(0, CpbContainerBuilder.HeaderSize);

        Assert.Equal(0x00310000u, CpbFormat.DeriveKey(header));
    }

    [Fact]
    public void Decode_ObfuscatedOffsetAndSize_RoundTripsThroughItsOwnInverse()
    {
        var key = CpbFormat.DeriveKey(CpbContainerBuilder.Build([("system.img", BuildPayload(0x14, 16))]));

        var encoded = CpbFormat.Decode(0x12345678, 3, key);
        var decoded = CpbFormat.Decode(encoded, 3, key);

        Assert.Equal(0x12345678u, decoded);
        Assert.NotEqual(0x12345678u, encoded);
    }

    private static byte[] BuildPayload(byte seed, int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++)
        {
            payload[index] = (byte)(seed + index);
        }

        return payload;
    }
}