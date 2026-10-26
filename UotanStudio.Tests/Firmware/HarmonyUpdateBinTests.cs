using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.HarmonyOS;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class HarmonyUpdateBinTests
{
    private const string FormatName = "HarmonyOS update.bin";

    public HarmonyUpdateBinTests() => FirmwareFormatRegistry.RegisterAll();

    [Fact]
    public void Detect_BinExtensionPackage_RoutesToHandler()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])]));

        var handler = FormatDetector.Detect(path);
        var detailed = FormatDetector.DetectDetailed(path);

        Assert.Equal(FormatName, handler?.FormatName);
        Assert.NotNull(detailed);
        Assert.Equal(FormatName, detailed!.Handler.FormatName);
    }

    [Fact]
    public void Detect_ExtensionlessPackage_ValidatesStructure()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])]));

        var detailed = FormatDetector.DetectDetailed(path);

        Assert.NotNull(detailed);
        Assert.Equal(FormatName, detailed!.Handler.FormatName);
        Assert.Equal(DetectionConfidence.ValidatedStructure, detailed.Confidence);
    }

    [Fact]
    public void Detect_UpdateAppMagic_IsNotClaimed()
    {
        using var temp = new TempDirectory();
        var image = new byte[512];
        new byte[] { 0x55, 0xAA, 0x5A, 0xA5 }.CopyTo(image, 0);
        var path = temp.WriteBytes("update.app", image);

        var handler = FormatDetector.Detect(path);

        Assert.NotEqual(FormatName, handler?.FormatName);
    }

    [Fact]
    public void CanHandle_UnknownLayoutHint_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var bytes = HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2, [("system", 0, [1, 2, 3])]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x7F7F);
        var path = temp.WriteBytes("update.bin", bytes);

        var handler = new HarmonyUpdateBinFormatHandler();

        Assert.False(handler.CanHandle(path));
    }

    [Fact]
    public async Task ReadMetadata_L2Package_ReportsLayoutComponentsAndMetadata()
    {
        using var temp = new TempDirectory();
        byte[] systemPayload = [1, 2, 3, 4];
        byte[] boardPayload = Encoding.ASCII.GetBytes("HI3516");
        byte[] configPayload = [0x50, 0x4B];
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, systemPayload),
            ("board_list", 0, boardPayload),
            ("config", 1, configPayload)
        ]));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("L2", result.Summary, StringComparison.Ordinal);
        Assert.Equal(["system.img", "BOARD.list", "config.zip"], result.Entries);
        Assert.Equal("L2", result.Metadata["Layout"]);
        Assert.Equal("3", result.Metadata["ComponentCount"]);
        Assert.Equal("signature", result.Metadata["SignatureTlv"]);
        Assert.Equal((systemPayload.Length + boardPayload.Length + configPayload.Length).ToString(),
            result.Metadata["PayloadBytes"]);
        Assert.Equal(3, result.Components.Count);
        Assert.Equal(systemPayload.Length, result.Components[0].SizeBytes);
        Assert.Equal("BOARD.list", result.Components[1].Name);
        Assert.False(result.Metadata.ContainsKey("PackageVersion"));
    }

    [Fact]
    public async Task ReadMetadata_VersionListComponent_DecodesPackageVersion()
    {
        using var temp = new TempDirectory();
        var version = "OpenHarmony 3.2.6.1\nall/cn\u0000"u8.ToArray();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("version_list", 0, version)]));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["VERSION.mbn"], result.Entries);
        Assert.Equal("OpenHarmony 3.2.6.1 all/cn", result.Metadata["PackageVersion"]);
    }

    [Fact]
    public async Task ReadMetadata_HashInfoChain_ReportsHashAndSignature()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])], HarmonyUpdateBinSignatureKind.HashAndSignature));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("hash+signature", result.Metadata["SignatureTlv"]);
    }

    [Fact]
    public async Task ReadMetadata_L1Package_ReportsL1Layout()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L1,
            [("system", 0, [7, 8, 9])]));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("L1", result.Summary, StringComparison.Ordinal);
        Assert.Equal("L1", result.Metadata["Layout"]);
        Assert.Equal(["system.img"], result.Entries);
    }

    [Fact]
    public async Task Unpack_AllComponents_WritesReferenceOutputNames()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        byte[] systemPayload = [1, 2, 3, 4, 5];
        byte[] boardPayload = Encoding.ASCII.GetBytes("HI3516\nHI3518");
        byte[] zipPayload = [0x50, 0x4B, 0x03, 0x04];
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, systemPayload),
            ("board_list", 0, boardPayload),
            ("config", 1, zipPayload),
            ("empty", 0, [])
        ]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["system.img", "BOARD.list", "config.zip", "empty.img"], result.Entries);
        Assert.Equal(systemPayload, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"),
            TestContext.Current.CancellationToken));
        Assert.Equal(boardPayload, await File.ReadAllBytesAsync(Path.Combine(output, "BOARD.list"),
            TestContext.Current.CancellationToken));
        Assert.Equal(zipPayload, await File.ReadAllBytesAsync(Path.Combine(output, "config.zip"),
            TestContext.Current.CancellationToken));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(output, "empty.img"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unpack_L1Package_WritesComponent()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L1,
            [("system", 0, [9, 8, 7])]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(Path.Combine(output, "system.img"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unpack_MetadataOnly_PublishesRepackMetadataWithoutComponents()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])]));
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { MetadataOnly = true }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var metadata = RepackMetadata.Read(output, RepackMetadataContext.LocalDirectory);
        Assert.NotNull(metadata);
        Assert.Equal(FormatName, metadata!.FormatName);
        Assert.False(metadata.CanPackDirectory);
        Assert.False(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task Unpack_SelectedByNameOrOutputName_ExtractsOnlyRequested()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, [1]),
            ("vendor", 0, [2]),
            ("board_list", 0, Encoding.ASCII.GetBytes("HI3516"))
        ]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { SelectedEntries = ["system.img", "board_list"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["system.img", "BOARD.list"], result.Entries);
        Assert.True(File.Exists(Path.Combine(output, "system.img")));
        Assert.True(File.Exists(Path.Combine(output, "BOARD.list")));
        Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
    }

    [Fact]
    public async Task Unpack_UnknownSelection_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1])]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path,
            Path.Combine(temp.Path, "out"), new UnpackOptions { SelectedEntries = ["missing"] },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("missing", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpack_CorruptedDigestWithVerification_FailsWithoutPublishingFile()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var digests = new Dictionary<string, byte[]> { ["system"] = Enumerable.Repeat((byte)0xFF, 32).ToArray() };
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])], digests: digests));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { VerifyChecksums = true }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task Unpack_ZeroDigestWithVerification_Fails()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var digests = new Dictionary<string, byte[]> { ["system"] = new byte[32] };
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3])], digests: digests));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { VerifyChecksums = true }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no recorded SHA-256", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task Unpack_ExistingOutputWithoutOverwrite_FailsBeforeWritingOtherComponents()
    {
        using var temp = new TempDirectory();
        var output = Directory.CreateDirectory(Path.Combine(temp.Path, "out")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "system.img"), "keep",
            TestContext.Current.CancellationToken);
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, [1]),
            ("vendor", 0, [2])
        ]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { Overwrite = false }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(output, "system.img"),
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
    }

    [Fact]
    public async Task Unpack_OutputBudgetExceeded_FailsBeforeWriting()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, new byte[32])]));

        var result = await new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { MaximumOutputBytes = 8 }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("output budget", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "system.img")));
    }

    [Fact]
    public async Task Unpack_PreCancelled_ThrowsWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out");
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1])]));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HarmonyUpdateBinFormatHandler().UnpackAsync(path, output, null, source.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ReadMetadata_TruncatedPayload_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("system", 0, [1, 2, 3, 4])], truncateBytes: 2));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(new HarmonyUpdateBinFormatHandler().CanHandle(path));
    }

    [Fact]
    public async Task ReadMetadata_UnsafeComponentName_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
            [("../system", 0, [1])]));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("safe file name", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadata_DuplicateOutputName_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, [1]),
            ("SYSTEM", 0, [2])
        ]));

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("duplicated", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadata_UnknownMetadataTlv_Fails()
    {
        using var temp = new TempDirectory();
        var bytes = HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2, [("system", 0, [1])]);
        var tableLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(178));
        bytes[180 + tableLength + 16] = 0x09;
        var path = temp.WriteBytes("update.bin", bytes);

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not supported", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadata_InvalidTableLength_Fails()
    {
        using var temp = new TempDirectory();
        var bytes = HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2, [("system", 0, [1])]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(178), 3);
        var path = temp.WriteBytes("update.bin", bytes);

        var result = await new HarmonyUpdateBinFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}