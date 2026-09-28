using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Rockchip;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class RockchipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-rk-" + Guid.NewGuid().ToString("N"));
    public RockchipTests() => Directory.CreateDirectory(_root);

    internal static byte[] BuildRkaf(string path, byte[] payload)
    {
        var image = new byte[4096 + 4];
        "RKAF"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)image.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(136), 1);
        Encoding.UTF8.GetBytes("boot").CopyTo(image, 140);
        Encoding.UTF8.GetBytes(path).CopyTo(image, 172);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(236), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(244), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(248), (uint)payload.Length);
        payload.CopyTo(image, 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4), RkCrc32(image.AsSpan(0, image.Length - 4)));
        return image;
    }

    internal static byte[] BuildRkfw(byte[] loader, byte[] image)
    {
        var data = new byte[102 + loader.Length + image.Length + 32];
        "RKFW"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 102);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), 0x08010000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), 0x70);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(25), 102);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(29), (uint)loader.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(33), (uint)(102 + loader.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(37), (uint)image.Length);
        loader.CopyTo(data, 102);
        image.CopyTo(data, 102 + loader.Length);
        var digest = Convert.ToHexStringLower(MD5.HashData(data.AsSpan(0, data.Length - 32)));
        Encoding.ASCII.GetBytes(digest).CopyTo(data, data.Length - 32);
        return data;
    }

    private static uint RkCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0U;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C10DB7U : crc << 1;
        }
        return crc;
    }

    [Fact]
    public async Task Rkfw_UnpacksStandardLoaderAndEmbeddedRkaf()
    {
        var loader = "BOOTloader"u8.ToArray();
        var rkaf = BuildRkaf("Image/boot.img", [1, 2, 3, 4, 5]);
        var path = Write(BuildRkfw(loader, rkaf));
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.IsType<RockchipFormatHandler>(handler);
        var result = await handler.UnpackAsync(path, Path.Combine(_root, "out"), null, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(loader, File.ReadAllBytes(Path.Combine(_root, "out", "BOOT")));
        Assert.Equal(rkaf, File.ReadAllBytes(Path.Combine(_root, "out", "embedded-update.img")));
    }

    [Fact]
    public void Rkaf_UsesEntryTableAndExactActualSize()
    {
        var payload = "partition payload"u8.ToArray();
        var path = Write(BuildRkaf("Image/boot.img", payload));
        Assert.Equal("Image/boot.img", Assert.Single(RockchipExtractor.List(path)).Name);
        Assert.Equal(1, RockchipExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "out", "Image", "boot.img")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Image/../../escape")]
    [InlineData("C:/escape")]
    public void Rkaf_RejectsEscapingPaths(string name)
    {
        var path = Write(BuildRkaf(name, [1]));
        Assert.Throws<InvalidDataException>(() => RockchipExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TruncatedRkfw_DoesNotReplaceExistingOutput()
    {
        var image = BuildRkfw("BOOTdata"u8.ToArray(), BuildRkaf("boot.img", [1, 2]));
        var path = Write(image[..^1]);
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "BOOT"), "preserved");
        Assert.Throws<InvalidDataException>(() => RockchipExtractor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(output, "BOOT")));
    }

    [Fact]
    public void Rkaf_RejectsTruncatedTable()
    {
        var image = BuildRkaf("boot.img", [1]);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(136), uint.MaxValue);
        var path = Write(image);
        Assert.Throws<InvalidDataException>(() => RockchipExtractor.List(path));
    }

    [Fact]
    public void OldPrivateMagic_IsRejected()
    {
        var image = BuildRkfw("BOOTdata"u8.ToArray(), BuildRkaf("boot.img", [1]));
        "Rkfw"u8.CopyTo(image);
        var path = Write(image);
        Assert.False(new RockchipFormatHandler().CanHandle(path));
        Assert.Throws<InvalidDataException>(() => RockchipExtractor.List(path));
    }

    [Fact]
    public void Rkaf_ParameterWrapperIsRemoved()
    {
        var wrapped = new byte[16];
        "PARM"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4), 4);
        "test"u8.CopyTo(wrapped.AsSpan(8));
        var image = BuildRkaf("parameter.txt", wrapped);
        image.AsSpan(140, 32).Clear();
        "parameter"u8.CopyTo(image.AsSpan(140));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4), RkCrc32(image.AsSpan(0, image.Length - 4)));
        var path = Write(image);
        RockchipExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("test", File.ReadAllText(Path.Combine(_root, "out", "parameter.txt")));
    }

    [Fact]
    public async Task Rkaf_PackDirectory_RoundTripsEntries()
    {
        var source = Path.Combine(_root, "rkaf-source");
        Directory.CreateDirectory(Path.Combine(source, "Image"));
        await File.WriteAllTextAsync(Path.Combine(source, "Image", "boot.img"), "boot payload",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "parameter.txt"), "test parameter",
            TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "packed-rkaf.img");

        var result = await new RockchipFormatHandler().PackAsync(source, image, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var output = Path.Combine(_root, "rkaf-out");
        RockchipExtractor.Extract(image, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("boot payload", await File.ReadAllTextAsync(Path.Combine(output, "Image", "boot.img"),
            TestContext.Current.CancellationToken));
        Assert.Equal("test parameter", await File.ReadAllTextAsync(Path.Combine(output, "parameter.txt"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rkfw_PackDirectory_RoundTripsLoaderAndEmbeddedImage()
    {
        var source = Path.Combine(_root, "rkfw-source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "BOOT"), "BOOTloader"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(source, "embedded-update.img"), BuildRkaf("boot.img", [7, 8, 9]),
            TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "packed-rkfw.img");

        var result = await new RockchipFormatHandler().PackAsync(source, image, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var output = Path.Combine(_root, "rkfw-out");
        RockchipExtractor.Extract(image, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("BOOTloader", await File.ReadAllTextAsync(Path.Combine(output, "BOOT"),
            TestContext.Current.CancellationToken));
        Assert.Equal(BuildRkaf("boot.img", [7, 8, 9]), await File.ReadAllBytesAsync(
            Path.Combine(output, "embedded-update.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rkfw_MetadataOptionsAndSavedHeader_RoundTripByteIdentically()
    {
        var source = Path.Combine(_root, "rkfw-metadata-source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "BOOT"), "BOOTmetadata"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(source, "embedded-update.img"), BuildRkaf("boot.img", [4, 5, 6]),
            TestContext.Current.CancellationToken);
        var original = Path.Combine(_root, "rkfw-metadata.img");
        var handler = new RockchipFormatHandler();

        var packed = await handler.PackAsync(source, original, new RockchipPackOptions
        {
            Chip = "RK3588",
            Version = "11.2.513",
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_731_031_994),
            Code = 0x02000000
        }, TestContext.Current.CancellationToken);

        Assert.True(packed.Success, packed.Summary);
        var preview = await handler.ReadMetadataAsync(original, TestContext.Current.CancellationToken);
        Assert.True(preview.Success, preview.Summary);
        Assert.Equal("RKFW", preview.Metadata["Container"]);
        Assert.Equal("11.2.513", preview.Metadata["RkfwVersion"]);
        Assert.Equal("0x02000000", preview.Metadata["MergeVersion"]);
        Assert.Equal("0x33353838", preview.Metadata["ChipId"]);
        Assert.Equal("2024-11-08 02:13:14", preview.Metadata["ReleaseTime"]);
        Assert.Equal("NotVerified", preview.Metadata["ChecksumStatus"]);
        Assert.Contains(preview.Components!, component => component.Name == "embedded-update.img/boot.img" && component.SizeBytes == 3);
        var bytes = await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken);
        Assert.Equal("8853"u8.ToArray(), bytes.AsSpan(0x15, 4).ToArray());
        Assert.Equal(11, bytes[9]);
        Assert.Equal(2, bytes[8]);
        Assert.Equal(513, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(0x02000000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10)));

        var extracted = Path.Combine(_root, "rkfw-metadata-out");
        RockchipExtractor.Extract(original, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(extracted, "rkfw-header.bin")));
        var rebuilt = Path.Combine(_root, "rkfw-metadata-rebuilt.img");
        var repacked = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);
        Assert.True(repacked.Success, repacked.Summary);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(rebuilt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rkfw_MetadataPreview_DoesNotValidatePayloadButExtractionDoes()
    {
        var image = BuildRkfw("BOOTloader"u8.ToArray(), BuildRkaf("boot.img", [1, 2, 3]));
        image[108] ^= 0x01;
        var path = Write(image);

        var preview = await new RockchipFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(preview.Success, preview.Summary);
        Assert.Equal("NotVerified", preview.Metadata["ChecksumStatus"]);
        Assert.Throws<InvalidDataException>(() => RockchipExtractor.List(path));
    }

    [Fact]
    public async Task Rkaf_MetadataOptionsAndSavedHeader_PreserveUnknownBytes()
    {
        var source = Path.Combine(_root, "rkaf-metadata-source");
        Directory.CreateDirectory(Path.Combine(source, "Image"));
        await File.WriteAllTextAsync(Path.Combine(source, "package-file"),
            "package-file package-file\nboot Image/boot.img\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "Image", "boot.img"), "metadata payload",
            TestContext.Current.CancellationToken);
        var original = Path.Combine(_root, "rkaf-metadata.img");
        var handler = new RockchipFormatHandler();

        var packed = await handler.PackAsync(source, original, new RockchipPackOptions
        {
            Model = "RK3566-demo",
            Manufacturer = "Uotan"
        }, TestContext.Current.CancellationToken);

        Assert.True(packed.Success, packed.Summary);
        var extracted = Path.Combine(_root, "rkaf-metadata-out");
        RockchipExtractor.Extract(original, extracted, cancellationToken: TestContext.Current.CancellationToken);
        var templatePath = Path.Combine(extracted, "rkaf-header.bin");
        var template = await File.ReadAllBytesAsync(templatePath, TestContext.Current.CancellationToken);
        template[0x25] = 0x48;
        template[0x26] = 0x01;
        await File.WriteAllBytesAsync(templatePath, template, TestContext.Current.CancellationToken);

        var rebuilt = Path.Combine(_root, "rkaf-metadata-rebuilt.img");
        var repacked = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);

        Assert.True(repacked.Success, repacked.Summary);
        var bytes = await File.ReadAllBytesAsync(rebuilt, TestContext.Current.CancellationToken);
        Assert.Equal("RK3566-demo", Encoding.UTF8.GetString(bytes.AsSpan(8, 11)));
        Assert.Equal("Uotan", Encoding.UTF8.GetString(bytes.AsSpan(72, 5)));
        Assert.Equal(0x48, bytes[0x25]);
        Assert.Equal(0x01, bytes[0x26]);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140 + 104)) * 2048u);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136)));
    }

    private string Write(byte[] image)
    {
        var path = Path.Combine(_root, "firmware.img");
        File.WriteAllBytes(path, image);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}