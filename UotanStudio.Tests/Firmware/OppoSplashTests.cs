using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class OppoSplashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-splash-" + Guid.NewGuid().ToString("N"));

    public OppoSplashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        return buffer.ToArray();
    }

    private static byte[] BuildSplash((string Name, byte[] Raw)[] images, uint width = 1080, uint height = 1920,
        bool ddph = true, uint? declaredCount = null, bool corruptPayloadEnd = false)
    {
        var blobs = images.Select(image => Gzip(image.Raw)).ToArray();
        var image = new byte[(int)(OppoSplashFormat.DataOffset + blobs.Sum(b => (long)b.Length))];
        if (ddph)
        {
            "DDPH"u8.CopyTo(image);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 0x11);
        }

        "SPLASH LOGO!"u8.CopyTo(image.AsSpan(0x4000));
        "OPP1234"u8.CopyTo(image.AsSpan(0x400C));
        "eng.oppo"u8.CopyTo(image.AsSpan(0x404C));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x410C), declaredCount ?? (uint)images.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4110), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4114), width);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4118), height);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x411C), 0);
        var payloadOffset = 0L;
        for (var index = 0; index < images.Length; index++)
        {
            var record = 0x4120 + index * 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record), (uint)payloadOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 4), (uint)images[index].Raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 8), (uint)blobs[index].Length);
            Encoding.ASCII.GetBytes(images[index].Name).CopyTo(image, record + 12);
            blobs[index].CopyTo(image, OppoSplashFormat.DataOffset + payloadOffset);
            payloadOffset += blobs[index].Length;
        }

        if (corruptPayloadEnd)
        {
            var last = image.Length - 1;
            image[last] ^= 0xFF;
            image[last] |= 3;
        }

        return image;
    }

    private static byte[] BmpBytes(int size)
    {
        var raw = new byte[size];
        "BM"u8.CopyTo(raw);
        for (var index = 2; index < raw.Length; index++)
        {
            raw[index] = (byte)(index * 7 & 0xFF);
        }

        return raw;
    }

    [Fact]
    public void Read_FullSplash_ReturnsDimensionsMetadataAndEntries()
    {
        using var stream = new MemoryStream(BuildSplash(
            [("logo.bmp", BmpBytes(600)), ("constantplash", BmpBytes(300))], width: 720, height: 1560));

        var splash = OppoSplashFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.True(splash.HasDdphHeader);
        Assert.Equal(0x11u, splash.DdphFlag);
        Assert.Equal("OPP1234", splash.Metadata[0]);
        Assert.Equal(720u, splash.Width);
        Assert.Equal(1560u, splash.Height);
        Assert.Equal(2, splash.Entries.Count);
        Assert.Equal("logo.bmp", splash.Entries[0].Name);
        Assert.Equal(600, splash.Entries[0].RealSize);
        Assert.True(splash.Entries[1].Offset >= splash.Entries[0].CompressedSize);
    }

    [Fact]
    public void Read_WithoutDdphHeader_ReportsAbsence()
    {
        using var stream = new MemoryStream(BuildSplash([("logo.bmp", BmpBytes(256))], ddph: false));

        var splash = OppoSplashFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.False(splash.HasDdphHeader);
    }

    [Theory]
    [InlineData("count-above-ceiling")]
    [InlineData("magic-missing")]
    [InlineData("payload-escapes-file")]
    [InlineData("file-too-small")]
    public void Read_MalformedVariants_RejectsWithInvalidData(string failure)
    {
        var image = failure switch
        {
            "count-above-ceiling" => BuildSplash([("logo.bmp", BmpBytes(64))], declaredCount: 200),
            "magic-missing" => BuildSplash([("logo.bmp", BmpBytes(64))]),
            "payload-escapes-file" => BuildSplash([("logo.bmp", BmpBytes(64))], corruptPayloadEnd: true),
            _ => new byte[4096]
        };
        if (failure == "magic-missing")
        {
            image[0x4000] = (byte)'X';
        }
        else if (failure == "payload-escapes-file")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4120 + 8), 100000);
        }

        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() => OppoSplashFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DecompressEntry_DeclaredRawSizeMismatch_ThrowsInvalidData()
    {
        var raw = BmpBytes(512);
        var image = BuildSplash([("logo.bmp", raw)]);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4120 + 4), 7);
        using var stream = new MemoryStream(image);
        var splash = OppoSplashFormat.Read(stream, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => OppoSplashFormat.DecompressEntry(stream, splash.Entries[0],
            output, 1 << 20, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanHandle_MagicAtFixedOffset_DecidesEvenForImgFiles()
    {
        var handler = new OppoSplashFormatHandler();
        var splash = Path.Combine(_root, "logo.img");
        await File.WriteAllBytesAsync(splash, BuildSplash([("logo.bmp", BmpBytes(64))]),
            TestContext.Current.CancellationToken);
        var foreign = Path.Combine(_root, "boot.img");
        await File.WriteAllBytesAsync(foreign, new byte[0x9000], TestContext.Current.CancellationToken);

        Assert.True(handler.CanHandle(splash));
        Assert.False(handler.CanHandle(foreign));
    }

    [Fact]
    public async Task UnpackAsync_TwoImages_WritesDecompressedPayloadsWithExtensions()
    {
        var logo = BmpBytes(600);
        var constant = BmpBytes(320);
        var path = Path.Combine(_root, "logo.img");
        await File.WriteAllBytesAsync(path, BuildSplash([("logo.bmp", logo), ("constantplash", constant)]),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");

        var result = await new OppoSplashFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(logo, await File.ReadAllBytesAsync(Path.Combine(output, "logo.bmp"), TestContext.Current.CancellationToken));
        Assert.Equal(constant, await File.ReadAllBytesAsync(Path.Combine(output, "constantplash.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(600, result.Components.Single(c => c.Name == "logo.bmp").SizeBytes);
    }

    [Fact]
    public async Task ReadMetadataAsync_ValidSplash_ReportsGeometryAndMetadataStrings()
    {
        var path = Path.Combine(_root, "meta.img");
        await File.WriteAllBytesAsync(path, BuildSplash([("logo.bmp", BmpBytes(128))], width: 1080, height: 2340),
            TestContext.Current.CancellationToken);

        var result = await new OppoSplashFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("1080", result.Metadata["Width"]);
        Assert.Equal("2340", result.Metadata["Height"]);
        Assert.Contains("OPP1234", result.Metadata["Metadata"]);
    }
}