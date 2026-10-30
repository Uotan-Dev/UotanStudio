using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Rockchip;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Detection, metadata and extraction cases for RKBOOT/RKLOADER loader images, built as synthetic images with the
/// upstream header layout so every field is derived from the geometry rather than from a fixture.
/// <para>RKBOOT/RKLOADER loader 镜像的识别、元数据与解包用例；镜像按上游头部布局合成，每个字段都由几何推导而不是取自固定夹具。</para>
/// </summary>
public sealed class RockchipLoaderTests : IDisposable
{
    private const int HeaderSize = 102;
    private const int EntrySize = 57;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-rk-loader-" + Guid.NewGuid().ToString("N"));

    public RockchipLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed record Entry(string Name, byte[] Payload);

    private static byte[] BuildLoader(
        Entry[] stage471,
        Entry[] stage472,
        Entry[] loader,
        string tag = "BOOT",
        ushort year = 2024,
        uint chip = 0x33353838,
        uint version = 0x010B,
        byte signFlag = (byte)'S',
        byte rc4Flag = 1)
    {
        List<(Entry Entry, uint Type)> entries = [];
        entries.AddRange(stage471.Select(static entry => (entry, 1u)));
        entries.AddRange(stage472.Select(static entry => (entry, 2u)));
        entries.AddRange(loader.Select(static entry => (entry, 4u)));

        var tableBytes = entries.Count * EntrySize;
        var payloadBytes = entries.Sum(static item => item.Entry.Payload.Length);
        var image = new byte[HeaderSize + tableBytes + payloadBytes + 4];

        Encoding.ASCII.GetBytes(tag).CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(6), version);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(10), 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14), year);
        image[16] = 3;
        image[17] = 12;
        image[18] = 9;
        image[19] = 30;
        image[20] = 15;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(21), chip);
        image[43] = signFlag;
        image[44] = rc4Flag;

        var offset471 = HeaderSize;
        var offset472 = offset471 + (stage471.Length * EntrySize);
        var offsetLoader = offset472 + (stage472.Length * EntrySize);
        image[25] = (byte)stage471.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(26), (uint)offset471);
        image[30] = EntrySize;
        image[31] = (byte)stage472.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), (uint)offset472);
        image[36] = EntrySize;
        image[37] = (byte)loader.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(38), (uint)offsetLoader);
        image[42] = EntrySize;

        var dataOffset = HeaderSize + tableBytes;
        for (var index = 0; index < entries.Count; index++)
        {
            var (entry, type) = entries[index];
            var entryOffset = HeaderSize + (index * EntrySize);
            image[entryOffset] = EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 1), type);
            Encoding.Unicode.GetBytes(entry.Name).CopyTo(image, entryOffset + 5);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 45), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 49), (uint)entry.Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 53), type == 1 ? 1u : 0u);
            entry.Payload.CopyTo(image, dataOffset);
            dataOffset += entry.Payload.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        return image;
    }

    private static byte[] StandardLoader(string tag = "BOOT") => BuildLoader(
        [new Entry("ddr800", [1, 2, 3, 4])],
        [new Entry("usbplug", [5, 6, 7])],
        [new Entry("FlashData", [8, 9]), new Entry("FlashBoot", [10])],
        tag);

    private string Write(byte[] image, string name = "MiniLoaderAll.bin")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    private static string LocateVendorSample()
    {
        var sample = Environment.GetEnvironmentVariable("UOTAN_RK_LOADER_SAMPLE");
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
        {
            Assert.Skip("Set UOTAN_RK_LOADER_SAMPLE to a Rockchip RKBOOT/RKLOADER file (for example MiniLoaderAll.bin) to run this case.");
        }

        return sample!;
    }

    [Fact]
    public void Detect_LoaderHeader_SelectsLoaderHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        var path = Write(StandardLoader());

        Assert.IsType<RockchipLoaderFormatHandler>(FormatDetector.Detect(path));
    }

    [Fact]
    public void ResolveMode_LoaderImage_RoutesToFirmwarePanel()
    {
        FirmwareFormatRegistry.RegisterAll();
        var path = Write(StandardLoader());

        var header = FormatDetector.ReadHeader(path, DocumentFormatPolicy.ContentProbeLength);

        Assert.Equal(EditorOpenMode.Firmware, DocumentFormatPolicy.ResolveMode(path, header));
    }

    [Fact]
    public void CanHandle_PlainBinaryStartingWithBootTag_IsRejected()
    {
        var path = Write([.. "BOOT"u8, .. new byte[252]]);

        Assert.False(new RockchipLoaderFormatHandler().CanHandle(path));
    }

    [Fact]
    public void CanHandle_RkfwFirmware_IsRejected()
    {
        var path = Write([.. "RKFW"u8, .. new byte[252]], "update.rkfw");

        Assert.False(new RockchipLoaderFormatHandler().CanHandle(path));
    }

    [Fact]
    public async Task ReadMetadataAsync_BootLoader_ReportsHeaderAndEntryTables()
    {
        var path = Write(StandardLoader());
        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("BOOT", result.Metadata["LoaderTag"]);
        Assert.Equal("1.11", result.Metadata["LoaderVersion"]);
        Assert.Equal("1.3.0", result.Metadata["MergeVersion"]);
        Assert.Equal("2024-03-12 09:30:15", result.Metadata["ReleaseTime"]);
        Assert.Equal("3588", result.Metadata["SupportChip"]);
        Assert.Equal("True", result.Metadata["Signed"]);
        Assert.Equal("True", result.Metadata["Rc4Disabled"]);
        Assert.Equal(HeaderSize.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Metadata["HeaderSize"]);
        Assert.Equal("1", result.Metadata["EntryCount471"]);
        Assert.Equal("1", result.Metadata["EntryCount472"]);
        Assert.Equal("2", result.Metadata["EntryCountLoader"]);
        Assert.Equal("Verified", result.Metadata["ChecksumStatus"]);
        Assert.Equal(
            ["471_0_ddr800.bin", "472_0_usbplug.bin", "loader_0_FlashData.bin", "loader_1_FlashBoot.bin"],
            result.Components.Select(static component => component.Name));
        Assert.Equal([4L, 3L, 2L, 1L], result.Components.Select(static component => component.SizeBytes));
    }

    [Fact]
    public async Task ReadMetadataAsync_MiniLoaderTag_ReportsTrimmedTagAndPlainFlags()
    {
        var path = Write(StandardLoader("LDR "), "rk3399_loader.bin");
        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("LDR", result.Metadata["LoaderTag"]);
        Assert.Equal("True", result.Metadata["Signed"]);
        Assert.Equal("True", result.Metadata["Rc4Disabled"]);
    }

    [Fact]
    public async Task ReadMetadataAsync_Rc4FlagClear_ReportsRc4Enabled()
    {
        var path = Write(BuildLoader([new Entry("ddr", [1])], [new Entry("usb", [2])], [], signFlag: 0, rc4Flag: 0));
        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("False", result.Metadata["Signed"]);
        Assert.Equal("False", result.Metadata["Rc4Disabled"]);
    }

    [Fact]
    public async Task ReadMetadataAsync_InvalidCalendarFields_OmitsReleaseTime()
    {
        var image = BuildLoader([new Entry("ddr", [1])], [], []);
        image[16] = 13;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        var path = Write(image);

        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.False(result.Metadata.ContainsKey("ReleaseTime"));
    }

    [Fact]
    public async Task ReadMetadataAsync_CorruptedPayload_ReportsChecksumMismatch()
    {
        var image = StandardLoader();
        image[HeaderSize + (3 * EntrySize)] ^= 0xFF;
        var path = Write(image);

        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("Mismatch", result.Metadata["ChecksumStatus"]);
    }

    [Fact]
    public async Task ReadMetadataAsync_TruncatedHeader_Fails()
    {
        var image = StandardLoader();
        var path = Write(image[..(HeaderSize - 4)]);

        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public async Task ReadMetadataAsync_EntryPayloadOutsideImage_Fails()
    {
        var image = StandardLoader();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(HeaderSize + 49), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        var path = Write(image);

        var result = await new RockchipLoaderFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("outside the image", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackAsync_LoaderEntries_WritesDeclaredPayloads()
    {
        var path = Write(StandardLoader());
        var output = Path.Combine(_root, "out");

        var result = await new RockchipLoaderFormatHandler()
            .UnpackAsync(path, output, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(output, "471_0_ddr800.bin")));
        Assert.Equal([5, 6, 7], File.ReadAllBytes(Path.Combine(output, "472_0_usbplug.bin")));
        Assert.Equal([8, 9], File.ReadAllBytes(Path.Combine(output, "loader_0_FlashData.bin")));
        Assert.Equal([10], File.ReadAllBytes(Path.Combine(output, "loader_1_FlashBoot.bin")));
    }

    [Fact]
    public async Task UnpackAsync_SelectedEntry_WritesOnlyThatPayload()
    {
        var path = Write(StandardLoader());
        var output = Path.Combine(_root, "out");
        var options = new UnpackOptions { SelectedEntries = ["472_0_usbplug.bin"] };

        var result = await new RockchipLoaderFormatHandler()
            .UnpackAsync(path, output, options, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["472_0_usbplug.bin"], result.Entries);
        Assert.Equal([5, 6, 7], File.ReadAllBytes(Path.Combine(output, "472_0_usbplug.bin")));
        Assert.False(File.Exists(Path.Combine(output, "471_0_ddr800.bin")));
    }

    [Fact]
    public async Task UnpackAsync_ChecksumMismatch_WritesNoFiles()
    {
        var image = StandardLoader();
        image[HeaderSize + (3 * EntrySize)] ^= 0xFF;
        var path = Write(image);
        var output = Path.Combine(_root, "out");

        var result = await new RockchipLoaderFormatHandler()
            .UnpackAsync(path, output, null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("checksum mismatch", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task UnpackAsync_EntryAboveSizeCeiling_Fails()
    {
        var path = Write(StandardLoader());
        var options = new UnpackOptions { MaximumOutputBytes = 2 };

        var result = await new RockchipLoaderFormatHandler()
            .UnpackAsync(path, Path.Combine(_root, "out"), options, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("above the", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMetadataAsync_VendorLoaderSample_ReportsVerifiedChecksum()
    {
        var sample = LocateVendorSample();
        FirmwareFormatRegistry.RegisterAll();
        var handler = Assert.IsType<RockchipLoaderFormatHandler>(FormatDetector.Detect(sample));

        var result = await handler.ReadMetadataAsync(sample, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("Verified", result.Metadata["ChecksumStatus"]);
        Assert.NotEmpty(result.Components);
        Assert.NotEqual("0", result.Metadata["EntryCount471"]);
    }
}