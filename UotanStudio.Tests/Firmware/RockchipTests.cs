using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Rockchip;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class RockchipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-rk-" + Guid.NewGuid().ToString("N"));
    public RockchipTests() => Directory.CreateDirectory(_root);

    // Independent offsets from RKImage.h and rkflashtool/rkunpack.c.
    internal static byte[] BuildRkaf(string path, byte[] payload)
    {
        var image = new byte[512 + payload.Length];
        "RKAF"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)image.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(136), 1);
        Encoding.UTF8.GetBytes("boot").CopyTo(image, 140);
        Encoding.UTF8.GetBytes(path).CopyTo(image, 172);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(236), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(244), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(248), (uint)payload.Length);
        payload.CopyTo(image, 512);
        return image;
    }

    internal static byte[] BuildRkfw(byte[] loader, byte[] image)
    {
        var data = new byte[102 + loader.Length + image.Length];
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
        return data;
    }

    [Fact]
    public void NativeAfptoolImage_ExtractsBothContainerLayers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Firmware", "Files", "review-native-rkfw.img");
        var output = Path.Combine(_root, "native");
        RockchipExtractor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("BOOTIndependent loader fixture", File.ReadAllText(Path.Combine(output, "BOOT")));
        var embedded = Path.Combine(output, "embedded-update.img");
        RockchipExtractor.Extract(embedded, Path.Combine(output, "rkaf"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Independent Rockchip native fixture payload\n", File.ReadAllText(Path.Combine(output, "rkaf", "Image", "boot.img")));
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
        var path = Write(image);
        RockchipExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("test", File.ReadAllText(Path.Combine(_root, "out", "parameter.txt")));
    }

    private string Write(byte[] image)
    {
        var path = Path.Combine(_root, "firmware.img");
        File.WriteAllBytes(path, image);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}