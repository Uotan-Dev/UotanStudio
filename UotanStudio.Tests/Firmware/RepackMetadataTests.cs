using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services.Cli;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class RepackMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsWindows()
        ? Path.GetPathRoot(Environment.SystemDirectory)! : Path.GetTempPath(), "uotanstudio-test-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private string Source => Path.Combine(_root, "source");
    private string Extracted => Path.Combine(_root, "extracted");
    private string Image => Path.Combine(_root, "original.img");
    private string Output => Path.Combine(_root, "rebuilt.img");

    public RepackMetadataTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "hello.txt"), "firmware metadata test");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task FileSystemDirectories_RecordFormatAndExcludeMetadataFromRepack(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        var packed = await handler.PackAsync(Source, Image, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        var unpacked = await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpacked.Success, unpacked.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal(format, metadata.FormatName);
        Assert.Equal("original.img", metadata.SourceFileName);
        Assert.Equal(new FileInfo(Image).Length, metadata.SourceLength);
        Assert.Equal("4096", metadata.PackingOptions["BlockSize"]);
        Assert.True(metadata.CanPackDirectory);
        Assert.Equal(format, metadata.Configuration["fileSystem"]!.GetValue<string>());
        var superblock = metadata.Configuration["superblock"]!.AsObject();
        using (var source = File.OpenRead(Image))
        {
            var bytes = new byte[4096];
            source.ReadExactly(bytes);
            var uuidOffset = format switch { "EXT4" => 1128, "EROFS" => 1072, _ => 1132 };
            Assert.Equal(Convert.ToHexStringLower(bytes.AsSpan(uuidOffset, 16)), superblock["uuid"]!.GetValue<string>());
        }
        Assert.NotNull(superblock[format == "F2FS" ? "feature" : "featureIncompat"]);
        Assert.Equal(0, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        var info = await handler.ReadMetadataAsync(Output, Cancellation);
        Assert.True(info.Success, info.Summary);
        Assert.DoesNotContain(info.Entries, path => path.Contains(RepackMetadata.FileName));
        Assert.Contains(info.Entries, path => path.EndsWith("hello.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ErofsMetadata_PreservesVolumeName_AndAllowsEditingOptions()
    {
        new ErofsBuilder().Build(Source, Image, new ErofsOptions { VolumeName = "vendor" });
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal("vendor", metadata.PackingOptions["VolumeName"]);
        EditMetadata(json => json["packingOptions"]!["VolumeName"] = "modified");
        Assert.Equal(0, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        using var parser = new ErofsParser(Output);
        Assert.Equal("modified", parser.VolumeName);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task SparseFileSystemMetadata_MatchesOriginalSuperblock(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        Assert.True((await handler.PackAsync(Source, Image, Cancellation)).Success);
        Assert.True((await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var original = RepackMetadata.Read(Extracted)!.Configuration;
        var sparsePath = Path.Combine(_root, "sparse.img");
        var sparseOutput = Path.Combine(_root, "sparse-output");
        UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseImageConverter.ConvertRawToSparse(Image, sparsePath);
        var result = await new UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseFormatHandler()
            .UnpackAsync(sparsePath, sparseOutput, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var wrapped = RepackMetadata.Read(sparseOutput)!;
        Assert.Equal("Android Sparse Image (simg)", wrapped.FormatName);
        Assert.True(JsonNode.DeepEquals(original, wrapped.Configuration));
    }

    [Fact]
    public async Task CpioMetadata_IsNotAddedToArchive()
    {
        var handler = new CpioFormatHandler();
        var packed = await handler.PackAsync(Source, Image, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        var unpack = await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.True(RepackMetadata.Read(Extracted)!.CanPackDirectory);
        Assert.Equal(0, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        var info = await handler.ReadMetadataAsync(Output, Cancellation);
        Assert.Equal(["hello.txt"], info.Entries);
    }

    [Fact]
    public async Task DtboMetadata_EmbedsOriginalConfiguration()
    {
        File.WriteAllBytes(Image, DtboFormat.Pack(DtbFormat.Write(new DeviceTree())));
        var result = await new DtFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.NotNull(metadata.Configuration["header"]);
        Assert.Single(metadata.Configuration["entries"]!.AsArray());
        Assert.False(File.Exists(Path.Combine(Extracted, "dtbo.json")));
        Assert.Equal("1", metadata.SourceProperties["EntryCount"]);
        Assert.Equal(0, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
    }

    [Fact]
    public async Task ReadOnlyDirectory_HasMetadata_AndDoesNotFallBackToExt4()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Firmware", "Files", "review-native-rkfw.img");
        var result = await new RockchipFormatHandler().UnpackAsync(fixture, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal("Rockchip RKAF/RKFW Firmware", metadata.FormatName);
        Assert.False(metadata.CanPackDirectory);
        Assert.Equal(1, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task ApplicationFileSystemExtraction_WritesMetadata(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        Assert.True((await handler.PackAsync(Source, Image, Cancellation)).Success);
        var original = await handler.ReadMetadataAsync(Image, Cancellation);
        var result = await FirmwareService.Instance.UnpackFileSystemImageAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal(format, metadata.FormatName);
        Assert.Equal(format, metadata.Configuration["fileSystem"]!.GetValue<string>());
        Assert.NotEmpty(metadata.Configuration["superblock"]!.AsObject());
        foreach (var property in original.Metadata)
            Assert.Equal(property.Value, metadata.SourceProperties[property.Key]);
    }

    [Fact]
    public async Task FailedExtraction_DoesNotCreateMetadata()
    {
        File.WriteAllText(Image, "invalid image");
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(Extracted, RepackMetadata.FileName)));
    }

    [Fact]
    public async Task NoOverwrite_ProtectsMetadataAndContents()
    {
        new ErofsBuilder().Build(Source, Image);
        var handler = new ErofsFormatHandler();
        Assert.True((await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var before = File.ReadAllBytes(Path.Combine(Extracted, RepackMetadata.FileName));
        File.WriteAllText(Path.Combine(Extracted, "hello.txt"), "keep");
        var result = await handler.UnpackAsync(Image, Extracted, new UnpackOptions { Overwrite = false }, Cancellation);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Extracted, RepackMetadata.FileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Extracted, "hello.txt")));
    }

    [Fact]
    public async Task InvalidPackingOptions_DoNotTruncateOutput()
    {
        Ext4Builder.Build(Source, Image);
        Assert.True((await new Ext4FormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        EditMetadata(json => json["packingOptions"]!["BlockSize"] = "invalid");
        File.WriteAllText(Output, "keep");
        Assert.Equal(1, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public void InvalidMetadata_IsNotSilentlyPackedAsExt4()
    {
        Directory.CreateDirectory(Extracted);
        File.WriteAllText(Path.Combine(Extracted, RepackMetadata.FileName), "{invalid");
        File.WriteAllText(Output, "keep");
        Assert.Equal(1, FirmwareCli.Run(["pack", Extracted, "-o", Output]));
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public async Task ReservedFilenameCollision_PreservesTheOriginalPayloadFile()
    {
        File.WriteAllText(Path.Combine(Source, RepackMetadata.FileName), "ordinary payload file");
        new ErofsBuilder().Build(Source, Image);
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.False(result.Success);
        Assert.Equal("ordinary payload file", File.ReadAllText(Path.Combine(Extracted, RepackMetadata.FileName)));
    }

    private void EditMetadata(Action<JsonObject> edit)
    {
        var path = Path.Combine(Extracted, RepackMetadata.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }
}
